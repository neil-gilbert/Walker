using System.Text.Json;
using Walker.Core;
using Walker.Execution;
using Xunit;

namespace Walker.Tests;

public sealed class AgentWorkflowTests
{
    [Fact]
    public void InvestigationGroupsOnlySurvivorsWithoutChangingVerdict()
    {
        var a = DiscoveryTests.Dummy("a");
        var report = new VerificationResult("failed", "HEAD", 1, 3, 3,
            [new(a, MutationOutcome.Survived), new(a with { Id = "b" }, MutationOutcome.Survived), new(a with { Id = "c" }, MutationOutcome.Killed)], 1, new());
        var enriched = AgentWorkflows.Investigate(report);
        Assert.Equal(1, enriched.ExitCode);
        Assert.Equal(report.Results, enriched.Results);
        Assert.Equal(["a", "b"], Assert.Single(enriched.Investigation!.Gaps).MutantIds);
        Assert.Null(report.Investigation);
    }

    [Theory]
    [InlineData("wrong-hash", "balance >= price", 0, "challenge_stale")]
    [InlineData(null, "wrong-original", 0, "challenge_stale")]
    [InlineData(null, "balance >= price", 999, "challenge_stale")]
    [InlineData(null, "balance >= price", 0, null)]
    public async Task ExplicitFaultsRequireExactProductionSource(string? hash, string original, int span, string? error)
    {
        using var workspace = new Workspace();
        const string source = "balance >= price";
        workspace.Write("Code/Code.cs", source);
        var request = Request(workspace);
        var manifest = new ChallengeManifest(1, [new("Code/Code.cs", hash ?? Mutant.Hash(source), span, original, "balance > price", "Exact balance", "Equality permits purchase")]);
        var discovery = new ChallengeDiscovery(manifest, new Scope(Path.Combine(workspace.Root, "Code/Code.cs")));
        if (error != null)
        {
            var ex = await Assert.ThrowsAsync<VerificationException>(() => discovery.GetChangesAsync(request, default));
            Assert.Equal(error, ex.Diagnostic.Code);
        }
        else
        {
            var files = await discovery.GetChangesAsync(request, default);
            var found = await discovery.DiscoverAsync(workspace.Root, files, default);
            Assert.Equal(MutationOperator.CustomFault, Assert.Single(found.Mutants).Operator);
            Assert.Equal(1, found.Mutants[0].Line);
        }
        Assert.Equal(source, File.ReadAllText(Path.Combine(workspace.Root, "Code/Code.cs")));
    }

    [Theory]
    [InlineData("../Code.cs")]
    [InlineData("Code/bin/Code.cs")]
    [InlineData("Code/obj/Code.cs")]
    [InlineData("Code/.git/Code.cs")]
    [InlineData("Code/Code.csproj")]
    public void ExplicitWorkflowRejectsEscapingAndBuildPaths(string file)
    {
        using var workspace = new Workspace();
        Assert.Throws<VerificationException>(() => AgentWorkflows.ResolveSource(workspace.Root, file));
    }

    [Fact]
    public void ChallengesNeverSilentlyDropFaultsOutsideBudget()
    {
        var fault = new FaultChallenge("Code.cs", "hash", 0, "true", "false", "Concern", "Contract");
        Assert.Throws<ArgumentException>(() => AgentWorkflows.Validate(new ChallengeManifest(1, [fault, fault]), 1));
    }

