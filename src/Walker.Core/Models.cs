using System.Security.Cryptography;
using System.Text;
namespace Walker.Core;
public enum MutationOperator { ConditionalBoundary, Equality, NullHandling, BooleanLogic, ReturnValue, Arithmetic, CustomFault }
// Hung: tests exceeded the per-mutant hang limit derived from the baseline; the change was detected.
public enum MutationOutcome { Killed, Survived, CompileError, TestError, TimedOut, Skipped, Hung }
public enum SurvivorClassification { Survived, IgnoredEquivalent, Accepted }
public record LineRange(int Start, int End) { public bool Intersects(int start, int end) => Start <= end && End >= start; }
public record SourceChange(string File, IReadOnlyList<LineRange> Lines, bool HasUncommittedChanges);
public record Mutant(string Id, string File, int Line, string Member, MutationOperator Operator,
    string Original, string Replacement, int SpanStart, int SpanLength, string SourceHash)
{
    public static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}
public record MutationResult(Mutant Mutant, MutationOutcome Outcome, long DurationMs = 0,
    long BuildMs = 0, long TestMs = 0, string? Detail = null, SurvivorClassification? Classification = null, IReadOnlyList<string>? FailingTests = null,
    bool? KillConfirmed = null, long ConfirmationMs = 0)
{
    public IReadOnlyList<VerificationDiagnostic> Diagnostics { get; init; } = [];
}
public record VerificationRequest(string Root, string Base, string Project, IReadOnlyList<string> Tests,
    int MaxMutants = 20, int TimeoutSeconds = 60, IReadOnlyList<string>? Exclude = null, string? Filter = null, bool ConfirmKills = false, int Workers = 1)
{
    public IReadOnlyList<string>? MutantIds { get; init; }
    public DateTimeOffset? DeadlineUtc { get; init; }
}
public record MutationSelection(string Kind, IReadOnlyList<string> RequestedIds);
public record VerificationScope(string Project, IReadOnlyList<string> Tests, string? Filter, int MaxMutants, int TimeoutSeconds);
public record IsolationSummary(string RunId, string SourceRoot, string? SourceHead, string? ResolvedBase, string? MergeBase,
    string? SnapshotFingerprint, VerificationScope Scope, long SetupMs, string ArtifactDirectory,
    string ReportPath, string LogPath, string WorktreePath, string CleanupState, IReadOnlyList<string> UntrackedProductionFiles);
public record TestSelection(IReadOnlyList<string> Projects, string? Filter = null);
public record VerificationContext(VerificationRequest Request, ITestSelector TestSelector);
public record DiscoveryResult(IReadOnlyList<Mutant> Mutants, long ParsingMs, long DiscoveryMs, int UnresolvedArithmetic = 0, int UnresolvedBoolean = 0);
public record FileVerificationSummary(string File, int ChangedLines, int MutantsDiscovered, int MutantsSelected);
public record PhaseTimings(long GitMs = 0, long ParsingMs = 0, long DiscoveryMs = 0, long BaselineMs = 0);
public record PreparationSummary(long DurationMs, int Supported, int Fallback, string? Detail = null);
public record VerificationResult(string Status, string Base, int ChangedFiles, int MutantsDiscovered,
    int MutantsSelected, IReadOnlyList<MutationResult> Results, long DurationMs, PhaseTimings Timings, string? Error = null, int UnresolvedArithmetic = 0, string? TestFilter = null, int UnresolvedBoolean = 0)
{
    public IReadOnlyList<VerificationDiagnostic> Diagnostics { get; init; } = [];
    public IReadOnlyList<FileVerificationSummary> Files { get; init; } = [];
    public MutationSelection Selection { get; init; } = new("bounded", []);
    public IsolationSummary? Isolation { get; init; }
    public bool ConfirmKills { get; init; }
    public InvestigationPacket? Investigation { get; init; }
    public IReadOnlyList<FaultChallenge>? Challenges { get; init; }
    public TestImprovementEvidence? TestImprovement { get; init; }
    public PreparationSummary? Preparation { get; init; }
    public int WorkersRequested { get; init; } = 1;
    public int WorkersUsed { get; init; } = 1;
    public int SchemaVersion => 1;
    public int MutantsExecuted => Results.Count(r => r.Outcome != MutationOutcome.Skipped);
    public int Killed => Results.Count(r => r.Outcome == MutationOutcome.Killed);
    public int Survived => Results.Count(r => r.Outcome == MutationOutcome.Survived);
    public int Hung => Results.Count(r => r.Outcome == MutationOutcome.Hung);
    public int CompileErrors => Results.Count(r => r.Outcome == MutationOutcome.CompileError);
    public int TestErrors => Results.Count(r => r.Outcome == MutationOutcome.TestError);
    public int TimedOut => Results.Count(r => r.Outcome == MutationOutcome.TimedOut);
    public int Skipped => Results.Count(r => r.Outcome == MutationOutcome.Skipped);
    public IEnumerable<Mutant> Survivors => Results.Where(r => r.Outcome == MutationOutcome.Survived).Select(r => r.Mutant);
    public int ExitCode => Status switch { "passed" => 0, "failed" => 1, "error" => 2, _ => 3 };
    public string Guidance => "A surviving mutant does not necessarily mean production code should change. Investigate missing tests, weak assertions, equivalent mutations, intentionally unspecified behaviour, and production defects before deciding what to change.";
}
public record ProcessRequest(string FileName, IReadOnlyList<string> Arguments, string WorkingDirectory, int OutputLimit = 16384,
    IReadOnlyDictionary<string, string?>? Environment = null);
public record ProcessResult(int ExitCode, string StandardOutput, string StandardError, long DurationMs, bool OutputTruncated = false);
public interface IProcessRunner { Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken); }
public interface IChangeProvider { Task<IReadOnlyList<SourceChange>> GetChangesAsync(VerificationRequest request, CancellationToken cancellationToken); }
public interface IMutationDiscoverer { Task<DiscoveryResult> DiscoverAsync(string root, IReadOnlyList<SourceChange> changes, CancellationToken cancellationToken); }
public interface ITestSelector { Task<TestSelection> SelectTestsAsync(Mutant mutant, CancellationToken cancellationToken); }
public interface IMutationExecutor
{
    Task<MutationResult> ExecuteAsync(Mutant mutant, VerificationContext context, CancellationToken cancellationToken);
}
public interface IBaselineVerifier { Task VerifyAsync(VerificationRequest request, CancellationToken cancellationToken); }
public interface IBatchMutationPreparer
{
    Task<IPreparedMutationSession?> PrepareAsync(VerificationRequest request, IReadOnlyList<Mutant> selected, CancellationToken token);
}
public interface IPreparedMutationSession : IMutationExecutor, IAsyncDisposable
{
    int WorkerCount => 1;
    IReadOnlySet<string> SupportedMutantIds { get; }
    string? Detail { get; }
}
public interface IMutationBatchExecutor
{
    Task<IReadOnlyList<MutationResult>> ExecuteBatchAsync(IReadOnlyList<Mutant> selected, VerificationContext context, CancellationToken token);
}
public sealed class AllTestsSelector(IReadOnlyList<string> projects, string? filter = null) : ITestSelector
{
    public Task<TestSelection> SelectTestsAsync(Mutant mutant, CancellationToken cancellationToken) => Task.FromResult(new TestSelection(projects, filter));
}

public interface IProductionSourceScope { Task<IReadOnlySet<string>> GetFilesAsync(VerificationRequest request, CancellationToken cancellationToken); }
