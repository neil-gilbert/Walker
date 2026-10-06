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
    private async Task<IReadOnlySet<string>> EvaluateAsync(VerificationRequest request, CancellationToken cancellationToken)
    {
        var result = await runner.RunAsync(new("dotnet", ["msbuild", request.Project, "--nologo", "-getItem:Compile"],
            request.Root, OutputLimit: 16 * 1024 * 1024), cancellationToken);
        if (result.ExitCode != 0 || result.OutputTruncated) throw new InvalidOperationException("Could not evaluate production Compile items: " + result.StandardError);
        using var document = JsonDocument.Parse(result.StandardOutput);
        return document.RootElement.GetProperty("Items").GetProperty("Compile").EnumerateArray()
            .Select(item => Path.GetFullPath(item.GetProperty("FullPath").GetString()!))
            .ToHashSet(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    }
}