    [Theory]
    [InlineData(MutationOutcome.Killed, true, "verified", 0)]
    [InlineData(MutationOutcome.Killed, false, "not_verified", 3)]
    [InlineData(MutationOutcome.Hung, null, "not_verified", 3)]
    [InlineData(MutationOutcome.Survived, null, "not_verified", 1)]
    public async Task PairedVerificationRequiresConfirmedFailuresAndRestoresTests(MutationOutcome outcome, bool? confirmed, string status, int exit)
    {
        using var workspace = new Workspace();
        workspace.Write("Code/Code.cs", "production");
        workspace.Write("Tests/Tests.cs", "original tests");
        var path = Path.Combine(workspace.Root, "Tests/Tests.cs");
        var original = File.ReadAllBytes(path);
        var patch = new TestPatchManifest(1, "Equality permits purchase", [new("Tests/Tests.cs", AgentWorkflows.ByteHash(original), "improved tests")]);
        var calls = 0;
        var report = await AgentWorkflows.VerifyTestPatchAsync(Request(workspace), patch, new ScopeRunner(), (request, _) =>
        {
            calls++;
            if (calls == 1)
            {
                Assert.Equal("original tests", File.ReadAllText(path));
                return Task.FromResult(Result("failed", MutationOutcome.Survived));
            }
            Assert.Equal("improved tests", File.ReadAllText(path));
            Assert.True(request.ConfirmKills);
            Assert.Equal(["survivor"], request.MutantIds);
            return Task.FromResult(Result(outcome == MutationOutcome.Survived ? "failed" : "passed", outcome, confirmed));
        }, default);
        Assert.Equal(2, calls);
        Assert.Equal(status, report.TestImprovement!.Status);
        Assert.Equal(exit, report.ExitCode);
        Assert.Equal(original, File.ReadAllBytes(path));
        Assert.Equal("production", File.ReadAllText(Path.Combine(workspace.Root, "Code/Code.cs")));
        ReportContract.AssertValid(JsonSerializer.Serialize(report, VerificationReportJson.Options));
    }

    [Fact]
    public async Task FailedSecondBaselineRetainsOriginalEvidenceAndRemovesProposedNewTest()
    {
        using var workspace = new Workspace();
        workspace.Write("Code/Code.cs", "production");
        workspace.Write("Tests/Tests.cs", "original tests");
        var patch = new TestPatchManifest(1, "contract", [new("Tests/NewTest.cs", null, "new test")]);
        var calls = 0;
        var report = await AgentWorkflows.VerifyTestPatchAsync(Request(workspace), patch, new ScopeRunner(), (_, _) =>
            Task.FromResult(++calls == 1 ? Result("failed", MutationOutcome.Survived) : Result("error", MutationOutcome.Skipped)), default);
        Assert.Equal(2, report.ExitCode);
        Assert.Equal("Survived", report.TestImprovement!.Before.Results[0].Outcome.ToString());
        Assert.False(File.Exists(Path.Combine(workspace.Root, "Tests/NewTest.cs")));
    }

    [Fact]
    public async Task StaleAndProductionTestPatchesFailBeforeAnyExecution()
    {
        using var workspace = new Workspace();
        workspace.Write("Code/Code.cs", "production");
        workspace.Write("Tests/Tests.cs", "tests");
        foreach (var file in new[] { new TestFilePatch("Code/Code.cs", "wrong", "patch"), new TestFilePatch("Tests/Tests.cs", "wrong", "patch") })
        {
            await Assert.ThrowsAsync<VerificationException>(() => AgentWorkflows.VerifyTestPatchAsync(Request(workspace), new(1, "contract", [file]), new ScopeRunner(),
                (_, _) => throw new Exception("Must fail before executing"), default));
        }
        Assert.Equal("production", File.ReadAllText(Path.Combine(workspace.Root, "Code/Code.cs")));
        Assert.Equal("tests", File.ReadAllText(Path.Combine(workspace.Root, "Tests/Tests.cs")));
    }

    [Fact]
    public async Task CancellationAfterApplyingPatchRestoresTestsAndPreservesBeforeReport()
    {
        using var workspace = new Workspace();
        workspace.Write("Code/Code.cs", "production");
        workspace.Write("Tests/Tests.cs", "tests");
        var patch = new TestPatchManifest(1, "contract", [new("Tests/Tests.cs", AgentWorkflows.ByteHash(File.ReadAllBytes(Path.Combine(workspace.Root, "Tests/Tests.cs"))), "improved")]);
        var calls = 0;
        var report = await AgentWorkflows.VerifyTestPatchAsync(Request(workspace), patch, new ScopeRunner(), (_, _) =>
            ++calls == 1 ? Task.FromResult(Result("failed", MutationOutcome.Survived)) : throw new OperationCanceledException(), default);
        Assert.Equal(3, report.ExitCode);
        Assert.NotNull(report.TestImprovement);
        Assert.Equal("tests", File.ReadAllText(Path.Combine(workspace.Root, "Tests/Tests.cs")));
    }

