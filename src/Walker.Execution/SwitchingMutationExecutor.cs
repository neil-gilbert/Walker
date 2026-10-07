using Walker.Core;
using System.Text;
using System.Text.Json;
using Walker.Roslyn;

namespace Walker.Execution;

public sealed class SwitchingMutationExecutor(IProcessRunner runner, Action<string>? trace = null) : IMutationExecutor, IBaselineVerifier, IBatchMutationPreparer
{
    private readonly DotnetMutationExecutor source = new(runner, captureBaselineIdentities: true);
    public Task VerifyAsync(VerificationRequest request, CancellationToken token) => source.VerifyAsync(request, token);
    public Task<MutationResult> ExecuteAsync(Mutant mutant, VerificationContext context, CancellationToken token) => source.ExecuteAsync(mutant, context, token);
    public async Task<IPreparedMutationSession?> PrepareAsync(VerificationRequest request, IReadOnlyList<Mutant> selected, CancellationToken token)
    {
        MutationWorkspace? workspace = null;
        try
        {
            if (selected.Count == 0 || request.Tests.Any(p => !source.BaselineIdentities.ContainsKey(Path.GetFullPath(p, request.Root)))) return null;
            // Establish the input generation before evaluating compiler context.
            // All later observations must still agree with this snapshot.
            workspace = await MutationWorkspace.CreateAsync(runner, request, token);
            if (workspace == null) return null;
            var compilation = await ProjectCompilation.Read(runner, request, token);
            if (compilation == null) { trace?.Invoke("Switch preparation: unsupported project/compiler context."); return null; }
            var suffix = Guid.NewGuid().ToString("N");
            var environmentName = "WALKER_ACTIVE_" + suffix;
            var instrumented = new MutationInstrumenter().Instrument(compilation, selected, "WalkerGenerated_" + suffix, environmentName, token);
            if (instrumented.ActiveIds.Count == 0) { trace?.Invoke("Switch preparation: no supported numeric boundaries."); return null; }
            // Reject unknown test imports/settings before a scratch build can execute them.
            var ordinaryScopes = new List<PreparedMutationSession.Scope>();
            var testSources = new HashSet<string>(StringComparer.Ordinal);
            foreach (var project in request.Tests)
            {
                // One evaluation supplies both the output-layout guard and the
                // ordinary single-framework DLL metadata. Multi-target projects
                // still need their separate explicit inner-framework evaluations.
                var first = await runner.RunAsync(new("dotnet", ["msbuild", project, "--nologo",
                    "-getProperty:" + string.Join(',', LayoutProperties.Concat(CompiledTestOutput.Query.Split(',')).Distinct()),
                    "-getItem:Compile,Analyzer"], request.Root, OutputLimit: 16 * 1024 * 1024), token);
                var layout = ReadLayout(first, Path.GetFullPath(project, request.Root), request.Root);
                if (layout == null) return null;
                var frameworks = layout["TargetFrameworks"].Length > 0 ? layout["TargetFrameworks"].Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) : new[] { layout["TargetFramework"] };
                foreach (var framework in frameworks)
                {
                    var observed = layout["TargetFrameworks"].Length == 0 ? first : await runner.RunAsync(new("dotnet", ["msbuild", project, "--nologo", "-p:TargetFramework=" + framework,
                        "-getProperty:" + CompiledTestOutput.Query, "-getItem:Compile,Analyzer"], request.Root, OutputLimit: 16 * 1024 * 1024), token);
                    var ordinaryOutput = CompiledTestOutput.Read(observed, project, request.Root, framework);
                    if (ordinaryOutput == null) return null;
                    ordinaryScopes.Add(new(project, framework, ordinaryOutput));
                    using var document = JsonDocument.Parse(observed.StandardOutput);
                    var properties = document.RootElement.GetProperty("Properties");
                    var tools = properties.GetProperty("MSBuildToolsPath").GetString()!;
                    var items = document.RootElement.GetProperty("Items");
                    foreach (var analyzer in items.GetProperty("Analyzer").EnumerateArray())
                    {
                        var path = analyzer.GetProperty("FullPath").GetString()!.Replace('\\', '/');
                        if (!path.StartsWith(tools.Replace('\\', '/') + "/", StringComparison.Ordinal)
                            && !path.Contains("/packs/Microsoft.NETCore.App.Ref/", StringComparison.Ordinal)
                            && !path.Contains("/.nuget/packages/xunit.analyzers/", StringComparison.Ordinal)) return null;
                    }
                    foreach (var item in items.GetProperty("Compile").EnumerateArray())
                    {
                        var path = item.GetProperty("FullPath").GetString()!;
                        if (!Inside(request.Root, path)) return null;
                        testSources.Add(path);
                    }
                }
            }
            trace?.Invoke("Switch preparation: " + instrumented.ActiveIds.Count + " supported boundaries; validating snapshot inputs.");
            foreach (var tree in compilation.SyntaxTrees)
            {
                var copied = workspace.Map(tree.FilePath);
                // SDK-generated globals are regenerated by the scratch build;
                // copied user sources must match the semantic model exactly.
                if (File.Exists(copied) && await File.ReadAllTextAsync(copied, token) != tree.GetText(token).ToString()) return await Abandon(workspace);
            }
            if (!await workspace.Unchanged(token)) return await Abandon(workspace);
            // Validate evaluated output locations BEFORE allowing any scratch graph to build.
            var layouts = new Dictionary<string, string[]>(StringComparer.Ordinal);
            var singleFrameworkProjects = new HashSet<string>(StringComparer.Ordinal);
            foreach (var project in request.Tests.Prepend(request.Project).Distinct(StringComparer.Ordinal))
            {
                var mapped = workspace.Map(project);
                var outer = await Layout(mapped, workspace.Root, null, token);
                if (outer == null) { trace?.Invoke("Switch preparation: unknown/unsafe output layout: " + project); return await Abandon(workspace); }
                var frameworks = outer["TargetFrameworks"].Length > 0 ? outer["TargetFrameworks"].Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) : new[] { outer["TargetFramework"] };
                if (frameworks.Length == 0 || frameworks.Any(f => f.Length == 0)) return await Abandon(workspace);
                foreach (var framework in frameworks)
                    if (await Layout(mapped, workspace.Root, framework, token) == null) { trace?.Invoke("Switch preparation: unknown/unsafe framework layout."); return await Abandon(workspace); }
                layouts[project] = frameworks;
                if (outer["TargetFrameworks"].Length == 0) singleFrameworkProjects.Add(project);
            }
            foreach (var (file, rewritten) in instrumented.Sources)
            {
                var path = workspace.Map(file);
                var original = await File.ReadAllBytesAsync(path, token);
                using var reader = new StreamReader(new MemoryStream(original), Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
                await reader.ReadToEndAsync(token);
                var encoding = reader.CurrentEncoding;
                var bytes = encoding.GetBytes(rewritten);
                var preamble = encoding.GetPreamble();
                if (preamble.Length > 0 && original.AsSpan().StartsWith(preamble)) bytes = [..preamble, ..bytes];
                await File.WriteAllBytesAsync(path, bytes, token);
            }
            var runtimeFile = Path.Combine(Path.GetDirectoryName(workspace.Map(request.Project))!, "WalkerRuntime_" + suffix + ".cs");
            await File.WriteAllTextAsync(runtimeFile, instrumented.RuntimeSource, token);
            var builtOutputs = new Dictionary<string, CompiledTestOutput>(StringComparer.Ordinal);
            foreach (var project in request.Tests)
            {
                List<string> args = ["build", workspace.Map(project), "--nologo", "--verbosity", "quiet", "-p:RunAnalyzers=false"];
                if (singleFrameworkProjects.Contains(project)) args.AddRange(["-target:Build", "-getProperty:" + CompiledTestOutput.Query]);
                var build = await runner.RunAsync(new("dotnet", args, workspace.Root, OutputLimit: 1024 * 1024), token);
                if (build.ExitCode != 0) { trace?.Invoke("Switch preparation build rejected: " + build.StandardError + build.StandardOutput); return await Abandon(workspace); }
                if (singleFrameworkProjects.Contains(project) && CompiledTestOutput.Read(build, workspace.Map(project), workspace.Root, layouts[project][0]) is { } output)
                    builtOutputs.Add(project, output);
            }
            var scopes = new List<PreparedMutationSession.Scope>();
            var coverage = new List<MutationCoverage>();
            var pureMethods = selected.Where(m => instrumented.ActiveIds.TryGetValue(m.Id, out var id) && instrumented.BatchableIds.Contains(id))
                .SelectMany(m => new[] { m.Member, string.Join(".", m.Member.Split('.').TakeLast(2)) }).ToHashSet(StringComparer.Ordinal);
            var collectCoverage = instrumented.BatchableIds.Count > 1 && !request.ConfirmKills;
            if (collectCoverage)
            {
                var copiedTests = new List<string>();
                foreach (var path in testSources)
                {
                    var copied = workspace.Map(path);
                    if (!File.Exists(copied)) { collectCoverage = false; break; }
                    copiedTests.Add(await File.ReadAllTextAsync(copied, token));
                }
                collectCoverage &= TestBatchSafety.Allows(copiedTests, pureMethods);
            }
            foreach (var project in request.Tests)
            {
                var identities = new List<string>();
                foreach (var framework in layouts[project])
                {
                    if (!builtOutputs.TryGetValue(project, out var output))
                    {
                        var query = await runner.RunAsync(new("dotnet", ["msbuild", workspace.Map(project), "--nologo", "-p:TargetFramework=" + framework,
                            "-p:RunAnalyzers=false", "-getProperty:" + CompiledTestOutput.Query], workspace.Root, OutputLimit: 1024 * 1024), token);
                        output = CompiledTestOutput.Read(query, workspace.Map(project), workspace.Root, framework);
                    }
                    if (output == null || !Inside(workspace.Root, output.Assembly) || scopes.Any(s => s.Output.Assembly == output.Assembly)) { trace?.Invoke("Switch preparation: unsupported/ambiguous compiled output."); return await Abandon(workspace); }
                    using var capture = collectCoverage ? new CoverageCapture(environmentName, "0") : null;
                    var original = await source.RunAssembly(output, project, request.Root, request.Filter, framework,
                        capture?.Environment ?? new Dictionary<string, string?> { [environmentName] = "0" }, token, captureIdentity: true, settings: capture?.Settings);
                    if (capture != null && MutationCoverage.Read(capture.Report, original.Cases) is { } observedCoverage) coverage.Add(observedCoverage);
                    if (original.Outcome != MutationOutcome.Survived || original.Identities == null) { trace?.Invoke("Switch preparation baseline rejected: " + original.Detail); return await Abandon(workspace); }
                    identities.AddRange(original.Identities);
                    scopes.Add(new(project, framework, output));
                }
                if (!identities.Order(StringComparer.Ordinal).SequenceEqual(source.BaselineIdentities[Path.GetFullPath(project, request.Root)].Order(StringComparer.Ordinal)))
                    return await Abandon(workspace);
            }
            if (!await workspace.Unchanged(token)) return await Abandon(workspace);
            var session = new PreparedMutationSession(source, workspace, request, instrumented.ActiveIds, environmentName, scopes);
            var groups = MutationCoverage.Pack(selected, instrumented.ActiveIds, instrumented.BatchableIds, coverage.Count == scopes.Count ? coverage : null);
            if (coverage.Count == scopes.Count && groups.Any(b => b.Length > 1)
                && (request.Workers == 1 || groups.Count(g => g.All(m => instrumented.ActiveIds.ContainsKey(m.Id))) <= (instrumented.ActiveIds.Count + 1) / 2))
            {
                session.Coverage = coverage;
                session.BatchableIds = instrumented.BatchableIds;
                trace?.Invoke("Switch preparation: complete per-test coverage; using disjoint coverage batches on one fresh host per batch.");
                workspace = null;
                return session;
            }
            var preparedSession = await MutationWorkerPool.CreateAsync(session, runner, workspace, request,
                instrumented.ActiveIds, environmentName, scopes, ordinaryScopes, source, trace, token);
            workspace = null; // The returned session owns cleanup.
            return preparedSession;
        }
        catch (Exception ex) when (!token.IsCancellationRequested && ex is IOException or InvalidOperationException or JsonException or ArgumentException or System.Xml.XmlException or BadImageFormatException)
        { trace?.Invoke("Switch preparation fallback: " + ex.Message); return null; }
        finally { if (workspace != null) await workspace.DisposeAsync(); }
    }
    private static async Task<IPreparedMutationSession?> Abandon(MutationWorkspace workspace) { await workspace.DisposeAsync(); return null; }
    private static bool Inside(string root, string path) => Path.GetFullPath(path).StartsWith(Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    private static readonly string[] LayoutProperties = ["TargetFramework", "TargetFrameworks", "TargetPath", "OutputPath", "BaseIntermediateOutputPath", "MSBuildProjectExtensionsPath", "IntermediateOutputPath"];
    private async Task<Dictionary<string, string>?> Layout(string project, string root, string? framework, CancellationToken token)
    {
        List<string> args = ["msbuild", project, "--nologo", "-getProperty:" + string.Join(',', LayoutProperties)];
        if (framework != null) args.Add("-p:TargetFramework=" + framework);
        var query = await runner.RunAsync(new("dotnet", args, root, OutputLimit: 1024 * 1024), token);
        return ReadLayout(query, project, root);
    }
    private static Dictionary<string, string>? ReadLayout(ProcessResult query, string project, string root)
    {
        if (query.ExitCode != 0 || query.OutputTruncated) return null;
        using var json = JsonDocument.Parse(query.StandardOutput);
        if (!json.RootElement.TryGetProperty("Properties", out var evaluated) || evaluated.ValueKind != JsonValueKind.Object
            || LayoutProperties.Any(name => !evaluated.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String)) return null;
        var properties = LayoutProperties.ToDictionary(name => name, name => evaluated.GetProperty(name).GetString()!, StringComparer.Ordinal);
        foreach (var name in new[] { "TargetPath", "OutputPath", "BaseIntermediateOutputPath", "MSBuildProjectExtensionsPath", "IntermediateOutputPath" })
        {
            var value = properties[name];
            if (value.Length > 0 && (value.Contains("$(", StringComparison.Ordinal) || !Inside(root, Path.GetFullPath(value.Replace('\\', Path.DirectorySeparatorChar), Path.GetDirectoryName(project)!)))) return null;
        }
        return properties;
    }
}
