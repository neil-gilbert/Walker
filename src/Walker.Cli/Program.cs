using System.Text.Json;
using System.Text.Json.Serialization;
using Walker.Core;
using Walker.Execution;
using Walker.Git;
using Walker.Roslyn;

var jsonOptions = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true,
    Converters = { new JsonStringEnumConverter() } };
var format = "text";
// Determine requested error format even if other arguments are malformed.
for (var i = 0; i + 1 < args.Length; i++) if (args[i] == "--format") format = args[i + 1];
try
{
    if (args.Length == 0 || args.Contains("--help") || args.Contains("-h"))
    {
        Console.WriteLine("walker verify [--base HEAD~1] --project <csproj> --tests <csproj> [--tests <csproj> ...]\n" +
            "  [--max-mutants 20] [--timeout 60] [--format text|json] [--exclude <glob>] [--verbose]\n" +
            "Reads walker.json in the current directory; CLI options override configuration.\n" +
            "Exit codes: 0 passed, 1 survivors, 2 infrastructure error, 3 incomplete.\n" +
            "No eligible mutations yields incomplete. Surviving mutants require investigation, not automatic production changes.");
        return 0;
    }
    if (args[0] != "verify") throw new ArgumentException("Expected command: verify.");
    var cwd = Directory.GetCurrentDirectory();
    var config = File.Exists("walker.json") ? JsonSerializer.Deserialize<Configuration>(await File.ReadAllTextAsync("walker.json"),
        new JsonSerializerOptions { PropertyNameCaseInsensitive = true, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow }) ?? new() : new Configuration();
    var tests = new List<string>(); var exclude = new List<string>(); var verbose = false;
    format = config.Format ?? "text";
    for (var i = 1; i < args.Length; i++)
    {
        var option = args[i];
        if (option == "--verbose") { verbose = true; continue; }
        if (++i >= args.Length) throw new ArgumentException("Missing value for " + option);
        var value = args[i];
        switch (option)
        {
            case "--base": config.Base = value; break;
            case "--project": config.Project = value; break;
            case "--tests": tests.Add(value); break;
            case "--exclude": exclude.Add(value); break;
            case "--max-mutants": config.MaxMutants = int.Parse(value); break;
            case "--timeout": config.TimeoutSeconds = int.Parse(value); break;
            case "--format": format = value; break;
            default: throw new ArgumentException("Unknown option: " + option);
        }
    }
    if (format is not ("text" or "json")) throw new ArgumentException("Format must be text or json.");
    if (tests.Count == 0) tests.AddRange(config.Tests ?? []);
    if (exclude.Count == 0) exclude.AddRange(config.Exclude ?? []);
    if (config.Project == null || tests.Count == 0) throw new ArgumentException("Provide --project and at least one --tests (or walker.json).");
    if (config.MaxMutants <= 0 || config.TimeoutSeconds <= 0) throw new ArgumentException("--max-mutants and --timeout must be positive.");
    foreach (var path in tests.Prepend(config.Project))
        if (!File.Exists(Path.GetFullPath(path, cwd)) || !path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Project does not exist or is not a .csproj: " + path);
    var runner = new ProcessRunner();
    var rootResult = await runner.RunAsync(new("git", ["rev-parse", "--show-toplevel"], cwd), CancellationToken.None);
    if (rootResult.ExitCode != 0) throw new ArgumentException("Run inside a Git repository.");
    // Git prints forward slashes on Windows; normalise so path comparisons match .NET paths.
    var root = Path.GetFullPath(rootResult.StandardOutput.Trim());
    // Repair a source file left mutated by a previously killed run before discovery reads it.
    if (await MutationJournal.RecoverAsync(root, CancellationToken.None) is { } recovered) Console.Error.WriteLine(recovered);
    var request = new VerificationRequest(root, config.Base ?? "HEAD~1", Path.GetFullPath(config.Project, cwd),
        tests.Select(p => Path.GetFullPath(p, cwd)).ToArray(), config.MaxMutants, config.TimeoutSeconds, exclude);
    using var cancelled = new CancellationTokenSource();
    ConsoleCancelEventHandler handler = (_, e) => { e.Cancel = true; cancelled.Cancel(); };
    Console.CancelKeyPress += handler;
    VerificationResult result;
    try
    {
        var executor = new DotnetMutationExecutor(runner);
        var scope = new MsBuildSourceScope(runner);
        var discoverer = new RoslynMutationDiscoverer(async token => await scope.GetFilesAsync(request, token));
        result = await new VerificationEngine(new GitChangeProvider(runner, scope), discoverer, executor, executor)
            .VerifyAsync(request, cancelled.Token);
    }
    finally { Console.CancelKeyPress -= handler; }
    if (format == "json") Console.WriteLine(JsonSerializer.Serialize(result, jsonOptions));
    else Walker.Cli.TextReportWriter.Write(result, Console.Out, verbose);
    return result.ExitCode;
}
catch (Exception ex)
{
    var result = new VerificationResult("error", "unknown", 0, 0, 0, [], 0, new(), ex.Message);
    Console.WriteLine(format == "json" ? JsonSerializer.Serialize(result, jsonOptions) : "WALKER\nVERIFICATION ERROR\n" + ex.Message);
    return 2;
}
sealed class Configuration
{
    public string? Base { get; set; }
    public string? Project { get; set; }
    public string[]? Tests { get; set; }
    public string[]? Exclude { get; set; }
    public string? Format { get; set; }
    public int MaxMutants { get; set; } = 20;
    public int TimeoutSeconds { get; set; } = 60;
}
