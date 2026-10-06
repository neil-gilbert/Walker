using System.Text;
using Walker.Core;
using Walker.Execution;
using Walker.Roslyn;
using Xunit;
namespace Walker.Tests;
public sealed class ExecutionTests
{
    [Theory]
    [InlineData("compile", MutationOutcome.CompileError)]
    [InlineData("crash", MutationOutcome.TestError)]
    [InlineData("cancel", MutationOutcome.TimedOut)]
    [InlineData("pass", MutationOutcome.Survived)]
    [InlineData("fail", MutationOutcome.Killed)]
    [InlineData("no-tests", MutationOutcome.TestError)]
    [InlineData("test-build", MutationOutcome.TestError)]
    [InlineData("no-report", MutationOutcome.TestError)]
    public async Task RestoresExactDirtyWorkingCopyBytesForAllOutcomes(string mode, MutationOutcome expected)
    {
        using var workspace = new Workspace();
        var source = "// uncommitted user edit\r\nclass C { bool M(int a, int b) => a >= b; }\r\n";
        var encoding = new UTF8Encoding(true);
        var original = encoding.GetPreamble().Concat(encoding.GetBytes(source)).ToArray();
        var path = Path.Combine(workspace.Root, "Code.cs");
        await File.WriteAllBytesAsync(path, original);
        var candidate = Assert.Single((await new RoslynMutationDiscoverer().DiscoverAsync(workspace.Root,
            [new("Code.cs", [new(2, 2)], true)], default)).Mutants);
        var runner = new FakeRunner(request =>
        {
            Assert.Contains("a > b", File.ReadAllText(path));
            if (mode == "cancel") throw new OperationCanceledException();
            if (mode == "crash") throw new IOException("test process crashed");
            if (mode == "compile") return new(1, "compile error", "", 1);
            if (mode == "test-build" && request.Arguments.Contains("Tests.csproj")) return new(1, "compile error", "", 1);
            if (request.Arguments[0] == "test")
            {
                if (mode == "no-report") return new(1, "crash", "", 1);
                var directory = request.Arguments[request.Arguments.ToList().IndexOf("--results-directory") + 1];
                var failed = mode == "fail" ? 1 : 0;
                var total = mode == "no-tests" ? 0 : 1;
                File.WriteAllText(Path.Combine(directory, "tests.trx"), $"<TestRun><ResultSummary><Counters executed='{total}' passed='{total - failed}' failed='{failed}' /><RunInfos><RunInfo outcome='Error'>Test run failed</RunInfo></RunInfos></ResultSummary></TestRun>");
                return new(failed, "", "", 1);
            }
            return new(0, "", "", 1);
        });
        var request = new VerificationRequest(workspace.Root, "HEAD~1", "Code.csproj", ["Tests.csproj"]);
        var result = await new DotnetMutationExecutor(runner).ExecuteAsync(candidate, new(request, new AllTestsSelector(request.Tests)), default);
        Assert.Equal(expected, result.Outcome);
        Assert.Equal(original, await File.ReadAllBytesAsync(path));
    }
    [Fact]
    public async Task RefusesStaleMutationsWithoutOverwritingNewEdits()
    {
        using var workspace = new Workspace();
        workspace.Write("Code.cs", "class C { bool M(int a, int b) => a >= b; }");
        var candidate = Assert.Single((await new RoslynMutationDiscoverer().DiscoverAsync(workspace.Root,
            [new("Code.cs", [new(1, 1)], true)], default)).Mutants);
        workspace.Write("Code.cs", "// newer edit\nclass C { bool M(int a, int b) => a >= b; }");
        var bytes = File.ReadAllBytes(Path.Combine(workspace.Root, "Code.cs"));
        var runner = new FakeRunner(_ => throw new Exception("Must not execute"));
        var request = new VerificationRequest(workspace.Root, "HEAD~1", "Code.csproj", ["Tests.csproj"]);
        var result = await new DotnetMutationExecutor(runner).ExecuteAsync(candidate, new(request, new AllTestsSelector(request.Tests)), default);
        Assert.Equal(MutationOutcome.TestError, result.Outcome);
        Assert.Equal(bytes, File.ReadAllBytes(Path.Combine(workspace.Root, "Code.cs")));
    }
    [Fact]
    public async Task ProcessCancellationKillsChildAndReturnsPromptly()
    {
        if (OperatingSystem.IsWindows()) return;
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new ProcessRunner().RunAsync(new("bash", ["-c", "sleep 30 & wait"], Path.GetTempPath()), cts.Token));
    }
}
internal sealed class FakeRunner(Func<ProcessRequest, ProcessResult> run) : IProcessRunner
{
    public Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken) => Task.FromResult(run(request));
}
