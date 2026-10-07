using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Walker.Core;
namespace Walker.Execution;
public sealed class DotnetMutationExecutor(IProcessRunner runner, TimeSpan? hangAllowance = null, bool compiledTests = false,
    bool captureBaselineIdentities = false, string? resultsRoot = null) : IMutationExecutor, IBaselineVerifier
{
    // Analyzers cannot change compiled behaviour; skipping them shortens every build. Source generators still run.
    private static readonly string[] BuildProperties = ["-p:RunAnalyzers=false"];
    private const int HangFactor = 3;
    private readonly TimeSpan allowance = hangAllowance ?? TimeSpan.FromSeconds(5);
    private BuildCoverage? buildCoverage;
    private readonly Dictionary<string, string[]> testFrameworks = new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    private readonly PreferredTestPlanner preferredTests = new();
    private readonly Dictionary<(string Project, string? Filter, string? Framework), CompiledTestOutput> compiledOutputs = new();
    internal readonly Dictionary<string, IReadOnlyList<string>> BaselineIdentities = new(StringComparer.Ordinal);
    private bool baselinePassed;
    private long baselineTestMs;
    // A mutant whose build and tests take far longer than the baseline is treated as hung (e.g. an infinite loop).
    private TimeSpan HangLimit => TimeSpan.FromMilliseconds(HangFactor * baselineTestMs) + allowance;
    internal TimeSpan PreparedHangLimit => HangLimit;

    public async Task VerifyAsync(VerificationRequest request, CancellationToken cancellationToken)
    {
        baselinePassed = false;
        buildCoverage = null;
        testFrameworks.Clear();
        preferredTests.Reset();
        compiledOutputs.Clear();
        BaselineIdentities.Clear();
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
        TestRunResult? killedRun = null;
        // Await the entire attempt, including its finally: confirmation must see restored source and a cleared journal.
        var result = await ExecuteMutationAsync(mutant, context, cancellationToken, run => killedRun = run);
        return await ConfirmResultAsync(result, context, killedRun, cancellationToken);
    }
    internal async Task<MutationResult> ConfirmResultAsync(MutationResult result, VerificationContext context, TestRunResult? killedRun, CancellationToken cancellationToken,
        CompiledTestOutput? ordinaryOutput = null)
    {
        if (!context.Request.ConfirmKills || result.Outcome != MutationOutcome.Killed) return result;
        var confirmationTimer = Stopwatch.StartNew();
        long buildMs = 0, testMs = 0;
        MutationResult Finish(MutationOutcome outcome, string? detail, bool? confirmed = null) => result with
        {
            Outcome = outcome, Detail = detail, KillConfirmed = confirmed,
            DurationMs = result.DurationMs + confirmationTimer.ElapsedMilliseconds, ConfirmationMs = confirmationTimer.ElapsedMilliseconds,
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
        if (baselinePassed || ordinaryOutput != null) hang.CancelAfter(HangLimit);
        try
        {
            TestRunResult confirmation;
            if (ordinaryOutput != null)
                // Workers own a frozen ordinary generation, independently validated before dispatch.
                // Its outputs are separate from the active prepared DLLs and serial fallback builds.
                confirmation = await RunAssembly(ordinaryOutput, failures[0].Project, context.Request.Root, filter,
                    killedRun.Framework, new Dictionary<string, string?>(), hang.Token);
            else
            {
                // Restoration changed production again. Rebuild explicitly even for customized references.
                var build = await Build(context.Request.Project, context.Request.Root, hang.Token, restore: false);
                buildMs += build.DurationMs;
                if (build.ExitCode != 0) return Finish(MutationOutcome.TestError, "Unmutated confirmation build failed: " + Diagnostic(build));
                confirmation = await RunTests(new([failures[0].Project], filter), context.Request.Root, hang.Token, baseline: false,
                    confirmationFramework: killedRun.Framework, prioritize: false);
            }
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
            var selection = await context.TestSelector.SelectTestsAsync(mutant, cancellationToken);
            // A passing full-suite baseline does not prove a subset is independent of test order.
            // Validate each preferred scope on unmutated source once before attributing failures to mutations.
            var prepared = await PreparePreferred(selection, context.Request, mutant, cancellationToken);
            buildMs += prepared.BuildMs; testMs += prepared.TestMs;
            await MutationJournal.WriteAsync(context.Request.Root, path, bytes, encoded);
            journaled = true;
            await File.WriteAllBytesAsync(path, encoded, cancellationToken);
            var covered = baselinePassed && selection.Projects.Count > 0
                && buildCoverage?.Covers(selection.Projects[0], context.Request.Root, context.Request.Project,
                    Frameworks(selection.Projects[0], context.Request.Root)[0]) == true;
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
                    classifyBuildFailureFor: covered ? context.Request.Project : null, mutant: mutant);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && hang.IsCancellationRequested)
            {
                return new(mutant, MutationOutcome.Hung, timer.ElapsedMilliseconds, buildMs, timer.ElapsedMilliseconds - buildMs,
                    $"Build and tests exceeded the hang limit ({HangLimit.TotalSeconds:F1}s, {HangFactor}x baseline); the change was detected.");
            }
            buildMs += tests.BuildMs;
            testMs += tests.TestMs;
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
    private Task<ProcessResult> Build(string project, string root, CancellationToken token, bool restore = true, bool observe = false, string? framework = null, BuildObservation? observation = null)
    {
        List<string> args = ["build", project, "--nologo", "--verbosity", "quiet", .. BuildProperties];
        if (!restore) args.Add("--no-restore");
        if (framework != null) args.AddRange(["--framework", framework]);
        if (observation != null)
            args.AddRange(["-verbosity:diagnostic", "-consoleloggerparameters:Verbosity=Quiet;NoSummary;NoPerformanceSummary", observation.Argument]);
        if (observe && observation == null)
        {
            // Queries alone request evaluation-only mode, even under `dotnet build`.
            args.Add("-target:Build");
            args.Add("-getProperty:TargetFramework,TargetFrameworks,Configuration,Platform,RuntimeIdentifier,BuildProjectReferences");
            args.Add("-getItem:_MSBuildProjectReferenceExistent");
        }
        return runner.RunAsync(new("dotnet", args, root, OutputLimit: observe ? 1024 * 1024 : 16384), token);
    }
    private IReadOnlyList<string?> Frameworks(string project, string root) =>
        baselinePassed && testFrameworks.TryGetValue(Path.GetFullPath(project, root), out var frameworks) ? frameworks : new string?[] { null };
    private async Task<TestRunResult> PreparePreferred(TestSelection selection, VerificationRequest request, Walker.Core.Mutant mutant, CancellationToken token)
    {
        if (!baselinePassed || selection.Projects.Count == 0) return new(MutationOutcome.Survived, 0, 0);
        // Only the first attempted scope may need a new validation. Later scopes use an exact
        // cached validation or the original filter, so an early kill cannot waste later startups.
        var project = selection.Projects[0];
        var framework = Frameworks(project, request.Root)[0];
        var scope = preferredTests.For(project, request.Root, framework, selection.Filter, mutant);
        if (scope == null || preferredTests.IsValidated(scope)
            || buildCoverage?.Covers(project, request.Root, request.Project, framework) != true) return new(MutationOutcome.Survived, 0, 0);
        using var hang = CancellationTokenSource.CreateLinkedTokenSource(token);
        hang.CancelAfter(HangLimit);
        var timer = Stopwatch.StartNew();
        try
        {
            var result = await RunTestScope(project, request.Root, scope.Filter, framework, hang.Token, noBuild: false, classifyBuildFailureFor: null);
            preferredTests.Validate(scope, result.Outcome == MutationOutcome.Survived, result.BuildMs + result.TestMs);
            return new(MutationOutcome.Survived, result.BuildMs, result.TestMs);
        }
        catch (Exception ex) when (!token.IsCancellationRequested && (ex is OperationCanceledException or IOException or InvalidOperationException))
        {
            preferredTests.Validate(scope, false, timer.ElapsedMilliseconds);
            return new(MutationOutcome.Survived, 0, timer.ElapsedMilliseconds);
        }
    }
    private static string[] ReadFrameworks(ProcessResult build)
    {
        if (build.OutputTruncated) return [];
        try
        {
            using var document = JsonDocument.Parse(build.StandardOutput);
            if (!document.RootElement.TryGetProperty("Properties", out var properties)
                || !properties.TryGetProperty("TargetFramework", out var framework) || framework.GetString() != ""
                || !properties.TryGetProperty("TargetFrameworks", out var frameworks) || frameworks.ValueKind != JsonValueKind.String) return [];
            return frameworks.GetString()!.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Distinct(StringComparer.Ordinal).ToArray();
        }
        catch (JsonException) { return []; }
        catch (InvalidOperationException) { return []; }
    }
    private async Task<TestRunResult> RunTests(TestSelection selection, string root, CancellationToken token, bool baseline = true,
        string? classifyBuildFailureFor = null, string? confirmationFramework = null, bool prioritize = true, Walker.Core.Mutant? mutant = null)
    {
        if (selection.Projects.Count == 0) return new(MutationOutcome.TestError, 0, 0, "No relevant test projects selected.");
        long buildMs = 0, testMs = 0;
        
        foreach (var project in selection.Projects)
        {
            if (baseline)
            {
                // Baseline builds every dependency once and records reference metadata for build sharing.
                using var observation = BuildObservation.For(project, root);
                var build = await Build(project, root, token, observe: true, observation: observation);
                buildMs += build.DurationMs;
                if (build.ExitCode != 0) return new(MutationOutcome.TestError, buildMs, testMs, "Test project build failed: " + Diagnostic(build));
                var metadata = observation?.Read(project, root);
                // Property-query mode evaluates the root before loggers attach. Use an ordinary build
                // for capture, and retain the original query route if the observer could not prove it.
                if (observation != null && metadata == null)
                {
                    build = await Build(project, root, token, restore: false, observe: true);
                    buildMs += build.DurationMs;
                    if (build.ExitCode != 0) return new(MutationOutcome.TestError, buildMs, testMs, "Test metadata build failed: " + Diagnostic(build));
                }
                if (!build.OutputTruncated) buildCoverage?.ObserveTestBuild(project, build.StandardOutput);
                var frameworks = metadata == null ? ReadFrameworks(build) : ReadFrameworks(build with { StandardOutput = metadata, OutputTruncated = false });
                if (frameworks.Length > 0)
                {
                    testFrameworks[Path.GetFullPath(project, root)] = frameworks;
                    // Outer multi-target builds expose no reference graph. Observe each already-warm inner build
                    // once so later mutants can share compatible production builds, including transitive references.
                    foreach (var framework in frameworks)
                    {
                        var captured = observation?.Read(project, root, framework);
                        if (captured != null)
                        {
                            buildCoverage?.ObserveTestBuild(project, captured, framework);
                            continue;
                        }
                        var inner = await Build(project, root, token, restore: false, observe: true, framework: framework);
                        buildMs += inner.DurationMs;
                        if (inner.ExitCode != 0) return new(MutationOutcome.TestError, buildMs, testMs, "Test framework build failed: " + Diagnostic(inner));
                        if (!inner.OutputTruncated) buildCoverage?.ObserveTestBuild(project, inner.StandardOutput, framework);
                    }
                }
            }
            IReadOnlyList<string?> scopes = baseline ? [null] : confirmationFramework != null ? [confirmationFramework] : Frameworks(project, root);
            foreach (var framework in scopes)
            {
                var noBuild = baseline;
                CompiledTestOutput? compiledOutput = null;
                var scope = !baseline && prioritize && mutant != null
                    ? preferredTests.For(project, root, framework, selection.Filter, mutant) : null;
                if (scope != null && preferredTests.IsValidated(scope))
                {
                    var preferred = await RunTestScope(project, root, scope.Filter, framework, token, noBuild: false, classifyBuildFailureFor);
                    buildMs += preferred.BuildMs; testMs += preferred.TestMs;
                    preferredTests.RecordAttempt(scope, preferred.Outcome, preferred.NoTests, preferred.BuildMs + preferred.TestMs);
                    if (preferred.Outcome == MutationOutcome.Killed)
                        preferredTests.Learn(project, root, framework, selection.Filter, mutant!, (preferred.Failures ?? []).Select(f => f.FullyQualifiedName));
                    if (preferred.Outcome != MutationOutcome.Survived && !preferred.NoTests)
                        return preferred with { BuildMs = buildMs, TestMs = testMs };
                    // The preferred invocation refreshed the mutated graph. Reuse only these fresh
                    // outputs when the original full filter is needed; a subset cannot prove survival.
                    noBuild = true;
                    if (compiledTests && compiledOutputs.TryGetValue((Path.GetFullPath(project, root), selection.Filter, framework), out var verified))
                    {
                        // This successful project invocation built the current mutant. Evaluate its
                        // output again; never use a baseline assembly merely because it still exists.
                        var refreshed = await ObserveOutput(project, root, framework, token);
                        buildMs += refreshed.DurationMs;
                        var output = CompiledTestOutput.Read(refreshed, project, root, framework);
                        if (output?.Identity == verified.Identity) compiledOutput = output;
                        else noBuild = false; // An unknown/stale output requires the ordinary fresh build.
                    }
                }
                var result = await RunTestScope(project, root, selection.Filter, framework, token, noBuild, classifyBuildFailureFor, baseline, compiledOutput);
                buildMs += result.BuildMs; testMs += result.TestMs;
                if (baseline && result.Outcome == MutationOutcome.Survived)
                {
                    if (result.Identities != null) BaselineIdentities[Path.GetFullPath(project, root)] = result.Identities;
                    preferredTests.ObserveBaseline(project, root, selection.Filter, result.TestMs);
                    if (compiledTests && result.Identities is { Count: > 0 })
                    {
                        using var probeBudget = CancellationTokenSource.CreateLinkedTokenSource(token);
                        probeBudget.CancelAfter(TimeSpan.FromMilliseconds(HangFactor * (buildMs + result.TestMs)) + allowance);
                        var probeTimer = Stopwatch.StartNew();
                        long observedMs = 0;
                        try
                        {
                            var outputs = new List<(string? Framework, CompiledTestOutput Output)>();
                            var identities = new List<string>();
                            IReadOnlyList<string?> probeFrameworks = testFrameworks.TryGetValue(Path.GetFullPath(project, root), out var known) ? known.Cast<string?>().ToArray() : new string?[] { null };
                            foreach (var probeFramework in probeFrameworks)
                            {
                                var observed = await ObserveOutput(project, root, probeFramework, probeBudget.Token);
                                observedMs += observed.DurationMs;
                                var output = CompiledTestOutput.Read(observed, project, root, probeFramework);
                                if (output == null) break;
                                var probe = await RunTestScope(project, root, selection.Filter, probeFramework, probeBudget.Token,
                                    noBuild: true, classifyBuildFailureFor: null, output: output, captureIdentity: true);
                                if (probe.Outcome != MutationOutcome.Survived || probe.Identities == null) break;
                                outputs.Add((probeFramework, output)); identities.AddRange(probe.Identities);
                            }
                            if (outputs.Count == probeFrameworks.Count && identities.Order(StringComparer.Ordinal).SequenceEqual(result.Identities.Order(StringComparer.Ordinal)))
                                foreach (var output in outputs)
                                    compiledOutputs[(Path.GetFullPath(project, root), selection.Filter, output.Framework)] = output.Output;
                        }
                        catch (Exception ex) when (!token.IsCancellationRequested && ex is OperationCanceledException or IOException or InvalidOperationException or System.Xml.XmlException)
                        { /* Compatibility failure disables this optional path; the project baseline passed. */ }
                        buildMs += observedMs;
                        testMs += Math.Max(0, probeTimer.ElapsedMilliseconds - observedMs);
                    }
                }
                if (result.Outcome == MutationOutcome.Killed && !baseline && prioritize && mutant != null)
                    preferredTests.Learn(project, root, framework, selection.Filter, mutant, (result.Failures ?? []).Select(f => f.FullyQualifiedName));
                if (result.Outcome != MutationOutcome.Survived)
                    return result with { BuildMs = buildMs, TestMs = testMs };
            }
        }
        return new(MutationOutcome.Survived, buildMs, testMs, null);
    }
    private Task<ProcessResult> ObserveOutput(string project, string root, string? framework, CancellationToken token)
    {
        List<string> args = ["msbuild", project, "--nologo", "-getProperty:" + CompiledTestOutput.Query, .. BuildProperties];
        if (framework != null) args.Add("-p:TargetFramework=" + framework);
        return runner.RunAsync(new("dotnet", args, root, OutputLimit: 1024 * 1024), token);
    }
    private async Task<TestRunResult> RunTestScope(string project, string root, string? filter, string? framework,
        CancellationToken token, bool noBuild, string? classifyBuildFailureFor, bool baseline = false,
        CompiledTestOutput? output = null, bool captureIdentity = false, IReadOnlyDictionary<string, string?>? environment = null)
    {
        long buildMs = 0, testMs = 0;
        var directory = Path.Combine(resultsRoot ?? Path.GetTempPath(), "walker-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            // A mutant changes only production source: reuse restored packages and let one
            // `dotnet test` invocation refresh affected assemblies and run the tests.
            List<string> args = ["test", project, "--no-restore", "--nologo", "--verbosity", "quiet",
                "--logger", "trx", "--results-directory", directory, .. BuildProperties];
            if (output != null)
                args = ["test", output.Assembly, "--nologo", "--logger", "trx", "--results-directory", directory, "--framework", output.Moniker];
            else
            {
                if (noBuild) args.Add("--no-build");
                if (framework != null) args.AddRange(["--framework", framework]);
            }
            if (filter != null) args.AddRange(["--filter", filter]);
            var run = await runner.RunAsync(new("dotnet", args, root, Environment: environment), token);
            testMs += run.DurationMs;
            var reports = Directory.GetFiles(directory, "*.trx", SearchOption.AllDirectories);
            if (reports.Length == 0)
            {
                if (noBuild || run.ExitCode == 0) return new(MutationOutcome.TestError, buildMs, testMs, "Test runner produced no TRX results: " + Diagnostic(run));
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
            var allReportsExecuted = true;
            var identities = new List<string>();
            var identityComplete = true;
            foreach (var report in reports)
            {
                var counters = TrxReport.Read(report, captureIdentity || ((compiledTests || captureBaselineIdentities) && baseline));
                if (counters.Identity == null) identityComplete = false;
                else identities.Add(counters.Identity);
                if (counters.Error != null) return new(MutationOutcome.TestError, buildMs, testMs, counters.Error);
                total += counters.Executed; failed += counters.Failed;
                allReportsExecuted &= counters.Executed > 0;
                complete &= counters.FailureSelectionComplete;
                foreach (var failure in counters.Failures)
                {
                    if (failures.Count < 10) failures.Add(new(failure.Name, failure.FullyQualifiedName, project));
                    else complete = false;
                }
            }
            // A filter may legitimately have tests in only one TFM. Keep the original combined run
            // unless the baseline proves every framework actually executed tests.
            if (baseline && testFrameworks.TryGetValue(Path.GetFullPath(project, root), out var baselineFrameworks)
                && (reports.Length != baselineFrameworks.Length || !allReportsExecuted))
                testFrameworks.Remove(Path.GetFullPath(project, root));
            if (total == 0 && run.ExitCode == 0)
                return new(MutationOutcome.TestError, buildMs, testMs, "No executed tests: " + Diagnostic(run), NoTests: true);
            if (total == 0 || (run.ExitCode != 0 && failed == 0) || (run.ExitCode == 0 && failed > 0))
                return new(MutationOutcome.TestError, buildMs, testMs, "No executed tests or inconsistent runner results: " + Diagnostic(run));
            // A kill needs actual failed-test counters; build errors and empty scopes never count.
            if (failed > 0) return new(MutationOutcome.Killed, buildMs, testMs, Failures: failures,
                FailureSelectionComplete: complete, Filter: filter, Framework: framework);
            return new(MutationOutcome.Survived, buildMs, testMs, Identities: identityComplete ? identities : null);
        }
        finally
        {
            // A lingering test host can lock results (mainly on Windows); never let cleanup replace the outcome.
            try { Directory.Delete(directory, recursive: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }
    internal sealed record TestFailure(string Name, string? FullyQualifiedName, string Project);
    internal sealed record TestRunResult(MutationOutcome Outcome, long BuildMs, long TestMs, string? Detail = null,
        IReadOnlyList<TestFailure>? Failures = null, bool FailureSelectionComplete = false, string? Filter = null, string? Framework = null, bool NoTests = false,
        IReadOnlyList<string>? Identities = null);
    internal Task<TestRunResult> RunAssembly(CompiledTestOutput output, string project, string root, string? filter,
        string? framework, IReadOnlyDictionary<string, string?> environment, CancellationToken token, bool captureIdentity = false)
        => RunTestScope(project, root, filter, framework, token, noBuild: true, classifyBuildFailureFor: null,
            output: output, captureIdentity: captureIdentity, environment: environment);
    private static string Diagnostic(ProcessResult result)
    {
        var text = (result.StandardError + "\n" + result.StandardOutput).Trim();
        return text.Length > 2000 ? text[^2000..] : text;
    }
}
