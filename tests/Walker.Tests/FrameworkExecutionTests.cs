using System.Text.Json;
using Walker.Core;
using Walker.Execution;
using Walker.Roslyn;
using Xunit;
namespace Walker.Tests;

public sealed class FrameworkExecutionTests
{
    [Theory]
    [InlineData("net8.0", MutationOutcome.Killed, 1)]
    [InlineData("net9.0", MutationOutcome.Killed, 2)]
    [InlineData(null, MutationOutcome.Survived, 2)]
    public async Task ValidatesEveryFrameworkThenStopsMutationAtFirstFailure(string? failingFramework, MutationOutcome outcome, int runs)
    {
        using var fixture = new Fixture();
        await fixture.Baseline();
        fixture.FailingFramework = failingFramework;
        var result = await fixture.Execute();
        Assert.Equal(outcome, result.Outcome);
        Assert.Equal(runs, fixture.TestCalls.Count);
        Assert.Equal("net8.0", Framework(fixture.TestCalls[0]));
        if (runs == 2) Assert.Equal("net9.0", Framework(fixture.TestCalls[1]));
        Assert.All(fixture.TestCalls, c => Assert.Equal(fixture.Request.Filter, Argument(c, "--filter")));
        Assert.DoesNotContain(fixture.Calls, c => c.Arguments[0] == "build");
        Assert.Equal(fixture.Original, File.ReadAllBytes(fixture.Source));
    }

    [Fact]
    public async Task PassingFirstFrameworkDoesNotHideEmptySecondFramework()
    {
        using var fixture = new Fixture();
        await fixture.Baseline();
        fixture.EmptyFramework = "net9.0";
        var result = await fixture.Execute();
        Assert.Equal(MutationOutcome.TestError, result.Outcome);
        Assert.Equal(2, fixture.TestCalls.Count);
        Assert.Equal(fixture.Original, File.ReadAllBytes(fixture.Source));
    }

    [Theory]
    [InlineData(false, MutationOutcome.CompileError)]
    [InlineData(true, MutationOutcome.TestError)]
    public async Task FailedFrameworkBuildStillSeparatesProductionAndTestErrors(bool productionBuilds, MutationOutcome expected)
    {
        using var fixture = new Fixture();
        await fixture.Baseline();
        fixture.TestBuildFails = true;
        fixture.ProductionBuilds = productionBuilds;
        var result = await fixture.Execute();
        Assert.Equal(expected, result.Outcome);
        Assert.Equal(new[] { "test", "build" }, fixture.Calls.Select(c => c.Arguments[0]));
        Assert.Equal(fixture.Original, File.ReadAllBytes(fixture.Source));
    }

    [Fact]
    public async Task ConfirmationRerunsFailingFrameworkOnRestoredSource()
    {
        using var fixture = new Fixture(confirm: true);
        await fixture.Baseline();
        fixture.FailingFramework = "net9.0";
        var result = await fixture.Execute();
        Assert.Equal(MutationOutcome.Killed, result.Outcome);
        Assert.True(result.KillConfirmed);
        Assert.Equal(new[] { "net8.0", "net9.0", "net9.0" }, fixture.TestCalls.Select(Framework));
        Assert.Equal("(FullyQualifiedName~Boundary)&(FullyQualifiedName=Example.Tests.Boundary.Equality)",
            Argument(fixture.TestCalls[^1], "--filter"));
        Assert.Equal(fixture.Original, File.ReadAllBytes(fixture.Source));
    }

