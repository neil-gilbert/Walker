using System.Xml.Linq;
using Walker.Core;

namespace Walker.Execution;

// A conservative policy for conventional SDK projects, not a sandbox for arbitrary build logic.
internal static class IsolationInputPolicy
{
    internal static readonly StringComparer PathComparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
    private static readonly string[] ConfigurationFiles = ["Directory.Build.props", "Directory.Build.targets", "Directory.Packages.props", "global.json", "NuGet.Config"];
    private static readonly HashSet<string> OutputProperties = new(StringComparer.OrdinalIgnoreCase)
    {
        "OutputPath", "OutDir", "BaseOutputPath", "IntermediateOutputPath", "BaseIntermediateOutputPath",
        "MSBuildProjectExtensionsPath", "ArtifactsPath", "UseArtifactsOutput", "PublishDir", "PublishUrl", "RestorePackagesPath"
    };
    internal static string Normalize(string path)
    {
        path = Path.GetFullPath(path);
        if (OperatingSystem.IsMacOS())
        {
            if (path.StartsWith("/var/", StringComparison.Ordinal) || path.StartsWith("/tmp/", StringComparison.Ordinal)) path = "/private" + path;
        }
        return Path.TrimEndingDirectorySeparator(path);
    }
    internal static bool Inside(string root, string path) => Normalize(path).StartsWith(Normalize(root) + Path.DirectorySeparatorChar,
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    internal static VerificationException Unsupported(string message) => new("isolation_unsupported", "isolation", message);

    internal static void CheckLinks(string root, string path)
    {
        if (!Inside(root, path)) throw Unsupported("Snapshot input is outside the repository: " + path);
        for (FileSystemInfo? item = new FileInfo(path); item != null; item = item is FileInfo file ? file.Directory : ((DirectoryInfo)item).Parent)
        {
            if (item.LinkTarget != null) throw Unsupported("Linked snapshot input: " + item.FullName);
            if (PathComparer.Equals(Normalize(item.FullName), Normalize(root))) break;
        }
    }

    internal static void Validate(string root, IReadOnlySet<string> files, IReadOnlyList<string> projects)
    {
        foreach (var project in projects)
        {
            if (!Inside(root, project)) throw Unsupported("Project is outside the repository: " + project);
            if (!files.Contains(Relative(root, project))) throw Unsupported("Project is not a captured build input: " + project);
            // Parent imports and SDK selection must not disappear when moving the working directory.
            for (var directory = new DirectoryInfo(Path.GetDirectoryName(project)!); directory != null; directory = directory.Parent)
                foreach (var name in ConfigurationFiles)
                {
                    var found = directory.Exists ? directory.EnumerateFiles().FirstOrDefault(file => file.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) : null;
                    if (found != null && (!Inside(root, found.FullName) || !files.Contains(Relative(root, found.FullName))))
                        throw Unsupported("Parent or ignored build configuration cannot be isolated: " + found.FullName);
                }
        }
        foreach (var file in files)
        {
            var path = Path.GetFullPath(file, root);
            CheckLinks(root, path);
            if (!File.Exists(path)) continue; // Tracked deletions are part of the snapshot.
            if (file.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) || file.EndsWith(".props", StringComparison.OrdinalIgnoreCase)
                || file.EndsWith(".targets", StringComparison.OrdinalIgnoreCase)) ValidateProject(root, files, path);
            if (Path.GetFileName(file).Equals("NuGet.Config", StringComparison.OrdinalIgnoreCase)) ValidateNuget(path);
        }
    }

    private static void ValidateProject(string root, IReadOnlySet<string> files, string path)
    {
        XDocument document;
        try { document = XDocument.Load(path); }
        catch (System.Xml.XmlException ex) { throw Unsupported("Unreadable build input: " + path + ": " + ex.Message); }
        var elements = document.Descendants().ToArray();
        var sdk = document.Root?.Attribute("Sdk")?.Value;
        if (sdk != null && sdk is not ("Microsoft.NET.Sdk" or "Microsoft.NET.Sdk.Web" or "Microsoft.NET.Sdk.Worker" or "Microsoft.NET.Sdk.Razor" or "Microsoft.NET.Sdk.WindowsDesktop"))
            throw Unsupported("Custom project SDK cannot be isolated: " + path);
        if (elements.Any(e => e.Name.LocalName is "Target" or "Import" or "UsingTask" or "Sdk"
            || e.Attributes().Any(a => a.Name.LocalName == "Condition")))
            throw Unsupported("Custom targets, imports, SDKs, or conditional build settings cannot be isolated: " + path);
        if (elements.Any(e => OutputProperties.Contains(e.Name.LocalName)))
            throw Unsupported("Custom build output or package paths cannot be isolated: " + path);
        foreach (var element in elements)
        {
            if (element.Name.LocalName == "ProjectReference" && element.HasElements)
                throw Unsupported("Custom project reference settings cannot be isolated: " + path);
            foreach (var value in element.Attributes().Select(a => a.Value).Concat(element.HasElements ? [] : new[] { element.Value }))
            {
                if (value.Contains("$(", StringComparison.Ordinal) || value.Contains("@(", StringComparison.Ordinal) || value.Contains("%(", StringComparison.Ordinal))
                    throw Unsupported("Unevaluated build expressions cannot be isolated: " + path);
                foreach (var item in value.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
                {
                    if (Path.IsPathFullyQualified(item)) throw Unsupported("Absolute build input/output cannot be isolated: " + path);
                    if (item.Contains("..", StringComparison.Ordinal) && !Inside(root, Path.GetFullPath(item.Replace('\\', Path.DirectorySeparatorChar), Path.GetDirectoryName(path)!)))
                        throw Unsupported("External build input cannot be isolated: " + path);
                }
            }
            foreach (var attribute in element.Attributes().Where(a => a.Name.LocalName is "Include" or "Update"))
                foreach (var item in attribute.Value.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
                {
                    if (element.Name.LocalName == "PackageReference") continue;
                    if (item.IndexOfAny(['*', '?']) >= 0)
                        throw Unsupported("Explicit wildcard build inputs cannot be proven independent of ignored files: " + path);
                    var input = Path.GetFullPath(item.Replace('\\', Path.DirectorySeparatorChar), Path.GetDirectoryName(path)!);
                    if (!Inside(root, input)) throw Unsupported("External item cannot be isolated: " + input);
                    if (File.Exists(input) && !files.Contains(Relative(root, input)))
                        throw Unsupported("Ignored required build input cannot be isolated: " + input);
                }
        }
    }

    private static void ValidateNuget(string path)
    {
        var document = XDocument.Load(path);
        if (document.Descendants("add").Any(e => e.Attribute("key")?.Value is "globalPackagesFolder" or "repositoryPath"
            || (e.Ancestors("packageSources").Any() && e.Attribute("value") is { } value
                && (!Uri.TryCreate(value.Value, UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http")))))
            throw Unsupported("Custom NuGet cache paths or local feeds cannot be isolated: " + path);
    }

    internal static string Relative(string root, string path) => Path.GetRelativePath(Normalize(root), Normalize(path)).Replace(Path.DirectorySeparatorChar, '/');
}
