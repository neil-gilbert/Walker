using System.Collections;
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using Microsoft.Build.Framework;
namespace Walker.BuildLogger;

// Observe the SDK's actual reference-build task inputs without replacing project imports.
// Unsupported events or observer failures leave the executor's ordinary metadata queries intact.
public sealed class BuildObservationLogger : ILogger
{
    private static readonly string[] Settings = ["TargetFramework", "TargetFrameworks", "Configuration", "Platform", "RuntimeIdentifier", "BuildProjectReferences"];
    private static readonly string[] ReferenceSettings = ["FullPath", "BuildReference", "HasSingleTargetFramework", "NearestTargetFramework",
        "Targets", "SetConfiguration", "SetPlatform", "SetTargetFramework", "AdditionalProperties", "GlobalPropertiesToRemove", "UndefineProperties"];
    private readonly Dictionary<(int Node, int Evaluation), Dictionary<string, string>> evaluations = new();
    private readonly Dictionary<(int Node, int Project), Observation> projects = new();
    private readonly Dictionary<(int Node, int Project, int Target), string> targets = new();
    private readonly Dictionary<Type, (PropertyInfo? Name, PropertyInfo? Value)> propertyAccess = new();
    private bool failed;
    public LoggerVerbosity Verbosity { get; set; } = LoggerVerbosity.Diagnostic;
    public string? Parameters { get; set; } = "";

    public void Initialize(IEventSource eventSource)
    {
        // Target property assignments currently arrive as diagnostic messages. Restrict capture to
        // English/invariant MSBuild messages so localisation cannot bypass that invalidation check.
        if (eventSource is not IEventSource4 source || (CultureInfo.CurrentUICulture.Name.Length != 0
            && CultureInfo.CurrentUICulture.TwoLetterISOLanguageName != "en")) { failed = true; return; }
        source.IncludeTaskInputs();
        source.IncludeEvaluationPropertiesAndItems();
        source.AnyEventRaised += OnEvent;
    }

    private void OnEvent(object sender, BuildEventArgs args)
    {
        if (failed || args.BuildEventContext is not { } context) return;
        try
        {
            var key = (context.NodeId, context.ProjectContextId);
            switch (args)
            {
                case ProjectEvaluationFinishedEventArgs evaluation:
                    var properties = Settings.ToDictionary(name => name, _ => "", StringComparer.OrdinalIgnoreCase);
                    foreach (var property in evaluation.Properties ?? Array.Empty<object>())
                    {
                        string? name, value;
                        if (property is DictionaryEntry entry) { name = entry.Key.ToString(); value = entry.Value?.ToString(); }
                        else
                        {
                            var type = property.GetType();
                            if (!propertyAccess.TryGetValue(type, out var access))
                                propertyAccess[type] = access = (type.GetProperty("Name"), type.GetProperty("EvaluatedValue"));
                            name = access.Name?.GetValue(property)?.ToString();
                            value = access.Value?.GetValue(property)?.ToString();
                        }
                        if (name != null && properties.ContainsKey(name)) properties[name] = value ?? "";
                    }
                    evaluations[(context.NodeId, context.EvaluationId)] = properties;
                    break;
                case ProjectStartedEventArgs started when evaluations.TryGetValue((context.NodeId, context.EvaluationId), out var settings):
                    if (started.ProjectFile != null) projects[key] = new(started.ProjectFile, new(settings, StringComparer.OrdinalIgnoreCase));
                    break;
                case TargetStartedEventArgs target:
                    targets[(context.NodeId, context.ProjectContextId, context.TargetId)] = target.TargetName;
                    break;
                case TaskParameterEventArgs parameter when projects.TryGetValue(key, out var project):
                    if (parameter.Kind == TaskParameterMessageKind.TaskOutput && parameter.PropertyName is { } assigned && Settings.Contains(assigned, StringComparer.OrdinalIgnoreCase))
                        project.Invalid = true;
                    if (parameter.Kind != TaskParameterMessageKind.TaskInput || parameter.ParameterName != "Projects" || !parameter.LogItemMetadata
                        || !targets.TryGetValue((context.NodeId, context.ProjectContextId, context.TargetId), out var targetName) || targetName != "ResolveProjectReferences") break;
                    project.ReferencesObserved = true;
                    foreach (var item in parameter.Items)
                    {
                        if (item is not ITaskItem reference) { project.Invalid = true; continue; }
                        var metadata = ReferenceSettings.ToDictionary(name => name, name => reference.GetMetadata(name) ?? "", StringComparer.Ordinal);
                        // Forwarded worker-node events omit built-in metadata. Resolve ItemSpec against the
                        // owning project exactly as MSBuild's FullPath metadata does (including Windows separators).
                        if (metadata["FullPath"].Length == 0 && reference.ItemSpec.Length != 0)
                            metadata["FullPath"] = System.IO.Path.GetFullPath(reference.ItemSpec.Replace('\\', System.IO.Path.DirectorySeparatorChar), System.IO.Path.GetDirectoryName(project.Path)!);
                        var referencePath = metadata["FullPath"];
                        if (referencePath.Length == 0) { project.Invalid = true; continue; }
                        if (project.References.TryGetValue(referencePath, out var previous) && metadata.Any(p => previous[p.Key] != p.Value)) project.Invalid = true;
                        project.References[referencePath] = metadata;
                    }
                    break;
                case TargetFinishedEventArgs target when target.TargetName == "Build" && target.Succeeded && projects.TryGetValue(key, out var project):
                    project.Built = true;
                    break;
                case ProjectFinishedEventArgs finished when projects.Remove(key, out var project):
                    if (!finished.Succeeded || !project.Built || project.Invalid) break;
                    // Outer builds expose the framework list; only observed reference inputs prove an inner graph.
                    if (project.Properties["TargetFramework"].Length != 0 && !project.ReferencesObserved) break;
                    var path = Path.Combine(Parameters ?? "", Guid.NewGuid().ToString("N") + ".json");
                    var temporary = path + ".tmp";
                    File.WriteAllText(temporary, JsonSerializer.Serialize(new { SchemaVersion = 1, Project = project.Path,
                        Properties = project.Properties, Items = new { _MSBuildProjectReferenceExistent = project.References.Values.ToArray() } }));
                    File.Move(temporary, path);
                    break;
                case BuildMessageEventArgs message when projects.TryGetValue(key, out var project) && message.Message is { } text && text.StartsWith("Set Property:", StringComparison.Ordinal):
                    var assignment = text["Set Property:".Length..].TrimStart();
                    var equal = assignment.IndexOf('=');
                    if (equal > 0 && Settings.Contains(assignment[..equal].Trim(), StringComparer.OrdinalIgnoreCase)) project.Invalid = true;
                    break;
            }
        }
        catch (Exception)
        {
            // Observation is optional and must not fail an otherwise valid production build.
            failed = true;
            try { File.WriteAllText(Path.Combine(Parameters ?? "", "invalid"), "Observation failed."); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { }
        }
    }

    public void Shutdown() { }
    private sealed class Observation(string path, Dictionary<string, string> properties)
    {
        public string Path { get; } = path;
        public Dictionary<string, string> Properties { get; } = properties;
        public Dictionary<string, Dictionary<string, string>> References { get; } = new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        public bool Built, Invalid, ReferencesObserved;
    }
}
