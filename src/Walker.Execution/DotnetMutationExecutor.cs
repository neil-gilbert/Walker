using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
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
        var tests = await RunTests(new(request.Tests, request.Filter), request.Root, cancellationToken);
        if (tests.Outcome != MutationOutcome.Survived) throw new InvalidOperationException("Baseline tests failed or could not run: " + tests.Detail);
        baselineTestMs = tests.BuildMs + tests.TestMs;
        baselinePassed = true;
    }
    public async Task<MutationResult> ExecuteAsync(Walker.Core.Mutant mutant, VerificationContext context, CancellationToken cancellationToken)
    {
        var timer = Stopwatch.StartNew();
        TestRunResult? killedRun = null;
        // Await the entire attempt, including its finally: confirmation must see restored source and a cleared journal.
        var result = await ExecuteMutationAsync(mutant, context, cancellationToken, run => killedRun = run);
        if (!context.Request.ConfirmKills || result.Outcome != MutationOutcome.Killed) return result;
        var confirmationTimer = Stopwatch.StartNew();
        long buildMs = 0, testMs = 0;
        MutationResult Finish(MutationOutcome outcome, string? detail, bool? confirmed = null) => result with
        {
            Outcome = outcome, Detail = detail, KillConfirmed = confirmed,
            DurationMs = timer.ElapsedMilliseconds, ConfirmationMs = confirmationTimer.ElapsedMilliseconds,
            BuildMs = result.BuildMs + buildMs, TestMs = result.TestMs + testMs
        };
        var failures = killedRun?.Failures ?? [];
        // Bounded reporting must never silently confirm only a subset of failures, or broaden an unsafe filter.
        if (killedRun?.FailureSelectionComplete != true || failures.Count == 0
            || failures.Any(f => f.FullyQualifiedName == null || !Regex.IsMatch(f.FullyQualifiedName, @"^[\p{L}\p{N}_.+`<>\[\],]+$")))
            return Finish(MutationOutcome.TestError, "Kill could not be confirmed: missing, unsupported, or more than 10 failing-test identities in TRX. No unmutated tests were run.");
        var failedFilter = string.Join("|", failures.Select(f => f.FullyQualifiedName!).Distinct(StringComparer.Ordinal)
            .Select(name => "FullyQualifiedName=" + name.Replace(",", "%2C")));
        // Preserve the original scope (including trait filters) when selecting failed test methods.
        var filter = killedRun!.Filter == null ? failedFilter : "(" + killedRun.Filter + ")&(" + failedFilter + ")";
        using var hang = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (baselinePassed) hang.CancelAfter(HangLimit);
        try
        {
            // Restoration changed production again. Rebuild it explicitly even for customized test references.
            var build = await Build(context.Request.Project, context.Request.Root, hang.Token, restore: false);
            buildMs += build.DurationMs;
            if (build.ExitCode != 0) return Finish(MutationOutcome.TestError, "Unmutated confirmation build failed: " + Diagnostic(build));
            var confirmation = await RunTests(new([failures[0].Project], filter), context.Request.Root, hang.Token, baseline: false);
            buildMs += confirmation.BuildMs;
            testMs += confirmation.TestMs;
            return confirmation.Outcome switch
            {
                MutationOutcome.Survived => Finish(MutationOutcome.Killed, "Failing test methods passed on restored, unmutated source.", true),
                MutationOutcome.Killed => Finish(MutationOutcome.TestError, "Failing tests also failed on unmutated source; the failure is not caused by the mutant (possible flaky tests).", false),
                _ => Finish(MutationOutcome.TestError, "Kill could not be confirmed on unmutated source: " + confirmation.Detail)
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Finish(MutationOutcome.TimedOut, "Kill confirmation cancelled or exhausted the verification budget; source was already restored.");
        }
        catch (OperationCanceledException)
        {
            return Finish(MutationOutcome.TestError, "Unmutated kill confirmation exceeded the hang limit; evidence is unreliable.");
        }
        catch (Exception ex) { return Finish(MutationOutcome.TestError, "Kill confirmation failed: " + ex.Message); }
    }
    private async Task<MutationResult> ExecuteMutationAsync(Walker.Core.Mutant mutant, VerificationContext context,
        CancellationToken cancellationToken, Action<TestRunResult> recordKilled)
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
            TestRunResult tests;
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
            if (tests.Outcome == MutationOutcome.Killed) recordKilled(tests);
            return new(mutant, tests.Outcome, timer.ElapsedMilliseconds, buildMs, testMs, tests.Detail,
                tests.Outcome == MutationOutcome.Survived ? SurvivorClassification.Survived : null,
                tests.Outcome == MutationOutcome.Killed ? tests.Failures?.Select(f => f.Name).ToArray() ?? [] : null);
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
    private async Task<TestRunResult> RunTests(TestSelection selection, string root, CancellationToken token, bool baseline = true, string? classifyBuildFailureFor = null)
    {
        if (selection.Projects.Count == 0) return new(MutationOutcome.TestError, 0, 0, "No relevant test projects selected.");
        long buildMs = 0, testMs = 0;
        
        foreach (var project in selection.Projects)
        {
            if (baseline)
            {
                // Baseline builds every dependency once and records reference metadata for build sharing.
                var build = await Build(project, root, token, observe: true);
                buildMs += build.DurationMs;
                if (build.ExitCode != 0) return new(MutationOutcome.TestError, buildMs, testMs, "Test project build failed: " + Diagnostic(build));
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
                    if (baseline || run.ExitCode == 0) return new(MutationOutcome.TestError, buildMs, testMs, "Test runner produced no TRX results: " + Diagnostic(run));
                    // The test build includes production on the fast path. Diagnose only a failed
                    // run with a separate production build so CompileError/TestError remain exact.
                    if (classifyBuildFailureFor != null)
                    {
                        var production = await Build(classifyBuildFailureFor, root, token, restore: false);
                        buildMs += production.DurationMs;
                        if (production.ExitCode != 0) return new(MutationOutcome.CompileError, buildMs, testMs, Diagnostic(production));
                    }
                    return new(MutationOutcome.TestError, buildMs, testMs, "Test project build or test run failed without results: " + Diagnostic(run));
                }
                var total = 0; var failed = 0;
                var failures = new List<TestFailure>();
                var complete = true;
                foreach (var report in reports)
                {
                    var counters = TrxReport.Read(report);
                    if (counters.Error != null) return new(MutationOutcome.TestError, buildMs, testMs, counters.Error);
                    total += counters.Executed; failed += counters.Failed;
                    complete &= counters.FailureSelectionComplete;
                    foreach (var failure in counters.Failures)
                    {
                        if (failures.Count < 10) failures.Add(new(failure.Name, failure.FullyQualifiedName, project));
                        else complete = false;
                    }
                }
                if (total == 0 || (run.ExitCode != 0 && failed == 0) || (run.ExitCode == 0 && failed > 0))
                    return new(MutationOutcome.TestError, buildMs, testMs, "No executed tests or inconsistent runner results: " + Diagnostic(run));
                // One confirmed test failure is sufficient evidence; baseline validated every project.
                if (failed > 0) return new(MutationOutcome.Killed, buildMs, testMs, Failures: failures,
                    FailureSelectionComplete: complete, Filter: selection.Filter);
            }
            finally
            {
                // A lingering test host can lock results (mainly on Windows); never let cleanup replace the outcome.
                try { Directory.Delete(directory, recursive: true); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }
        return new(MutationOutcome.Survived, buildMs, testMs, null);
    }
    private sealed record TestFailure(string Name, string? FullyQualifiedName, string Project);
    private sealed record TestRunResult(MutationOutcome Outcome, long BuildMs, long TestMs, string? Detail = null,
        IReadOnlyList<TestFailure>? Failures = null, bool FailureSelectionComplete = false, string? Filter = null);
    private static string Diagnostic(ProcessResult result)
    {
        var text = (result.StandardError + "\n" + result.StandardOutput).Trim();
        return text.Length > 2000 ? text[^2000..] : text;
    }
}
