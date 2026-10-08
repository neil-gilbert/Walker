using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Walker.Core;

namespace Walker.Execution;

public static class AgentWorkflows
{
    public static async Task<T> ReadManifestAsync<T>(string path, CancellationToken token = default) =>
        JsonSerializer.Deserialize<T>(await File.ReadAllTextAsync(path, token), new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            AllowDuplicateProperties = false
        }) ?? throw new ArgumentException("Empty workflow manifest.");

    public static VerificationResult Investigate(VerificationResult report) => report with
    {
        Investigation = new(report.Results.Where(r => r.Outcome == MutationOutcome.Survived)
            .GroupBy(r => (r.Mutant.File, r.Mutant.Member, r.Mutant.Operator))
            .Select(g => new BehaviourGap(g.Key.File, g.Key.Member, g.Key.Operator,
                g.Select(r => r.Mutant.Id).ToArray(), Hint(g.Key.Operator))).ToArray(),
            "Groups are investigation hints, not equivalence or risk verdicts. Determine intended behaviour from the contract; inspect each original/replacement in results. A passing test on current code alone does not establish correctness.")
    };

    private static string Hint(MutationOperator op) => op switch
    {
        MutationOperator.ConditionalBoundary => "Try the exact boundary and values on either side; establish whether equality is permitted.",
        MutationOperator.Equality => "Find equal and unequal inputs and check the observable outcome.",
        MutationOperator.NullHandling => "Exercise null and non-null inputs; assert the specified result or exception.",
        MutationOperator.BooleanLogic => "Vary independent conditions and assert their effect on the public outcome.",
        MutationOperator.ReturnValue => "Assert the returned value for a contract-defined scenario.",
        MutationOperator.Arithmetic => "Choose inputs that distinguish the calculations and assert the specified numeric result.",
        _ => "Use the supplied concern and contract to find an observable difference between the original and faulty version."
    };

    public static void Validate(ChallengeManifest manifest, int maximum)
    {
        if (manifest.SchemaVersion != 1 || manifest.Challenges == null || manifest.Challenges.Count == 0 || manifest.Challenges.Count > maximum)
            throw new ArgumentException("Challenge manifest requires schemaVersion 1 and 1..max-mutants challenges; none are silently dropped.");
        foreach (var fault in manifest.Challenges)
            if (fault == null || string.IsNullOrWhiteSpace(fault.File) || string.IsNullOrWhiteSpace(fault.SourceHash)
                || fault.SpanStart < 0 || string.IsNullOrEmpty(fault.Original) || fault.Replacement == null || fault.Original == fault.Replacement
                || string.IsNullOrWhiteSpace(fault.Concern) || string.IsNullOrWhiteSpace(fault.ExpectedBehaviour))
                throw new ArgumentException("Each challenge needs a file, sourceHash, spanStart, distinct original/replacement, concern and expectedBehaviour.");
    }

    public static void Validate(TestPatchManifest manifest)
    {
        if (manifest.SchemaVersion != 1 || string.IsNullOrWhiteSpace(manifest.Contract) || manifest.Files == null || manifest.Files.Count == 0)
            throw new ArgumentException("Test patch requires schemaVersion 1, a contract and at least one file.");
        if (manifest.Files.Any(f => f == null || string.IsNullOrWhiteSpace(f.File) || f.Content == null))
            throw new ArgumentException("Test patch entries require file and content; originalHash is null only for a new file.");
    }

    public static string ResolveSource(string root, string relative)
    {
        var full = Path.GetFullPath(relative, root);
        var local = Path.GetRelativePath(root, full);
        if (Path.IsPathRooted(relative) || local == ".." || local.StartsWith(".." + Path.DirectorySeparatorChar)
            || !relative.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
            || local.Split(Path.DirectorySeparatorChar).Any(p => p is ".git" or "bin" or "obj") || HasLink(full, root))
            throw new VerificationException("workflow_scope_invalid", "workflow", "Workflow files must be repository-relative C# source without links or build-output paths.");
        return full;
    }

    private static bool HasLink(string path, string root)
    {
        for (var current = path; current != root && current != null; current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current)) && File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint)) return true;
        return false;
    }

    public static string ByteHash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    // Runs inside native isolation. The proposed tests are applied only to that disposable snapshot.
    public static async Task<VerificationResult> VerifyTestPatchAsync(VerificationRequest request, TestPatchManifest patch,
        IProcessRunner runner, Func<VerificationRequest, CancellationToken, Task<VerificationResult>> verify, CancellationToken token)
    {
        var timer = System.Diagnostics.Stopwatch.StartNew();
        Validate(patch);
        var scope = new MsBuildSourceScope(runner);
        var production = await scope.GetAllFrameworkFilesAsync(request, token);
        var testSources = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        foreach (var project in request.Tests)
            testSources.UnionWith(await scope.GetAllFrameworkFilesAsync(request with { Project = project }, token));
        var originals = new Dictionary<string, byte[]?>(testSources.Comparer);
        foreach (var file in patch.Files)
        {
            var path = ResolveSource(request.Root, file.File);
            if (!originals.TryAdd(path, null) || production.Contains(path))
                throw new VerificationException("workflow_scope_invalid", "workflow", "Test patch contains duplicate paths or production source.");
            var bytes = File.Exists(path) ? await File.ReadAllBytesAsync(path, token) : null;
            if (bytes != null ? !testSources.Contains(path) || ByteHash(bytes) != file.OriginalHash
                : file.OriginalHash != null || !request.Tests.Any(project => Path.GetRelativePath(Path.GetDirectoryName(project)!, path) is var local
                    && !Path.IsPathRooted(local) && local != ".." && !local.StartsWith(".." + Path.DirectorySeparatorChar)))
                throw new VerificationException("test_patch_stale", "workflow", "Test patch must match existing test-source bytes, or add a new source under a requested test project.");
            if (bytes != null && ByteHash(Encoding.UTF8.GetBytes(file.Content)) == ByteHash(bytes))
                throw new ArgumentException("Test patch must change test source.");
            originals[path] = bytes;
        }
        // Freeze captured repository inputs across the pair, excluding build outputs and proposed tests.
        var protectedInputs = new Dictionary<string, string>(testSources.Comparer);
        foreach (var path in Directory.EnumerateFiles(request.Root, "*", SearchOption.AllDirectories))
        {
            token.ThrowIfCancellationRequested();
            if (!Path.GetRelativePath(request.Root, path).Split(Path.DirectorySeparatorChar).Any(p => p is ".git" or "bin" or "obj" or "TestResults")
                && !originals.ContainsKey(path)) protectedInputs[path] = ByteHash(await File.ReadAllBytesAsync(path, token));
        }
        var before = await verify(request, token);
        var survivors = before.Results.Where(r => r.Outcome == MutationOutcome.Survived).Select(r => r.Mutant.Id).ToArray();
        TestImprovementEvidence Evidence(string status, string[] verified) => new(patch.Contract, status, before, patch.Files, verified,
            "Execution evidence for the selected faults only. Review the caller-supplied contract and failing tests to distinguish meaningful assertions from setup failures or exceptions; repeated passing runs do not prove permanent stability.");
        if (before.Status is "error" or "incomplete") return before with { DurationMs = timer.ElapsedMilliseconds, TestImprovement = Evidence("not_verified", []) };
        if (survivors.Length == 0) return before with
        {
            Status = "incomplete", DurationMs = timer.ElapsedMilliseconds, TestImprovement = Evidence("not_verified", []),
            Diagnostics = [..before.Diagnostics, VerificationDiagnostic.Create("no_survivors_to_verify", "workflow", "No selected fault survived the original tests; no improvement was demonstrated.")]
        };
        async Task CheckProtectedInputs()
        {
            foreach (var (path, hash) in protectedInputs)
            {
                token.ThrowIfCancellationRequested();
                if (!File.Exists(path) || ByteHash(await File.ReadAllBytesAsync(path, token)) != hash)
                    throw new VerificationException("workflow_inputs_changed", "workflow", "An input outside the proposed test patch changed during paired verification.");
            }
        }
        try
        {
            await CheckProtectedInputs();
            foreach (var (path, bytes) in originals)
                if (bytes == null ? File.Exists(path) : !File.Exists(path) || ByteHash(await File.ReadAllBytesAsync(path, token)) != ByteHash(bytes))
                    throw new VerificationException("workflow_inputs_changed", "workflow", "Original test inputs changed during the first verification; proposed tests were not applied.");
            foreach (var file in patch.Files)
            {
                var path = ResolveSource(request.Root, file.File);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                await File.WriteAllTextAsync(path, file.Content, new UTF8Encoding(false), token);
            }
            // Re-evaluate after adding test files. Reject additions excluded from the requested projects.
            var updatedScope = new MsBuildSourceScope(runner);
            var updatedTests = new HashSet<string>(testSources.Comparer);
            foreach (var project in request.Tests)
                updatedTests.UnionWith(await updatedScope.GetAllFrameworkFilesAsync(request with { Project = project }, token));
            if (originals.Keys.Any(path => !updatedTests.Contains(path))
                || !production.SetEquals(await updatedScope.GetAllFrameworkFilesAsync(request, token)))
                throw new VerificationException("workflow_scope_invalid", "workflow", "Patch must compile as requested test source and leave the production source set unchanged.");
            await CheckProtectedInputs();
            // Recheck all original targets so replacing tests cannot silently remove previously observed protection.
            var after = await verify(request with { MutantIds = before.Results.Select(r => r.Mutant.Id).ToArray(), ConfirmKills = true }, token);
            await CheckProtectedInputs();
            var verified = after.Results.Where(r => survivors.Contains(r.Mutant.Id) && r.Outcome == MutationOutcome.Killed && r.KillConfirmed == true).Select(r => r.Mutant.Id).ToArray();
            var demonstrated = after.Status == "passed" && verified.Length == survivors.Length;
            return after with
            {
                DurationMs = timer.ElapsedMilliseconds,
                Status = !demonstrated && after.Status == "passed" ? "incomplete" : after.Status,
                TestImprovement = Evidence(demonstrated ? "verified" : "not_verified", verified),
                Diagnostics = !demonstrated && after.Status == "passed"
                    ? [..after.Diagnostics, VerificationDiagnostic.Create("test_improvement_unproven", "workflow", "A test failure confirmed on ordinary code is required for every original survivor; hangs are insufficient.")]
                    : after.Diagnostics
            };
        }
        catch (Exception ex)
        {
            return before with
            {
                Status = ex is OperationCanceledException ? "incomplete" : "error", Error = ex.Message,
                DurationMs = timer.ElapsedMilliseconds, TestImprovement = Evidence("not_verified", []),
                Diagnostics = [..before.Diagnostics, ex is OperationCanceledException
                    ? VerificationDiagnostic.Create("budget_exhausted", "workflow", "Paired verification cancelled or exhausted its shared budget.")
                    : VerificationDiagnostic.FromException(ex, "workflow")]
            };
        }
        finally
        {
            foreach (var (path, bytes) in originals)
                if (bytes != null) await File.WriteAllBytesAsync(path, bytes, CancellationToken.None);
                else if (File.Exists(path)) File.Delete(path);
        }
    }
}