    [Fact]
    public async Task PreviouslyFailingMethodsCanKillLaterMutantsBeforeFullSuite()
    {
        using var fixture = new Fixture();
        await fixture.Baseline();
        fixture.FailingFramework = "net8.0";
        Assert.Equal(MutationOutcome.Killed, (await fixture.Execute()).Outcome);
        fixture.Calls.Clear();
        Assert.Equal(MutationOutcome.Killed, (await fixture.Execute()).Outcome);
        Assert.Equal(2, fixture.TestCalls.Count); // One unmutated subset baseline, then the mutant.
        var fast = fixture.TestCalls[1];
        Assert.Equal("(FullyQualifiedName~Boundary)&(FullyQualifiedName=Example.Tests.Boundary.Equality)", Argument(fast, "--filter"));
        Assert.DoesNotContain("--no-build", fast.Arguments);
        Assert.Equal(fixture.Original, File.ReadAllBytes(fixture.Source));
        fixture.Calls.Clear();
        Assert.Equal(MutationOutcome.Killed, (await fixture.Execute()).Outcome);
        Assert.Single(fixture.TestCalls); // The unchanged subset baseline is reused.
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task PassingOrEmptyPreferredTestsStillRunsFullScopeWithFreshlyBuiltAssembly(bool empty, bool emptyMutant)
    {
        using var fixture = new Fixture();
        await fixture.Baseline();
        fixture.FailingFramework = "net8.0";
        Assert.Equal(MutationOutcome.Killed, (await fixture.Execute()).Outcome);
        fixture.Calls.Clear();
        fixture.PreferredTestsPass = true;
        fixture.EmptyPreferredTests = empty;
        fixture.EmptyPreferredOnlyMutated = emptyMutant;
        Assert.Equal(MutationOutcome.Killed, (await fixture.Execute()).Outcome);
        Assert.Equal(empty ? 2 : 3, fixture.TestCalls.Count);
        Assert.Equal(fixture.Request.Filter, Argument(fixture.TestCalls[^1], "--filter"));
        // An empty unmutated subset is rejected; a passing subset builds the graph for the fallback.
        Assert.Equal(!empty, fixture.TestCalls[^1].Arguments.Contains("--no-build"));
        Assert.Equal(fixture.Original, File.ReadAllBytes(fixture.Source));
    }

    [Fact]
    public async Task SurvivorStillPassesEveryFrameworkAfterPreferredTestsPass()
    {
        using var fixture = new Fixture();
        await fixture.Baseline();
        fixture.FailingFramework = "net8.0";
        Assert.Equal(MutationOutcome.Killed, (await fixture.Execute()).Outcome);
        fixture.Calls.Clear();
        fixture.FailingFramework = null;
        Assert.Equal(MutationOutcome.Survived, (await fixture.Execute()).Outcome);
        Assert.Equal(new[] { "net8.0", "net8.0", "net8.0", "net9.0" }, fixture.TestCalls.Select(Framework));
        Assert.Equal(fixture.Request.Filter, Argument(fixture.TestCalls[2], "--filter"));
        Assert.Equal(fixture.Request.Filter, Argument(fixture.TestCalls[3], "--filter"));
        Assert.Equal(fixture.Original, File.ReadAllBytes(fixture.Source));
    }

    [Fact]
    public async Task NewBaselineClearsPreferredTests()
    {
        using var fixture = new Fixture();
        await fixture.Baseline();
        fixture.FailingFramework = "net8.0";
        Assert.Equal(MutationOutcome.Killed, (await fixture.Execute()).Outcome);
        await fixture.Baseline();
        Assert.Equal(MutationOutcome.Killed, (await fixture.Execute()).Outcome);
        Assert.Equal(fixture.Request.Filter, Argument(Assert.Single(fixture.TestCalls), "--filter"));
    }

    [Fact]
    public async Task EarlyFrameworkKillDoesNotValidateLaterFrameworkHint()
    {
        using var fixture = new Fixture();
        await fixture.Baseline();
        fixture.FailingFramework = "net9.0";
        Assert.Equal(MutationOutcome.Killed, (await fixture.Execute()).Outcome);
        fixture.Calls.Clear();
        fixture.FailingFramework = "net8.0";
        Assert.Equal(MutationOutcome.Killed, (await fixture.Execute()).Outcome);
        Assert.Equal("net8.0", Framework(Assert.Single(fixture.TestCalls)));
        fixture.Calls.Clear();
        Assert.Equal(MutationOutcome.Killed, (await fixture.Execute()).Outcome);
        Assert.Equal(2, fixture.TestCalls.Count);
        Assert.All(fixture.TestCalls, call => Assert.Equal("net8.0", Framework(call)));
    }

    [Fact]
    public async Task PreferredKillConfirmationKeepsActualFrameworkAndOriginalTraitScope()
    {
        using var fixture = new Fixture(confirm: true);
        await fixture.Baseline();
        fixture.FailingFramework = "net8.0";
        await fixture.Execute();
        fixture.Calls.Clear();
        var result = await fixture.Execute();
        Assert.Equal(MutationOutcome.Killed, result.Outcome);
        Assert.True(result.KillConfirmed);
        Assert.Equal(3, fixture.TestCalls.Count); // Validation, preferred mutant, restored confirmation.
        Assert.All(fixture.TestCalls, call => Assert.Equal("net8.0", Framework(call)));
        Assert.Equal("((FullyQualifiedName~Boundary)&(FullyQualifiedName=Example.Tests.Boundary.Equality))&(FullyQualifiedName=Example.Tests.Boundary.Equality)",
            Argument(fixture.TestCalls[^1], "--filter"));
        Assert.Equal(fixture.Original, File.ReadAllBytes(fixture.Source));
    }

    [Fact]
    public async Task QuickSuitesAvoidPreferredTestStartupOverhead()
    {
        using var fixture = new Fixture(quick: true);
        await fixture.Baseline();
        fixture.FailingFramework = "net8.0";
        Assert.Equal(MutationOutcome.Killed, (await fixture.Execute()).Outcome);
        fixture.Calls.Clear();
        Assert.Equal(MutationOutcome.Killed, (await fixture.Execute()).Outcome);
        Assert.Equal(fixture.Request.Filter, Argument(Assert.Single(fixture.TestCalls), "--filter"));
    }

    [Fact]
    public async Task OrderDependentSubsetCannotCreateFalseKillAndIsNotRetried()
    {
        using var fixture = new Fixture();
        await fixture.Baseline();
        fixture.FailingFramework = "net8.0";
        Assert.Equal(MutationOutcome.Killed, (await fixture.Execute()).Outcome);
        fixture.Calls.Clear();
        fixture.FailingFramework = null;
        fixture.PreferredFailsUnmutated = true;
        Assert.Equal(MutationOutcome.Survived, (await fixture.Execute()).Outcome);
        Assert.Equal(3, fixture.TestCalls.Count);
        fixture.Calls.Clear();
        Assert.Equal(MutationOutcome.Survived, (await fixture.Execute()).Outcome);
        Assert.Equal(2, fixture.TestCalls.Count);
        Assert.All(fixture.TestCalls, c => Assert.Equal(fixture.Request.Filter, Argument(c, "--filter")));
        Assert.Equal(fixture.Original, File.ReadAllBytes(fixture.Source));
    }

    [Fact]
    public async Task FilterWithTestsInOnlyOneFrameworkKeepsCombinedExecution()
    {
        using var fixture = new Fixture();
        fixture.EmptyBaselineFramework = true;
        await fixture.Baseline();
        Assert.Equal(MutationOutcome.Survived, (await fixture.Execute()).Outcome);
        Assert.Null(Framework(Assert.Single(fixture.TestCalls)));
        Assert.Single(fixture.Calls, c => c.Arguments[0] == "build");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task MissingOrTruncatedMetadataKeepsConservativeCombinedRun(bool truncated)
    {
        using var fixture = new Fixture(metadata: false, truncated: truncated);
        await fixture.Baseline();
        var result = await fixture.Execute();
        Assert.Equal(MutationOutcome.Survived, result.Outcome);
        Assert.Null(Framework(Assert.Single(fixture.TestCalls)));
        Assert.Single(fixture.Calls, c => c.Arguments[0] == "build");
    }

    private static string? Argument(ProcessRequest request, string name)
    {
        var index = request.Arguments.ToList().IndexOf(name);
        return index < 0 ? null : request.Arguments[index + 1];
    }
    private static string? Framework(ProcessRequest request) => Argument(request, "--framework");

    private sealed class Fixture : IDisposable
    {
        private readonly Workspace workspace = new();
        private bool baseline = true;
        public string? FailingFramework, EmptyFramework;
        public bool TestBuildFails, PreferredTestsPass, PreferredFailsUnmutated, EmptyPreferredTests, EmptyPreferredOnlyMutated, EmptyBaselineFramework, ProductionBuilds = true;
        public List<ProcessRequest> Calls { get; } = [];
        public List<ProcessRequest> TestCalls => Calls.Where(c => c.Arguments[0] == "test").ToList();
        public VerificationRequest Request { get; }
        public DotnetMutationExecutor Executor { get; }
        public string Source => Path.Combine(workspace.Root, "Code.cs");
        public byte[] Original { get; }
        public Fixture(bool confirm = false, bool metadata = true, bool truncated = false, bool quick = false)
        {
            workspace.Write("Code.cs", "class C { bool M(int a, int b) => a >= b; }");
            Original = File.ReadAllBytes(Source);
            Request = new(workspace.Root, "HEAD", "Code.csproj", ["Tests.csproj"], Filter: "FullyQualifiedName~Boundary", ConfirmKills: confirm);
            Executor = new(new FakeRunner(process =>
            {
                Calls.Add(process);
                var production = process.Arguments[1] == "Code.csproj";
                if (process.Arguments[0] == "build")
                {
                    if (!baseline && production && !ProductionBuilds) return new(1, "production compile error", "", 1);
                    // Framework-specific metadata follows a real inner build; an outer build has no references.
                    var framework = Framework(process);
                    var properties = new Dictionary<string, string>
                    {
                        ["TargetFramework"] = production ? "net8.0" : framework ?? "",
                        ["TargetFrameworks"] = production ? "" : "net8.0;net9.0", ["Configuration"] = "Debug",
                        ["Platform"] = "AnyCPU", ["RuntimeIdentifier"] = "", ["BuildProjectReferences"] = "true"
                    };
                    var reference = new Dictionary<string, string>
                    {
                        ["FullPath"] = Path.Combine(workspace.Root, "Code.csproj"), ["HasSingleTargetFramework"] = "true",
                        ["BuildReference"] = "true", ["NearestTargetFramework"] = "net8.0"
                    };
                    var text = metadata || truncated ? JsonSerializer.Serialize(new { Properties = properties,
                        Items = new { _MSBuildProjectReferenceExistent = framework == null ? Array.Empty<Dictionary<string, string>>() : [reference] } }) : "build succeeded";
                    return new(0, text, "", 1, truncated);
                }
                if (!baseline && TestBuildFails) return new(1, "test build failed without results", "", 1);
                var mutated = !File.ReadAllBytes(Source).SequenceEqual(Original);
                var scope = Framework(process);
                var preferred = Argument(process, "--filter")!.Contains("FullyQualifiedName=", StringComparison.Ordinal);
                var failed = mutated && scope == FailingFramework && scope != null && !(preferred && PreferredTestsPass) ? 1 : 0;
                if (!mutated && preferred && PreferredFailsUnmutated) failed = 1;
                var executed = (scope != null && scope == EmptyFramework) || (preferred && (EmptyPreferredTests || (mutated && EmptyPreferredOnlyMutated))) ? 0 : 1;
                var directory = Argument(process, "--results-directory")!;
                var report =
                    $"<TestRun><Results><UnitTestResult testId='id' testName='Equality' outcome='{(failed > 0 ? "Failed" : "Passed")}' /></Results>" +
                    "<TestDefinitions><UnitTest id='id'><TestMethod className='Example.Tests.Boundary' name='Equality' /></UnitTest></TestDefinitions>" +
                    $"<ResultSummary><Counters executed='{executed}' passed='{executed - failed}' failed='{failed}' /></ResultSummary></TestRun>";
                File.WriteAllText(Path.Combine(directory, "tests.trx"), report);
                if (baseline)
                    File.WriteAllText(Path.Combine(directory, "second-framework.trx"), EmptyBaselineFramework
                        ? "<TestRun><ResultSummary><Counters executed='0' passed='0' failed='0' /></ResultSummary></TestRun>" : report);
                return new(failed, "", "", baseline && !quick ? 5000 : 1);
            }));
        }
        public async Task Baseline()
        {
            baseline = true;
            Calls.Clear();
            await Executor.VerifyAsync(Request, default);
            Assert.All(TestCalls, c => Assert.Contains("--no-build", c.Arguments));
            Assert.Single(TestCalls); // Baseline still validates both frameworks using the default full run.
            baseline = false;
            Calls.Clear();
        }
        public async Task<MutationResult> Execute()
        {
            var candidate = Assert.Single((await new RoslynMutationDiscoverer().DiscoverAsync(workspace.Root,
                [new("Code.cs", [new(1, 1)], false)], default)).Mutants);
            return await Executor.ExecuteAsync(candidate, new(Request, new AllTestsSelector(Request.Tests, Request.Filter)), default);
        }
        public void Dispose() => workspace.Dispose();
    }
}
