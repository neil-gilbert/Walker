using System.Text.Json;
using System.Text.Json.Serialization;
using Walker.Core;
using Walker.Execution;
using Walker.Git;
using Walker.Roslyn;

var jsonOptions = VerificationReportJson.Options;
var format = "text";
// Determine requested error format even if other arguments are malformed.
string? explicitFormat = null;
for (var i = 0; i + 1 < args.Length; i++) if (args[i] == "--format") explicitFormat = args[++i];
format = explicitFormat ?? "text";
var phaseName = "arguments";
using var cancelled = new CancellationTokenSource();
var effectiveBase = "unknown";
try
{
    if (args.Length == 0 || args.Contains("--help") || args.Contains("-h"))
    {
        Console.WriteLine("walker verify [--base HEAD~1] --project <csproj> --tests <csproj> [--tests <csproj> ...]\n" +
            "  [--max-mutants 20] [--timeout 60] [--format text|json] [--exclude <glob>] [--filter <expression>] [--mutant <id> ...] [--isolate] [--confirm-kills] [--compiled-tests] [--mutant-mode source|switch] [--workers 1|2] [--verbose]\n" +
            "  [--progress] [--investigate] [--challenge <manifest.json>] [--test-patch <manifest.json>]\n" +
            "  --progress writes phases, elapsed/remaining time and a heartbeat every 15 seconds to stderr; JSON stdout stays clean. Off by default.\n" +
            "  --investigate adds grouped survivor hints; the agent reviews intended behaviour. Off by default.\n" +
            "  --challenge runs explicit contract-described faults, including unchanged production source. Requires --isolate and source mode.\n" +
            "  --test-patch compares original tests with proposed test files in isolation; confirms test failures without applying the patch to your checkout. Requires --isolate and source mode.\n" +
            "  --filter applies the same dotnet test filter to the baseline and all mutants; zero tests is an error.\n" +
            "  --mutant selects current eligible IDs explicitly; repeat for multiple IDs. A focused pass covers only those mutants.\n" +
            "  --isolate captures working contents in a disposable worktree; reports/logs persist outside it. Unsupported layouts fail without source fallback.\n" +
            "  --confirm-kills reruns failing test methods on restored source; adds builds/test runs and uses the same budget.\n" +
            "  --compiled-tests probes assembly compatibility, then uses verified DLLs for full retries after preferred tests; adds baseline work.\n" +
            "  --mutant-mode source|switch selects ordinary source mutation or experimental compile-once numeric boundaries (default source).\n" +
            "  --workers 1|2 opts into isolated prepared workers in switch mode (default 1); tests must support concurrent external resources.\n" +
            "Reads walker.json in the current directory; CLI options override configuration.\n" +
            "Exit codes: 0 passed, 1 survivors, 2 infrastructure error, 3 incomplete.\n" +
            "No eligible mutations yields incomplete. Surviving mutants require investigation, not automatic production changes.");
        return 0;
    }
    if (args[0] != "verify") throw new ArgumentException("Expected command: verify.");
    var cwd = Directory.GetCurrentDirectory();
    phaseName = "configuration";
    var config = File.Exists("walker.json") ? JsonSerializer.Deserialize<Configuration>(await File.ReadAllTextAsync("walker.json"),
        new JsonSerializerOptions { PropertyNameCaseInsensitive = true, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            AllowDuplicateProperties = false }) ?? new() : new Configuration();
    var tests = new List<string>(); var exclude = new List<string>(); var mutantIds = new List<string>(); var verbose = false;
    var investigate = false; string? challengePath = null, testPatchPath = null;
    format = explicitFormat ?? config.Format ?? "text";
    phaseName = "arguments";
    for (var i = 1; i < args.Length; i++)
    {
        var option = args[i];
        if (option == "--confirm-kills") { config.ConfirmKills = true; continue; }
        if (option == "--compiled-tests") { config.CompiledTests = true; continue; }
        if (option == "--verbose") { verbose = true; continue; }
        if (option == "--isolate") { config.Isolate = true; continue; }
        if (option == "--investigate") { investigate = true; continue; }
        if (option == "--progress") { config.Progress = true; continue; }
        if (++i >= args.Length) throw new ArgumentException("Missing value for " + option);
        var value = args[i];
        switch (option)
        {
            case "--filter": config.Filter = value; break;
            case "--mutant": mutantIds.Add(value); break;
            case "--challenge": challengePath = value; break;
            case "--test-patch": testPatchPath = value; break;
            case "--mutant-mode": config.MutantMode = value; break;
            case "--workers": config.Workers = int.Parse(value); break;
            case "--base": config.Base = value; break;
            case "--project": config.Project = value; break;
            case "--tests": tests.Add(value); break;
            case "--exclude": exclude.Add(value); break;
            case "--max-mutants": config.MaxMutants = int.Parse(value); break;
            case "--timeout": config.TimeoutSeconds = int.Parse(value); break;
            case "--format": break; // Resolved before fallible parsing, including duplicate-option precedence.
            default: throw new ArgumentException("Unknown option: " + option);
        }
    }
    if (format is not ("text" or "json")) throw new ArgumentException("Format must be text or json.");
    if (config.MutantMode is not ("source" or "switch")) throw new ArgumentException("Mutant mode must be source or switch.");
    if (config.MutantMode == "switch" && config.CompiledTests) throw new ArgumentException("Switch mode already verifies compiled outputs; omit --compiled-tests.");
    if (config.Workers is not (1 or 2)) throw new ArgumentException("Workers must be 1 or 2.");
    if (config.Workers > 1 && config.MutantMode != "switch") throw new ArgumentException("Multiple workers require --mutant-mode switch.");
    if (tests.Count == 0) tests.AddRange(config.Tests ?? []);
    if (exclude.Count == 0) exclude.AddRange(config.Exclude ?? []);
    if (config.Project == null || tests.Count == 0) throw new ArgumentException("Provide --project and at least one --tests (or walker.json).");
    if (config.MaxMutants <= 0 || config.TimeoutSeconds <= 0) throw new ArgumentException("--max-mutants and --timeout must be positive.");
    if ((challengePath != null || testPatchPath != null) && (!config.Isolate || config.MutantMode != "source" || config.CompiledTests))
        throw new ArgumentException("--challenge and --test-patch require --isolate, source mode, and no --compiled-tests.");
    if (challengePath != null && (mutantIds.Count > 0 || exclude.Count > 0))
        throw new ArgumentException("Explicit challenges execute the complete manifest; omit --mutant and --exclude.");
    var challenges = challengePath == null ? null : await AgentWorkflows.ReadManifestAsync<ChallengeManifest>(Path.GetFullPath(challengePath, cwd));
    var testPatch = testPatchPath == null ? null : await AgentWorkflows.ReadManifestAsync<TestPatchManifest>(Path.GetFullPath(testPatchPath, cwd));
    if (challenges != null) AgentWorkflows.Validate(challenges, config.MaxMutants);
    if (testPatch != null) AgentWorkflows.Validate(testPatch);
    foreach (var path in tests.Prepend(config.Project))
        if (!File.Exists(Path.GetFullPath(path, cwd)) || !path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Project does not exist or is not a .csproj: " + path);
    using var progress = config.Progress ? new Walker.Cli.ProgressReporter(Console.Error, TimeSpan.FromSeconds(config.TimeoutSeconds)) : null;
    IProcessRunner runner = progress == null ? new ProcessRunner() : new Walker.Cli.ProgressProcessRunner(new ProcessRunner(), progress);
    ConsoleCancelEventHandler handler = (_, e) => { e.Cancel = true; cancelled.Cancel(); };
    Console.CancelKeyPress += handler;
    VerificationResult result;
    try
    {
        effectiveBase = config.Base ?? "HEAD~1";
        var deadline = DateTimeOffset.UtcNow.AddSeconds(config.TimeoutSeconds);
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancelled.Token);
        budget.CancelAfter(TimeSpan.FromSeconds(config.TimeoutSeconds));
        phaseName = "discovery";
        var rootResult = await runner.RunAsync(new("git", ["rev-parse", "--show-toplevel"], cwd,
            Environment: new Dictionary<string, string?> { ["GIT_OPTIONAL_LOCKS"] = "0" }), budget.Token);
        if (rootResult.ExitCode != 0) throw new VerificationException("git_discovery_failed", "discovery", "Run inside a Git repository.");
        var root = Path.GetFullPath(rootResult.StandardOutput.Trim());
        var request = new VerificationRequest(root, effectiveBase, Path.GetFullPath(config.Project, cwd),
            tests.Select(p => Path.GetFullPath(p, cwd)).ToArray(), config.MaxMutants, config.TimeoutSeconds, exclude, config.Filter, config.ConfirmKills, config.Workers)
            { MutantIds = mutantIds, DeadlineUtc = deadline };
        Task<VerificationResult> RunVerification(VerificationRequest current, IProcessRunner executionRunner, CancellationToken token)
        {
            IMutationExecutor executor = config.MutantMode == "switch"
                ? new SwitchingMutationExecutor(executionRunner, verbose ? message => Console.Error.WriteLine(message) : null)
                : new DotnetMutationExecutor(executionRunner, compiledTests: config.CompiledTests);
            var scope = new MsBuildSourceScope(executionRunner);
            if (challenges != null)
            {
                var faults = new ChallengeDiscovery(challenges, scope);
                return new VerificationEngine(faults, faults, executor, (IBaselineVerifier)executor, progress == null ? null : progress.Update).VerifyAsync(current, token);
            }
            var discoverer = new RoslynMutationDiscoverer(async scopeToken => await scope.GetFilesAsync(current, scopeToken));
            return new VerificationEngine(new GitChangeProvider(executionRunner, scope), discoverer, executor, (IBaselineVerifier)executor, progress == null ? null : progress.Update)
                .VerifyAsync(current, token);
        }
        async Task<VerificationResult> Verify(VerificationRequest current, IProcessRunner executionRunner, CancellationToken token)
        {
            if (testPatch != null) progress?.Update(new("test-improvement", Detail: "Comparing original tests with proposed test patch inside one snapshot"));
            var report = testPatch == null ? await RunVerification(current, executionRunner, token)
                : await AgentWorkflows.VerifyTestPatchAsync(current, testPatch, executionRunner,
                    (pairedRequest, pairedToken) => RunVerification(pairedRequest, executionRunner, pairedToken), token);
            if (challenges != null) report = report with { Challenges = challenges.Challenges };
            return investigate ? AgentWorkflows.Investigate(report) : report;
        }
        if (config.Isolate)
        {
            phaseName = "isolation";
            progress?.Update(new("isolation", Detail: "Capturing an isolated source snapshot"));
            result = await new IsolatedVerificationSession(runner, message => Console.Error.WriteLine(message), progress == null ? null : progress.Update).VerifyAsync(request, cwd, Verify, cancelled.Token);
        }
        else
        {
            phaseName = "recovery";
            if (await MutationJournal.RecoverAsync(root, budget.Token) is { } recovered) Console.Error.WriteLine(recovered);
            result = await Verify(request, runner, cancelled.Token);
        }
    }
    finally { Console.CancelKeyPress -= handler; }
    progress?.Update(new("finished", result.MutantsExecuted, result.MutantsSelected, $"{result.Status}; cleanup finished"));
    progress?.Dispose();
    if (format == "json") Console.WriteLine(JsonSerializer.Serialize(result, jsonOptions));
    else Walker.Cli.TextReportWriter.Write(result, Console.Out, verbose);
    return result.ExitCode;
}
catch (Exception ex)
{
    var diagnostic = ex is OperationCanceledException
        ? VerificationDiagnostic.Create(cancelled.IsCancellationRequested ? "cancelled" : "budget_exhausted", phaseName, "Verification cancelled or exhausted its global budget.")
        : phaseName == "configuration"
        ? VerificationDiagnostic.Create("invalid_configuration", phaseName, ex.Message)
        : phaseName == "arguments" && ex is ArgumentException or FormatException or OverflowException or JsonException or IOException
            ? VerificationDiagnostic.Create("invalid_argument", phaseName, ex.Message)
            : VerificationDiagnostic.FromException(ex, phaseName);
    var result = new VerificationResult(ex is OperationCanceledException ? "incomplete" : "error", effectiveBase, 0, 0, 0, [], 0, new(), ex.Message) { Diagnostics = [diagnostic] };
    if (format == "json") Console.WriteLine(JsonSerializer.Serialize(result, jsonOptions));
    else Walker.Cli.TextReportWriter.Write(result, Console.Out);
    return result.ExitCode;
}
sealed class Configuration
{
    public string? Base { get; set; }
    public string? Project { get; set; }
    public string[]? Tests { get; set; }
    public string[]? Exclude { get; set; }
    public string? Format { get; set; }
    public string? Filter { get; set; }
    public bool ConfirmKills { get; set; }
    public bool CompiledTests { get; set; }
    public bool Isolate { get; set; }
    public bool Progress { get; set; }
    public string MutantMode { get; set; } = "source";
    public int Workers { get; set; } = 1;
    public int MaxMutants { get; set; } = 20;
    public int TimeoutSeconds { get; set; } = 60;
}
