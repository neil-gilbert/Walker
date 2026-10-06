using Walker.Core;
using Walker.Execution;
using Walker.Git;
using Walker.Roslyn;
using Xunit;
namespace Walker.Tests;

public sealed class PerformanceRegressionTests
{
    [Fact]
    public async Task ExitedProcessDoesNotWaitForInheritedDaemonOutputHandles()
    {
        if (OperatingSystem.IsWindows()) return;
        using var workspace = new Workspace();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            var result = await new ProcessRunner().RunAsync(new("bash", ["-c", "sleep 30 & echo $! > child.pid; echo finished"], workspace.Root), timeout.Token)
                .WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(0, result.ExitCode);
            Assert.Contains("finished", result.StandardOutput);
            Assert.True(result.OutputTruncated);
            Assert.InRange(result.DurationMs, 0, 4000);
        }
        finally
        {
            var pidFile = Path.Combine(workspace.Root, "child.pid");
            if (File.Exists(pidFile))
            {
                try { using var child = System.Diagnostics.Process.GetProcessById(int.Parse(File.ReadAllText(pidFile))); child.Kill(); }
                catch (ArgumentException) { }
            }
        }
    }
    [Fact]
    public async Task MutantBuildsReuseRestoreAndStopAfterFirstConfirmedFailure()
    {
        using var workspace = new Workspace();
        workspace.Write("Code.cs", "class C { bool M(int a, int b) => a >= b; }");
        var source = File.ReadAllBytes(Path.Combine(workspace.Root, "Code.cs"));
        var candidate = Assert.Single((await new RoslynMutationDiscoverer().DiscoverAsync(workspace.Root,
            [new("Code.cs", [new(1, 1)], false)], default)).Mutants);
        var calls = new List<ProcessRequest>();
        var baseline = true;
        var runner = new FakeRunner(request =>
        {
            calls.Add(request);
            if (request.Arguments[0] == "test")
            {
                var directory = request.Arguments[request.Arguments.ToList().IndexOf("--results-directory") + 1];
                var failed = baseline ? 0 : 1;
                File.WriteAllText(Path.Combine(directory, "tests.trx"), $"<TestRun><ResultSummary><Counters executed='1' passed='{1 - failed}' failed='{failed}' /></ResultSummary></TestRun>");
                return new(failed, "", "", 1);
            }
            return new(0, "", "", 1);
        });
        var request = new VerificationRequest(workspace.Root, "HEAD~1", "Code.csproj", ["First.csproj", "Second.csproj"]);
        var executor = new DotnetMutationExecutor(runner);
        await executor.VerifyAsync(request, default);
        Assert.Equal(2, calls.Count(c => c.Arguments[0] == "test"));
        Assert.All(calls.Where(c => c.Arguments[0] == "build"), c => Assert.DoesNotContain("--no-restore", c.Arguments));
        baseline = false;
        calls.Clear();
        var result = await executor.ExecuteAsync(candidate, new(request, new AllTestsSelector(request.Tests)), default);
        Assert.Equal(MutationOutcome.Killed, result.Outcome);
        Assert.Single(calls, c => c.Arguments[0] == "test");
        Assert.DoesNotContain(calls, c => c.Arguments.Contains("Second.csproj"));
        Assert.All(calls.Where(c => c.Arguments[0] == "build"), c => Assert.Contains("--no-restore", c.Arguments));
        Assert.Equal(source, File.ReadAllBytes(Path.Combine(workspace.Root, "Code.cs")));
    }

    [Fact]
    public async Task PrunedTraversalRetainsMultilineExpressionsAndDeduplicatesOverlappingRegions()
    {
        using var workspace = new Workspace();
        workspace.Write("Code.cs", "class C {\n bool M(int x) =>\n    x >\n    0;\n bool N(int x) => x == 0;\n bool P(int x) => x < 0;\n}\n");
        var changes = new SourceChange("Code.cs", [new(6, 6), new(4, 4), new(3, 4)], false);
        var result = await new RoslynMutationDiscoverer().DiscoverAsync(workspace.Root, [changes], default);
        Assert.Equal(2, result.Mutants.Count);
        Assert.Contains(result.Mutants, m => m.Member == "C.M" && m.Operator == MutationOperator.ConditionalBoundary);
        Assert.Contains(result.Mutants, m => m.Member == "C.P");
        Assert.DoesNotContain(result.Mutants, m => m.Member == "C.N");
        Assert.Single(result.Mutants.Select(m => m.SourceHash).Distinct());
    }

    [Theory]
    [InlineData("")]
    [InlineData("src/Generated/Code.cs")]
    [InlineData("src/Code.Designer.cs")]
    [InlineData("src/DeletionOnly.cs")]
    public async Task NoEligibleFilesSkipsMsBuildSourceEvaluation(string changedName)
    {
        var hunk = changedName == "src/DeletionOnly.cs" ? "@@ -3,2 +2,0 @@" : "@@ -1 +1 @@";
        var patch = changedName.Length == 0 ? "" : $"diff --git a/{changedName} b/{changedName}\n--- a/{changedName}\n+++ b/{changedName}\n{hunk}\n-old\n+new\n";
        var runner = new FakeRunner(request => new(0, request.Arguments.Contains("diff") ? patch : "abc123\n", "", 0));
        var changes = await new GitChangeProvider(runner, new ForbiddenScope()).GetChangesAsync(
            new(Path.GetTempPath(), "HEAD~1", "Code.csproj", ["Tests.csproj"]), default);
        Assert.Empty(changes);
    }
    private sealed class ForbiddenScope : IProductionSourceScope
    {
        public Task<IReadOnlySet<string>> GetFilesAsync(VerificationRequest request, CancellationToken cancellationToken)
            => throw new InvalidOperationException("No source evaluation should occur without eligible changed files.");
    }
}
