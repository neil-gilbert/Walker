using System.Security;
using System.Text.Json;
using Walker.Core;
using Walker.Execution;
using Walker.Roslyn;
using Xunit;
namespace Walker.Tests;

public sealed class PreferredTestPlannerTests
{
    [Fact]
    public async Task SameMemberReusesItsValidatedSingletonAfterAnotherMemberFails()
    {
        using var fixture = new Fixture();
        await fixture.Baseline();
        Assert.Equal(MutationOutcome.Killed, (await fixture.Execute("A", "Example.Tests.A")).Outcome);
        Assert.Equal(MutationOutcome.Killed, (await fixture.Execute("B", "Example.Tests.B" )).Outcome);
        fixture.Calls.Clear();
        Assert.Equal(MutationOutcome.Killed, (await fixture.Execute("A", "Example.Tests.A")).Outcome);
        var attempt = Assert.Single(fixture.Tests);
        Assert.Equal("(Category=Unit)&(FullyQualifiedName=Example.Tests.A)", Argument(attempt, "--filter"));
        Assert.DoesNotContain("--no-build", attempt.Arguments);
        fixture.Calls.Clear();
        Assert.Equal(MutationOutcome.Killed, (await fixture.Execute("B", "Example.Tests.B")).Outcome);
        Assert.Equal(2, fixture.Tests.Count); // B's own singleton requires its own unmutated validation.
        Assert.All(fixture.Tests, call => Assert.Equal("(Category=Unit)&(FullyQualifiedName=Example.Tests.B)", Argument(call, "--filter")));
        Assert.Equal(fixture.Original, File.ReadAllBytes(fixture.Source));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TwoConsecutiveMissesStopPreferredStartupsUntilANewBaseline(bool emptyPreferred)
    {
        using var fixture = new Fixture();
        await fixture.Baseline();
        await fixture.Execute("A", "Example.Tests.A");
        fixture.EmptyMutatedPreferred = emptyPreferred;
        await fixture.Execute("A", "Example.Tests.B");
        await fixture.Execute("A", "Example.Tests.B");
        fixture.Calls.Clear();
        Assert.Equal(MutationOutcome.Killed, (await fixture.Execute("A", "Example.Tests.B")).Outcome);
        Assert.Equal(fixture.Request.Filter, Argument(Assert.Single(fixture.Tests), "--filter"));
        await fixture.Baseline();
        fixture.EmptyMutatedPreferred = false;
        await fixture.Execute("A", "Example.Tests.A");
        fixture.Calls.Clear();
        await fixture.Execute("A", "Example.Tests.A");
        Assert.Equal(2, fixture.Tests.Count); // A new unmutated validation, then a preferred kill.
        Assert.Equal(fixture.Original, File.ReadAllBytes(fixture.Source));
    }

    private static string? Argument(ProcessRequest request, string name)
    {
        var index = request.Arguments.ToList().IndexOf(name);
        return index < 0 ? null : request.Arguments[index + 1];
    }

    [Fact]
    public async Task SlowSecondProjectDoesNotEnablePreferredStartupsForFastFirstProject()
    {
        using var fixture = new Fixture(quickFirstProject: true);
        await fixture.Baseline();
        await fixture.Execute("A", "Example.Tests.A");
        fixture.Calls.Clear();
        Assert.Equal(MutationOutcome.Killed, (await fixture.Execute("A", "Example.Tests.A")).Outcome);
        Assert.Equal(fixture.Request.Filter, Argument(Assert.Single(fixture.Tests), "--filter"));
    }

    [Theory]
    [InlineData("failure")]
    [InlineData("empty")]
    [InlineData("hang")]
    public async Task RejectedUnmutatedScopeUsesFullTestsAndIsNotValidatedAgain(string rejection)
    {
        using var fixture = new Fixture();
        await fixture.Baseline();
        await fixture.Execute("A", "Example.Tests.A");
        fixture.ValidationRejection = rejection;
        fixture.Calls.Clear();
        Assert.Equal(MutationOutcome.Killed, (await fixture.Execute("A", "Example.Tests.A")).Outcome);
        Assert.Equal(2, fixture.Tests.Count);
        Assert.Equal(fixture.Request.Filter, Argument(fixture.Tests[^1], "--filter"));
        Assert.DoesNotContain("--no-build", fixture.Tests[^1].Arguments);
        fixture.Calls.Clear();
        Assert.Equal(MutationOutcome.Killed, (await fixture.Execute("A", "Example.Tests.A")).Outcome);
        Assert.Equal(fixture.Request.Filter, Argument(Assert.Single(fixture.Tests), "--filter"));
        Assert.Equal(fixture.Original, File.ReadAllBytes(fixture.Source));
    }

    [Fact]
    public async Task ChangedUnmutatedSourceRequiresNewExactValidation()
    {
        using var fixture = new Fixture();
        await fixture.Baseline();
        await fixture.Execute("A", "Example.Tests.A");
        await fixture.Execute("A", "Example.Tests.A");
        fixture.ChangeSource();
        fixture.Calls.Clear();
        Assert.Equal(MutationOutcome.Killed, (await fixture.Execute("A", "Example.Tests.A")).Outcome);
        Assert.Equal(2, fixture.Tests.Count);
        Assert.All(fixture.Tests, call => Assert.Equal("(Category=Unit)&(FullyQualifiedName=Example.Tests.A)", Argument(call, "--filter")));
        Assert.Equal(fixture.Original, File.ReadAllBytes(fixture.Source));
    }

    [Fact]
    public async Task AmbiguousFirstKillRetainsItsMethodsWithCanonicalOrderAndEscapedCommas()
    {
        using var fixture = new Fixture();
        await fixture.Baseline();
        await fixture.Execute("A", "Example.Tests.Z", "Example.Tests.A<System.Int32,System.String>");
        fixture.Calls.Clear();
        Assert.Equal(MutationOutcome.Killed, (await fixture.Execute("A", "Example.Tests.Z", "Example.Tests.A<System.Int32,System.String>")).Outcome);
        Assert.Equal(2, fixture.Tests.Count);
        Assert.All(fixture.Tests, call => Assert.Equal("(Category=Unit)&(FullyQualifiedName=Example.Tests.A<System.Int32%2CSystem.String>|FullyQualifiedName=Example.Tests.Z)", Argument(call, "--filter")));
        fixture.Calls.Clear();
        await fixture.Execute("A", "Example.Tests.A<System.Int32,System.String>", "Example.Tests.Z");
        Assert.Single(fixture.Tests);
    }

    [Fact]
    public async Task AmbiguousFirstKillDoesNotDiscardTheTestThatCoversAnotherMember()
    {
        using var fixture = new Fixture();
        await fixture.Baseline();
        await fixture.Execute("A", "Example.Tests.A", "Example.Tests.B");
        fixture.Calls.Clear();
        Assert.Equal(MutationOutcome.Killed, (await fixture.Execute("B", "Example.Tests.B")).Outcome);
        Assert.Equal(2, fixture.Tests.Count); // Validate the complete learned group, then kill with it.
        Assert.All(fixture.Tests, call => Assert.Equal("(Category=Unit)&(FullyQualifiedName=Example.Tests.A|FullyQualifiedName=Example.Tests.B)", Argument(call, "--filter")));
        Assert.DoesNotContain("--no-build", fixture.Tests[^1].Arguments);
        Assert.Equal(fixture.Original, File.ReadAllBytes(fixture.Source));
    }

    [Fact]
    public async Task APreferredKillResetsTheConsecutiveMissCount()
    {
        using var fixture = new Fixture();
        await fixture.Baseline();
        await fixture.Execute("A", "Example.Tests.A");
        await fixture.Execute("A", "Example.Tests.B"); // Miss.
        await fixture.Execute("A", "Example.Tests.A"); // Hit resets the streak.
        await fixture.Execute("A", "Example.Tests.B"); // First miss in a new streak.
        fixture.Calls.Clear();
        await fixture.Execute("A", "Example.Tests.A");
        Assert.Single(fixture.Tests);
        Assert.Contains("FullyQualifiedName=Example.Tests.A", Argument(fixture.Tests[0], "--filter"));
    }

    [Fact]
    public async Task DifferentOriginalFilterCannotReuseValidationOrHistory()
    {
        using var fixture = new Fixture();
        await fixture.Baseline();
        await fixture.Execute("A", "Example.Tests.A");
        await fixture.Execute("A", "Example.Tests.A");
        fixture.Calls.Clear();
        var request = fixture.Request with { Filter = "Category=Integration" };
        Assert.Equal(MutationOutcome.Killed, (await fixture.ExecuteWith(request, "A", "Example.Tests.A")).Outcome);
        Assert.Equal("Category=Integration", Argument(Assert.Single(fixture.Tests), "--filter"));
    }

    private sealed class Fixture : IDisposable
    {
        private readonly Workspace workspace = new();
        private IReadOnlyList<Mutant> mutants;
        private bool baseline;
        private string[] failing = [];
        public List<ProcessRequest> Calls { get; } = [];
        public List<ProcessRequest> Tests => Calls.Where(c => c.Arguments[0] == "test").ToList();
        public string Source => Path.Combine(workspace.Root, "Code.cs");
        public byte[] Original { get; private set; }
        public string? ValidationRejection { get; set; }
        public bool EmptyMutatedPreferred { get; set; }
        public VerificationRequest Request { get; }
        public DotnetMutationExecutor Executor { get; }
        public Fixture(bool quickFirstProject = false)
        {
            workspace.Write("Code.cs", "class C { bool A(int a, int b) => a >= b; bool B(int a, int b) => a >= b; }");
            Original = File.ReadAllBytes(Source);
            mutants = new RoslynMutationDiscoverer().DiscoverAsync(workspace.Root, [new("Code.cs", [new(1, 1)], false)], default).GetAwaiter().GetResult().Mutants;
            Request = new(workspace.Root, "HEAD", "Code.csproj", quickFirstProject ? ["Tests.csproj", "SlowTests.csproj"] : ["Tests.csproj"], Filter: "Category=Unit");
            Executor = new(new FakeRunner(process =>
            {
                Calls.Add(process);
                if (process.Arguments[0] == "build")
                {
                    var reference = new { FullPath = Path.Combine(workspace.Root, "Code.csproj"), BuildReference = "true", HasSingleTargetFramework = "true", NearestTargetFramework = "net8.0" };
                    return new(0, JsonSerializer.Serialize(new { Properties = new { TargetFramework = "net8.0", TargetFrameworks = "", Configuration = "Debug", Platform = "AnyCPU", RuntimeIdentifier = "", BuildProjectReferences = "true" },
                        Items = new { _MSBuildProjectReferenceExistent = new[] { reference } } }), "", 1);
                }
                var mutated = !File.ReadAllBytes(Source).SequenceEqual(Original);
                var filter = Argument(process, "--filter")!;
                var validating = !mutated && filter.Contains("FullyQualifiedName=", StringComparison.Ordinal);
                if (validating && ValidationRejection == "hang") throw new OperationCanceledException();
                var failures = mutated ? failing.Where(name => !filter.Contains("FullyQualifiedName=", StringComparison.Ordinal)
                    || filter.Contains("FullyQualifiedName=" + name.Replace(",", "%2C"), StringComparison.Ordinal)).ToArray() : [];
                if (validating && ValidationRejection == "failure") failures = ["Example.Tests.A"];
                var directory = Argument(process, "--results-directory")!;
                var results = string.Concat(failures.Select((name, i) => $"<UnitTestResult testId='id{i}' testName='{SecurityElement.Escape(name)}' outcome='Failed' />"));
                var definitions = string.Concat(failures.Select(Definition));
                var executed = (validating && ValidationRejection == "empty") || (mutated && EmptyMutatedPreferred && filter.Contains("FullyQualifiedName=", StringComparison.Ordinal))
                    ? 0 : Math.Max(1, failures.Length);
                File.WriteAllText(Path.Combine(directory, "tests.trx"), $"<TestRun><Results>{results}</Results><TestDefinitions>{definitions}</TestDefinitions><ResultSummary><Counters executed='{executed}' passed='{executed - failures.Length}' failed='{failures.Length}' /></ResultSummary></TestRun>");
                return new(failures.Length > 0 ? 1 : 0, "", "", baseline ? quickFirstProject && process.Arguments[1] == "Tests.csproj" ? 1000 : 5000 : 1);
            }));
        }
        public async Task Baseline()
        {
            baseline = true;
            await Executor.VerifyAsync(Request, default);
            baseline = false;
            Calls.Clear();
        }
        public Task<MutationResult> Execute(string member, params string[] failures)
            => ExecuteWith(Request, member, failures);
        public Task<MutationResult> ExecuteWith(VerificationRequest request, string member, params string[] failures)
        {
            failing = failures;
            var mutant = Assert.Single(mutants, m => m.Member.EndsWith("." + member, StringComparison.Ordinal));
            return Executor.ExecuteAsync(mutant, new(request, new AllTestsSelector(request.Tests, request.Filter)), default);
        }
        public void ChangeSource()
        {
            File.AppendAllText(Source, "\n// Changed unmutated input\n");
            Original = File.ReadAllBytes(Source);
            mutants = new RoslynMutationDiscoverer().DiscoverAsync(workspace.Root, [new("Code.cs", [new(1, 1)], false)], default).GetAwaiter().GetResult().Mutants;
        }
        private static string Definition(string name, int index)
        {
            var generic = name.IndexOf('<');
            var separator = name.LastIndexOf('.', generic < 0 ? name.Length - 1 : generic);
            return $"<UnitTest id='id{index}'><TestMethod className='{SecurityElement.Escape(name[..separator])}' name='{SecurityElement.Escape(name[(separator + 1)..])}' /></UnitTest>";
        }
        public void Dispose() => workspace.Dispose();
    }
}
