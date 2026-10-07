using System.Text.Json;
using Walker.Core;
using Walker.Execution;
using Walker.Roslyn;
using Xunit;

namespace Walker.Tests;

public sealed class CompiledTestRunnerTests
{
    [Fact]
    public async Task RealPreferredMissRunsFreshMutatedAssemblyAndConfirmationRebuildsRestoredSource()
    {
        using var workspace = new Workspace();
        workspace.Write("Code/Code.csproj", "<Project Sdk='Microsoft.NET.Sdk'><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");
        workspace.Write("Code/Rules.cs", "namespace Example; public static class Rules { public static bool Allow(int a, int b, bool active) { var boundary = a >= b; var result = boundary && active; return result; } }");
        workspace.Write("Tests/Tests.csproj", """
            <Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
              <ItemGroup><ProjectReference Include="../Code/Code.csproj"/>
                <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.11.1"/>
                <PackageReference Include="xunit" Version="2.9.2"/>
                <PackageReference Include="xunit.runner.visualstudio" Version="2.8.2"/>
              </ItemGroup>
            </Project>
            """);
        workspace.Write("Tests/Tests.cs", """
            using Xunit;
            namespace Example;
            public class Tests {
              [Fact] public void Boundary() => Assert.True(Rules.Allow(1, 1, true));
              [Fact] public void Inactive() => Assert.False(Rules.Allow(2, 1, false));
              [Fact] public void SlowUnrelated() => System.Threading.Thread.Sleep(3100);
            }
            """);
        var root = workspace.Root.StartsWith("/var/", StringComparison.Ordinal) ? "/private" + workspace.Root : workspace.Root;
        var runner = new RecordingRunner();
        var executor = new DotnetMutationExecutor(runner, compiledTests: true);
        var request = new VerificationRequest(root, "HEAD", "Code/Code.csproj", ["Tests/Tests.csproj"], ConfirmKills: true);
        await executor.VerifyAsync(request, default);
        var mutants = (await new RoslynMutationDiscoverer().DiscoverAsync(root, [new("Code/Rules.cs", [new(1, 1)], false)], default)).Mutants;
        var context = new VerificationContext(request, new AllTestsSelector(request.Tests));
        Assert.Equal(MutationOutcome.Killed, (await executor.ExecuteAsync(Assert.Single(mutants, m => m.Operator == MutationOperator.ConditionalBoundary), context, default)).Outcome);
        var original = File.ReadAllBytes(Path.Combine(root, "Code/Rules.cs"));
        runner.Calls.Clear();
        var result = await executor.ExecuteAsync(Assert.Single(mutants, m => m.Original == "boundary && active"), context, default);
        Assert.Equal(MutationOutcome.Killed, result.Outcome);
        Assert.True(result.KillConfirmed);
        var tests = runner.Calls.Where(c => c.Arguments[0] == "test").ToArray();
        Assert.Equal(4, tests.Length); // unmutated preferred validation, preferred miss, full DLL retry, confirmation
        Assert.EndsWith(".dll", tests[2].Arguments[1]);
        Assert.DoesNotContain("--no-build", tests[2].Arguments);
        Assert.EndsWith(".csproj", tests[3].Arguments[1]);
        Assert.DoesNotContain("--no-build", tests[3].Arguments);
        Assert.Contains("Example.Tests.Inactive", Assert.Single(result.FailingTests!));
        Assert.Equal(original, File.ReadAllBytes(Path.Combine(root, "Code/Rules.cs")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RealCustomOutputPreservesContentFilteringTransitiveReferencesAndFrameworks(bool multiFramework)
    {
        using var workspace = new Workspace();
        workspace.Write("Code/Code.csproj", "<Project Sdk='Microsoft.NET.Sdk'><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");
        workspace.Write("Code/Rules.cs", "namespace Example; public static class Rules { public static bool Allow(int a, int b) => a >= b; }");
        workspace.Write("Facade/Facade.csproj", "<Project Sdk='Microsoft.NET.Sdk'><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup><ItemGroup><ProjectReference Include='../Code/Code.csproj'/></ItemGroup></Project>");
        workspace.Write("Tests/Tests.csproj", $"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><{(multiFramework ? "TargetFrameworks" : "TargetFramework")}>{(multiFramework ? "net10.0;net10.0-windows" : "net10.0")}</{(multiFramework ? "TargetFrameworks" : "TargetFramework")}>
                <IsTestProject>true</IsTestProject><OutputPath>../custom output/</OutputPath><AssemblyName>Actual.Tests</AssemblyName></PropertyGroup>
              <ItemGroup><ProjectReference Include="../Facade/Facade.csproj"/>
                <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.11.1"/>
                <PackageReference Include="xunit" Version="2.9.2"/>
                <PackageReference Include="xunit.runner.visualstudio" Version="2.8.2"/>
                <None Include="content.txt" CopyToOutputDirectory="PreserveNewest"/>
              </ItemGroup>
            </Project>
            """);
        workspace.Write("Tests/content.txt", "expected content");
        workspace.Write("Tests/Tests.cs", """
            using System;
            using System.IO;
            using Xunit;
            namespace Example;
            public class Tests {
              [Fact, Trait("Category", "Unit")] public void Positive() {
                Assert.Equal("expected content", File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "content.txt")));
                Assert.True(Rules.Allow(2, 1));
              }
              [Fact, Trait("Category", "Unit")] public void Boundary() {
            #if WINDOWS
                Assert.True(Rules.Allow(1, 1));
            #else
                Assert.True(Rules.Allow(2, 1));
            #endif
              }
              [Fact] public void ExcludedFailure() => Assert.True(false);
            }
            """);
        var root = workspace.Root.StartsWith("/var/", StringComparison.Ordinal) ? "/private" + workspace.Root : workspace.Root;
        var runner = new RecordingRunner();
        var executor = new DotnetMutationExecutor(runner, compiledTests: true);
        var request = new VerificationRequest(root, "HEAD", "Code/Code.csproj", ["Tests/Tests.csproj"], Filter: "Category=Unit", ConfirmKills: true);
        await executor.VerifyAsync(request, default);
        var probes = runner.Calls.Where(c => c.Arguments[0] == "test" && c.Arguments[1].EndsWith("Actual.Tests.dll", StringComparison.Ordinal)).ToArray();
        Assert.True(probes.Length == (multiFramework ? 2 : 1), runner.Metadata);
        Assert.All(probes, p => Assert.Contains("custom output", p.Arguments[1]));
        var mutant = Assert.Single((await new RoslynMutationDiscoverer().DiscoverAsync(root, [new("Code/Rules.cs", [new(1, 1)], false)], default)).Mutants);
        var original = File.ReadAllBytes(Path.Combine(root, "Code/Rules.cs"));
        runner.Calls.Clear();
        var result = await executor.ExecuteAsync(mutant, new(request, new AllTestsSelector(request.Tests, request.Filter)), default);
        Assert.Equal(multiFramework ? MutationOutcome.Killed : MutationOutcome.Survived, result.Outcome);
        if (multiFramework)
        {
            Assert.True(result.KillConfirmed);
            Assert.Contains(runner.Calls, call => call.Arguments.Contains("net10.0-windows") && call.Arguments.Contains("--filter"));
        }
        Assert.DoesNotContain(runner.Calls, c => c.Arguments[0] == "test" && c.Arguments[1].EndsWith(".dll", StringComparison.Ordinal));
        Assert.Equal(original, File.ReadAllBytes(Path.Combine(root, "Code/Rules.cs")));
    }

    private sealed class RecordingRunner : IProcessRunner
    {
        private readonly ProcessRunner runner = new();
        public List<ProcessRequest> Calls { get; } = [];
        public string Metadata { get; private set; } = "";
        public async Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken token)
        {
            Calls.Add(request);
            var result = await runner.RunAsync(request, token);
            if (request.Arguments[0] == "msbuild")
            {
                Metadata += result.StandardOutput;
                Assert.True(result.ExitCode == 0, result.StandardOutput + result.StandardError);
            }
            return result;
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("identity")]
    [InlineData("missing-report")]
    [InlineData("empty")]
    [InlineData("malformed")]
    [InlineData("failure")]
    [InlineData("hang")]
    public async Task OnlyAnEquivalentPassingProbeEnablesTheFullFilterRetry(string? rejection)
    {
        using var fixture = new Fixture { ProbeRejection = rejection };
        await fixture.Baseline();
        await fixture.Execute("A");
        fixture.Calls.Clear();
        var result = await fixture.Execute("B");
        Assert.Equal(MutationOutcome.Killed, result.Outcome);
        var full = fixture.Tests[^1];
        Assert.Equal(rejection == null ? fixture.Assembly : "Tests.csproj", full.Arguments[1]);
        Assert.Contains("Category=Unit", full.Arguments);
        Assert.Equal(fixture.Original, File.ReadAllBytes(fixture.Source));
    }

    [Theory]
    [InlineData("missing-runtime")]
    [InlineData("changed-output")]
    [InlineData("changed-project")]
    public async Task ChangedBuildOutputsRequireAProjectBuildBeforeTheFullRetry(string change)
    {
        using var fixture = new Fixture();
        await fixture.Baseline();
        await fixture.Execute("A");
        fixture.AfterPreferred = () =>
        {
            if (change == "missing-runtime") File.Delete(Path.ChangeExtension(fixture.Assembly, ".runtimeconfig.json"));
            if (change == "changed-output") fixture.OutputSuffix = "Other.Tests";
            if (change == "changed-project") File.AppendAllText(fixture.Project, "<!-- changed build input -->");
        };
        fixture.Calls.Clear();
        Assert.Equal(MutationOutcome.Killed, (await fixture.Execute("B")).Outcome);
        Assert.Equal("Tests.csproj", fixture.Tests[^1].Arguments[1]);
        Assert.DoesNotContain("--no-build", fixture.Tests[^1].Arguments);
        Assert.Equal(fixture.Original, File.ReadAllBytes(fixture.Source));
    }

    [Theory]
    [InlineData("VSTestSetting")]
    [InlineData("RunSettingsFilePath")]
    [InlineData("VSTestTestAdapterPath")]
    [InlineData("RuntimeIdentifier")]
    [InlineData("TestingPlatformDotnetTestSupport")]
    [InlineData("VSTestCLIRunSettings")]
    [InlineData("VSTestCollect")]
    [InlineData("VSTestConsolePath")]
    [InlineData("missing-metadata")]
    [InlineData("custom-target")]
    [InlineData("custom-import")]
    [InlineData("directory-target")]
    public async Task UnsupportedSettingsAndTargetsKeepProjectExecution(string unsupported)
    {
        using var fixture = new Fixture { Unsupported = unsupported };
        if (unsupported == "custom-target") File.WriteAllText(fixture.Project, "<Project Sdk='Microsoft.NET.Sdk'><Target Name='Custom' BeforeTargets='VSTest'/></Project>");
        if (unsupported == "custom-import") File.WriteAllText(fixture.Project, "<Project Sdk='Microsoft.NET.Sdk'><Import Project='unobserved.targets'/></Project>");
        if (unsupported == "directory-target") File.WriteAllText(Path.Combine(Path.GetDirectoryName(fixture.Project)!, "Directory.Build.targets"), "<Project><Target Name='Custom' BeforeTargets='VSTest'/></Project>");
        await fixture.Baseline();
        Assert.DoesNotContain(fixture.Tests, call => call.Arguments[1].EndsWith(".dll", StringComparison.Ordinal));
        await fixture.Execute("A");
        fixture.Calls.Clear();
        Assert.Equal(MutationOutcome.Killed, (await fixture.Execute("B")).Outcome);
        Assert.Equal("Tests.csproj", fixture.Tests[^1].Arguments[1]);
    }

    [Fact]
    public async Task BaselineProbesTheEvaluatedAssemblyInItsOriginalDirectory()
    {
        using var workspace = new Workspace();
        workspace.Write("Tests.csproj", "<Project Sdk='Microsoft.NET.Sdk'><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup></Project>");
        workspace.Write("custom output/Actual.Tests.dll", "assembly");
        workspace.Write("custom output/Actual.Tests.deps.json", "{}");
        workspace.Write("custom output/Actual.Tests.runtimeconfig.json", "{}");
        var assembly = Path.Combine(workspace.Root, "custom output/Actual.Tests.dll");
        var calls = new List<ProcessRequest>();
        var runner = new FakeRunner(call =>
        {
            calls.Add(call);
            if (call.Arguments[0] == "msbuild")
            {
                var properties = call.Arguments.Single(a => a.StartsWith("-getProperty:", StringComparison.Ordinal))[13..]
                    .Split(',').ToDictionary(name => name, _ => "");
                properties["TargetFramework"] = "net8.0";
                properties["TargetPath"] = assembly;
                properties["TargetFrameworkMoniker"] = ".NETCoreApp,Version=v8.0";
                properties["IsTestProject"] = "true";
                properties["Configuration"] = "Debug";
                properties["Platform"] = properties["PlatformTarget"] = "AnyCPU";
                properties["MSBuildAllProjects"] = Path.Combine(workspace.Root, "Tests.csproj");
                return new(0, JsonSerializer.Serialize(new { Properties = properties }), "", 1);
            }
            if (call.Arguments[0] == "build") return new(0, "", "", 1);
            var directory = call.Arguments[call.Arguments.ToList().IndexOf("--results-directory") + 1];
            File.WriteAllText(Path.Combine(directory, "tests.trx"), "<TestRun><Results><UnitTestResult testId='id' testName='Example.Tests.A' outcome='Passed'/></Results><TestDefinitions><UnitTest id='id'><TestMethod className='Example.Tests' name='A'/></UnitTest></TestDefinitions><ResultSummary><Counters executed='1' passed='1' failed='0'/></ResultSummary></TestRun>");
            return new(0, "", "", 1);
        });
        await new DotnetMutationExecutor(runner, compiledTests: true).VerifyAsync(new(workspace.Root, "HEAD", "Code.csproj", ["Tests.csproj"], Filter: "Category=Unit"), default);
        var probe = Assert.Single(calls, c => c.Arguments.Count > 1 && c.Arguments[1] == assembly);
        Assert.Equal(workspace.Root, probe.WorkingDirectory);
        Assert.Contains("Category=Unit", probe.Arguments);
        Assert.DoesNotContain("--no-build", probe.Arguments);
        Assert.DoesNotContain("--no-restore", probe.Arguments);
        Assert.DoesNotContain(probe.Arguments, a => a.StartsWith("-p:", StringComparison.Ordinal));
    }

    private sealed class Fixture : IDisposable
    {
        private readonly Workspace workspace = new();
        private readonly Mutant mutant;
        private string failing = "A";
        public string? ProbeRejection { get; init; }
        public string? Unsupported { get; init; }
        public string OutputSuffix { get; set; } = "Actual.Tests";
        public Action? AfterPreferred { get; set; }
        public List<ProcessRequest> Calls { get; } = [];
        public List<ProcessRequest> Tests => Calls.Where(c => c.Arguments[0] == "test").ToList();
        public string Source => Path.Combine(workspace.Root, "Code.cs");
        public string Project => Path.Combine(workspace.Root, "Tests.csproj");
        public string Assembly => Path.Combine(workspace.Root, "custom output/Actual.Tests.dll");
        public byte[] Original { get; }
        public VerificationRequest Request { get; }
        public DotnetMutationExecutor Executor { get; }
        public Fixture()
        {
            workspace.Write("Code.cs", "class C { bool A(int a, int b) => a >= b; }");
            workspace.Write("Tests.csproj", "<Project Sdk='Microsoft.NET.Sdk'><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup></Project>");
            foreach (var name in new[] { "Actual.Tests", "Other.Tests" })
                foreach (var extension in new[] { ".dll", ".deps.json", ".runtimeconfig.json" }) workspace.Write("custom output/" + name + extension, "{}");
            Original = File.ReadAllBytes(Source);
            mutant = Assert.Single(new RoslynMutationDiscoverer().DiscoverAsync(workspace.Root, [new("Code.cs", [new(1, 1)], false)], default).GetAwaiter().GetResult().Mutants);
            Request = new(workspace.Root, "HEAD", "Code.csproj", ["Tests.csproj"], Filter: "Category=Unit");
            Executor = new(new FakeRunner(Run), compiledTests: true);
        }
        private ProcessResult Run(ProcessRequest call)
        {
            Calls.Add(call);
            if (call.Arguments[0] == "msbuild")
            {
                var properties = call.Arguments.Single(a => a.StartsWith("-getProperty:", StringComparison.Ordinal))[13..].Split(',').ToDictionary(n => n, _ => "");
                properties["TargetPath"] = Path.Combine(workspace.Root, "custom output/" + OutputSuffix + ".dll");
                properties["TargetFramework"] = "net8.0";
                properties["TargetFrameworkMoniker"] = ".NETCoreApp,Version=v8.0";
                properties["IsTestProject"] = "true";
                properties["Configuration"] = "Debug";
                properties["Platform"] = properties["PlatformTarget"] = "AnyCPU";
                properties["MSBuildAllProjects"] = Project;
                if (Unsupported == "missing-metadata") properties.Remove("TargetPath");
                else if (Unsupported != null && Unsupported is not ("custom-target" or "custom-import" or "directory-target")) properties[Unsupported] = Unsupported == "TestingPlatformDotnetTestSupport" ? "true" : "custom";
                return new(0, JsonSerializer.Serialize(new { Properties = properties }), "", 1);
            }
            if (call.Arguments[0] == "build")
            {
                var reference = new { FullPath = Path.Combine(workspace.Root, "Code.csproj"), BuildReference = "true", HasSingleTargetFramework = "true", NearestTargetFramework = "net8.0" };
                return new(0, JsonSerializer.Serialize(new { Properties = new { TargetFramework = "net8.0", TargetFrameworks = "", Configuration = "Debug", Platform = "AnyCPU", RuntimeIdentifier = "", BuildProjectReferences = "true" }, Items = new { _MSBuildProjectReferenceExistent = new[] { reference } } }), "", 1);
            }
            var mutated = !File.ReadAllBytes(Source).SequenceEqual(Original);
            var assembly = call.Arguments[1].EndsWith(".dll", StringComparison.Ordinal);
            var filter = call.Arguments[call.Arguments.ToList().IndexOf("--filter") + 1];
            var preferred = filter.Contains("FullyQualifiedName=", StringComparison.Ordinal);
            var directory = call.Arguments[call.Arguments.ToList().IndexOf("--results-directory") + 1];
            if (assembly && !mutated && ProbeRejection == "hang") throw new OperationCanceledException();
            if (assembly && !mutated && ProbeRejection == "missing-report") return new(0, "", "", 1);
            if (assembly && !mutated && ProbeRejection == "malformed")
            {
                File.WriteAllText(Path.Combine(directory, "tests.trx"), "<broken"); return new(0, "", "", 1);
            }
            var names = preferred ? new[] { "A" } : new[] { "A", "B" };
            if (assembly && !mutated && ProbeRejection == "identity") names = ["A", "C"];
            if (assembly && !mutated && ProbeRejection == "empty") names = [];
            var failed = 0;
            var results = string.Concat(names.Select(name =>
            {
                var fail = mutated && name == failing || assembly && !mutated && ProbeRejection == "failure" && name == "A";
                if (fail) failed++;
                return $"<UnitTestResult testId='{name}' testName='Example.Tests.{name}' outcome='{(fail ? "Failed" : "Passed")}'/>";
            }));
            var definitions = string.Concat(names.Select(name => $"<UnitTest id='{name}'><TestMethod className='Example.Tests' name='{name}'/></UnitTest>"));
            File.WriteAllText(Path.Combine(directory, "tests.trx"), $"<TestRun><Results>{results}</Results><TestDefinitions>{definitions}</TestDefinitions><ResultSummary><Counters executed='{names.Length}' passed='{names.Length - failed}' failed='{failed}'/></ResultSummary></TestRun>");
            if (mutated && preferred && failed == 0) AfterPreferred?.Invoke();
            return new(failed > 0 ? 1 : 0, "", "", !mutated && !preferred ? 5000 : 1);
        }
        public Task Baseline() => Executor.VerifyAsync(Request, default);
        public Task<MutationResult> Execute(string failure)
        {
            failing = failure;
            return Executor.ExecuteAsync(mutant, new(Request, new AllTestsSelector(Request.Tests, Request.Filter)), default);
        }
        public void Dispose() => workspace.Dispose();
    }
}
