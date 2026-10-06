using System.Text.Json;
using Walker.Core;
using Walker.Execution;
using Walker.Roslyn;
using Xunit;
namespace Walker.Tests;

public sealed class BuildReuseTests
{
    [Theory]
    [InlineData("standard", true)]
    [InlineData("no-metadata", false)]
    [InlineData("truncated", false)]
    [InlineData("multi-target", false)]
    [InlineData("runtime-specific", false)]
    [InlineData("different-framework", false)]
    [InlineData("different-configuration", false)]
    [InlineData("no-reference-build", false)]
    [InlineData("disabled-reference", false)]
    [InlineData("reference-targets", false)]
    [InlineData("reference-properties", false)]
    [InlineData("unrelated-project", false)]
    public async Task SharesBuildOnlyWithVerifiedCompatibleReference(string layout, bool sharesBuild)
    {
        using var fixture = new Fixture(layout);
        await fixture.Executor.VerifyAsync(fixture.Request, default);
        fixture.Baseline = false;
        fixture.Calls.Clear();
        var result = await fixture.Execute();
        Assert.Equal(MutationOutcome.Survived, result.Outcome);
        Assert.Equal(sharesBuild ? 1 : 2, fixture.Calls.Count(c => c.Arguments[0] == "build"));
        Assert.Contains(fixture.Calls, c => c.Arguments[0] == "build" && c.Arguments[1] == "Tests.csproj");
        Assert.Equal(fixture.Bytes, File.ReadAllBytes(fixture.SourcePath));
    }
    [Theory]
    [InlineData(true, MutationOutcome.CompileError)]
    [InlineData(false, MutationOutcome.TestError)]
    public async Task FailedSharedBuildDiagnosesProductionBeforeClassifying(bool productionFails, MutationOutcome expected)
    {
        using var fixture = new Fixture("standard");
        await fixture.Executor.VerifyAsync(fixture.Request, default);
        fixture.Baseline = false;
        fixture.TestBuildFails = true;
        fixture.ProductionBuildFails = productionFails;
        fixture.Calls.Clear();
        var result = await fixture.Execute();
        Assert.Equal(expected, result.Outcome);
        Assert.Equal(new[] { "Tests.csproj", "Code.csproj" }, fixture.Calls.Select(c => c.Arguments[1]));
        Assert.Equal(fixture.Bytes, File.ReadAllBytes(fixture.SourcePath));
    }
    [Fact]
    public async Task FailedBaselineDoesNotEnableBuildReuse()
    {
        using var fixture = new Fixture("standard");
        fixture.TestsFail = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Executor.VerifyAsync(fixture.Request, default));
        fixture.TestsFail = false;
        fixture.Baseline = false;
        fixture.Calls.Clear();
        await fixture.Execute();
        Assert.Equal("Code.csproj", fixture.Calls[0].Arguments[1]);
    }
    [Fact]
    public async Task NewlySelectedTestProjectUsesExplicitProductionBuild()
    {
        using var fixture = new Fixture("standard");
        await fixture.Executor.VerifyAsync(fixture.Request, default);
        fixture.Baseline = false;
        fixture.Calls.Clear();
        await fixture.Execute(["Other.Tests.csproj"]);
        Assert.Equal("Code.csproj", fixture.Calls[0].Arguments[1]);
    }
    private sealed class Fixture : IDisposable
    {
        private readonly Workspace workspace = new();
        public bool Baseline = true, TestBuildFails, ProductionBuildFails, TestsFail;
        public List<ProcessRequest> Calls { get; } = [];
        public DotnetMutationExecutor Executor { get; }
        public VerificationRequest Request { get; }
        public string SourcePath => Path.Combine(workspace.Root, "Code.cs");
        public byte[] Bytes { get; }
        public Fixture(string layout)
        {
            workspace.Write("Code.cs", "class C { bool M(int a, int b) => a >= b; }");
            Bytes = File.ReadAllBytes(SourcePath);
            Request = new(workspace.Root, "HEAD~1", "Code.csproj", ["Tests.csproj"]);
            Executor = new(new FakeRunner(request =>
            {
                Calls.Add(request);
                if (request.Arguments[0] == "build")
                {
                    if (Baseline) Assert.Contains("-target:Build", request.Arguments);
                    var production = request.Arguments[1] == "Code.csproj";
                    if (!Baseline && (production ? ProductionBuildFails : TestBuildFails)) return new(1, "build failed", "", 1);
                    return new(0, Metadata(layout, production), "", 1, layout == "truncated");
                }
                var directory = request.Arguments[request.Arguments.ToList().IndexOf("--results-directory") + 1];
                var failed = TestsFail ? 1 : 0;
                File.WriteAllText(Path.Combine(directory, "tests.trx"), $"<TestRun><ResultSummary><Counters executed='1' passed='{1 - failed}' failed='{failed}' /></ResultSummary></TestRun>");
                return new(failed, "", "", 1);
            }));
        }
        public async Task<MutationResult> Execute(IReadOnlyList<string>? selected = null)
        {
            var candidate = Assert.Single((await new RoslynMutationDiscoverer().DiscoverAsync(workspace.Root,
                [new("Code.cs", [new(1, 1)], false)], default)).Mutants);
            return await Executor.ExecuteAsync(candidate, new(Request, new AllTestsSelector(selected ?? Request.Tests)), default);
        }
        private string Metadata(string layout, bool production)
        {
            if (layout == "no-metadata") return "build succeeded";
            var properties = new Dictionary<string, string>
            {
                ["TargetFramework"] = "net8.0", ["TargetFrameworks"] = "", ["Configuration"] = "Debug",
                ["Platform"] = "AnyCPU", ["RuntimeIdentifier"] = "", ["BuildProjectReferences"] = "true"
            };
            var reference = new Dictionary<string, string>
            {
                ["FullPath"] = Path.Combine(workspace.Root, "Code.csproj"), ["HasSingleTargetFramework"] = "true",
                ["BuildReference"] = "true", ["NearestTargetFramework"] = "net8.0",
                ["UndefineProperties"] = ";TargetFramework;RuntimeIdentifier;SelfContained"
            };
            switch (layout)
            {
                case "multi-target": properties["TargetFrameworks"] = "net8.0;net9.0"; break;
                case "runtime-specific": properties["RuntimeIdentifier"] = "linux-x64"; break;
                case "different-framework": reference["NearestTargetFramework"] = "net9.0"; break;
                case "different-configuration" when !production: properties["Configuration"] = "Release"; break;
                case "no-reference-build": properties["BuildProjectReferences"] = "false"; break;
                case "disabled-reference": reference["BuildReference"] = "false"; break;
                case "reference-targets": reference["Targets"] = "GetTargetPath"; break;
                case "reference-properties": reference["AdditionalProperties"] = "DefineConstants=CUSTOM"; break;
                case "unrelated-project": reference["FullPath"] = Path.Combine(workspace.Root, "Unrelated.csproj"); break;
            }
            return JsonSerializer.Serialize(new { Properties = properties, Items = new { _MSBuildProjectReferenceExistent = production ? Array.Empty<Dictionary<string, string>>() : [reference] } });
        }
        public void Dispose() => workspace.Dispose();
    }
}
