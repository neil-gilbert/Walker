using System.Diagnostics;
namespace Walker.Core;
public sealed class VerificationEngine(IChangeProvider changes, IMutationDiscoverer discovery,
    IMutationExecutor executor, IBaselineVerifier baseline, Action<VerificationProgress>? progress = null)
{
    public async Task<VerificationResult> VerifyAsync(VerificationRequest request, CancellationToken cancellationToken = default)
    {
        var clock = Stopwatch.StartNew();
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(request.DeadlineUtc is { } deadline
            ? TimeSpan.FromTicks(Math.Max(0, (deadline - DateTimeOffset.UtcNow).Ticks))
            : TimeSpan.FromSeconds(request.TimeoutSeconds));
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
        var unresolvedBoolean = 0;
        PreparationSummary? preparation = null;
        var workersUsed = 1;
        var requestedIds = (request.MutantIds ?? []).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var diagnostics = new List<VerificationDiagnostic>();
        var currentPhase = "discovery";
        try
        {
            progress?.Invoke(new("discovery", Detail: "Finding eligible production changes"));
            var phase = Stopwatch.StartNew();
            files = await changes.GetChangesAsync(request, token);
            timings = timings with { GitMs = phase.ElapsedMilliseconds };
            var found = await discovery.DiscoverAsync(request.Root, files, token);
            mutants = found.Mutants;
            unresolved = found.UnresolvedArithmetic;
            unresolvedBoolean = found.UnresolvedBoolean;
            timings = timings with { ParsingMs = found.ParsingMs, DiscoveryMs = found.DiscoveryMs };
            if (requestedIds.Length > 0)
            {
                var availableIds = mutants.Select(m => m.Id).ToHashSet(StringComparer.Ordinal);
                var unknown = requestedIds.Where(id => !availableIds.Contains(id)).ToArray();
                if (unknown.Length > 0)
                    throw new VerificationException("unknown_mutant", "selection", "Requested mutants are not eligible in the current diff: " + string.Join(", ", unknown),
                        actions: ["rediscover_mutants"]);
                if (requestedIds.Length > request.MaxMutants)
                    throw new VerificationException("mutant_limit_exceeded", "selection", "Requested unique mutant IDs exceed --max-mutants; no requested mutant was executed.");
                var wanted = requestedIds.ToHashSet(StringComparer.Ordinal);
                selected = Select(mutants.Where(m => wanted.Contains(m.Id)), request.MaxMutants);
            }
            else selected = Select(mutants, request.MaxMutants);
            progress?.Invoke(new("selection", Total: selected.Length, Detail: $"Selected {selected.Length} of {mutants.Count} candidates"));
            if (selected.Length > 0)
            {
                phase.Restart();
                baselineStarted = true;
                currentPhase = "baseline";
                progress?.Invoke(new("baseline", Total: selected.Length, Detail: "Building and testing ordinary code"));
                try
                {
                    await baseline.VerifyAsync(request, token);
                    baselineCompleted = true;
                }
                finally { timings = timings with { BaselineMs = phase.ElapsedMilliseconds }; }
                var context = new VerificationContext(request, new AllTestsSelector(request.Tests, request.Filter));
                currentPhase = "execution";
                IPreparedMutationSession? session = null;
                try
                {
                    if (executor is IBatchMutationPreparer preparer)
                    {
                        progress?.Invoke(new("preparation", Total: selected.Length, Detail: "Preparing isolated mutation execution"));
                        phase.Restart();
                        try { session = await preparer.PrepareAsync(request, selected, token); }
                        finally
                        {
                            var supported = session?.SupportedMutantIds.Count ?? 0;
                            preparation = new(phase.ElapsedMilliseconds, supported, selected.Length - supported,
                                session?.Detail ?? "Prepared execution unavailable; ordinary source execution retained.");
                        }
                    }
                    workersUsed = session?.WorkerCount ?? 1;
                    progress?.Invoke(new("execution", Total: selected.Length, Detail: "Testing selected faults"));
                    if (session is IMutationBatchExecutor batch)
                    {
                        results.AddRange(await batch.ExecuteBatchAsync(selected, context, token));
                        progress?.Invoke(new("execution", results.Count, selected.Length, "Batch completed"));
                    }
                    else foreach (var mutant in selected)
                    {
                        token.ThrowIfCancellationRequested();
                        progress?.Invoke(new("execution", results.Count, selected.Length, $"Testing {mutant.Id} at {mutant.File}:{mutant.Line}"));
                        var result = await (session ?? executor).ExecuteAsync(mutant, context, token);
                        results.Add(result);
                        progress?.Invoke(new("execution", results.Count, selected.Length, $"{mutant.Id}: {result.Outcome}"));
                        // TimedOut means the global budget expired; a per-mutant Hung result does not stop the run.
                        if (result.Outcome == MutationOutcome.TimedOut) break;
                    }
                }
                finally { if (session != null) await session.DisposeAsync(); }
            }
        }
        catch (OperationCanceledException)
        {
            if (baselineStarted && !baselineCompleted)
                error = cancellationToken.IsCancellationRequested
                    ? "Baseline cancelled before any mutant could start; no mutation evidence was collected."
                    : "Verification budget expired during the baseline before any mutant could start. The budget is smaller than one baseline run; use --filter to narrow the test scope or increase --timeout.";
            diagnostics.Add(CancellationDiagnostic());
        }
        catch (Exception ex)
        {
            infrastructureError = true; error = ex.Message;
            diagnostics.Add(VerificationDiagnostic.FromException(ex, currentPhase));
        }
        var completed = results.Select(r => r.Mutant.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var mutant in selected.Where(m => !completed.Contains(m.Id)))
            results.Add(new(mutant, MutationOutcome.Skipped, Detail: baselineStarted && !baselineCompleted
                ? "Skipped because the baseline did not complete; no mutant could start. " + error
                : error ?? "Verification budget exhausted or cancelled."));
        var positions = selected.Select((mutant, index) => (mutant.Id, index)).ToDictionary(p => p.Id, p => p.index, StringComparer.Ordinal);
        results.Sort((a, b) => positions[a.Mutant.Id].CompareTo(positions[b.Mutant.Id]));
        var incomplete = token.IsCancellationRequested || results.Any(r => r.Outcome is MutationOutcome.TimedOut or MutationOutcome.Skipped);
        var status = infrastructureError || results.Any(r => r.Outcome is MutationOutcome.CompileError or MutationOutcome.TestError) ? "error"
            : incomplete ? "incomplete" : results.Any(r => r.Outcome == MutationOutcome.Survived) ? "failed"
            : selected.Length == 0 ? "incomplete" : "passed";
        if (status == "incomplete" && token.IsCancellationRequested)
        {
            error ??= "Verification budget exhausted or cancelled; available results are incomplete.";
            if (!diagnostics.Any(d => d.Code is "cancelled" or "budget_exhausted" or "baseline_budget_exhausted"))
                diagnostics.Add(CancellationDiagnostic());
        }
        if (selected.Length == 0 && error == null && !token.IsCancellationRequested)
        {
            error = "No eligible changed expressions; verification provides no mutation evidence.";
            diagnostics.Add(VerificationDiagnostic.Create("no_eligible_expressions", "discovery", error));
        }
        progress?.Invoke(new("complete", results.Count(r => r.Outcome != MutationOutcome.Skipped), selected.Length, status));
        return new(status, request.Base, files.Count, mutants.Count, selected.Length, results, clock.ElapsedMilliseconds, timings, error, unresolved, request.Filter, unresolvedBoolean)
        {
            Diagnostics = diagnostics,
            Selection = new(requestedIds.Length == 0 ? "bounded" : "explicit", requestedIds),
            Files = SummarizeFiles(files, mutants, selected),
            ConfirmKills = request.ConfirmKills,
            Preparation = preparation,
            WorkersRequested = request.Workers,
            WorkersUsed = workersUsed
        };
        VerificationDiagnostic CancellationDiagnostic() => (cancellationToken.IsCancellationRequested || !token.IsCancellationRequested)
            && !(request.DeadlineUtc is { } deadline && deadline <= DateTimeOffset.UtcNow)
            ? VerificationDiagnostic.Create("cancelled", currentPhase, error ?? "Verification cancelled; available results are incomplete.")
            : VerificationDiagnostic.Create(currentPhase == "baseline" ? "baseline_budget_exhausted" : "budget_exhausted", currentPhase,
                error ?? "Verification budget exhausted; available results are incomplete.", "increase_timeout", "review_test_scope");
    }
    private static FileVerificationSummary[] SummarizeFiles(IReadOnlyList<SourceChange> changes,
        IReadOnlyList<Mutant> mutants, IReadOnlyList<Mutant> selected)
    {
        var discoveredCounts = mutants.GroupBy(m => m.File).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        var selectedCounts = selected.GroupBy(m => m.File).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        return changes.GroupBy(c => c.File).OrderBy(g => g.Key, StringComparer.Ordinal).Select(group =>
        {
            // Count the union of line ranges without allocating one element for every changed line.
            var changedLines = 0;
            var lastEnd = 0;
            foreach (var range in group.SelectMany(c => c.Lines).OrderBy(r => r.Start))
            {
                var start = Math.Max(1, Math.Max(range.Start, lastEnd + 1));
                if (range.End >= start) changedLines += range.End - start + 1;
                lastEnd = Math.Max(lastEnd, range.End);
            }
            return new FileVerificationSummary(group.Key, changedLines,
                discoveredCounts.GetValueOrDefault(group.Key), selectedCounts.GetValueOrDefault(group.Key));
        }).ToArray();
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
