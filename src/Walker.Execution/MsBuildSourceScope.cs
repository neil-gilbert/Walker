using System.Collections.Concurrent;
using System.Text.Json;
using Walker.Core;
namespace Walker.Execution;
// Evaluates Compile items without building: respects exclusions and linked source files.
// Results are cached per project so Git filtering and Roslyn context share one evaluation.
public sealed class MsBuildSourceScope(IProcessRunner runner) : IProductionSourceScope
{
    private readonly ConcurrentDictionary<(string Root, string Project), Lazy<Task<IReadOnlySet<string>>>> cache = new();
    public async Task<IReadOnlySet<string>> GetFilesAsync(VerificationRequest request, CancellationToken cancellationToken)
    {
        var key = (request.Root, Path.GetFullPath(request.Project, request.Root));
        var entry = cache.GetOrAdd(key, _ => new(() => EvaluateAsync(request, cancellationToken)));
        try { return await entry.Value; }
        catch { cache.TryRemove(new(key, entry)); throw; }
    }
    // Optional paired/challenge workflows must include Compile items from every inner build.
    // Keep ordinary diff evaluation unchanged so its baseline and benchmark costs stay stable.
    public async Task<IReadOnlySet<string>> GetAllFrameworkFilesAsync(VerificationRequest request, CancellationToken token)
    {
        async Task<JsonDocument> Evaluate(string? framework)
        {
            var result = await runner.RunAsync(new("dotnet", ["msbuild", request.Project, "--nologo",
                "-getProperty:TargetFramework,TargetFrameworks", "-getItem:Compile", ..(framework == null ? Array.Empty<string>() : new[] { "-p:TargetFramework=" + framework })],
                request.Root, OutputLimit: 16 * 1024 * 1024), token);
            if (result.ExitCode != 0 || result.OutputTruncated)
                throw new VerificationException("project_evaluation_failed", "workflow", "Could not evaluate workflow source scope: " + result.StandardError);
            return JsonDocument.Parse(result.StandardOutput);
        }
        try
        {
            using var outer = await Evaluate(null);
            var frameworks = (outer.RootElement.GetProperty("Properties").GetProperty("TargetFrameworks").GetString() ?? "")
                .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.Ordinal).ToArray();
            var sources = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
            void Collect(JsonDocument document) => sources.UnionWith(document.RootElement.GetProperty("Items").GetProperty("Compile").EnumerateArray()
                .Select(item => Path.GetFullPath(item.GetProperty("FullPath").GetString()!)));
            if (frameworks.Length == 0) Collect(outer);
            else foreach (var framework in frameworks)
            {
                using var inner = await Evaluate(framework);
                Collect(inner);
            }
            return sources;
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or ArgumentException && ex is not VerificationException)
        { throw new VerificationException("project_evaluation_failed", "workflow", "Could not read workflow Compile items: " + ex.Message, ex); }
    }
    private async Task<IReadOnlySet<string>> EvaluateAsync(VerificationRequest request, CancellationToken cancellationToken)
    {
        var result = await runner.RunAsync(new("dotnet", ["msbuild", request.Project, "--nologo", "-getItem:Compile"],
            request.Root, OutputLimit: 16 * 1024 * 1024), cancellationToken);
        if (result.ExitCode != 0 || result.OutputTruncated) throw new VerificationException("project_evaluation_failed", "discovery", "Could not evaluate production Compile items: " + result.StandardError);
        try
        {
            using var document = JsonDocument.Parse(result.StandardOutput);
            return document.RootElement.GetProperty("Items").GetProperty("Compile").EnumerateArray()
                .Select(item => Path.GetFullPath(item.GetProperty("FullPath").GetString()!))
                .ToHashSet(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or ArgumentException)
        { throw new VerificationException("project_evaluation_failed", "discovery", "Could not read production Compile items: " + ex.Message, ex); }
    }
}
