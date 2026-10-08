using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Walker.Core;

namespace Walker.Execution;

public sealed class IsolatedVerificationSession(IProcessRunner runner, Action<string>? log = null, Action<VerificationProgress>? progress = null)
{
    private sealed record InputFile(string File, string? Hash, int? Mode);
    private sealed record Snapshot(string Head, string Base, string MergeBase, string Index, string Status, string PatchHash,
        string[] Inventory, string[] Untracked, IReadOnlyList<InputFile> Inputs, string Fingerprint);

    public async Task<VerificationResult> VerifyAsync(VerificationRequest request, string workingDirectory,
        Func<VerificationRequest, IProcessRunner, CancellationToken, Task<VerificationResult>> verify, CancellationToken cancellationToken = default)
    {
        var timer = Stopwatch.StartNew();
        var runId = Guid.NewGuid().ToString("N");
        var sourceRoot = IsolationInputPolicy.Normalize(request.Root);
        request = request with
        {
            Root = sourceRoot, Project = IsolationInputPolicy.Normalize(Path.GetFullPath(request.Project, sourceRoot)),
            Tests = request.Tests.Select(path => IsolationInputPolicy.Normalize(Path.GetFullPath(path, sourceRoot))).ToArray(),
            DeadlineUtc = request.DeadlineUtc ?? DateTimeOffset.UtcNow.AddSeconds(request.TimeoutSeconds)
        };
        workingDirectory = IsolationInputPolicy.Normalize(workingDirectory);
        var artifacts = IsolationInputPolicy.Normalize(Path.Combine(Path.GetTempPath(), "walker-isolated-" + runId));
        var worktree = Path.Combine(artifacts, "worktree");
        var reportPath = Path.Combine(artifacts, "report.json");
        var logPath = Path.Combine(artifacts, "process.log");
        var inputsRoot = Path.Combine(artifacts, "inputs");
        var process = new LoggedRunner(runner, logPath);
        var summary = new IsolationSummary(runId, sourceRoot, null, null, null, null,
            new(IsolationInputPolicy.Relative(sourceRoot, request.Project), request.Tests.Select(path => IsolationInputPolicy.Relative(sourceRoot, path)).ToArray(),
                request.Filter, request.MaxMutants, request.TimeoutSeconds), 0, artifacts, reportPath, logPath, worktree, "not_created", []);
        Snapshot? snapshot = null;
        var copied = new Dictionary<string, InputFile>(IsolationInputPolicy.PathComparer);
        string? ownership = null;
        string? worktreeIndex = null;
        var registrationStarted = false;
        VerificationResult? result = null;
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(TimeSpan.FromTicks(Math.Max(0, (request.DeadlineUtc.Value - DateTimeOffset.UtcNow).Ticks)));
        var token = budget.Token;
        try
        {
            if (IsolationInputPolicy.Inside(sourceRoot, artifacts)) throw IsolationInputPolicy.Unsupported("Artifacts must be outside the source checkout.");
            Directory.CreateDirectory(artifacts);
            Directory.CreateDirectory(inputsRoot);
            Directory.CreateDirectory(process.HooksPath);
            await File.WriteAllTextAsync(logPath, "", token);
            await Persist(Path.Combine(artifacts, "session.json"), new { state = "capturing", isolation = summary });
            if (File.Exists(MutationJournal.PathFor(sourceRoot)))
                throw new VerificationException("source_recovery_pending", "isolation", "The source checkout has a pending Walker restore journal. Inspect it before capturing a snapshot; source and journal were left untouched.", actions: ["inspect_source_recovery"]);
            if (!IsolationInputPolicy.PathComparer.Equals(sourceRoot, workingDirectory) && !IsolationInputPolicy.Inside(sourceRoot, workingDirectory))
                throw IsolationInputPolicy.Unsupported("Invocation directory is outside the repository.");
            snapshot = await Capture(process, request, inputsRoot, artifacts, token);
            summary = summary with { SourceHead = snapshot.Head, ResolvedBase = snapshot.Base, MergeBase = snapshot.MergeBase, SnapshotFingerprint = snapshot.Fingerprint };
            await Persist(Path.Combine(artifacts, "session.json"), new { state = "captured", isolation = summary, snapshot });
            token.ThrowIfCancellationRequested();
            registrationStarted = true;
            // No checkout: copy exact captured bytes ourselves, without checkout hooks or smudge filters.
            await Git(process, sourceRoot, token, "worktree", "add", "--detach", "--no-checkout", worktree, snapshot.Head);
            ownership = await File.ReadAllTextAsync(Path.Combine(worktree, ".git"), token);
            await Git(process, worktree, token, "read-tree", snapshot.Head);
            var patchPath = Path.Combine(artifacts, "source.patch");
            if (new FileInfo(patchPath).Length > 0)
                await Git(process, worktree, token, "apply", "--cached", "--binary", "--whitespace=nowarn", patchPath);
            worktreeIndex = await Git(process, worktree, token, "ls-files", "--stage", "-z");
            foreach (var input in snapshot.Inputs)
            {
                token.ThrowIfCancellationRequested();
                if (input.Hash == null) continue;
                var destination = Path.Combine(worktree, input.File);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Copy(Path.Combine(inputsRoot, input.File), destination);
                if (!OperatingSystem.IsWindows() && input.Mode is { } mode) File.SetUnixFileMode(destination, (UnixFileMode)mode);
                copied.Add(input.File, input);
            }
            if (!await SourceStillMatches(process, sourceRoot, snapshot, token))
                throw new VerificationException("snapshot_changed", "isolation", "Source inputs changed during capture; no verification was started.", actions: ["capture_again"]);
            await CheckOwnedSnapshot(process, worktree, snapshot.Head, ownership, worktreeIndex, copied, token);
            string Map(string path) => Path.Combine(worktree, IsolationInputPolicy.Relative(sourceRoot, path));
            var mapped = request with { Root = worktree, Project = Map(request.Project), Tests = request.Tests.Select(Map).ToArray(), Base = snapshot.Base };
            // Evaluate only the validated isolated project; untracked build inputs stay outside Git discovery.
            var compiled = await new MsBuildSourceScope(process).GetFilesAsync(mapped, token);
            if (compiled.Any(file => !IsolationInputPolicy.Inside(worktree, file))) throw IsolationInputPolicy.Unsupported("Evaluated Compile items extend outside the isolated worktree.");
            summary = summary with
            {
                SetupMs = timer.ElapsedMilliseconds,
                UntrackedProductionFiles = snapshot.Untracked.Where(file => compiled.Contains(Map(Path.Combine(sourceRoot, file)))).ToArray()
            };
            await Persist(Path.Combine(artifacts, "session.json"), new { state = "verifying", isolation = summary, snapshot });
            log?.Invoke("Isolated snapshot ready. Artifacts: " + artifacts);
            result = await verify(mapped, process, token);
        }
        catch (OperationCanceledException)
        {
            var diagnostic = VerificationDiagnostic.Create(cancellationToken.IsCancellationRequested ? "cancelled" : "budget_exhausted", "isolation",
                "Isolated verification cancelled or exhausted its global budget; available evidence is incomplete.");
            result = Empty("incomplete", diagnostic);
        }
        catch (Exception ex) { result = Empty("error", VerificationDiagnostic.FromException(ex, "isolation")); }

        summary = summary with { SetupMs = summary.SetupMs == 0 ? timer.ElapsedMilliseconds : summary.SetupMs, CleanupState = registrationStarted ? "pending" : "not_created" };
        progress?.Invoke(new("cleanup", result.MutantsExecuted, result.MutantsSelected, "Persisting evidence and restoring/removing the private snapshot"));
        result = result with { Isolation = summary, DurationMs = timer.ElapsedMilliseconds };
        var durable = false;
        try { await Persist(Path.Combine(artifacts, "provisional-report.json"), result); durable = true; }
        catch (Exception ex) { result = AddError(result, "isolation_artifact_failed", "Could not persist provisional evidence: " + ex.Message); }

        if (registrationStarted)
        {
            try
            {
                if (!durable || snapshot == null || ownership == null || worktreeIndex == null)
                    throw new IOException("Worktree ownership or complete setup metadata could not be verified; retaining it.");
                await CheckOwnedSnapshot(process, worktree, snapshot.Head, ownership, worktreeIndex, copied, CancellationToken.None);
                if (File.Exists(MutationJournal.PathFor(worktree))) throw new IOException("Isolated source still has a pending restore journal.");
                // Only the private worktree with matching captured inputs may be force-removed.
                await Git(process, sourceRoot, CancellationToken.None, "worktree", "remove", "--force", worktree);
                summary = summary with { CleanupState = "removed" };
            }
            catch (Exception ex)
            {
                summary = summary with { CleanupState = "retained" };
                result = AddError(result, "isolation_cleanup_failed", "Isolated worktree retained at " + worktree + ": " + ex.Message);
            }
        }
        result = result with { Isolation = summary, DurationMs = timer.ElapsedMilliseconds };
        try { await Persist(reportPath, result); }
        catch (Exception ex) { result = AddError(result, "isolation_artifact_failed", "Could not persist final report: " + ex.Message); }
        return result;

        VerificationResult Empty(string status, VerificationDiagnostic diagnostic) => new(status, request.Base, 0, 0, 0, [], timer.ElapsedMilliseconds, new(), diagnostic.Message)
        {
            Diagnostics = [diagnostic], TestFilter = request.Filter, ConfirmKills = request.ConfirmKills, WorkersRequested = request.Workers,
            Selection = new(request.MutantIds is { Count: > 0 } ? "explicit" : "bounded", (request.MutantIds ?? []).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray())
        };
    }

