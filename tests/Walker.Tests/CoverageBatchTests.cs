using Walker.Core;
using Walker.Execution;
using Walker.Roslyn;
using Xunit;

namespace Walker.Tests;

public class CoverageBatchTests
{
    [Theory]
    [InlineData("independent", 1, true)]
    [InlineData("overlap", 2, true)]
    [InlineData("missing-baseline", 3, false)]
    [InlineData("missing-batch", 4, true)]
    [InlineData("malformed-batch", 4, true)]
    [InlineData("hung-batch", 4, true)]
    [InlineData("cancelled-batch", 1, true)]
    [InlineData("stale-input", 0, true)]
    [InlineData("shared-state", 3, false)]
    [InlineData("snapshot-state", 3, false)]
    [InlineData("confirmation", 3, false)]
    [InlineData("missing-layout", 0, false)]
    public async Task BatchesMatchSerialOutcomesAndFallBackWhenEvidenceIsAmbiguous(string mode, int commands, bool preparedCoverage)
    {
        using var workspace = new Workspace();
        workspace.Write(".gitignore", "bin/\nobj/\n");
        workspace.Write("Code/Code.csproj", "<Project Sdk='Microsoft.NET.Sdk'><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup></Project>");
        workspace.Write("Code/Code.cs", "public static class C { public static bool A(int n) => n >= 1; public static bool B(int n) => n >= 2; public static bool D(int n) => n >= 3; }");
        workspace.Write("Tests/Tests.csproj", """
            <Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup>
            <ItemGroup><ProjectReference Include="../Code/Code.csproj"/>
            <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.11.1"/>
            <PackageReference Include="xunit" Version="2.9.2"/>
            <PackageReference Include="xunit.runner.visualstudio" Version="2.8.2"/></ItemGroup></Project>
            """);
        workspace.Write("Tests/Tests.cs", mode == "overlap"
            ? "using Xunit; public class Tests { [Fact] public void AB() { Assert.True(C.A(1)); Assert.True(C.B(2)); } [Fact] public void D() => Assert.False(C.D(2)); }"
            : "using Xunit; public class Tests { " + (mode == "shared-state" ? "private static int shared; [Fact] public void State() => Assert.Equal(0, shared); " : "")
                + "[Fact] public void A() => Assert.True(C.A(1)); [Fact] public void B() => Assert.True(C.B(2)); [Fact] public void D() => Assert.False(C.D(2)); }");
        var runner = new RecordingRunner(mode);
        foreach (var args in new[] { new[] { "init" }, ["config", "user.email", "test@example.invalid"], ["config", "user.name", "Test"], ["add", "."], ["commit", "-m", "baseline"] })
            Assert.Equal(0, (await runner.RunAsync(new("git", args, workspace.Root), default)).ExitCode);
        var root = OperatingSystem.IsMacOS() && workspace.Root.StartsWith("/var/", StringComparison.Ordinal) ? "/private" + workspace.Root : workspace.Root;
        var request = new VerificationRequest(root, "HEAD", "Code/Code.csproj", ["Tests/Tests.csproj"], ConfirmKills: mode == "confirmation");
        var executor = new SwitchingMutationExecutor(runner);
        await executor.VerifyAsync(request, default);
        var mutants = (await new RoslynMutationDiscoverer().DiscoverAsync(root, [new("Code/Code.cs", [new(1, 1)], false)], default)).Mutants;
        await using var session = await executor.PrepareAsync(request, mutants, default);
        if (mode == "missing-layout")
        {
            Assert.Null(session);
            var before = File.ReadAllBytes(Path.Combine(root, "Code/Code.cs"));
            var fallback = new List<MutationResult>();
            foreach (var mutant in mutants) fallback.Add(await executor.ExecuteAsync(mutant, new(request, new AllTestsSelector(request.Tests)), default));
            Assert.Equal(2, fallback.Count(r => r.Outcome == MutationOutcome.Killed));
            Assert.Single(fallback, r => r.Outcome == MutationOutcome.Survived);
            Assert.Equal(before, File.ReadAllBytes(Path.Combine(root, "Code/Code.cs")));
            return;
        }
        Assert.NotNull(session);
        Assert.True((session.Detail?.Contains("coverage batches", StringComparison.Ordinal) == true) == preparedCoverage, session.Detail + "\n" + string.Join("\n", runner.CoverageReports));
        var context = new VerificationContext(request, new AllTestsSelector(request.Tests));
        var serial = new List<MutationResult>();
        foreach (var mutant in mutants) serial.Add(await session.ExecuteAsync(mutant, context, default));
        if (mode == "stale-input") File.AppendAllText(Path.Combine(root, "Code/Code.cs"), "// external edit");
        runner.Calls.Clear();
        var batch = Assert.IsAssignableFrom<IMutationBatchExecutor>(session);
        var results = await batch.ExecuteBatchAsync(mutants, context, runner.GlobalCancellation.Token);
        if (mode == "cancelled-batch") Assert.Empty(results);
        else if (mode == "stale-input") Assert.All(results, r => Assert.Equal(MutationOutcome.TestError, r.Outcome));
        else
        {
            Assert.Equal(serial.Select(r => r.Outcome), results.Select(r => r.Outcome));
            Assert.Equal(2, results.Count(r => r.Outcome == MutationOutcome.Killed));
        }
        Assert.Equal(commands, runner.Calls.Count(c => c.Arguments[0] == "test" && c.Environment?.Any(e => e.Key.StartsWith("WALKER_ACTIVE_", StringComparison.Ordinal) && e.Value != "0") == true));
        if (mode == "confirmation") Assert.All(results.Where(r => r.Outcome == MutationOutcome.Killed), r => Assert.True(r.KillConfirmed));
    }

