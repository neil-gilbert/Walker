using System.Diagnostics;
using Walker.Core;

namespace Walker.Execution;

internal sealed class PreparedMutationSession(DotnetMutationExecutor source, MutationWorkspace workspace, VerificationRequest request,
    IReadOnlyDictionary<string, int> activeIds, string environmentName, IReadOnlyList<PreparedMutationSession.Scope> scopes,
    bool ownsWorkspace = true) : IPreparedMutationSession
{
    internal sealed record Scope(string Project, string Framework, CompiledTestOutput Output, CompiledTestOutput? OrdinaryOutput = null);
    public IReadOnlySet<string> SupportedMutantIds { get; } = activeIds.Keys.ToHashSet(StringComparer.Ordinal);
    public string? Detail => "Prepared built-in numeric boundaries; unsupported mutants retain source execution. Fresh host per attempt." + ParallelFallbackDetail;
    internal string? ParallelFallbackDetail { get; set; }
    public async Task<MutationResult> ExecuteAsync(Mutant mutant, VerificationContext context, CancellationToken token)
    {
        if (!activeIds.TryGetValue(mutant.Id, out var active) || context.Request != request) return await source.ExecuteAsync(mutant, context, token);
        var timer = Stopwatch.StartNew();
        long testMs = 0;
        try
        {
            if (!await workspace.Unchanged(token)) return new(mutant, MutationOutcome.TestError, timer.ElapsedMilliseconds, Detail: "Build inputs changed after preparation; refusing stale prepared execution.");
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
                { return new(mutant, MutationOutcome.Hung, timer.ElapsedMilliseconds, TestMs: timer.ElapsedMilliseconds, Detail: "Prepared tests exceeded the per-mutant hang limit."); }
                testMs += run.TestMs;
                if (run.Outcome == MutationOutcome.Survived) continue;
                var result = new MutationResult(mutant, run.Outcome, timer.ElapsedMilliseconds, TestMs: testMs, Detail: run.Detail,
                    FailingTests: run.Outcome == MutationOutcome.Killed ? (run.Failures ?? []).Select(f => f.Name).ToArray() : null);
                return await source.ConfirmResultAsync(result, context, run, token, scope.OrdinaryOutput);
            }
            return new(mutant, MutationOutcome.Survived, timer.ElapsedMilliseconds, TestMs: testMs, Classification: SurvivorClassification.Survived);
        }
        catch (OperationCanceledException) { return new(mutant, MutationOutcome.TimedOut, timer.ElapsedMilliseconds, TestMs: testMs, Detail: "Prepared execution cancelled; original source was untouched."); }
        catch (Exception ex) { return new(mutant, MutationOutcome.TestError, timer.ElapsedMilliseconds, TestMs: testMs, Detail: ex.Message); }
    }
    public ValueTask DisposeAsync() => ownsWorkspace ? workspace.DisposeAsync() : ValueTask.CompletedTask;
}
