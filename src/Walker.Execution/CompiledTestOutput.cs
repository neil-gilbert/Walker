using System.Security.Cryptography;
using System.Text.Json;
using System.Xml.Linq;
using Walker.Core;

namespace Walker.Execution;

// An evaluation is useful only after a successful project build. Keep this separate from
// BuildCoverage: knowing an output exists does not prove that production was rebuilt.
internal sealed record CompiledTestOutput(string Assembly, string Framework, string Moniker, string Identity)
{
    private static readonly string[] UnsupportedOptions = [
        "RuntimeIdentifier", "VSTestSetting", "RunSettingsFilePath", "VSTestTestAdapterPath",
        "VSTestCLIRunSettings", "VSTestCollect", "VSTestBlame", "VSTestBlameCrash",
        "VSTestBlameHang", "VSTestDiag", "VSTestLogger", "VSTestTestCaseFilter",
        "VSTestResultsDirectory", "VSTestListTests", "VSTestTraceDataCollectorDirectoryPath",
        "VSTestBlameCrashDumpType", "VSTestBlameCrashCollectAlways", "VSTestBlameHangDumpType",
        "VSTestBlameHangTimeout", "VSTestNoLogo", "VSTestArtifactsProcessingMode", "VSTestSessionCorrelationId",
        "_Xunit_ImportPropsFile", "_Xunit_ImportTargetsFile"
    ];
    private static readonly string[] ImportHooks = ["CustomBeforeMicrosoftCommonProps", "CustomAfterMicrosoftCommonProps",
        "CustomBeforeMicrosoftCommonTargets", "CustomAfterMicrosoftCommonTargets",
        "CustomBeforeMicrosoftCommonCrossTargetingTargets", "CustomAfterMicrosoftCommonCrossTargetingTargets"];
    public static string Query => string.Join(',', new[] {
        "TargetPath", "TargetFramework", "TargetFrameworks", "TargetFrameworkMoniker", "IsTestProject",
        "Configuration", "Platform", "PlatformTarget", "MSBuildAllProjects", "MSBuildToolsPath", "MSBuildProjectExtensionsPath",
        "VSTestConsolePath", "VSTestTaskAssemblyFile", "TestingPlatformDotnetTestSupport", "IsTestingPlatformApplication"
    }.Concat(UnsupportedOptions).Concat(ImportHooks));

