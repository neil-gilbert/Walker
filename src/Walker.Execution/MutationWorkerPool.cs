using Walker.Core;

namespace Walker.Execution;

// Only prepared DLL attempts overlap. Each worker owns outputs, content, cwd, temp,
// TRX directories and executor state. Source fallback belongs to the serial session.
internal sealed class MutationWorkerPool(PreparedMutationSession serial, IReadOnlyList<MutationWorkerPool.Worker> workers, VerificationRequest request)
    : IPreparedMutationSession, IMutationBatchExecutor
{
    internal sealed record Worker(string Root, VerificationRequest Request, PreparedMutationSession Session,
        DotnetMutationExecutor Executor, IReadOnlyList<PreparedMutationSession.Scope> Scopes);
    private readonly object lifetime = new();
    private CancellationTokenSource? runningBudget;
    private Task<IReadOnlyList<MutationResult>>? running;
    private bool disposed;
    public int WorkerCount => workers.Count;
    public IReadOnlySet<string> SupportedMutantIds => serial.SupportedMutantIds;
    public string? Detail => serial.Detail + " Two isolated workers; ordinary and ID-zero parallel baselines validated. Source fallback remains serial.";

    internal static async Task<IPreparedMutationSession> CreateAsync(PreparedMutationSession serial, IProcessRunner runner,
        MutationWorkspace workspace, VerificationRequest request, IReadOnlyDictionary<string, int> activeIds,
        string environmentName, IReadOnlyList<PreparedMutationSession.Scope> prepared,
        IReadOnlyList<PreparedMutationSession.Scope> ordinary, DotnetMutationExecutor source, Action<string>? trace, CancellationToken token)
    {
        if (request.Workers != 2 || activeIds.Count < 2) return serial;
        var roots = new List<string>();
        try
        {
            var workers = new List<Worker>();
            for (var index = 0; index < 2; index++)
            {
                token.ThrowIfCancellationRequested();
                var root = Path.Combine(Path.GetDirectoryName(workspace.Root)!, "walker-worker-" + Guid.NewGuid().ToString("N"));
                roots.Add(root);
                var originalRoot = Path.Combine(root, "ordinary");
                var preparedRoot = Path.Combine(root, "prepared");
                var temp = Path.Combine(root, "temp");
                Directory.CreateDirectory(temp);
                await workspace.CopyOriginalAsync(originalRoot, token);
                CopyTree(workspace.Root, preparedRoot, token);
                string MapOrdinary(string path) => Map(request.Root, originalRoot, Path.GetFullPath(path, request.Root));
                foreach (var directory in ordinary.Select(s => Path.GetDirectoryName(s.Output.Assembly)!).Distinct(StringComparer.Ordinal))
                    CopyTree(directory, MapOrdinary(directory), token);
                var workerRequest = request with { Root = originalRoot, Project = MapOrdinary(request.Project),
                    Tests = request.Tests.Select(MapOrdinary).ToArray(), Workers = 1 };
                var childRunner = new WorkerProcessRunner(runner, temp);
                var executor = new DotnetMutationExecutor(childRunner, hangAllowance: source.PreparedHangLimit, resultsRoot: Path.Combine(root, "results"));
                var scopes = prepared.Select(s => new PreparedMutationSession.Scope(MapOrdinary(s.Project), s.Framework,
                    s.Output with { Assembly = Map(workspace.Root, preparedRoot, s.Output.Assembly) },
                    ordinary.Single(o => o.Project == s.Project && o.Framework == s.Framework).Output with
                    { Assembly = MapOrdinary(ordinary.Single(o => o.Project == s.Project && o.Framework == s.Framework).Output.Assembly) })).ToArray();
                var session = new PreparedMutationSession(executor, workspace, workerRequest, activeIds, environmentName, scopes, ownsWorkspace: false);
                workers.Add(new(root, workerRequest, session, executor, scopes));
            }
            // Validate each complete generation while both workers are active, rather than
            // accepting two individually passing probes that could still conflict in parallel.
            async Task<bool> Validate(Worker worker, bool instrumented)
            {
                using var hang = CancellationTokenSource.CreateLinkedTokenSource(token);
                hang.CancelAfter(source.PreparedHangLimit);
                try
                {
                    foreach (var project in request.Tests)
                    {
                        var signatures = new List<string>();
                        foreach (var scope in worker.Scopes.Where(s => s.Project == Path.Combine(worker.Request.Root, Path.GetRelativePath(request.Root, Path.GetFullPath(project, request.Root)))))
                        {
                            var run = await worker.Executor.RunAssembly(instrumented ? scope.Output : scope.OrdinaryOutput!, scope.Project,
                                worker.Request.Root, request.Filter, scope.Framework, new Dictionary<string, string?> { [environmentName] = "0" }, hang.Token, captureIdentity: true);
                            if (run.Outcome != MutationOutcome.Survived || run.Identities == null) return false;
                            signatures.AddRange(run.Identities);
                        }
                        if (!signatures.Order(StringComparer.Ordinal).SequenceEqual(source.BaselineIdentities[Path.GetFullPath(project, request.Root)].Order(StringComparer.Ordinal))) return false;
                    }
                    return true;
                }
                catch (Exception) { return false; }
            }
            foreach (var instrumented in new[] { false, true })
            {
                var valid = await Task.WhenAll(workers.Select(w => Validate(w, instrumented)));
                token.ThrowIfCancellationRequested();
                if (valid.Any(v => !v)) throw new InvalidOperationException("Parallel baseline failed, was empty, or differed from the original scope.");
            }
            if (!await workspace.Unchanged(token)) throw new InvalidOperationException("Original inputs changed during worker preparation.");
            trace?.Invoke("Switch preparation: two isolated workers validated.");
            return new MutationWorkerPool(serial, workers, request);
        }
        catch (Exception ex)
        {
            foreach (var root in roots) Delete(root);
            token.ThrowIfCancellationRequested();
            serial.ParallelFallbackDetail = " Parallel preparation unavailable; using one worker. " + ex.Message;
            trace?.Invoke(serial.ParallelFallbackDetail);
            return serial;
        }
    }
    private static string Map(string from, string to, string path)
    {
        var relative = Path.GetRelativePath(from, path);
        if (Path.IsPathFullyQualified(relative) || relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new InvalidOperationException("Worker output is outside the isolated tree.");
        return Path.Combine(to, relative);
    }
    private static void CopyTree(string source, string destination, CancellationToken token)
    {
        if (new DirectoryInfo(source).LinkTarget != null) throw new InvalidOperationException("Linked worker output directory.");
        Directory.CreateDirectory(destination);
        foreach (var entry in Directory.EnumerateFileSystemEntries(source))
        {
            token.ThrowIfCancellationRequested();
            if (File.GetAttributes(entry).HasFlag(FileAttributes.ReparsePoint)) throw new InvalidOperationException("Linked worker output.");
            var target = Path.Combine(destination, Path.GetFileName(entry));
            if (Directory.Exists(entry)) CopyTree(entry, target, token);
            else File.Copy(entry, target, overwrite: true);
        }
    }
    public Task<MutationResult> ExecuteAsync(Mutant mutant, VerificationContext context, CancellationToken token) => serial.ExecuteAsync(mutant, context, token);
    public Task<IReadOnlyList<MutationResult>> ExecuteBatchAsync(IReadOnlyList<Mutant> selected, VerificationContext context, CancellationToken token)
    {
        lock (lifetime)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (running != null) throw new InvalidOperationException("A worker batch is already running.");
            runningBudget = CancellationTokenSource.CreateLinkedTokenSource(token);
            return running = ExecuteBatch(selected, context, runningBudget.Token);
        }
    }
    private async Task<IReadOnlyList<MutationResult>> ExecuteBatch(IReadOnlyList<Mutant> selected, VerificationContext context, CancellationToken token)
    {
        var results = new MutationResult?[selected.Count];
        // A custom selector has not been validated on the relocated full baseline. Keep it serial.
        var compatible = context.Request == request;
        foreach (var mutant in selected)
        {
            var selection = await context.TestSelector.SelectTestsAsync(mutant, token);
            compatible &= selection.Filter == context.Request.Filter && selection.Projects.SequenceEqual(context.Request.Tests);
        }
        var cursor = 0;
        while (cursor < selected.Count && !token.IsCancellationRequested)
        {
            if (!compatible || !SupportedMutantIds.Contains(selected[cursor].Id))
            {
                try { results[cursor] = await serial.ExecuteAsync(selected[cursor], context, token); }
                catch (OperationCanceledException) { results[cursor] = new(selected[cursor], MutationOutcome.TimedOut) { Diagnostics = [VerificationDiagnostic.Create("cancelled", "execution", "Worker execution cancelled.")] }; }
                catch (Exception ex) { results[cursor] = new(selected[cursor], MutationOutcome.TestError, Detail: ex.Message) { Diagnostics = [VerificationDiagnostic.FromException(ex, "execution")] }; }
                if (results[cursor++]!.Outcome == MutationOutcome.TimedOut) break;
                continue;
            }
            var end = cursor;
            while (end < selected.Count && SupportedMutantIds.Contains(selected[end].Id)) end++;
            var next = cursor;
            var dispatch = new object();
            using var segment = CancellationTokenSource.CreateLinkedTokenSource(token);
            async Task Run(Worker worker)
            {
                var workerContext = new VerificationContext(worker.Request, new AllTestsSelector(worker.Request.Tests, worker.Request.Filter));
                while (true)
                {
                    int index;
                    lock (dispatch)
                    {
                        if (segment.IsCancellationRequested || next >= end) return;
                        index = next++;
                    }
                    // Prepared sessions classify per-attempt hangs/errors and always drain their child.
                    results[index] = await worker.Session.ExecuteAsync(selected[index], workerContext, segment.Token);
                    if (results[index]!.Outcome == MutationOutcome.TimedOut) segment.Cancel();
                }
            }
            await Task.WhenAll(workers.Select(Run));
            if (segment.IsCancellationRequested) break;
            cursor = end;
        }
        return results.OfType<MutationResult>().ToArray();
    }
    public async ValueTask DisposeAsync()
    {
        Task<IReadOnlyList<MutationResult>>? pending;
        lock (lifetime)
        {
            if (disposed) return;
            disposed = true;
            runningBudget?.Cancel();
            pending = running;
        }
        try { if (pending != null) await pending; }
        finally
        {
            foreach (var worker in workers) Delete(worker.Root);
            await serial.DisposeAsync();
            runningBudget?.Dispose();
        }
    }
    private static void Delete(string root)
    {
        try { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
    private sealed class WorkerProcessRunner(IProcessRunner inner, string temp) : IProcessRunner
    {
        public Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken token)
        {
            var environment = new Dictionary<string, string?> { ["TMPDIR"] = temp, ["TMP"] = temp, ["TEMP"] = temp };
            if (request.Environment != null) foreach (var pair in request.Environment) environment[pair.Key] = pair.Value;
            return inner.RunAsync(request with { Environment = environment }, token);
        }
    }
}
