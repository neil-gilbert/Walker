using System.Text.Json;
using System.Xml.Linq;
using System.Globalization;
namespace Walker.Execution;

internal sealed class BuildObservation : IDisposable
{
    private static readonly string[] ReferenceFields = ["FullPath", "BuildReference", "HasSingleTargetFramework", "NearestTargetFramework",
        "Targets", "SetConfiguration", "SetPlatform", "SetTargetFramework", "AdditionalProperties", "GlobalPropertiesToRemove", "UndefineProperties"];
    private static readonly StringComparer Paths = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
    private readonly string directory = Path.Combine(Path.GetTempPath(), "walker-build-" + Guid.NewGuid().ToString("N"));
    public string Argument { get; }
    private BuildObservation(string assembly)
    {
        Directory.CreateDirectory(directory);
        Argument = "-logger:" + assembly + ";" + directory;
    }
    public static BuildObservation? For(string project, string root)
    {
        var assembly = Path.Combine(Path.GetDirectoryName(typeof(DotnetMutationExecutor).Assembly.Location)!, "Walker.BuildLogger.dll");
        if (!File.Exists(assembly)) return null;
        var language = Environment.GetEnvironmentVariable("DOTNET_CLI_UI_LANGUAGE");
        if ((language != null && !language.StartsWith("en", StringComparison.OrdinalIgnoreCase))
            || (CultureInfo.CurrentUICulture.Name.Length != 0 && CultureInfo.CurrentUICulture.TwoLetterISOLanguageName != "en")) return null;
        try
        {
            // This is an opportunity hint, not a correctness decision. Imported/unknown layouts keep queries.
            var document = XDocument.Load(Path.GetFullPath(project, root));
            if (!document.Descendants().Any(e => e.Name.LocalName == "TargetFrameworks" && e.Value.Contains(';'))) return null;
            return new(assembly);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Xml.XmlException or ArgumentException) { return null; }
    }
    public string? Read(string project, string root, string? framework = null)
    {
        try
        {
            if (File.Exists(Path.Combine(directory, "invalid"))) return null;
            string? found = null;
            foreach (var file in Directory.EnumerateFiles(directory, "*.json"))
            {
                if (new FileInfo(file).Length > 1024 * 1024) return null;
                var text = File.ReadAllText(file);
                using var document = JsonDocument.Parse(text);
                var value = document.RootElement;
                if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty("SchemaVersion", out var version) || version.GetInt32() != 1
                    || !value.TryGetProperty("Project", out var path) || path.ValueKind != JsonValueKind.String) return null;
                if (!Paths.Equals(Path.GetFullPath(path.GetString()!, root), Path.GetFullPath(project, root))) continue;
                if (!value.TryGetProperty("Properties", out var properties) || properties.ValueKind != JsonValueKind.Object
                    || !properties.TryGetProperty("TargetFramework", out var target) || target.ValueKind != JsonValueKind.String) return null;
                if (target.GetString() != (framework ?? "")) continue;
                if (new[] { "TargetFrameworks", "Configuration", "Platform", "RuntimeIdentifier", "BuildProjectReferences" }
                    .Any(name => !properties.TryGetProperty(name, out var property) || property.ValueKind != JsonValueKind.String)
                    || !value.TryGetProperty("Items", out var items) || items.ValueKind != JsonValueKind.Object
                    || !items.TryGetProperty("_MSBuildProjectReferenceExistent", out var references) || references.ValueKind != JsonValueKind.Array) return null;
                if (references.EnumerateArray().Any(reference => reference.ValueKind != JsonValueKind.Object
                    || ReferenceFields.Any(name => !reference.TryGetProperty(name, out var field) || field.ValueKind != JsonValueKind.String)
                    || !Path.IsPathFullyQualified(reference.GetProperty("FullPath").GetString()!))) return null;
                // Multiple matching records are ambiguous, even if they came from a customised repeated build.
                if (found != null) return null;
                found = text;
            }
            return found;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or FormatException or ArgumentException) { return null; }
    }
    public void Dispose()
    {
        try { Directory.Delete(directory, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