    public static CompiledTestOutput? Read(ProcessResult result, string project, string root, string? framework)
    {
        if (result.ExitCode != 0 || result.OutputTruncated) return null;
        try
        {
            using var json = JsonDocument.Parse(result.StandardOutput);
            var properties = json.RootElement.GetProperty("Properties");
            string Get(string name) => properties.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString()! : "";
            // Missing option fields mean the observation is incomplete, rather than empty defaults.
            if (Query.Split(',').Any(name => !properties.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String)
                || UnsupportedOptions.Any(name => Get(name).Length != 0)
                || !Get("IsTestProject").Equals("true", StringComparison.OrdinalIgnoreCase)
                || Get("TargetFramework").Length == 0 || (framework != null && Get("TargetFramework") != framework)
                || Get("TestingPlatformDotnetTestSupport").Equals("true", StringComparison.OrdinalIgnoreCase)
                || Get("IsTestingPlatformApplication").Equals("true", StringComparison.OrdinalIgnoreCase)
                || Get("PlatformTarget") is not ("" or "AnyCPU" or "AnyCpu")
                || Get("Platform") is not ("" or "AnyCPU")
                || !Get("TargetFrameworkMoniker").StartsWith(".NETCoreApp,Version=", StringComparison.Ordinal)) return null;
            // Common.targets defines conventional hook paths even when they do not exist.
            if (ImportHooks.Any(name => Get(name).Length > 0 && File.Exists(Path.GetFullPath(Get(name).Replace('\\', Path.DirectorySeparatorChar), root)))) return null;
            var console = Get("VSTestConsolePath");
            if (console.Length > 0 && Path.GetFullPath(console) != Path.Combine(Get("MSBuildToolsPath"), "vstest.console.dll")) return null;
            if (Get("VSTestTaskAssemblyFile") is not ("" or "Microsoft.TestPlatform.Build.dll")) return null;
            var assembly = Get("TargetPath");
            if (!Path.IsPathFullyQualified(assembly) || !assembly.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
                || !File.Exists(assembly) || !File.Exists(Path.ChangeExtension(assembly, ".deps.json"))
                || !File.Exists(Path.ChangeExtension(assembly, ".runtimeconfig.json"))) return null;
            var extraInputs = new List<string> { Path.GetFullPath(project, root) };
            for (var directory = new DirectoryInfo(Path.GetDirectoryName(Path.GetFullPath(project, root))!); directory != null; directory = directory.Parent)
                foreach (var name in new[] { "Directory.Build.props", "Directory.Build.targets" })
                {
                    var path = Path.Combine(directory.FullName, name);
                    if (File.Exists(path)) extraInputs.Add(path);
                }
            if (Get("MSBuildProjectExtensionsPath").Length > 0)
                foreach (var suffix in new[] { ".nuget.g.props", ".nuget.g.targets" })
                {
                    var path = Path.Combine(Path.GetFullPath(Get("MSBuildProjectExtensionsPath"), root), Path.GetFileName(project) + suffix);
                    if (File.Exists(path)) extraInputs.Add(path);
                }
            var inputs = Get("MSBuildAllProjects").Split(';', StringSplitOptions.RemoveEmptyEntries).Concat(extraInputs)
                .Select(p => Path.GetFullPath(p, root)).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
            if (inputs.Length == 0 || inputs.Any(p => !File.Exists(p))) return null;
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            foreach (var path in inputs)
            {
                // SDK and the standard VSTest/xUnit imports own their build/test targets. User
                // targets can rewrite outputs or change VSTest parameters after evaluation.
                var standard = Get("MSBuildToolsPath").Length > 0 && path.StartsWith(Get("MSBuildToolsPath") + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                    || path.Replace('\\', '/').Contains("/.nuget/packages/microsoft.net.test.sdk/", StringComparison.OrdinalIgnoreCase)
                    || path.Replace('\\', '/').Contains("/.nuget/packages/microsoft.testplatform.", StringComparison.OrdinalIgnoreCase)
                    || path.Replace('\\', '/').Contains("/.nuget/packages/microsoft.codecoverage/", StringComparison.OrdinalIgnoreCase)
                    || path.Replace('\\', '/').Contains("/.nuget/packages/xunit.", StringComparison.OrdinalIgnoreCase);
                var bytes = File.ReadAllBytes(path);
                if (!standard)
                {
                    var elements = XDocument.Load(new MemoryStream(bytes)).Descendants().ToArray();
                    if (elements.Any(e => e.Name.LocalName == "Target")) return null;
                    // MSBuildAllProjects is cooperative, so it cannot prove that an arbitrary
                    // explicit import was observed. Reject such imports even if this file has no targets.
                    var generated = path.EndsWith(".nuget.g.props", StringComparison.Ordinal) || path.EndsWith(".nuget.g.targets", StringComparison.Ordinal);
                    foreach (var import in elements.Where(e => e.Name.LocalName == "Import"))
                    {
                        var imported = ((string?)import.Attribute("Project") ?? "").Replace('\\', '/').ToLowerInvariant();
                        if (!generated || !(imported.Contains("microsoft.net.test.sdk/") || imported.Contains("microsoft.testplatform.") || imported.Contains("microsoft.codecoverage/") || imported.Contains("xunit."))) return null;
                    }
                }
                hash.AppendData(System.Text.Encoding.UTF8.GetBytes(path));
                hash.AppendData(bytes);
            }
            hash.AppendData(System.Text.Encoding.UTF8.GetBytes(result.StandardOutput));
            return new(assembly, Get("TargetFramework"), Get("TargetFrameworkMoniker"), Convert.ToHexString(hash.GetHashAndReset()));
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException or IOException
            or UnauthorizedAccessException or ArgumentException or System.Xml.XmlException) { return null; }
    }
}
