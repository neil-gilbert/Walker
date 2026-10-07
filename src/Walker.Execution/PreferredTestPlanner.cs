using System.Text.RegularExpressions;
using Walker.Core;
namespace Walker.Execution;

// History is only a priority hint. An exact scope must pass on restored source before use;
// neither a hint nor a passing preferred attempt can establish survival.
internal sealed class PreferredTestPlanner
{
    private readonly Dictionary<(string Project, string? Framework, string? Filter, string File, string Member), string> members = new();
    private readonly Dictionary<(string Project, string? Framework, string? Filter), ProjectHistory> hints = new();
    private const int MaximumConsecutiveMisses = 2;
    private const long MinimumBaselineTestMs = 3000;
    private readonly Dictionary<PreferredScope, ScopeHistory> scopes = new();
    private readonly Dictionary<(string Project, string Root, string? Filter), long> baselineTimes = new();

    public void Reset() { members.Clear(); hints.Clear(); scopes.Clear(); baselineTimes.Clear(); }
    public void ObserveBaseline(string project, string root, string? filter, long elapsedMs) =>
        baselineTimes[(Normalize(project, root), Normalize(".", root), filter)] = elapsedMs;
    public PreferredScope? For(string project, string root, string? framework, string? filter, Mutant mutant)
    {
        var path = Normalize(project, root);
        var workingDirectory = Normalize(".", root);
        // Combined TRX filenames do not prove framework identity. Retain the project cost
        // rather than guessing framework costs or adding unrelated test projects.
        if (!baselineTimes.TryGetValue((path, workingDirectory, filter), out var elapsedMs) || elapsedMs < MinimumBaselineTestMs) return null;
        var file = Normalize(mutant.File, root);
        if (!hints.TryGetValue((path, framework, filter), out var projectHistory)) return null;
        var names = projectHistory.Methods;
        if (projectHistory.UseSingletons && members.TryGetValue((path, framework, filter, file, mutant.Member), out var name)) names = [name];
        var methodFilter = string.Join("|", names.Select(method => "FullyQualifiedName=" + method.Replace(",", "%2C")));
        var scope = new PreferredScope(path, workingDirectory, framework, filter, file, mutant.SourceHash,
            filter == null ? methodFilter : "(" + filter + ")&(" + methodFilter + ")");
        return scopes.TryGetValue(scope, out var history) && (history.Rejected || history.ConsecutiveMisses >= MaximumConsecutiveMisses) ? null : scope;
    }
    public bool IsValidated(PreferredScope scope) => scopes.TryGetValue(scope, out var history) && history.Validated;
    public void Validate(PreferredScope scope, bool passed, long elapsedMs)
    {
        var history = History(scope);
        history.Validated = passed;
        history.Rejected = !passed;
        history.ValidationAttempts++;
        history.ValidationMs += elapsedMs;
    }
    public void RecordAttempt(PreferredScope scope, MutationOutcome outcome, bool noTests, long elapsedMs)
    {
        var history = History(scope);
        history.Attempts++;
        history.AttemptMs += elapsedMs;
        if (outcome == MutationOutcome.Killed) { history.Kills++; history.ConsecutiveMisses = 0; }
        else if (outcome == MutationOutcome.Survived || noTests) { history.Misses++; history.ConsecutiveMisses++; }
    }
    public void Learn(string project, string root, string? framework, string? filter, Mutant mutant, IEnumerable<string?> failures)
    {
        var names = failures.Where(name => name != null && Regex.IsMatch(name, @"^[\p{L}\p{N}_.+`<>\[\],]+$"))
            .Select(name => name!).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        if (names.Length == 0) return;
        var path = Normalize(project, root);
        var key = (path, framework, filter);
        if (!hints.TryGetValue(key, out var projectHistory))
            hints.Add(key, projectHistory = new(names.Length == 1));
        if (projectHistory.UseSingletons)
        {
            // A unique first method provides a stable cold-start hint. Different members
            // can learn their own singleton without invalidating the existing scope.
            members.TryAdd((path, framework, filter, Normalize(mutant.File, root), mutant.Member), names[0]);
            if (projectHistory.Methods.Count == 0) projectHistory.Methods.Add(names[0]);
        }
        else
        {
            // Several distinct methods in the first kill are ambiguous evidence. Retain
            // the broader policy instead of choosing one arbitrary method that may miss.
            foreach (var method in names)
                if (projectHistory.Methods.Count < MaximumMethods && !projectHistory.Methods.Contains(method)) projectHistory.Methods.Add(method);
            projectHistory.Methods.Sort(StringComparer.Ordinal);
        }
    }
    private static string Normalize(string path, string root)
    {
        var full = Path.GetFullPath(path, root);
        return OperatingSystem.IsWindows() ? full.ToUpperInvariant() : full;
    }
    private ScopeHistory History(PreferredScope scope)
    {
        if (!scopes.TryGetValue(scope, out var history)) scopes.Add(scope, history = new());
        return history;
    }
    private sealed class ScopeHistory
    {
        public bool Validated, Rejected;
        public int Attempts, Kills, Misses, ConsecutiveMisses, ValidationAttempts;
        public long AttemptMs, ValidationMs;
    }
    private const int MaximumMethods = 20;
    private sealed class ProjectHistory(bool useSingletons)
    {
        public bool UseSingletons { get; } = useSingletons;
        public List<string> Methods { get; } = [];
    }
}

internal sealed record PreferredScope(string Project, string Root, string? Framework, string? OriginalFilter,
    string SourceFile, string SourceHash, string Filter);
