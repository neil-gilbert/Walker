using System.Security.Cryptography;
using System.Xml.Linq;
using Walker.Core;

namespace Walker.Execution;

internal sealed class MutationWorkspace : IAsyncDisposable
{
    private readonly IProcessRunner runner;
    private readonly string originalRoot;
    private readonly Dictionary<string, string> hashes = new(StringComparer.Ordinal);
    public string Root { get; } = ScratchRoot();
    private static string ScratchRoot()
    {
        var path = Path.Combine(Path.GetTempPath(), "walker-prepared-" + Guid.NewGuid().ToString("N"));
        return OperatingSystem.IsMacOS() && path.StartsWith("/var/", StringComparison.Ordinal) ? "/private" + path : path;
    }
    private MutationWorkspace(IProcessRunner runner, string originalRoot) { this.runner = runner; this.originalRoot = originalRoot; }
    private static bool Inside(string root, string path) => Path.GetFullPath(path).StartsWith(Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    private static readonly string[] BuildConfigurationFiles = ["Directory.Build.props", "Directory.Build.targets", "Directory.Packages.props", "global.json"];
    public string Map(string path) => Path.Combine(Root, Path.GetRelativePath(originalRoot, Path.GetFullPath(path, originalRoot)));
    internal async Task CopyOriginalAsync(string destination, CancellationToken token)
    {
        foreach (var (file, hash) in hashes)
        {
            var source = Path.Combine(originalRoot, file);
            var bytes = await File.ReadAllBytesAsync(source, token);
            if (new FileInfo(source).LinkTarget != null || Convert.ToHexString(SHA256.HashData(bytes)) != hash)
                throw new InvalidOperationException("Snapshot inputs changed while creating workers.");
            var target = Path.Combine(destination, file);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            await File.WriteAllBytesAsync(target, bytes, token);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(target, File.GetUnixFileMode(source));
        }
    }
    public static async Task<MutationWorkspace?> CreateAsync(IProcessRunner runner, VerificationRequest request, CancellationToken token)
    {
        var workspace = new MutationWorkspace(runner, request.Root);
        try
        {
            var files = await workspace.Inventory(token);
            var copied = files.ToHashSet(StringComparer.Ordinal);
            // A scratch root must inherit exactly the same user build configuration. Parent or
            // ignored configuration would otherwise disappear while an ID-zero probe might still pass.
            foreach (var project in files.Where(file => file.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
                .Concat(request.Tests.Prepend(request.Project)).Select(path => Path.GetFullPath(path, request.Root)).Distinct(StringComparer.Ordinal))
                for (var directory = new DirectoryInfo(Path.GetDirectoryName(Path.GetFullPath(project, request.Root))!); directory != null; directory = directory.Parent)
                    foreach (var name in BuildConfigurationFiles)
                    {
                        var path = Path.Combine(directory.FullName, name);
                        if (File.Exists(path) && (!Inside(request.Root, path) || !copied.Contains(Path.GetRelativePath(request.Root, path).Replace(Path.DirectorySeparatorChar, '/'))))
                            throw new InvalidOperationException("Build configuration cannot be isolated: " + path);
                    }
            Directory.CreateDirectory(workspace.Root);
            foreach (var file in files)
            {
                token.ThrowIfCancellationRequested();
                var source = Path.GetFullPath(file, request.Root);
                if (!Inside(request.Root, source) || !File.Exists(source)) throw new InvalidOperationException("Unknown snapshot input.");
                for (FileSystemInfo? item = new FileInfo(source); item != null && item.FullName != Path.GetFullPath(request.Root); item = item is FileInfo f ? f.Directory : ((DirectoryInfo)item).Parent)
                    if (item.LinkTarget != null) throw new InvalidOperationException("Linked snapshot input.");
                var bytes = await File.ReadAllBytesAsync(source, token);
                if (file.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) || file.EndsWith(".props", StringComparison.OrdinalIgnoreCase) || file.EndsWith(".targets", StringComparison.OrdinalIgnoreCase))
                {
                    var elements = XDocument.Load(new MemoryStream(bytes)).Descendants().ToArray();
                    if (elements.Any(e => e.Attributes().Any(a => a.Name.LocalName == "Condition")))
                        throw new InvalidOperationException("Conditional user build settings cannot be isolated.");
                    if (elements.Any(e => e.Name.LocalName is "Target" or "Import")) throw new InvalidOperationException("Custom snapshot build targets/imports.");
                    if (elements.Any(e => e.Name.LocalName == "ProjectReference" && e.HasElements)) throw new InvalidOperationException("Custom snapshot reference settings.");
                    foreach (var value in elements.SelectMany(e => e.Attributes().Select(a => a.Value)).Concat(elements.Where(e => !e.HasElements).Select(e => e.Value)))
                    {
                        if (Path.IsPathFullyQualified(value.Trim())) throw new InvalidOperationException("Absolute snapshot build input/output.");
                        if (!value.Contains("$(", StringComparison.Ordinal) && value.Contains("..", StringComparison.Ordinal)
                            && !Inside(request.Root, Path.GetFullPath(value.Replace('\\', Path.DirectorySeparatorChar), Path.GetDirectoryName(source)!)))
                            throw new InvalidOperationException("External snapshot build input.");
                    }
                }
                workspace.hashes.Add(file, Convert.ToHexString(SHA256.HashData(bytes)));
                var destination = Path.Combine(workspace.Root, file);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                await File.WriteAllBytesAsync(destination, bytes, token);
                if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(destination, File.GetUnixFileMode(source));
            }
            return workspace;
        }
        catch
        {
            await workspace.DisposeAsync();
            throw;
        }
    }
    private async Task<string[]> Inventory(CancellationToken token)
    {
        var result = await runner.RunAsync(new("git", ["ls-files", "-z", "--cached", "--others", "--exclude-standard"], originalRoot, OutputLimit: 16 * 1024 * 1024), token);
        if (result.ExitCode != 0 || result.OutputTruncated) throw new InvalidOperationException("Could not enumerate snapshot inputs.");
        return result.StandardOutput.Split('\0', StringSplitOptions.RemoveEmptyEntries).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
    }
    public async Task<bool> Unchanged(CancellationToken token)
    {
        var files = await Inventory(token);
        if (!files.SequenceEqual(hashes.Keys.Order(StringComparer.Ordinal))) return false;
        foreach (var file in files)
        {
            var path = Path.Combine(originalRoot, file);
            if (!File.Exists(path) || new FileInfo(path).LinkTarget != null) return false;
            var bytes = await File.ReadAllBytesAsync(path, token);
            if (Convert.ToHexString(SHA256.HashData(bytes)) != hashes[file]) return false;
        }
        return true;
    }
    public ValueTask DisposeAsync()
    {
        try { if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return ValueTask.CompletedTask;
    }
}
