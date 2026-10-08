using Walker.Core;
using Walker.Execution;
using Walker.Roslyn;
using Xunit;

namespace Walker.Tests;

public sealed class DiagnosticTests
{
    [Fact]
    public async Task NoCandidatesHasAStableCauseAndNoRetryAdvice()
    {
        var fake = new FakeEngine([], _ => throw new Exception("Must not execute"));
        var report = await new VerificationEngine(fake, fake, fake, fake).VerifyAsync(Request());
        Assert.Equal(3, report.ExitCode);
        var diagnostic = Assert.Single(report.Diagnostics);
        Assert.Equal("no_eligible_expressions", diagnostic.Code);
        Assert.Equal("discovery", diagnostic.Phase);
        Assert.Empty(diagnostic.Actions);
    }

    [Theory]
    [InlineData(false, "baseline_budget_exhausted")]
    [InlineData(true, "cancelled")]
    public async Task BaselineTimeoutAndCancellationAreDistinct(bool cancel, string code)
    {
        using var cancellation = new CancellationTokenSource();
        var fake = new FakeEngine([DiscoveryTests.Dummy("a")], _ => throw new Exception("Must not execute"));
        var baseline = new Baseline(async token =>
        {
            if (cancel) cancellation.Cancel();
            await Task.Delay(Timeout.Infinite, token);
        });
        var report = await new VerificationEngine(fake, fake, fake, baseline)
            .VerifyAsync(Request() with { TimeoutSeconds = 1 }, cancellation.Token);
        Assert.Equal(3, report.ExitCode);
        Assert.Equal(code, Assert.Single(report.Diagnostics).Code);
        Assert.Equal("baseline", report.Diagnostics[0].Phase);
        Assert.Equal(MutationOutcome.Skipped, Assert.Single(report.Results).Outcome);
    }

    [Theory]
    [InlineData("First explanation")]
    [InlineData("Different human wording")]
    public async Task TypedBaselineFailureDoesNotDependOnMessage(string message)
    {
        var fake = new FakeEngine([DiscoveryTests.Dummy("a")], _ => throw new Exception("Must not execute"));
        var baseline = new Baseline(_ => throw new VerificationException("baseline_build_failed", "baseline", message));
        var report = await new VerificationEngine(fake, fake, fake, baseline).VerifyAsync(Request());
        Assert.Equal(2, report.ExitCode);
        Assert.Equal("baseline_build_failed", Assert.Single(report.Diagnostics).Code);
        Assert.Equal(message, report.Error);
    }

    [Fact]
    public async Task EmptyTestRunHasTypedCauseAtItsOrigin()
    {
        using var workspace = new Workspace();
        var runner = new FakeRunner(request =>
        {
            if (request.Arguments[0] == "test")
            {
                var directory = request.Arguments[request.Arguments.ToList().IndexOf("--results-directory") + 1];
                File.WriteAllText(Path.Combine(directory, "test.trx"), "<TestRun><ResultSummary><Counters executed='0' passed='0' failed='0'/></ResultSummary></TestRun>");
            }
            return new(0, "", "", 1);
        });
        var error = await Assert.ThrowsAsync<VerificationException>(() => new DotnetMutationExecutor(runner).VerifyAsync(Request() with { Root = workspace.Root }, default));
        Assert.Equal("no_executed_tests", error.Diagnostic.Code);
        Assert.Equal("baseline", error.Diagnostic.Phase);
    }

    [Fact]
    public async Task SourceChangedHasTypedCauseAndNeverOverwritesAnEdit()
    {
        using var workspace = new Workspace();
        workspace.Write("Code.cs", "class C { bool M(int a, int b) => a >= b; }");
        var mutant = Assert.Single((await new RoslynMutationDiscoverer().DiscoverAsync(workspace.Root, [new("Code.cs", [new(1, 1)], true)], default)).Mutants);
        workspace.Write("Code.cs", "// new edit\nclass C { bool M(int a, int b) => a >= b; }");
        var source = File.ReadAllBytes(Path.Combine(workspace.Root, "Code.cs"));
        var request = Request() with { Root = workspace.Root };
        var result = await new DotnetMutationExecutor(new FakeRunner(_ => throw new Exception("Must not execute")))
            .ExecuteAsync(mutant, new(request, new AllTestsSelector(request.Tests)), default);
        Assert.Equal("source_changed", Assert.Single(result.Diagnostics).Code);
        Assert.Equal(source, File.ReadAllBytes(Path.Combine(workspace.Root, "Code.cs")));
    }

    private sealed class Baseline(Func<CancellationToken, Task> run) : IBaselineVerifier
    {
        public Task VerifyAsync(VerificationRequest request, CancellationToken token) => run(token);
    }
    private static VerificationRequest Request() => new("root", "HEAD", "Code.csproj", ["Tests.csproj"]);
}
