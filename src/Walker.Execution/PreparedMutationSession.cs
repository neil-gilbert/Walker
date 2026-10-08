using System.Diagnostics;
using Walker.Core;

namespace Walker.Execution;

internal sealed class PreparedMutationSession(DotnetMutationExecutor source, MutationWorkspace workspace, VerificationRequest request,
    IReadOnlyDictionary<string, int> activeIds, string environmentName, IReadOnlyList<PreparedMutationSession.Scope> scopes,
    bool ownsWorkspace = true) : IPreparedMutationSession, IMutationBatchExecutor
{
    internal sealed record Scope(string Project, string Framework, CompiledTestOutput Output, CompiledTestOutput? OrdinaryOutput = null);
    public IReadOnlySet<string> SupportedMutantIds { get; } = activeIds.Keys.ToHashSet(StringComparer.Ordinal);
    internal IReadOnlyList<MutationCoverage>? Coverage { get; set; }
    internal IReadOnlySet<int> BatchableIds { get; set; } = new HashSet<int>();
    public string? Detail => "Prepared built-in numeric boundaries; unsupported mutants retain source execution. Fresh host per attempt." + ParallelFallbackDetail
        + (Coverage == null ? "" : " Complete per-test coverage; disjoint pure boundaries share fresh-process coverage batches.");
    internal string? ParallelFallbackDetail { get; set; }
    public async Task<IReadOnlyList<MutationResult>> ExecuteBatchAsync(IReadOnlyList<Mutant> selected, VerificationContext context, CancellationToken token)
    {
        var compatible = context.Request == request && !request.ConfirmKills;
        foreach (var mutant in selected)
        {
            var selection = await context.TestSelector.SelectTestsAsync(mutant, token);
            compatible &= selection.Filter == request.Filter && selection.Projects.SequenceEqual(request.Tests);
        }
        var groups = MutationCoverage.Pack(selected, activeIds, BatchableIds, compatible ? Coverage : null);
        var results = new Dictionary<string, MutationResult>(StringComparer.Ordinal);
        foreach (var group in groups)
        {
            if (token.IsCancellationRequested) break;
            var batched = group.Length > 1 ? await ExecuteGroup(group, token) : null;
            if (batched != null) foreach (var result in batched) results.Add(result.Mutant.Id, result);
            else foreach (var mutant in group)
            {
                if (token.IsCancellationRequested) break;
                var result = await ExecuteAsync(mutant, context, token);
                results.Add(mutant.Id, result);
                if (result.Outcome == MutationOutcome.TimedOut) break;
            }
        }
        return selected.Where(m => results.ContainsKey(m.Id)).Select(m => results[m.Id]).ToArray();
    }
    private async Task<IReadOnlyList<MutationResult>?> ExecuteGroup(IReadOnlyList<Mutant> group, CancellationToken token)
    {
        if (Coverage == null || !await workspace.Unchanged(token)) return null;
        var timer = Stopwatch.StartNew();
        var active = group.Select(m => activeIds[m.Id]).ToHashSet();
        var failures = active.ToDictionary(id => id, _ => new List<string>());
        long testMs = 0;
        using var hang = CancellationTokenSource.CreateLinkedTokenSource(token);
        hang.CancelAfter(source.PreparedHangLimit);
        try
        {
            for (var index = 0; index < scopes.Count; index++)
            {
                var scope = scopes[index];
                using var capture = new CoverageCapture(environmentName, string.Join(",", active.Order()));
                var run = await source.RunAssembly(scope.Output, scope.Project, request.Root, request.Filter, scope.Framework,
                    capture.Environment, hang.Token, captureIdentity: true, settings: capture.Settings);
                testMs += run.TestMs;
                var observed = MutationCoverage.Read(capture.Report, run.Cases);
                if (run.Outcome is not (MutationOutcome.Killed or MutationOutcome.Survived) || observed == null) return null;
                var baseline = Coverage[index].Cases.ToDictionary(c => c.Id, StringComparer.Ordinal);
                if (observed.Cases.Count != baseline.Count) return null;
                foreach (var test in observed.Cases)
                {
                    if (!baseline.TryGetValue(test.Id, out var expected) || test.Name != expected.Name || test.Method != expected.Method
                        || !test.Hits.IsSubsetOf(expected.Hits)) return null;
                    var owners = expected.Hits.Where(active.Contains).ToArray();
                    if (owners.Length > 1) return null;
                    if (test.Outcome == "Failed")
                    {
                        if (owners.Length != 1 || !test.Hits.Contains(owners[0])) return null;
                        failures[owners[0]].Add(test.Name);
                    }
                    else if (owners.Any(id => !test.Hits.Contains(id))) return null;
                }
            }
            return group.Select(m => new MutationResult(m, failures[activeIds[m.Id]].Count > 0 ? MutationOutcome.Killed : MutationOutcome.Survived,
                timer.ElapsedMilliseconds, TestMs: testMs, Detail: $"Executed in a disjoint coverage batch of {group.Count} mutations; duration and test time are shared by the batch.",
                Classification: failures[activeIds[m.Id]].Count == 0 ? SurvivorClassification.Survived : null,
                FailingTests: failures[activeIds[m.Id]].Count > 0 ? failures[activeIds[m.Id]].Take(10).ToArray() : null)).ToArray();
        }
        // A hung/aborted batch cannot attribute any member, including a hang.
        // Drain it and retry each member through the existing single-mutant path.
        catch (OperationCanceledException) { return null; }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or System.Text.Json.JsonException) { return null; }
    }
    public async Task<MutationResult> ExecuteAsync(Mutant mutant, VerificationContext context, CancellationToken token)
    {
        if (!activeIds.TryGetValue(mutant.Id, out var active) || context.Request != request) return await source.ExecuteAsync(mutant, context, token);
        var timer = Stopwatch.StartNew();
        long testMs = 0;
        try
        {
            if (!await workspace.Unchanged(token)) return new(mutant, MutationOutcome.TestError, timer.ElapsedMilliseconds, Detail: "Build inputs changed after preparation; refusing stale prepared execution.")
            { Diagnostics = [VerificationDiagnostic.Create("source_changed", "execution", "Build inputs changed after preparation; refusing stale prepared execution.", "rediscover_mutants")] };
            var selection = await context.TestSelector.SelectTestsAsync(mutant, token);
            if (selection.Filter != request.Filter || !selection.Projects.SequenceEqual(request.Tests)) return await source.ExecuteAsync(mutant, context, token);
            using var hang = CancellationTokenSource.CreateLinkedTokenSource(token);
            hang.CancelAfter(source.PreparedHangLimit);
            foreach (var scope in scopes)
            {
                DotnetMutationExecutor.TestRunResult run;
                try
                {
                    run = await source.RunAssembly(scope.Output, scope.Project, request.Root, request.Filter, scope.Framework,
                        new Dictionary<string, string?> { [environmentName] = active.ToString(System.Globalization.CultureInfo.InvariantCulture) }, hang.Token);
                }
                catch (OperationCanceledException) when (!token.IsCancellationRequested)
                { return new(mutant, MutationOutcome.Hung, timer.ElapsedMilliseconds, TestMs: timer.ElapsedMilliseconds, Detail: "Prepared tests exceeded the per-mutant hang limit.")
                    { Diagnostics = [VerificationDiagnostic.Create("mutant_hung", "execution", "Prepared tests exceeded the per-mutant hang limit; counted as detected.")] }; }
                testMs += run.TestMs;
                if (run.Outcome == MutationOutcome.Survived) continue;
                var result = new MutationResult(mutant, run.Outcome, timer.ElapsedMilliseconds, TestMs: testMs, Detail: run.Detail,
                    FailingTests: run.Outcome == MutationOutcome.Killed ? (run.Failures ?? []).Select(f => f.Name).ToArray() : null)
                { Diagnostics = DotnetMutationExecutor.TestDiagnostics(run) };
                return await source.ConfirmResultAsync(result, context, run, token, scope.OrdinaryOutput);
            }
            return new(mutant, MutationOutcome.Survived, timer.ElapsedMilliseconds, TestMs: testMs, Classification: SurvivorClassification.Survived);
        }
        catch (OperationCanceledException) { return new(mutant, MutationOutcome.TimedOut, timer.ElapsedMilliseconds, TestMs: testMs, Detail: "Prepared execution cancelled; original source was untouched.")
            { Diagnostics = [VerificationDiagnostic.Create("cancelled", "execution", "Prepared execution cancelled; original source was untouched.")] }; }
        catch (Exception ex) { return new(mutant, MutationOutcome.TestError, timer.ElapsedMilliseconds, TestMs: testMs, Detail: ex.Message)
            { Diagnostics = [VerificationDiagnostic.FromException(ex, "execution")] }; }
    }
    public ValueTask DisposeAsync() => ownsWorkspace ? workspace.DisposeAsync() : ValueTask.CompletedTask;
}
