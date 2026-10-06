using Walker.Core;
using Xunit;
namespace Walker.Tests;
public sealed class EngineTests
{
    [Fact]
    public async Task CancellationPreservesCompletedResultsAndSkipsRemaining()
    {
        var candidates = new[] { DiscoveryTests.Dummy("a"), DiscoveryTests.Dummy("b"), DiscoveryTests.Dummy("c") };
        using var cancellation = new CancellationTokenSource();
        var fake = new FakeEngine(candidates, m => { cancellation.Cancel(); return new(m, MutationOutcome.Survived); });
        var result = await new VerificationEngine(fake, fake, fake, fake).VerifyAsync(Request(), cancellation.Token);
        Assert.Equal("incomplete", result.Status);
        Assert.Equal(3, result.ExitCode);
        Assert.Equal(1, result.Survived);
        Assert.Equal(2, result.Skipped);
    }
    [Fact]
    public async Task GlobalBudgetCancelsDiscoveryInsteadOfPassing()
    {
        var fake = new FakeEngine([], _ => throw new Exception("Should not execute"));
        var result = await new VerificationEngine(new SlowChanges(), fake, fake, fake)
            .VerifyAsync(Request() with { TimeoutSeconds = 1 });
        Assert.Equal(3, result.ExitCode);
        Assert.Equal(0, result.MutantsExecuted);
        Assert.InRange(result.DurationMs, 800, 5000);
    }
    private sealed class SlowChanges : IChangeProvider
    {
        public async Task<IReadOnlyList<SourceChange>> GetChangesAsync(VerificationRequest request, CancellationToken cancellationToken)
        {
            await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken);
            return [];
        }
    }
    [Fact]
    public async Task BaselineFailureDoesNotExecuteMutants()
    {
        var fake = new FakeEngine([DiscoveryTests.Dummy("a")], _ => throw new Exception("Should not execute"), baselineFails: true);
        var result = await new VerificationEngine(fake, fake, fake, fake).VerifyAsync(Request());
        Assert.Equal(2, result.ExitCode);
        Assert.Equal(0, result.MutantsExecuted);
        Assert.Equal(1, result.Skipped);
    }
    [Fact]
    public async Task EmptyDiscoveryCannotPass()
    {
        var fake = new FakeEngine([], _ => throw new Exception("Should not execute"));
        var result = await new VerificationEngine(fake, fake, fake, fake).VerifyAsync(Request());
        Assert.Equal(3, result.ExitCode);
    }
    [Theory]
    [InlineData(MutationOutcome.Killed, 0)]
    [InlineData(MutationOutcome.Survived, 1)]
    [InlineData(MutationOutcome.CompileError, 2)]
    [InlineData(MutationOutcome.TestError, 2)]
    [InlineData(MutationOutcome.TimedOut, 3)]
    public async Task MapsOutcomesToExitCodes(MutationOutcome outcome, int code)
    {
        var fake = new FakeEngine([DiscoveryTests.Dummy("a")], m => new(m, outcome));
        var result = await new VerificationEngine(fake, fake, fake, fake).VerifyAsync(Request());
        Assert.Equal(code, result.ExitCode);
    }
    [Theory]
    [InlineData("budget", 3)]
    [InlineData("cancel", 3)]
    [InlineData("failure", 2)]
    public async Task UnfinishedBaselineRetainsTimingAndExplainsSkippedMutants(string mode, int exitCode)
    {
        var fake = new FakeEngine([DiscoveryTests.Dummy("a")], _ => throw new Exception("Must not execute"));
        using var cancellation = new CancellationTokenSource();
        var baseline = new Baseline(async token =>
        {
            if (mode == "budget") await Task.Delay(Timeout.Infinite, token);
            else
            {
                await Task.Delay(30, token);
                if (mode == "cancel") { cancellation.Cancel(); token.ThrowIfCancellationRequested(); }
                throw new InvalidOperationException("Baseline build failed");
            }
        });
        var result = await new VerificationEngine(fake, fake, fake, baseline)
            .VerifyAsync(Request() with { TimeoutSeconds = 1 }, cancellation.Token);
        Assert.Equal(exitCode, result.ExitCode);
        Assert.True(result.Timings.BaselineMs > 0);
        Assert.Equal(0, result.MutantsExecuted);
        Assert.Contains("baseline did not complete", Assert.Single(result.Results).Detail);
        if (mode == "budget")
        {
            Assert.Contains("during the baseline before any mutant could start", result.Error);
            Assert.Contains("smaller than one baseline run", result.Error);
            Assert.Contains("--filter", result.Error);
            Assert.Contains("--timeout", result.Error);
        }
        if (mode == "cancel") Assert.Contains("Baseline cancelled", result.Error);
    }
    private sealed class Baseline(Func<CancellationToken, Task> run) : IBaselineVerifier
    {
        public Task VerifyAsync(VerificationRequest request, CancellationToken token) => run(token);
    }
    private static VerificationRequest Request() => new("root", "HEAD~1", "project", ["tests"]);
}
internal sealed class FakeEngine(IReadOnlyList<Walker.Core.Mutant> mutants, Func<Walker.Core.Mutant, MutationResult> execute,
    bool baselineFails = false) : IChangeProvider, IMutationDiscoverer, IMutationExecutor, IBaselineVerifier
{
    public Task<IReadOnlyList<SourceChange>> GetChangesAsync(VerificationRequest request, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<SourceChange>>([new("Code.cs", [new(1, 1)], false)]);
    public Task<DiscoveryResult> DiscoverAsync(string root, IReadOnlyList<SourceChange> changes, CancellationToken cancellationToken) => Task.FromResult(new DiscoveryResult(mutants, 0, 0));
    public Task<MutationResult> ExecuteAsync(Walker.Core.Mutant mutant, VerificationContext context, CancellationToken cancellationToken) => Task.FromResult(execute(mutant));
    public Task VerifyAsync(VerificationRequest request, CancellationToken cancellationToken) => baselineFails ? Task.FromException(new InvalidOperationException("Baseline failed")) : Task.CompletedTask;
}
