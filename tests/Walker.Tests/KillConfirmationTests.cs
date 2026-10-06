using System.Security;
using System.Text;
using System.Text.Json;
using Walker.Core;
using Walker.Execution;
using Walker.Roslyn;
using Xunit;
namespace Walker.Tests;

public sealed class KillConfirmationTests
{
    [Theory]
    [InlineData("pass", true, MutationOutcome.Killed, true, 3)]
    [InlineData("flaky", true, MutationOutcome.TestError, false, 3)]
    [InlineData("cancel", true, MutationOutcome.TimedOut, null, 3)]
    [InlineData("empty", true, MutationOutcome.TestError, null, 3)]
    [InlineData("crash", true, MutationOutcome.TestError, null, 3)]
    [InlineData("bad-build", true, MutationOutcome.TestError, null, 2)]
    [InlineData("no-identity", true, MutationOutcome.TestError, null, 2)]
    [InlineData("unsafe-name", true, MutationOutcome.TestError, null, 2)]
    [InlineData("too-many", true, MutationOutcome.TestError, null, 2)]
    [InlineData("flaky", false, MutationOutcome.Killed, null, 2)]
    public async Task ConfirmationRunsOnlyAfterRestoreAndDistinguishesFlakiness(string mode, bool confirm,
        MutationOutcome expected, bool? confirmed, int testRuns)
    {
        using var workspace = new Workspace();
        var source = "// dirty user change\r\nclass C { bool M(int a, int b) => a >= b; }\r\n";
        var encoding = new UTF8Encoding(true);
        var bytes = encoding.GetPreamble().Concat(encoding.GetBytes(source)).ToArray();
        var path = Path.Combine(workspace.Root, "Code.cs");
        await File.WriteAllBytesAsync(path, bytes);
        var candidate = Assert.Single((await new RoslynMutationDiscoverer().DiscoverAsync(workspace.Root,
            [new("Code.cs", [new(2, 2)], true)], default)).Mutants);
        using var cancelled = new CancellationTokenSource();
        var runs = 0;
        var request = new VerificationRequest(workspace.Root, "HEAD~1", "Code.csproj", ["Tests.csproj"],
            Filter: "FullyQualifiedName~Trial&Category=Integration", ConfirmKills: confirm);
        var runner = new AsyncRunner(async (process, token) =>
        {
            if (process.Arguments[0] == "build")
            {
                if (runs >= 2)
                {
                    Assert.Equal(bytes, await File.ReadAllBytesAsync(path));
                    Assert.False(File.Exists(MutationJournal.PathFor(workspace.Root)));
                    if (mode == "bad-build") return new(1, "build failed", "", 2);
                }
                return new(0, "", "", 2);
            }
            runs++;
            var filter = process.Arguments[process.Arguments.ToList().IndexOf("--filter") + 1];
            if (runs <= 2) Assert.Equal(request.Filter, filter);
            else
            {
                Assert.Equal(bytes, await File.ReadAllBytesAsync(path));
                Assert.False(File.Exists(MutationJournal.PathFor(workspace.Root)));
                Assert.Equal("(FullyQualifiedName~Trial&Category=Integration)&(FullyQualifiedName=Trial.Tests.DebitTests.EqualBalance)", filter);
                if (mode == "cancel") { cancelled.Cancel(); token.ThrowIfCancellationRequested(); }
                if (mode == "crash") throw new IOException("runner crashed");
            }
            var failed = runs == 2 || (runs == 3 && mode == "flaky") ? (mode == "too-many" ? 12 : 1) : 0;
            var executed = mode == "empty" && runs == 3 ? 0 : Math.Max(1, failed);
            WriteReport(process, executed, failed, mode != "no-identity",
                mode == "unsafe-name" ? "Trial.Tests.Debit|Tests" : "Trial.Tests.DebitTests");
            return new(failed > 0 ? 1 : 0, "", "", 2);
        });
        var executor = new DotnetMutationExecutor(runner);
        await executor.VerifyAsync(request, cancelled.Token);
        var result = await executor.ExecuteAsync(candidate,
            new(request, new AllTestsSelector(request.Tests, request.Filter)), cancelled.Token);
        Assert.Equal(expected, result.Outcome);
        Assert.Equal(confirmed, result.KillConfirmed);
        Assert.Equal(testRuns, runs);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(path));
        Assert.False(File.Exists(MutationJournal.PathFor(workspace.Root)));
        Assert.Equal(mode == "too-many" ? 10 : 1, result.FailingTests!.Count);
        Assert.Contains("EqualBalance(row: 0)", result.FailingTests);
        if (mode == "flaky" && confirm) Assert.Contains("not caused by the mutant", result.Detail);
        if (confirmed == true) Assert.Contains("unmutated source", result.Detail);
        if (mode == "no-identity" || mode == "too-many" || mode == "unsafe-name")
            Assert.Contains("No unmutated tests were run", result.Detail);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailureNamesAreDecodedBoundedAndWorkWithDefinitionsBeforeOrAfterResults(bool definitionsFirst)
    {
        using var workspace = new Workspace();
        workspace.Write("Code.cs", "class C { bool M(int a, int b) => a >= b; }");
        var candidate = Assert.Single((await new RoslynMutationDiscoverer().DiscoverAsync(workspace.Root,
            [new("Code.cs", [new(1, 1)], false)], default)).Mutants);
        var runner = new FakeRunner(process =>
        {
            if (process.Arguments[0] == "test")
            {
                WriteReport(process, 12, 12, definitionsFirst: definitionsFirst, displayName: "Boundary(a: 0, b: 1 & <2>)");
                return new(1, "", "", 1);
            }
            return new(0, "", "", 1);
        });
        var request = new VerificationRequest(workspace.Root, "HEAD", "Code.csproj", ["Tests.csproj"]);
        var result = await new DotnetMutationExecutor(runner).ExecuteAsync(candidate,
            new(request, new AllTestsSelector(request.Tests)), default);
        Assert.Equal(MutationOutcome.Killed, result.Outcome);
        Assert.Equal(10, result.FailingTests!.Count);
        Assert.Equal("Boundary(a: 0, b: 1 & <2>)", result.FailingTests[0]);
        var json = JsonSerializer.Serialize(result, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        using var document = JsonDocument.Parse(json);
        Assert.Equal(10, document.RootElement.GetProperty("failingTests").GetArrayLength());
    }
    private static void WriteReport(ProcessRequest request, int executed, int failed, bool definitions = true,
        string type = "Trial.Tests.DebitTests", bool definitionsFirst = false, string? displayName = null)
    {
        var results = "<Results>" + string.Concat(Enumerable.Range(0, failed).Select(i =>
            $"<UnitTestResult testId='id{i}' testName='{SecurityElement.Escape(displayName ?? $"EqualBalance(row: {i})")}' outcome='Failed' />")) + "</Results>";
        var methods = definitions ? "<TestDefinitions>" + string.Concat(Enumerable.Range(0, failed).Select(i =>
            $"<UnitTest id='id{i}'><TestMethod className='{SecurityElement.Escape(type)}' name='EqualBalance' /></UnitTest>")) + "</TestDefinitions>" : "";
        var report = "<TestRun xmlns='http://microsoft.com/schemas/VisualStudio/TeamTest/2010'>"
            + (definitionsFirst ? methods + results : results + methods)
            + $"<ResultSummary outcome='{(failed > 0 ? "Failed" : "Completed")}'><Counters executed='{executed}' passed='{executed - failed}' failed='{failed}' /></ResultSummary></TestRun>";
        var directory = request.Arguments[request.Arguments.ToList().IndexOf("--results-directory") + 1];
        File.WriteAllText(Path.Combine(directory, "tests.trx"), report);
    }
}
