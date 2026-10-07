using System.Text.Json;
namespace Walker.Execution;

// Observe actual baseline-build metadata. Unknown/customized layouts use the original
// explicit production build. Inner-build metadata also includes SDK-resolved transitive references.
internal sealed class BuildCoverage
{
    private static readonly StringComparer Paths = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
    private readonly Dictionary<string, HashSet<string?>> coveringTests = new(Paths);
    private readonly string root;
    private readonly string productionProject;
    private readonly Settings? production;

    public BuildCoverage(string root, string productionProject, string metadata)
    {
        this.root = root;
        this.productionProject = Path.GetFullPath(productionProject, root);
        using var document = Parse(metadata);
        production = document == null ? null : ReadSettings(document.RootElement);
    }
    public bool Covers(string project, string requestedRoot, string requestedProduction, string? framework = null) =>
        Paths.Equals(root, requestedRoot) && Paths.Equals(productionProject, Path.GetFullPath(requestedProduction, requestedRoot))
        && coveringTests.TryGetValue(Path.GetFullPath(project, requestedRoot), out var frameworks) && frameworks.Contains(framework);

    public void ObserveTestBuild(string project, string metadata, string? framework = null)
    {
        if (production == null) return;
        using var document = Parse(metadata);
        if (document == null) return;
        var settings = ReadSettings(document.RootElement, framework);
        if (settings == null || !settings.BuildReferences || settings.Configuration != production.Configuration
            || settings.Platform != production.Platform) return;
        if (!document.RootElement.TryGetProperty("Items", out var items) || items.ValueKind != JsonValueKind.Object
            || !items.TryGetProperty("_MSBuildProjectReferenceExistent", out var references) || references.ValueKind != JsonValueKind.Array) return;
        foreach (var reference in references.EnumerateArray())
        {
            if (reference.ValueKind != JsonValueKind.Object) continue;
            var path = Value(reference, "FullPath");
            if (path.Length == 0 || !Paths.Equals(Path.GetFullPath(path, root), productionProject)) continue;
            if (Value(reference, "BuildReference") != "true" || Value(reference, "HasSingleTargetFramework") != "true"
                || Value(reference, "NearestTargetFramework") != production.Framework) continue;
            // Do not infer coverage across reference-specific build/property overrides.
            if (new[] { "Targets", "SetConfiguration", "SetPlatform", "SetTargetFramework", "AdditionalProperties", "GlobalPropertiesToRemove" }
                .Any(name => Value(reference, name).Length != 0)) continue;
            if (Value(reference, "UndefineProperties").Split(';', StringSplitOptions.RemoveEmptyEntries)
                .Any(name => name is not ("TargetFramework" or "RuntimeIdentifier" or "SelfContained"))) continue;
            var pathKey = Path.GetFullPath(project, root);
            if (!coveringTests.TryGetValue(pathKey, out var frameworks)) coveringTests[pathKey] = frameworks = [];
            frameworks.Add(framework);
        }
    }
    // Metadata is optional; anything that is not a JSON object falls back to explicit builds.
    private static JsonDocument? Parse(string metadata)
    {
        try
        {
            var document = JsonDocument.Parse(metadata);
            if (document.RootElement.ValueKind == JsonValueKind.Object) return document;
            document.Dispose();
        }
        catch (JsonException) { }
        return null;
    }
    private static Settings? ReadSettings(JsonElement root, string? selectedFramework = null)
    {
        if (!root.TryGetProperty("Properties", out var properties) || properties.ValueKind != JsonValueKind.Object) return null;
        if (new[] { "TargetFramework", "TargetFrameworks", "Configuration", "Platform", "RuntimeIdentifier", "BuildProjectReferences" }
            .Any(name => !properties.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String)) return null;
        var framework = Value(properties, "TargetFramework");
        var configuration = Value(properties, "Configuration");
        var platform = Value(properties, "Platform");
        if (framework.Length == 0 || configuration.Length == 0 || platform.Length == 0
            || Value(properties, "RuntimeIdentifier").Length != 0) return null;
        var frameworks = Value(properties, "TargetFrameworks");
        if (selectedFramework == null ? frameworks.Length != 0 : framework != selectedFramework
            || (frameworks.Length != 0 && !frameworks.Split(';', StringSplitOptions.TrimEntries).Contains(selectedFramework))) return null;
        return new(framework, configuration, platform, Value(properties, "BuildProjectReferences") == "true");
    }
    private static string Value(JsonElement element, string name) => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString()! : "";
    private sealed record Settings(string Framework, string Configuration, string Platform, bool BuildReferences);
}