    private sealed class RecordingRunner(string mode) : IProcessRunner
    {
        private readonly ProcessRunner inner = new();
        public List<ProcessRequest> Calls { get; } = [];
        public List<string> CoverageReports { get; } = [];
        public CancellationTokenSource GlobalCancellation { get; } = new();
        private bool edited;
        public async Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken token)
        {
            Calls.Add(request);
            if (mode == "snapshot-state" && !edited && request.FileName == "git" && request.Arguments[0] == "ls-files")
            {
                edited = true;
                var path = Path.Combine(request.WorkingDirectory, "Tests/Tests.cs");
                File.WriteAllText(path, File.ReadAllText(path).Replace("public class Tests {", "public class Tests { private static bool shared;", StringComparison.Ordinal));
            }
            var mixed = request.Environment?.Any(e => e.Key.StartsWith("WALKER_ACTIVE_", StringComparison.Ordinal) && e.Value?.Contains(',') == true) == true;
            if (mode == "cancelled-batch" && mixed) { GlobalCancellation.Cancel(); token.ThrowIfCancellationRequested(); }
            if (mode == "hung-batch" && mixed) await Task.Delay(Timeout.Infinite, token);
            var result = await inner.RunAsync(request, token);
            if (mode == "missing-layout" && request.Arguments.Any(a => a.StartsWith("-getProperty:", StringComparison.Ordinal) && a.Contains("OutputPath") && a.Contains("IsTestProject")))
            {
                var json = System.Text.Json.Nodes.JsonNode.Parse(result.StandardOutput)!;
                json["Properties"]!.AsObject().Remove("OutputPath");
                result = result with { StandardOutput = json.ToJsonString() };
            }
            if (request.Environment?.TryGetValue("WALKER_COVERAGE_REPORT", out var report) == true)
            {
                CoverageReports.Add(report != null && File.Exists(report) ? File.ReadAllText(report) : result.StandardOutput + result.StandardError);
                if (report != null && (mode == "missing-baseline" || mode == "missing-batch" && mixed))
                    File.Delete(report);
                if (report != null && mode == "malformed-batch" && mixed) File.WriteAllText(report, "{\"Valid\":true,\"Cases\":[null]}");
            }
            return result;
        }
    }

    [Theory]
    [InlineData("using Xunit; class T { [Fact] void A() => Assert.True(C.A(1)); }", true)]
    [InlineData("using Xunit; class T { static bool state; [Fact] void A() => Assert.True(C.A(1)); }", false)]
    [InlineData("using Xunit; class T : Hooks { [Fact] void A() => Assert.True(C.A(1)); }", false)]
    [InlineData("using Xunit; class T { [Fact] void A() => Assert.True(External.State); }", false)]
    [InlineData("using Xunit; class T { [Fact] void A() => Assert.True(C.A((Custom)null)); }", false)]
    [InlineData("using Xunit; class T {\n#if SHARED\n static bool state;\n#endif\n [Fact] void A() => Assert.True(C.A(1)); }", false)]
    [InlineData("using Xunit; class T { [Fact] async void A() { await Run(); Assert.True(C.A(1)); } }", false)]
    [InlineData("using Xunit; class T { [Fact] void A() { File.WriteAllText(\"x\",\"y\"); Assert.True(C.A(1)); } }", false)]
    [InlineData("using Xunit; class T { [Theory, MemberData(nameof(Data))] void A(int n) => Assert.True(C.A(n)); }", false)]
    public void SharedStateAndUnprovenTestCodeCannotEnterBatches(string source, bool allowed)
        => Assert.Equal(allowed, TestBatchSafety.Allows([source], new HashSet<string> { "C.A" }));
}