    [Fact]
    public async Task TestReplacementCannotLosePreviouslyObservedProtection()
    {
        using var workspace = new Workspace();
        workspace.Write("Code/Code.cs", "production");
        workspace.Write("Tests/Tests.cs", "tests");
        var patch = new TestPatchManifest(1, "contract", [new("Tests/Tests.cs", AgentWorkflows.ByteHash(File.ReadAllBytes(Path.Combine(workspace.Root, "Tests/Tests.cs"))), "improved")]);
        var calls = 0;
        var report = await AgentWorkflows.VerifyTestPatchAsync(Request(workspace), patch, new ScopeRunner(), (request, _) =>
        {
            if (++calls == 1) return Task.FromResult(new VerificationResult("failed", "HEAD", 1, 2, 2,
                [new(DiscoveryTests.Dummy("survivor"), MutationOutcome.Survived), new(DiscoveryTests.Dummy("already-caught"), MutationOutcome.Killed)], 1, new()));
            Assert.Equal(["survivor", "already-caught"], request.MutantIds);
            return Task.FromResult(new VerificationResult("failed", "HEAD", 1, 2, 2,
                [new(DiscoveryTests.Dummy("survivor"), MutationOutcome.Killed, KillConfirmed: true), new(DiscoveryTests.Dummy("already-caught"), MutationOutcome.Survived)], 1, new()));
        }, default);
        Assert.Equal(1, report.ExitCode);
        Assert.Equal("not_verified", report.TestImprovement!.Status);
        Assert.Equal(["survivor"], report.TestImprovement.VerifiedMutantIds);
        Assert.Equal("tests", File.ReadAllText(Path.Combine(workspace.Root, "Tests/Tests.cs")));
    }

    [Fact]
    public async Task ChangingOriginalTestInputsDuringFirstRunRejectsComparison()
    {
        using var workspace = new Workspace();
        workspace.Write("Code/Code.cs", "production");
        workspace.Write("Tests/Tests.cs", "tests");
        var patch = new TestPatchManifest(1, "contract", [new("Tests/Tests.cs", AgentWorkflows.ByteHash(File.ReadAllBytes(Path.Combine(workspace.Root, "Tests/Tests.cs"))), "improved")]);
        var calls = 0;
        var report = await AgentWorkflows.VerifyTestPatchAsync(Request(workspace), patch, new ScopeRunner(), (_, _) =>
        {
            calls++;
            workspace.Write("Tests/Tests.cs", "unexpected edit");
            return Task.FromResult(Result("failed", MutationOutcome.Survived));
        }, default);
        Assert.Equal(1, calls);
        Assert.Equal(2, report.ExitCode);
        Assert.Contains(report.Diagnostics, d => d.Code == "workflow_inputs_changed");
        Assert.Equal("not_verified", report.TestImprovement!.Status);
        Assert.Equal("tests", File.ReadAllText(Path.Combine(workspace.Root, "Tests/Tests.cs")));
    }

    private static VerificationResult Result(string status, MutationOutcome outcome, bool? confirmed = null) =>
        new(status, "HEAD", 1, 1, 1, [new(DiscoveryTests.Dummy("survivor"), outcome, KillConfirmed: confirmed)], 1, new());
    private static VerificationRequest Request(Workspace workspace) => new(workspace.Root, "HEAD", Path.Combine(workspace.Root, "Code/Code.csproj"), [Path.Combine(workspace.Root, "Tests/Tests.csproj")]);
    private sealed class Scope(string file) : IProductionSourceScope
    {
        public Task<IReadOnlySet<string>> GetFilesAsync(VerificationRequest request, CancellationToken token) => Task.FromResult<IReadOnlySet<string>>(new HashSet<string> { file });
    }
    private sealed class ScopeRunner : IProcessRunner
    {
        public Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken token)
        {
            Assert.Contains("-getItem:Compile", request.Arguments);
            var directory = Path.GetDirectoryName(request.Arguments[1])!;
            var items = Directory.GetFiles(directory, "*.cs").Select(path => new { FullPath = path });
            return Task.FromResult(new ProcessResult(0, JsonSerializer.Serialize(new { Properties = new { TargetFramework = "net10.0", TargetFrameworks = "" }, Items = new { Compile = items } }), "", 1));
        }
    }
}
