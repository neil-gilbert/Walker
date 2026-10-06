using System.Diagnostics;
using System.Text;
using System.Xml.Linq;
using Walker.Core;
namespace Walker.Execution;
public sealed class DotnetMutationExecutor(IProcessRunner runner) : IMutationExecutor, IBaselineVerifier
{
    private BuildCoverage? buildCoverage;
    private bool baselinePassed;

    public async Task VerifyAsync(VerificationRequest request, CancellationToken cancellationToken)
    {
        baselinePassed = false;
        buildCoverage = null;
        var build = await Build(request.Project, request.Root, cancellationToken, observe: true);
        if (build.ExitCode != 0) throw new InvalidOperationException("Baseline production build failed: " + Diagnostic(build));
        buildCoverage = new(request.Root, request.Project, build.OutputTruncated ? "" : build.StandardOutput);
        var tests = await RunTests(new(request.Tests), request.Root, cancellationToken);
        if (tests.Outcome != MutationOutcome.Survived) throw new InvalidOperationException("Baseline tests failed or could not run: " + tests.Detail);
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
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
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
            var tests = await RunTests(selection, context.Request.Root, cancellationToken, baseline: false,
                classifyBuildFailureFor: covered ? context.Request.Project : null);
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
        }
    }
    private Task<ProcessResult> Build(string project, string root, CancellationToken token, bool restore = true, bool observe = false)
    {
        var args = new List<string> { "build", project, "--nologo", "--verbosity", "quiet" };
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
            // Baseline builds every dependency once. A mutant changes only production source:
            // reuse restored packages; let MSBuild refresh any affected dependent assemblies.
            var build = await Build(project, root, token, restore: baseline, observe: baseline);
            buildMs += build.DurationMs;
            if (build.ExitCode != 0)
            {
                // The test build includes production on the fast path. Diagnose only a failed
                // build with a separate production build so CompileError/TestError remain exact.
                if (classifyBuildFailureFor != null)
                {
                    var production = await Build(classifyBuildFailureFor, root, token, restore: false);
                    buildMs += production.DurationMs;
                    if (production.ExitCode != 0) return (MutationOutcome.CompileError, buildMs, testMs, Diagnostic(production));
                }
                return (MutationOutcome.TestError, buildMs, testMs, "Test project build failed: " + Diagnostic(build));
            }
            if (baseline && !build.OutputTruncated) buildCoverage?.ObserveTestBuild(project, build.StandardOutput);
            var directory = Path.Combine(Path.GetTempPath(), "walker-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                var args = new List<string> { "test", project, "--no-build", "--no-restore", "--nologo", "--verbosity", "quiet",
                    "--logger", "trx", "--results-directory", directory };
                if (selection.Filter != null) args.AddRange(["--filter", selection.Filter]);
                var run = await runner.RunAsync(new("dotnet", args, root), token);
                testMs += run.DurationMs;
                var reports = Directory.GetFiles(directory, "*.trx", SearchOption.AllDirectories);
                if (reports.Length == 0) return (MutationOutcome.TestError, buildMs, testMs, "Test runner produced no TRX results: " + Diagnostic(run));
                var total = 0; var failed = 0;
                foreach (var report in reports)
                {
                    var document = XDocument.Load(report);
                    var counters = document.Descendants().FirstOrDefault(e => e.Name.LocalName == "Counters");
                    if (counters == null) return (MutationOutcome.TestError, buildMs, testMs, "Malformed test report.");
                    var executed = (int?)counters.Attribute("executed") ?? 0;
                    var failures = (int?)counters.Attribute("failed") ?? 0;
                    var passed = (int?)counters.Attribute("passed") ?? 0;
                    if (executed != passed + failures || counters.Attributes().Any(a => (a.Name.LocalName is "error" or "timeout" or "aborted") && int.TryParse(a.Value, out var count) && count > 0)
                        || document.Descendants().Any(e => e.Name.LocalName == "ResultSummary" && ((string?)e.Attribute("outcome") is "Aborted" or "Error")))
                        return (MutationOutcome.TestError, buildMs, testMs, "Test execution aborted or reported infrastructure errors.");
                    total += executed; failed += failures;
                }
                if (total == 0 || (run.ExitCode != 0 && failed == 0) || (run.ExitCode == 0 && failed > 0))
                    return (MutationOutcome.TestError, buildMs, testMs, "No executed tests or inconsistent runner results: " + Diagnostic(run));
                // One confirmed test failure is sufficient evidence; baseline validated every project.
                if (failed > 0) return (MutationOutcome.Killed, buildMs, testMs, null);
            }
            finally { Directory.Delete(directory, recursive: true); }
        }
        return (MutationOutcome.Survived, buildMs, testMs, null);
    }
    private static string Diagnostic(ProcessResult result)
    {
        var text = (result.StandardError + "\n" + result.StandardOutput).Trim();
        return text.Length > 2000 ? text[^2000..] : text;
    }
}
