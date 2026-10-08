using Walker.Core;
using Xunit;

namespace Walker.Tests;

public sealed class TargetedVerificationTests
{
    [Fact]
    public async Task RequestedCandidateBeyondNormalBudgetIsExecutedExactlyOnce()
    {
        var candidates = new[] { DiscoveryTests.Dummy("a"), DiscoveryTests.Dummy("b"), DiscoveryTests.Dummy("c") };
        var fake = new ObservedEngine(candidates);
        var request = Request() with { MaxMutants = 1, MutantIds = ["c", "c"] };
        var report = await Engine(fake).VerifyAsync(request);
        Assert.Equal(["c"], fake.Executed);
        Assert.Equal(1, fake.Baselines);
        Assert.Equal(3, report.MutantsDiscovered);
        Assert.Equal(1, report.MutantsSelected);
        Assert.Equal("explicit", report.Selection.Kind);
        Assert.Equal(["c"], report.Selection.RequestedIds);
        Assert.Equal(request, fake.BaselineRequest);
    }

    [Theory]
    [InlineData("missing", "unknown_mutant")]
    [InlineData("a,missing", "unknown_mutant")]
    [InlineData("a,b", "mutant_limit_exceeded")]
    public async Task InvalidSelectionFailsBeforeBaselineOrMutation(string ids, string code)
    {
        var fake = new ObservedEngine([DiscoveryTests.Dummy("a"), DiscoveryTests.Dummy("b")]);
        var report = await Engine(fake).VerifyAsync(Request() with { MutantIds = ids.Split(','), MaxMutants = 1 });
        Assert.Equal(2, report.ExitCode);
        Assert.Empty(fake.Executed);
        Assert.Equal(0, fake.Baselines);
        Assert.Equal(0, report.MutantsSelected);
        Assert.Equal(code, Assert.Single(report.Diagnostics).Code);
        Assert.Equal(2, report.MutantsDiscovered);
    }

    [Fact]
    public async Task ExplicitSelectionPreservesStatusPrecedenceAndRequestedScope()
    {
        var fake = new ObservedEngine([DiscoveryTests.Dummy("a"), DiscoveryTests.Dummy("b"), DiscoveryTests.Dummy("c")],
            m => m.Id == "b" ? MutationOutcome.TestError : MutationOutcome.Survived);
        var report = await Engine(fake).VerifyAsync(Request() with { MutantIds = ["b", "a"] });
        Assert.Equal(2, report.ExitCode);
        Assert.Equal(["a", "b"], fake.Executed);
        Assert.Equal(1, report.Survived);
        Assert.All(fake.Scopes, scope =>
        {
            Assert.Equal(["Tests.csproj", "Other.Tests.csproj"], scope.Projects);
            Assert.Equal("FullyQualifiedName~Contract", scope.Filter);
        });
    }

    [Fact]
    public async Task CancellationRetainsCompletedTargetAndSkipsOnlyOtherTargets()
    {
        using var cancellation = new CancellationTokenSource();
        var fake = new ObservedEngine([DiscoveryTests.Dummy("a"), DiscoveryTests.Dummy("b"), DiscoveryTests.Dummy("c")], _ =>
        { cancellation.Cancel(); return MutationOutcome.Survived; });
        var report = await Engine(fake).VerifyAsync(Request() with { MutantIds = ["a", "c"] }, cancellation.Token);
        Assert.Equal(3, report.ExitCode);
        Assert.Equal(["a", "c"], report.Results.Select(r => r.Mutant.Id));
        Assert.Equal(1, report.Survived);
        Assert.Equal(1, report.Skipped);
    }

    private static VerificationRequest Request() => new("root", "HEAD", "Code.csproj", ["Tests.csproj", "Other.Tests.csproj"], Filter: "FullyQualifiedName~Contract");
    private static VerificationEngine Engine(ObservedEngine fake) => new(fake, fake, fake, fake);
    private sealed class ObservedEngine(IReadOnlyList<Mutant> mutants, Func<Mutant, MutationOutcome>? outcome = null)
        : IChangeProvider, IMutationDiscoverer, IBaselineVerifier, IMutationExecutor
    {
        public int Baselines { get; private set; }
        public VerificationRequest? BaselineRequest { get; private set; }
        public List<string> Executed { get; } = [];
        public List<TestSelection> Scopes { get; } = [];
        public Task<IReadOnlyList<SourceChange>> GetChangesAsync(VerificationRequest request, CancellationToken token) =>
            Task.FromResult<IReadOnlyList<SourceChange>>([new("Code.cs", [new(1, 1)], true)]);
        public Task<DiscoveryResult> DiscoverAsync(string root, IReadOnlyList<SourceChange> changes, CancellationToken token) => Task.FromResult(new DiscoveryResult(mutants, 0, 0));
        public Task VerifyAsync(VerificationRequest request, CancellationToken token)
        { Baselines++; BaselineRequest = request; return Task.CompletedTask; }
        public async Task<MutationResult> ExecuteAsync(Mutant mutant, VerificationContext context, CancellationToken token)
        { Executed.Add(mutant.Id); Scopes.Add(await context.TestSelector.SelectTestsAsync(mutant, token)); return new(mutant, outcome?.Invoke(mutant) ?? MutationOutcome.Killed); }
    }
}
