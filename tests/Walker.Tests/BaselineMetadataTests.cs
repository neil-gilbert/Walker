using System.Text.Json;
using Walker.Core;
using Walker.Execution;
using Walker.Roslyn;
using Xunit;
namespace Walker.Tests;

public sealed class BaselineMetadataTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RealMultiFrameworkGraphPreservesImportsAndRejectsChangedProperties(bool changeProperties)
    {
        using var workspace = new Workspace();
        workspace.Write("Code/Code.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");
        workspace.Write("Code/Code.cs", "public class C { public bool M(int a, int b) => a >= b; }");
        workspace.Write("Facade/Facade.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup><ItemGroup><ProjectReference Include=\"../Code/Code.csproj\" /></ItemGroup></Project>");
        workspace.Write("Tests/Tests.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFrameworks>net10.0;net10.0-windows</TargetFrameworks><IsTestProject>true</IsTestProject></PropertyGroup>
              <ItemGroup>
                <ProjectReference Include="../Facade/Facade.csproj" />
                <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.11.1" />
                <PackageReference Include="xunit" Version="2.9.2" />
                <PackageReference Include="xunit.runner.visualstudio" Version="2.8.2" />
              </ItemGroup>
              <Import Project="Custom.targets" />
            </Project>
            """);
        workspace.Write("Tests/Custom.targets", "<Project><Target Name=\"ExistingHook\" AfterTargets=\"Build\"><WriteLinesToFile File=\"hook.txt\" Lines=\"$(TargetFramework)\" Overwrite=\"false\" /></Target></Project>");
        if (changeProperties)
            workspace.Write("Tests/Custom.targets", "<Project><Target Name=\"ExistingHook\" AfterTargets=\"Build\"><WriteLinesToFile File=\"hook.txt\" Lines=\"$(TargetFramework)\" Overwrite=\"false\" /><PropertyGroup Condition=\"'$(TargetFramework)' != ''\"><Platform>Custom</Platform></PropertyGroup></Target></Project>");
        workspace.Write("Tests/Tests.cs", "using Xunit; public class Tests { [Fact] public void Equality() => Assert.True(new C().M(2, 2)); }");
        // macOS temp paths are exposed through /var while MSBuild reports their /private/var identity.
        var root = OperatingSystem.IsMacOS() && workspace.Root.StartsWith("/var/", StringComparison.Ordinal) ? "/private" + workspace.Root : workspace.Root;
        var runner = new RecordingRunner();
        var request = new VerificationRequest(root, "HEAD", "Code/Code.csproj", ["Tests/Tests.csproj"]);
        var executor = new DotnetMutationExecutor(runner);
        await executor.VerifyAsync(request, default);
        Assert.True(runner.Records.Any(), runner.Diagnostics);
        Assert.True(runner.Calls.Count(c => c.Arguments[0] == "build") == (changeProperties ? 4 : 2), "root=" + root + "\n" + runner.Diagnostics);
        Assert.Equal(changeProperties ? [] : new[] { "net10.0", "net10.0-windows" }, runner.Records.Where(r => r.GetProperty("Project").GetString() == Path.Combine(root, "Tests/Tests.csproj"))
            .Select(r => r.GetProperty("Properties").GetProperty("TargetFramework").GetString()).Where(f => f != "").Order());
        Assert.All(runner.Records.Where(r => r.GetProperty("Project").GetString() == Path.Combine(root, "Tests/Tests.csproj")
            && r.GetProperty("Properties").GetProperty("TargetFramework").GetString() != ""), record =>
            Assert.Contains(record.GetProperty("Items").GetProperty("_MSBuildProjectReferenceExistent").EnumerateArray(),
                item => item.GetProperty("FullPath").GetString() == Path.Combine(root, "Code/Code.csproj")));
        foreach (var record in runner.Records.Where(r => r.GetProperty("Project").GetString() == Path.Combine(root, "Tests/Tests.csproj")
            && r.GetProperty("Properties").GetProperty("TargetFramework").GetString() != ""))
        {
            var queried = await new ProcessRunner().RunAsync(new("dotnet", ["build", "Tests/Tests.csproj", "--no-restore", "--nologo", "--verbosity", "quiet",
                "--framework", record.GetProperty("Properties").GetProperty("TargetFramework").GetString()!, "-target:Build",
                "-getProperty:TargetFramework,TargetFrameworks,Configuration,Platform,RuntimeIdentifier,BuildProjectReferences",
                "-getItem:_MSBuildProjectReferenceExistent"], root), default);
            Assert.True(queried.ExitCode == 0, queried.StandardOutput + queried.StandardError);
            using var metadata = JsonDocument.Parse(queried.StandardOutput);
            foreach (var property in record.GetProperty("Properties").EnumerateObject())
                Assert.Equal(property.Value.GetString(), metadata.RootElement.GetProperty("Properties").GetProperty(property.Name).GetString());
            var references = metadata.RootElement.GetProperty("Items").GetProperty("_MSBuildProjectReferenceExistent").EnumerateArray().ToArray();
            foreach (var reference in record.GetProperty("Items").GetProperty("_MSBuildProjectReferenceExistent").EnumerateArray())
            {
                var matching = Assert.Single(references, r => r.GetProperty("FullPath").GetString() == reference.GetProperty("FullPath").GetString());
                foreach (var field in reference.EnumerateObject())
                    Assert.Equal(field.Value.GetString(), matching.TryGetProperty(field.Name, out var value) ? value.GetString() : "");
            }
        }
        Assert.Contains("net10.0-windows", File.ReadAllText(Path.Combine(root, "Tests/hook.txt")));
        runner.Calls.Clear();
        var candidate = Assert.Single((await new RoslynMutationDiscoverer().DiscoverAsync(root,
            [new("Code/Code.cs", [new(1, 1)], false)], default)).Mutants);
        var bytes = File.ReadAllBytes(Path.Combine(root, "Code/Code.cs"));
        var result = await executor.ExecuteAsync(candidate, new(request, new AllTestsSelector(request.Tests)), default);
        Assert.Equal(MutationOutcome.Killed, result.Outcome);
        Assert.Equal(changeProperties ? 1 : 0, runner.Calls.Count(c => c.Arguments[0] == "build"));
        Assert.Equal(bytes, File.ReadAllBytes(Path.Combine(root, "Code/Code.cs")));
    }

    [Fact]
    public async Task CancelledBuildDiscardsItsObservationDirectory()
    {
        using var workspace = new Workspace();
        workspace.Write("Tests.csproj", "<Project><PropertyGroup><TargetFrameworks>net8.0;net9.0</TargetFrameworks></PropertyGroup></Project>");
        string? directory = null;
        var executor = new DotnetMutationExecutor(new FakeRunner(request =>
        {
            var argument = request.Arguments.FirstOrDefault(a => a.StartsWith("-logger:", StringComparison.Ordinal));
            if (argument == null) return new(0, Metadata(workspace.Root, true, null), "", 1);
            directory = argument[(argument.IndexOf(';') + 1)..];
            File.WriteAllText(Path.Combine(directory, "partial.json"), Metadata(workspace.Root, false, "net8.0"));
            throw new OperationCanceledException();
        }));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => executor.VerifyAsync(new(workspace.Root, "HEAD", "Code.csproj", ["Tests.csproj"]), default));
        Assert.NotNull(directory);
        Assert.False(Directory.Exists(directory));
    }

    [Theory]
    [InlineData("valid", 2)]
    [InlineData("missing-second", 3)]
    [InlineData("missing-all", 5)]
    [InlineData("malformed", 5)]
    [InlineData("duplicate", 3)]
    [InlineData("invalid-marker", 5)]
    [InlineData("wrong-project", 3)]
    [InlineData("wrong-schema", 5)]
    [InlineData("incomplete-reference", 3)]
    public async Task UsesOnlyUnambiguousCurrentBuildRecords(string layout, int builds)
    {
        using var workspace = new Workspace();
        workspace.Write("Code.cs", "class C { bool M(int a, int b) => a >= b; }");
        workspace.Write("Tests.csproj", "<Project><PropertyGroup><TargetFrameworks>net8.0;net9.0</TargetFrameworks></PropertyGroup></Project>");
        var calls = new List<ProcessRequest>();
        var runner = new FakeRunner(request =>
        {
            calls.Add(request);
            if (request.Arguments[0] == "build")
            {
                var production = request.Arguments[1] == "Code.csproj";
                var framework = Argument(request, "--framework");
                var observer = request.Arguments.FirstOrDefault(a => a.StartsWith("-logger:", StringComparison.Ordinal));
                if (!production && framework == null && observer != null)
                {
                    var directory = observer[(observer.IndexOf(';') + 1)..];
                    if (layout is not ("missing-all" or "malformed" or "invalid-marker" or "wrong-schema"))
                        File.WriteAllText(Path.Combine(directory, "outer.json"), Metadata(workspace.Root, false, null));
                    foreach (var target in layout == "missing-all" ? Array.Empty<string>() : layout == "missing-second" ? new[] { "net8.0" } : new[] { "net8.0", "net9.0" })
                        File.WriteAllText(Path.Combine(directory, target + ".json"), Metadata(workspace.Root, false, target));
                    if (layout == "malformed") File.WriteAllText(Path.Combine(directory, "bad.json"), "{ truncated");
                    if (layout == "duplicate") File.WriteAllText(Path.Combine(directory, "duplicate.json"), Metadata(workspace.Root, false, "net8.0"));
                    if (layout == "invalid-marker") File.WriteAllText(Path.Combine(directory, "invalid"), "failed");
                    if (layout == "wrong-project") File.WriteAllText(Path.Combine(directory, "net8.0.json"), Metadata(workspace.Root, false, "net8.0").Replace("Tests.csproj", "Other.csproj"));
                    if (layout == "wrong-schema") File.WriteAllText(Path.Combine(directory, "bad.json"), "{\"SchemaVersion\":2,\"Project\":\"Tests.csproj\"}");
                    if (layout == "incomplete-reference") File.WriteAllText(Path.Combine(directory, "net8.0.json"), Metadata(workspace.Root, false, "net8.0").Replace("\"Targets\":\"\",", ""));
                }
                return new(0, Metadata(workspace.Root, production, framework), "", 1);
            }
            var results = Argument(request, "--results-directory")!;
            const string report = "<TestRun><ResultSummary><Counters executed='1' passed='1' failed='0' /></ResultSummary></TestRun>";
            File.WriteAllText(Path.Combine(results, "first.trx"), report);
            if (Argument(request, "--framework") == null) File.WriteAllText(Path.Combine(results, "second.trx"), report);
            return new(0, "", "", 1);
        });
        var request = new VerificationRequest(workspace.Root, "HEAD", "Code.csproj", ["Tests.csproj"]);
        var executor = new DotnetMutationExecutor(runner);
        await executor.VerifyAsync(request, default);
        Assert.Equal(builds, calls.Count(c => c.Arguments[0] == "build"));
        var observerArgument = Assert.Single(calls.SelectMany(c => c.Arguments), a => a.StartsWith("-logger:", StringComparison.Ordinal));
        Assert.False(Directory.Exists(observerArgument[(observerArgument.IndexOf(';') + 1)..]));
        await executor.VerifyAsync(request, default);
        var nextObserver = calls.SelectMany(c => c.Arguments).Last(a => a.StartsWith("-logger:", StringComparison.Ordinal));
        Assert.NotEqual(observerArgument, nextObserver);
        Assert.Equal(2 * builds, calls.Count(c => c.Arguments[0] == "build"));
        calls.Clear();
        var candidate = Assert.Single((await new RoslynMutationDiscoverer().DiscoverAsync(workspace.Root,
            [new("Code.cs", [new(1, 1)], false)], default)).Mutants);
        var result = await executor.ExecuteAsync(candidate, new(request, new AllTestsSelector(request.Tests)), default);
        Assert.Equal(MutationOutcome.Survived, result.Outcome);
        Assert.DoesNotContain(calls, c => c.Arguments[0] == "build");
        Assert.Equal(new[] { "net8.0", "net9.0" }, calls.Select(c => Argument(c, "--framework")));
    }

    private static string? Argument(ProcessRequest request, string name)
    {
        var index = request.Arguments.ToList().IndexOf(name);
        return index < 0 ? null : request.Arguments[index + 1];
    }

    private static string Metadata(string root, bool production, string? framework)
    {
        var properties = new Dictionary<string, string>
        {
            ["TargetFramework"] = production ? "net8.0" : framework ?? "", ["TargetFrameworks"] = production ? "" : "net8.0;net9.0",
            ["Configuration"] = "Debug", ["Platform"] = "AnyCPU", ["RuntimeIdentifier"] = "", ["BuildProjectReferences"] = "true"
        };
        var reference = new Dictionary<string, string>
        {
            ["FullPath"] = Path.Combine(root, "Code.csproj"), ["HasSingleTargetFramework"] = "true",
            ["BuildReference"] = "true", ["NearestTargetFramework"] = "net8.0"
        };
        foreach (var name in new[] { "Targets", "SetConfiguration", "SetPlatform", "SetTargetFramework", "AdditionalProperties", "GlobalPropertiesToRemove", "UndefineProperties" }) reference[name] = "";
        return JsonSerializer.Serialize(new { SchemaVersion = 1, Project = Path.Combine(root, production ? "Code.csproj" : "Tests.csproj"),
            Properties = properties, Items = new { _MSBuildProjectReferenceExistent = framework == null ? Array.Empty<Dictionary<string, string>>() : [reference] } });
    }

    private sealed class RecordingRunner : IProcessRunner
    {
        private readonly ProcessRunner inner = new();
        public List<ProcessRequest> Calls { get; } = [];
        public List<JsonElement> Records { get; } = [];
        public string Diagnostics = "";
        public async Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken token)
        {
            Calls.Add(request);
            var result = await inner.RunAsync(request, token);
            var argument = request.Arguments.FirstOrDefault(a => a.StartsWith("-logger:", StringComparison.Ordinal));
            if (argument != null)
            {
                Diagnostics = string.Join("\n", Directory.GetFiles(argument[(argument.IndexOf(';') + 1)..]).Select(f => File.ReadAllText(f)));
                foreach (var file in Directory.GetFiles(argument[(argument.IndexOf(';') + 1)..], "*.json"))
                {
                    using var document = JsonDocument.Parse(File.ReadAllText(file));
                    Records.Add(document.RootElement.Clone());
                }
            }
            return result;
        }
    }
}