public sealed class ChallengeDiscovery(ChallengeManifest manifest, IProductionSourceScope scope) : IChangeProvider, IMutationDiscoverer
{
    private Mutant[] mutants = [];
    public async Task<IReadOnlyList<SourceChange>> GetChangesAsync(VerificationRequest request, CancellationToken token)
    {
        AgentWorkflows.Validate(manifest, request.MaxMutants);
        var sources = scope is MsBuildSourceScope msbuild ? await msbuild.GetAllFrameworkFilesAsync(request, token) : await scope.GetFilesAsync(request, token);
        var found = new List<Mutant>();
        foreach (var fault in manifest.Challenges)
        {
            var path = AgentWorkflows.ResolveSource(request.Root, fault.File);
            if (!sources.Contains(path)) throw new VerificationException("workflow_scope_invalid", "discovery", "Challenge must target a compiled production source in --project.");
            var source = await File.ReadAllTextAsync(path, token);
            if (Mutant.Hash(source) != fault.SourceHash || fault.SpanStart > source.Length - fault.Original.Length
                || source.Substring(fault.SpanStart, fault.Original.Length) != fault.Original)
                throw new VerificationException("challenge_stale", "discovery", "Challenge does not match current production source; regenerate its hash and span after review.");
            var relative = Path.GetRelativePath(request.Root, path).Replace('\\', '/');
            var id = Mutant.Hash(JsonSerializer.Serialize(new { relative, fault.SourceHash, fault.SpanStart, fault.Original, fault.Replacement }))[..20];
            found.Add(new(id, relative, 1 + source[..fault.SpanStart].Count(c => c == '\n'), fault.Concern,
                MutationOperator.CustomFault, fault.Original, fault.Replacement, fault.SpanStart, fault.Original.Length, fault.SourceHash));
        }
        if (found.Select(m => m.Id).Distinct().Count() != found.Count) throw new ArgumentException("Duplicate fault challenges.");
        mutants = found.ToArray();
        return mutants.GroupBy(m => m.File).Select(g => new SourceChange(g.Key, g.Select(m => new LineRange(m.Line, m.Line + m.Original.Count(c => c == '\n'))).ToArray(), true)).ToArray();
    }
    public Task<DiscoveryResult> DiscoverAsync(string root, IReadOnlyList<SourceChange> changes, CancellationToken token) =>
        Task.FromResult(new DiscoveryResult(mutants, 0, 0));
}
