using System.Diagnostics;
using System.Text;
using System.Xml;
using Walker.Core;
namespace Walker.Execution;
public sealed class DotnetMutationExecutor(IProcessRunner runner, TimeSpan? hangAllowance = null) : IMutationExecutor, IBaselineVerifier
{
    // Analyzers cannot change compiled behaviour; skipping them shortens every build. Source generators still run.
    private static readonly string[] BuildProperties = ["-p:RunAnalyzers=false"];
    private const int HangFactor = 3;
    private readonly TimeSpan allowance = hangAllowance ?? TimeSpan.FromSeconds(5);
    private BuildCoverage? buildCoverage;
    private bool baselinePassed;
    private long baselineTestMs;
    // A mutant whose build and tests take far longer than the baseline is treated as hung (e.g. an infinite loop).
    private TimeSpan HangLimit => TimeSpan.FromMilliseconds(HangFactor * baselineTestMs) + allowance;

    public async Task VerifyAsync(VerificationRequest request, CancellationToken cancellationToken)
    {
        baselinePassed = false;
        buildCoverage = null;
        var build = await Build(request.Project, request.Root, cancellationToken, observe: true);
        if (build.ExitCode != 0) throw new InvalidOperationException("Baseline production build failed: " + Diagnostic(build));
        buildCoverage = new(request.Root, request.Project, build.OutputTruncated ? "" : build.StandardOutput);
        var tests = await RunTests(new(request.Tests), request.Root, cancellationToken);
        if (tests.Outcome != MutationOutcome.Survived) throw new InvalidOperationException("Baseline tests failed or could not run: " + tests.Detail);
        baselineTestMs = tests.BuildMs + tests.TestMs;
        baselinePassed = true;
    }
    public async Task<MutationResult> ExecuteAsync(Walker.Core.Mutant mutant, VerificationContext context, CancellationToken cancellationToken)
    {
        var timer = Stopwatch.StartNew();
        var path = Path.GetFullPath(mutant.File, context.Request.Root);
        var bytes = await File.ReadAllBytesAsync(path, cancellationToken);
        // Decode with BOM detection, but restore the original bytes, including encoding and newline style.
        using var reader = new StreamReader(new MemoryStream(bytes), Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var source = await reader.ReadToEndAsync(cancellationToken);
        var encoding = reader.CurrentEncoding;
        if (Walker.Core.Mutant.Hash(source) != mutant.SourceHash || source.Substring(mutant.SpanStart, mutant.SpanLength) != mutant.Original)
            return new(mutant, MutationOutcome.TestError, Detail: "Source changed since discovery; refusing to apply stale mutation.");
        var replacement = source[..mutant.SpanStart] + mutant.Replacement + source[(mutant.SpanStart + mutant.SpanLength)..];
        var encoded = encoding.GetBytes(replacement);
        var preamble = encoding.GetPreamble();
        var hasPreamble = preamble.Length > 0 && bytes.AsSpan().StartsWith(preamble);
        if (hasPreamble) encoded = [..preamble, ..encoded];
        long buildMs = 0, testMs = 0;
        var journaled = false;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            await MutationJournal.WriteAsync(context.Request.Root, path, bytes, encoded);
            journaled = true;
            await File.WriteAllBytesAsync(path, encoded, cancellationToken);
            var selection = await context.TestSelector.SelectTestsAsync(mutant, cancellationToken);
            var covered = baselinePassed && selection.Projects.Count > 0
                && buildCoverage?.Covers(selection.Projects[0], context.Request.Root, context.Request.Project) == true;
            if (!covered)
            {
                var build = await Build(context.Request.Project, context.Request.Root, cancellationToken, restore: false);
                buildMs += build.DurationMs;
                if (build.ExitCode != 0) return new(mutant, MutationOutcome.CompileError, timer.ElapsedMilliseconds, buildMs, Detail: Diagnostic(build));
            }
            using var hang = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            if (baselinePassed) hang.CancelAfter(HangLimit);
            (MutationOutcome Outcome, long BuildMs, long TestMs, string? Detail) tests;
            try
            {
                tests = await RunTests(selection, context.Request.Root, hang.Token, baseline: false,
                    classifyBuildFailureFor: covered ? context.Request.Project : null);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && hang.IsCancellationRequested)
            {
                return new(mutant, MutationOutcome.Hung, timer.ElapsedMilliseconds, buildMs, timer.ElapsedMilliseconds - buildMs,
                    $"Build and tests exceeded the hang limit ({HangLimit.TotalSeconds:F1}s, {HangFactor}x baseline); the change was detected.");
            }
            buildMs += tests.BuildMs;
            testMs = tests.TestMs;
            return new(mutant, tests.Outcome, timer.ElapsedMilliseconds, buildMs, testMs, tests.Detail,
                tests.Outcome == MutationOutcome.Survived ? SurvivorClassification.Survived : null);
        }
        catch (OperationCanceledException) { return new(mutant, MutationOutcome.TimedOut, timer.ElapsedMilliseconds, buildMs, testMs, "Execution cancelled; source restored."); }
        catch (Exception ex) { return new(mutant, MutationOutcome.TestError, timer.ElapsedMilliseconds, buildMs, testMs, ex.Message); }
        finally
        {
            // Cleanup is deliberately independent of the cancelled verification token.
            await File.WriteAllBytesAsync(path, bytes, CancellationToken.None);
            if (journaled) MutationJournal.Clear(context.Request.Root);
        }
    }
    private Task<ProcessResult> Build(string project, string root, CancellationToken token, bool restore = true, bool observe = false)
    {
        List<string> args = ["build", project, "--nologo", "--verbosity", "quiet", .. BuildProperties];
        if (!restore) args.Add("--no-restore");
        if (observe)
        {
            // Queries alone request evaluation-only mode, even under `dotnet build`.
            args.Add("-target:Build");
            args.Add("-getProperty:TargetFramework,TargetFrameworks,Configuration,Platform,RuntimeIdentifier,BuildProjectReferences");
            args.Add("-getItem:_MSBuildProjectReferenceExistent");
        }
        return runner.RunAsync(new("dotnet", args, root, OutputLimit: observe ? 1024 * 1024 : 16384), token);
    }
    private async Task<(MutationOutcome Outcome, long BuildMs, long TestMs, string? Detail)> RunTests(TestSelection selection, string root, CancellationToken token, bool baseline = true, string? classifyBuildFailureFor = null)
    {
        if (selection.Projects.Count == 0) return (MutationOutcome.TestError, 0, 0, "No relevant test projects selected.");
        long buildMs = 0, testMs = 0;
        
        foreach (var project in selection.Projects)
        {
            if (baseline)
            {
                // Baseline builds every dependency once and records reference metadata for build sharing.
                var build = await Build(project, root, token, observe: true);
                buildMs += build.DurationMs;
                if (build.ExitCode != 0) return (MutationOutcome.TestError, buildMs, testMs, "Test project build failed: " + Diagnostic(build));
                if (!build.OutputTruncated) buildCoverage?.ObserveTestBuild(project, build.StandardOutput);
            }
            var directory = Path.Combine(Path.GetTempPath(), "walker-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                // A mutant changes only production source: reuse restored packages and let one
                // `dotnet test` invocation refresh affected assemblies and run the tests.
                List<string> args = ["test", project, "--no-restore", "--nologo", "--verbosity", "quiet",
                    "--logger", "trx", "--results-directory", directory, .. BuildProperties];
                if (baseline) args.Add("--no-build");
                if (selection.Filter != null) args.AddRange(["--filter", selection.Filter]);
                var run = await runner.RunAsync(new("dotnet", args, root), token);
                testMs += run.DurationMs;
                var reports = Directory.GetFiles(directory, "*.trx", SearchOption.AllDirectories);
                if (reports.Length == 0)
                {
                    if (baseline || run.ExitCode == 0) return (MutationOutcome.TestError, buildMs, testMs, "Test runner produced no TRX results: " + Diagnostic(run));
                    // The test build includes production on the fast path. Diagnose only a failed
                    // run with a separate production build so CompileError/TestError remain exact.
                    if (classifyBuildFailureFor != null)
                    {
                        var production = await Build(classifyBuildFailureFor, root, token, restore: false);
                        buildMs += production.DurationMs;
                        if (production.ExitCode != 0) return (MutationOutcome.CompileError, buildMs, testMs, Diagnostic(production));
                    }
                    return (MutationOutcome.TestError, buildMs, testMs, "Test project build or test run failed without results: " + Diagnostic(run));
                }
                var total = 0; var failed = 0;
                foreach (var report in reports)
                {
                    var counters = ReadReport(report);
                    if (counters.Error != null) return (MutationOutcome.TestError, buildMs, testMs, counters.Error);
                    total += counters.Executed; failed += counters.Failed;
                }
                if (total == 0 || (run.ExitCode != 0 && failed == 0) || (run.ExitCode == 0 && failed > 0))
                    return (MutationOutcome.TestError, buildMs, testMs, "No executed tests or inconsistent runner results: " + Diagnostic(run));
                // One confirmed test failure is sufficient evidence; baseline validated every project.
                if (failed > 0) return (MutationOutcome.Killed, buildMs, testMs, null);
            }
            finally
            {
                // A lingering test host can lock results (mainly on Windows); never let cleanup replace the outcome.
                try { Directory.Delete(directory, recursive: true); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }
        return (MutationOutcome.Survived, buildMs, testMs, null);
    }
    // Stream the report: only ResultSummary/Counters are needed, and Results can be large.
    private static (int Executed, int Failed, string? Error) ReadReport(string path)
    {
        using var reader = XmlReader.Create(path, new XmlReaderSettings { IgnoreComments = true, IgnoreWhitespace = true });
        (int Executed, int Failed)? counters = null;
        while (reader.Read())
        {
            if (reader.NodeType != XmlNodeType.Element) continue;
            if (reader.LocalName == "ResultSummary" && reader.GetAttribute("outcome") is "Aborted" or "Error")
                return (0, 0, "Test execution aborted or reported infrastructure errors.");
            if (reader.LocalName != "Counters" || counters != null) continue;
            int Count(string name) => int.TryParse(reader.GetAttribute(name), out var value) ? value : 0;
            var executed = Count("executed"); var failures = Count("failed");
            if (executed != Count("passed") + failures || Count("error") > 0 || Count("timeout") > 0 || Count("aborted") > 0)
                return (0, 0, "Test execution aborted or reported infrastructure errors.");
            counters = (executed, failures);
        }
        return counters is { } found ? (found.Executed, found.Failed, null) : (0, 0, "Malformed test report.");
    }
    private static string Diagnostic(ProcessResult result)
    {
        var text = (result.StandardError + "\n" + result.StandardOutput).Trim();
        return text.Length > 2000 ? text[^2000..] : text;
    }
}
