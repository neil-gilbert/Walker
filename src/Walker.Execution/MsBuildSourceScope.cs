using System.Text.Json;
using Walker.Core;
namespace Walker.Execution;
// Evaluates Compile items without building: respects exclusions and linked source files.
public sealed class MsBuildSourceScope(IProcessRunner runner) : IProductionSourceScope
{
    public async Task<IReadOnlySet<string>> GetFilesAsync(VerificationRequest request, CancellationToken cancellationToken)
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
