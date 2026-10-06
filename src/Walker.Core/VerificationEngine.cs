using System.Diagnostics;
namespace Walker.Core;
public sealed class VerificationEngine(IChangeProvider changes, IMutationDiscoverer discovery,
    IMutationExecutor executor, IBaselineVerifier baseline)
{
    public async Task<VerificationResult> VerifyAsync(VerificationRequest request, CancellationToken cancellationToken = default)
    {
        var clock = Stopwatch.StartNew();
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(TimeSpan.FromSeconds(request.TimeoutSeconds));
        var token = budget.Token;
        IReadOnlyList<SourceChange> files = [];
        IReadOnlyList<Mutant> mutants = [];
        Mutant[] selected = [];
        var results = new List<MutationResult>();
        var timings = new PhaseTimings();
        string? error = null;
        var infrastructureError = false;
        var baselineStarted = false;
        var baselineCompleted = false;
        var unresolved = 0;
        try
        {
            var phase = Stopwatch.StartNew();
            files = await changes.GetChangesAsync(request, token);
            timings = timings with { GitMs = phase.ElapsedMilliseconds };
            var found = await discovery.DiscoverAsync(request.Root, files, token);
            mutants = found.Mutants;
            unresolved = found.UnresolvedArithmetic;
            timings = timings with { ParsingMs = found.ParsingMs, DiscoveryMs = found.DiscoveryMs };
            selected = Select(mutants, request.MaxMutants);
            if (selected.Length > 0)
            {
                phase.Restart();
                baselineStarted = true;
                try
                {
                    await baseline.VerifyAsync(request, token);
                    baselineCompleted = true;
                }
                finally { timings = timings with { BaselineMs = phase.ElapsedMilliseconds }; }
                var context = new VerificationContext(request, new AllTestsSelector(request.Tests, request.Filter));
                foreach (var mutant in selected)
                {
                    token.ThrowIfCancellationRequested();
                    var result = await executor.ExecuteAsync(mutant, context, token);
                    results.Add(result);
                    // TimedOut means the global budget expired; a per-mutant Hung result does not stop the run.
                    if (result.Outcome == MutationOutcome.TimedOut) break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            if (baselineStarted && !baselineCompleted)
                error = cancellationToken.IsCancellationRequested
                    ? "Baseline cancelled before any mutant could start; no mutation evidence was collected."
                    : "Verification budget expired during the baseline before any mutant could start. The budget is smaller than one baseline run; use --filter to narrow the test scope or increase --timeout.";
        }
        catch (Exception ex) { infrastructureError = true; error = ex.Message; }
        var completed = results.Select(r => r.Mutant.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var mutant in selected.Where(m => !completed.Contains(m.Id)))
            results.Add(new(mutant, MutationOutcome.Skipped, Detail: baselineStarted && !baselineCompleted
                ? "Skipped because the baseline did not complete; no mutant could start. " + error
                : error ?? "Verification budget exhausted or cancelled."));
        var incomplete = token.IsCancellationRequested || results.Any(r => r.Outcome is MutationOutcome.TimedOut or MutationOutcome.Skipped);
        var status = infrastructureError || results.Any(r => r.Outcome is MutationOutcome.CompileError or MutationOutcome.TestError) ? "error"
            : incomplete ? "incomplete" : results.Any(r => r.Outcome == MutationOutcome.Survived) ? "failed"
            : selected.Length == 0 ? "incomplete" : "passed";
        if (status == "incomplete" && token.IsCancellationRequested)
            error ??= "Verification budget exhausted or cancelled; available results are incomplete.";
        if (selected.Length == 0 && error == null && !token.IsCancellationRequested)
            error = "No eligible changed expressions; verification provides no mutation evidence.";
        return new(status, request.Base, files.Count, mutants.Count, selected.Length, results, clock.ElapsedMilliseconds, timings, error, unresolved, request.Filter);
    }
    // Priority order, then round-robin across (file, operator) groups so one dense file or
    // operator cannot consume the whole budget.
    public static Mutant[] Select(IEnumerable<Mutant> mutants, int maximum)
    {
        var groups = mutants
            .OrderBy(m => m.Operator).ThenBy(m => m.File, StringComparer.Ordinal).ThenBy(m => m.Line)
            .ThenBy(m => m.SpanStart).ThenBy(m => m.Id, StringComparer.Ordinal)
            .GroupBy(m => (m.File, m.Operator)).Select(g => new Queue<Mutant>(g)).ToList();
        var selected = new List<Mutant>();
        while (selected.Count < maximum && groups.Count > 0)
        {
            foreach (var group in groups)
            {
                if (selected.Count == maximum) break;
                selected.Add(group.Dequeue());
            }
            groups.RemoveAll(g => g.Count == 0);
        }
        return selected.ToArray();
    }
}