    private static VerificationResult AddError(VerificationResult result, string code, string message) => result with
    {
        Status = "error", Error = result.Error == null ? message : result.Error + "\n" + message,
        Diagnostics = [..result.Diagnostics, VerificationDiagnostic.Create(code, "isolation", message)]
    };

    private static async Task<Snapshot> Capture(IProcessRunner process, VerificationRequest request, string inputsRoot, string artifacts, CancellationToken token)
    {
        var root = request.Root;
        var head = (await Git(process, root, token, "rev-parse", "HEAD")).Trim();
        var resolvedBase = (await Git(process, root, token, "rev-parse", "--verify", "--end-of-options", request.Base + "^{commit}")).Trim();
        var mergeBase = (await Git(process, root, token, "merge-base", resolvedBase, head)).Trim();
        var index = await Git(process, root, token, "ls-files", "--stage", "-z");
        var tree = await Git(process, root, token, "ls-tree", "-r", "-z", head);
        if (index.Split('\0').Concat(tree.Split('\0')).Any(entry => entry.StartsWith("120000 ", StringComparison.Ordinal) || entry.StartsWith("160000 ", StringComparison.Ordinal)))
            throw IsolationInputPolicy.Unsupported("Symlinks and submodules cannot be isolated by this version.");
        var status = await Git(process, root, token, "status", "--porcelain=v1", "-z", "--untracked-files=all");
        var inventory = await Inventory(process, root, token);
        var files = inventory.ToHashSet(IsolationInputPolicy.PathComparer);
        var projects = inventory.Where(file => file.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)).Select(file => Path.Combine(root, file))
            .Concat(request.Tests.Prepend(request.Project)).Distinct(IsolationInputPolicy.PathComparer).ToArray();
        IsolationInputPolicy.Validate(root, files, projects.Where(File.Exists).ToArray());
        var untracked = Split(await Git(process, root, token, "ls-files", "--others", "--exclude-standard", "-z"));
        // Default SDK items can include ignored source, resources, or content; do not silently omit them.
        var ignored = Split(await Git(process, root, token, "ls-files", "--others", "--ignored", "--exclude-standard", "-z"));
        var projectDirectories = projects.Select(path => Path.GetDirectoryName(path)!).ToArray();
        foreach (var file in ignored)
        {
            var path = Path.GetFullPath(file, root);
            if (projectDirectories.Any(directory => IsolationInputPolicy.Inside(directory, path))
                && !projectDirectories.Any(directory => IsolationInputPolicy.Inside(Path.Combine(directory, "bin"), path) || IsolationInputPolicy.Inside(Path.Combine(directory, "obj"), path)))
                throw IsolationInputPolicy.Unsupported("Ignored build input cannot be isolated: " + file);
        }
        var patch = await Git(process, root, token, "diff", "--binary", "--full-index", "--no-ext-diff", "--no-textconv", "--no-color", head, "--");
        await File.WriteAllTextAsync(Path.Combine(artifacts, "source.patch"), patch, new UTF8Encoding(false), token);
        var inputs = new List<InputFile>();
        foreach (var file in inventory)
        {
            token.ThrowIfCancellationRequested();
            var path = Path.GetFullPath(file, root);
            IsolationInputPolicy.CheckLinks(root, path);
            if (!File.Exists(path)) { inputs.Add(new(file, null, null)); continue; }
            var bytes = await File.ReadAllBytesAsync(path, token);
            int? mode = OperatingSystem.IsWindows() ? null : (int)File.GetUnixFileMode(path);
            inputs.Add(new(file, Hash(bytes), mode));
            var destination = Path.Combine(inputsRoot, file);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            await File.WriteAllBytesAsync(destination, bytes, token);
        }
        var fingerprint = Mutant.Hash(JsonSerializer.Serialize(new { head, resolvedBase, mergeBase, index, inputs, untracked }));
        // Validate the captured bytes too: live files may have changed after the initial preflight.
        IsolationInputPolicy.Validate(inputsRoot, files, projects.Where(File.Exists).Select(path => Path.Combine(inputsRoot, IsolationInputPolicy.Relative(root, path))).ToArray());
        var snapshot = new Snapshot(head, resolvedBase, mergeBase, index, status, Mutant.Hash(patch), inventory, untracked, inputs, fingerprint);
        if (!await SourceStillMatches(process, root, snapshot, token))
            throw new VerificationException("snapshot_changed", "isolation", "Source inputs changed during capture; no verification was started.", actions: ["capture_again"]);
        return snapshot;
    }

    private static async Task<bool> SourceStillMatches(IProcessRunner process, string root, Snapshot snapshot, CancellationToken token)
    {
        if ((await Git(process, root, token, "rev-parse", "HEAD")).Trim() != snapshot.Head
            || await Git(process, root, token, "ls-files", "--stage", "-z") != snapshot.Index
            || await Git(process, root, token, "status", "--porcelain=v1", "-z", "--untracked-files=all") != snapshot.Status
            || Mutant.Hash(await Git(process, root, token, "diff", "--binary", "--full-index", "--no-ext-diff", "--no-textconv", "--no-color", snapshot.Head, "--")) != snapshot.PatchHash
            || !(await Inventory(process, root, token)).SequenceEqual(snapshot.Inventory)) return false;
        foreach (var input in snapshot.Inputs)
        {
            token.ThrowIfCancellationRequested();
            var path = Path.Combine(root, input.File);
            IsolationInputPolicy.CheckLinks(root, path);
            if (!await Matches(path, input, token)) return false;
        }
        return true;
    }

    private static async Task CheckOwnedSnapshot(IProcessRunner process, string root, string head, string ownership, string index,
        IReadOnlyDictionary<string, InputFile> inputs, CancellationToken token)
    {
        if (!Directory.Exists(root) || new DirectoryInfo(root).LinkTarget != null
            || new FileInfo(Path.Combine(root, ".git")).LinkTarget != null
            || await File.ReadAllTextAsync(Path.Combine(root, ".git"), token) != ownership
            || (await Git(process, root, token, "rev-parse", "HEAD")).Trim() != head
            || await Git(process, root, token, "ls-files", "--stage", "-z") != index)
            throw new IOException("Owned worktree metadata changed.");
        foreach (var input in inputs.Values)
        {
            var path = Path.Combine(root, input.File);
            IsolationInputPolicy.CheckLinks(root, path);
            if (!await Matches(path, input, token)) throw new IOException("Snapshot input changed: " + input.File);
        }
        var outputDirectories = inputs.Keys.Where(file => file.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
            .SelectMany(file => new[] { Path.Combine(root, Path.GetDirectoryName(file)!, "bin"), Path.Combine(root, Path.GetDirectoryName(file)!, "obj") }).ToArray();
        foreach (var file in Split(await Git(process, root, token, "ls-files", "--others", "-z")))
        {
            if (inputs.ContainsKey(file)) continue;
            var path = Path.Combine(root, file);
            if (!outputDirectories.Any(directory => IsolationInputPolicy.Inside(directory, path)))
                throw new IOException("Unexpected file in isolated worktree: " + file);
        }
    }

    private static async Task<bool> Matches(string path, InputFile input, CancellationToken token)
    {
        if (input.Hash == null) return !File.Exists(path) && !Directory.Exists(path);
        if (!File.Exists(path) || Hash(await File.ReadAllBytesAsync(path, token)) != input.Hash) return false;
        return OperatingSystem.IsWindows() || (int)File.GetUnixFileMode(path) == input.Mode;
    }
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    private static string[] Split(string output) => output.Split('\0', StringSplitOptions.RemoveEmptyEntries).Distinct(IsolationInputPolicy.PathComparer).Order(StringComparer.Ordinal).ToArray();
    private static async Task<string[]> Inventory(IProcessRunner process, string root, CancellationToken token) =>
        Split(await Git(process, root, token, "ls-files", "--cached", "--others", "--exclude-standard", "-z"));
    private static async Task<string> Git(IProcessRunner process, string root, CancellationToken token, params string[] arguments)
    {
        var hooksPath = ((LoggedRunner)process).HooksPath;
        var result = await process.RunAsync(new("git", ["-c", "core.quotePath=false", "-c", "core.hooksPath=" + hooksPath, ..arguments], root,
            OutputLimit: 16 * 1024 * 1024, Environment: new Dictionary<string, string?> { ["GIT_OPTIONAL_LOCKS"] = "0", ["GIT_TERMINAL_PROMPT"] = "0" }), token);
        if (result.ExitCode != 0 || result.OutputTruncated)
            throw new VerificationException("isolation_setup_failed", "isolation", "Git isolation operation failed: " + result.StandardError.Trim());
        return result.StandardOutput;
    }
    private static async Task Persist(string path, object value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp";
        await using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
        {
            await JsonSerializer.SerializeAsync(stream, value, VerificationReportJson.Options, CancellationToken.None);
            await stream.FlushAsync(CancellationToken.None);
        }
        File.Move(temporary, path, overwrite: true);
    }

    private sealed class LoggedRunner(IProcessRunner inner, string logPath) : IProcessRunner
    {
        public string HooksPath { get; } = Path.Combine(Path.GetDirectoryName(logPath)!, "disabled-hooks");
        private readonly SemaphoreSlim writes = new(1);
        public async Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken token)
        {
            await Append(JsonSerializer.Serialize(new { request.FileName, request.Arguments, request.WorkingDirectory }) + "\n");
            if (request.FileName == "git") request = request with
            { Environment = new Dictionary<string, string?>(request.Environment ?? new Dictionary<string, string?>()) { ["GIT_OPTIONAL_LOCKS"] = "0" } };
            if (request.FileName == "dotnet") request = request with
            { Environment = new Dictionary<string, string?>(request.Environment ?? new Dictionary<string, string?>())
                { ["MSBUILDDISABLENODEREUSE"] = "1", ["DOTNET_CLI_USE_MSBUILD_SERVER"] = "0" } };
            try
            {
                var result = await inner.RunAsync(request, token);
                await Append(JsonSerializer.Serialize(result) + "\n");
                return result;
            }
            catch (Exception ex) { await Append(JsonSerializer.Serialize(new { error = ex.Message }) + "\n"); throw; }
        }
        private async Task Append(string text)
        {
            await writes.WaitAsync(CancellationToken.None);
            try { await File.AppendAllTextAsync(logPath, text, CancellationToken.None); }
            finally { writes.Release(); }
        }
    }
}
