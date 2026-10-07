using Walker.Core;
using Walker.Execution;
using Walker.Roslyn;
using System.Collections.Concurrent;
using System.Text;
using Xunit;

namespace Walker.Tests;

public sealed class MutationWorkerTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PreparedWorkersOverlapWithIsolatedOutputsContentAndConfirmation(bool multiFramework)
    {
        using var fixture = new WorkerFixture(multiFramework);
        await fixture.Initialize();
        var runner = new BarrierRunner();
        var executor = new SwitchingMutationExecutor(runner);
        var request = fixture.Request;
        await executor.VerifyAsync(request, default);
        var selected = await fixture.Mutants();
        selected = [selected[0], selected[1], selected[^1], selected[2]]; // Resume prepared work after serial fallback.
        await using var session = await executor.PrepareAsync(request, selected, default);
        Assert.NotNull(session);
        Assert.Equal(2, session.WorkerCount);
        var batch = Assert.IsAssignableFrom<IMutationBatchExecutor>(session);
        runner.Armed = true;
        var execution = batch.ExecuteBatchAsync(selected, new(request, new AllTestsSelector(request.Tests)), default);
        await runner.BothStarted.Task.WaitAsync(TimeSpan.FromSeconds(30));
        var active = runner.Active.ToArray();
        Assert.Equal(2, active.Length);
        Assert.Equal(2, active.Select(c => c.WorkingDirectory).Distinct().Count());
        Assert.Equal(2, active.Select(c => c.Arguments[1]).Distinct().Count());
        Assert.Equal(2, active.Select(c => c.Environment!.Single(p => p.Key.StartsWith("WALKER_ACTIVE_", StringComparison.Ordinal)).Value).Distinct().Count());
        foreach (var call in active)
        {
            Assert.Equal(fixture.Original, File.ReadAllBytes(Path.Combine(call.WorkingDirectory, "Code/Rules.cs")));
            Assert.Equal("dirty content", File.ReadAllText(Path.Combine(call.WorkingDirectory, "required-untracked.txt")));
            Assert.StartsWith(Path.GetDirectoryName(call.WorkingDirectory)!, call.Environment!["TMPDIR"]);
            Assert.StartsWith(Path.GetDirectoryName(call.WorkingDirectory)!, call.Arguments[call.Arguments.ToList().IndexOf("--results-directory") + 1]);
        }
        runner.Release.TrySetResult();
        var results = await execution;
        Assert.Equal(selected.Select(m => m.Id), results.Select(r => r.Mutant.Id));
        Assert.All(results, r =>
        {
            Assert.Equal(r.Mutant.Member.EndsWith(".D", StringComparison.Ordinal) ? MutationOutcome.Survived : MutationOutcome.Killed, r.Outcome);
            if (r.Outcome == MutationOutcome.Killed) Assert.True(r.KillConfirmed);
        });
        Assert.Equal(0, runner.FallbackOverlaps);
        Assert.Equal(fixture.Original, File.ReadAllBytes(Path.Combine(request.Root, "Code/Rules.cs")));
        Assert.Contains(runner.Calls, c => c.WorkingDirectory.Contains("walker-worker-", StringComparison.Ordinal)
            && c.Arguments[0] == "test" && c.Arguments[1].Contains("/ordinary/", StringComparison.Ordinal)
            && c.Arguments.Contains("--filter"));
        var roots = active.Select(c => Path.GetDirectoryName(c.WorkingDirectory)!).ToArray();
        await session.DisposeAsync();
        Assert.All(roots, root => Assert.False(Directory.Exists(root)));
        if (multiFramework) Assert.Contains(runner.Calls, c => c.Arguments[0] == "test" && c.Arguments[1].Contains("/ordinary/", StringComparison.Ordinal)
            && c.Arguments.Contains(".NETCoreApp,Version=v8.0") && c.Arguments.Contains("--filter")
            && c.Arguments[1].Contains("net8.0-windows", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("reject")]
    [InlineData("error")]
    [InlineData("cancel")]
    [InlineData("single")]
    [InlineData("hang")]
    public async Task WorkerFailureFallbackCancellationAndSmallBatchesKeepTheirEvidence(string mode)
    {
        using var fixture = new WorkerFixture();
        await fixture.Initialize();
        var runner = new BarrierRunner { Mode = mode };
        var executor = new SwitchingMutationExecutor(runner);
        var request = fixture.Request;
        await executor.VerifyAsync(request, default);
        var selected = await fixture.Mutants();
        if (mode == "single") selected = [selected[0]];
        await using var session = await executor.PrepareAsync(request, selected, default);
        Assert.NotNull(session);
        Assert.Equal(mode is "single" or "reject" ? 1 : 2, session.WorkerCount);
        if (mode is "single" or "reject")
        {
            Assert.Equal(MutationOutcome.Killed, (await session.ExecuteAsync(selected[0], new(request, new AllTestsSelector(request.Tests)), default)).Outcome);
            if (mode == "reject") Assert.Contains("Parallel preparation unavailable", session.Detail);
            var workerRoots = runner.Calls.Where(c => c.WorkingDirectory.Contains("walker-worker-", StringComparison.Ordinal)).Select(c => Path.GetDirectoryName(c.WorkingDirectory)!);
            Assert.All(workerRoots, root => Assert.False(Directory.Exists(root)));
            return;
        }
        runner.Armed = true;
        using var cancellation = new CancellationTokenSource();
        var execution = ((IMutationBatchExecutor)session).ExecuteBatchAsync(selected, new(request, new AllTestsSelector(request.Tests)), cancellation.Token);
        await runner.BothStarted.Task.WaitAsync(TimeSpan.FromSeconds(30));
        var active = runner.Active.ToArray();
        if (mode == "cancel")
        {
            using var wait = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            while (active.Any(c => !File.Exists(Path.Combine(c.Environment!["TMPDIR"]!, "child.pid")))) await Task.Delay(10, wait.Token);
            var children = active.Select(c => int.Parse(File.ReadAllText(Path.Combine(c.Environment!["TMPDIR"]!, "child.pid")))).ToArray();
            cancellation.Cancel();
            var results = await execution.WaitAsync(TimeSpan.FromSeconds(15));
            Assert.Equal(2, results.Count);
            Assert.All(results, r => Assert.Equal(MutationOutcome.TimedOut, r.Outcome));
            Assert.All(children, pid =>
            {
                try { using var process = System.Diagnostics.Process.GetProcessById(pid); Assert.True(process.HasExited); }
                catch (ArgumentException) { }
            });
        }
        else
        {
            runner.Release.TrySetResult();
            var results = await execution;
            Assert.Equal(selected.Length, results.Count);
            Assert.Equal(mode == "hang" ? MutationOutcome.Hung : MutationOutcome.TestError, results[0].Outcome);
            Assert.Equal(MutationOutcome.Killed, results[1].Outcome);
            Assert.Contains(results, r => r.Outcome == MutationOutcome.Survived);
            Assert.Equal(MutationOutcome.Killed, results[^1].Outcome);
        }
        await session.DisposeAsync();
        Assert.All(active, c => Assert.False(Directory.Exists(Path.GetDirectoryName(c.WorkingDirectory))));
        Assert.Equal(fixture.Original, File.ReadAllBytes(Path.Combine(request.Root, "Code/Rules.cs")));
    }

    private sealed class WorkerFixture : IDisposable
    {
        private readonly Workspace workspace = new();
        public VerificationRequest Request { get; }
        public byte[] Original { get; private set; } = [];
        private readonly bool multiFramework;
        public WorkerFixture(bool multiFramework = false)
        {
            this.multiFramework = multiFramework;
            var root = workspace.Root;
            if (OperatingSystem.IsMacOS() && root.StartsWith("/var/", StringComparison.Ordinal)) root = "/private" + root;
            Request = new(root, "HEAD", "Code/Code.csproj", ["Tests/Tests.csproj"], TimeoutSeconds: 180, ConfirmKills: true, Workers: 2);
        }
        public async Task Initialize()
        {
            workspace.Write(".gitignore", "bin/\nobj/\n");
            workspace.Write("Code/Code.csproj", "<Project Sdk='Microsoft.NET.Sdk'><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup></Project>");
            workspace.Write("Code/Rules.cs", "public static class Rules { public static bool A(int v) => v >= 0; public static bool B(int v) => v <= 0; public static bool D(int v) => v >= 0; public static bool C(bool a, bool b) => a && b; }");
            workspace.Write("Tests/Tests.csproj", """
                <Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup>
                <ItemGroup><ProjectReference Include="../Code/Code.csproj"/>
                <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.11.1"/>
                <PackageReference Include="xunit" Version="2.9.2"/>
                <PackageReference Include="xunit.runner.visualstudio" Version="2.8.2"/>
                <Content Include="../required-untracked.txt" Link="required-untracked.txt" CopyToOutputDirectory="Always"/>
                </ItemGroup></Project>
                """);
            workspace.Write("Tests/Tests.cs", """
                using Xunit;
                public class Tests {
                  static int hosts;
                  [Fact] public void Boundaries() { Assert.Equal(1, ++hosts); Assert.True(Rules.A(0)); Assert.True(Rules.D(1)); Assert.False(Rules.C(true,false));
                #if SINGLE || WINDOWS
                    Assert.True(Rules.B(0));
                #else
                    Assert.True(Rules.B(-1));
                #endif
                    Assert.Equal("dirty content", System.IO.File.ReadAllText(System.IO.Path.Combine(System.AppContext.BaseDirectory, "required-untracked.txt"))); }
                }
                """);
            var testProject = Path.Combine(Request.Root, "Tests/Tests.csproj");
            File.WriteAllText(testProject, File.ReadAllText(testProject).Replace("<TargetFramework>net8.0</TargetFramework>", multiFramework
                ? "<TargetFrameworks>net8.0;net8.0-windows</TargetFrameworks>" : "<TargetFramework>net8.0</TargetFramework><DefineConstants>SINGLE</DefineConstants>", StringComparison.Ordinal));
            if (multiFramework)
            {
                workspace.Write("Facade/Facade.csproj", "<Project Sdk='Microsoft.NET.Sdk'><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup><ItemGroup><ProjectReference Include='../Code/Code.csproj'/></ItemGroup></Project>");
                File.WriteAllText(testProject, File.ReadAllText(testProject).Replace("../Code/Code.csproj", "../Facade/Facade.csproj", StringComparison.Ordinal));
            }
            var real = new ProcessRunner();
            foreach (var args in new[] { new[] { "init" }, ["config", "user.email", "test@example.invalid"], ["config", "user.name", "Test"], ["add", "."], ["commit", "-m", "baseline"] })
                Assert.Equal(0, (await real.RunAsync(new("git", args, Request.Root), default)).ExitCode);
            Original = [.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes(File.ReadAllText(Path.Combine(Request.Root, "Code/Rules.cs")) + "\r\n// dirty\r\n")];
            File.WriteAllBytes(Path.Combine(Request.Root, "Code/Rules.cs"), Original);
            workspace.Write("required-untracked.txt", "dirty content");
        }
        public async Task<Mutant[]> Mutants() => (await new RoslynMutationDiscoverer().DiscoverAsync(Request.Root,
            [new("Code/Rules.cs", [new(1,3)], true)], default)).Mutants.OrderBy(m => m.Operator).ThenBy(m => m.SpanStart).ToArray();
        public void Dispose() => workspace.Dispose();
    }
    private sealed class BarrierRunner : IProcessRunner
    {
        private readonly ProcessRunner real = new();
        public ConcurrentQueue<ProcessRequest> Calls { get; } = new();
        public ConcurrentQueue<ProcessRequest> Active { get; } = new();
        public TaskCompletionSource BothStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Armed { get; set; }
        public string? Mode { get; set; }
        private string? rejectedRoot;
        private int inFlight;
        public int FallbackOverlaps;
        public async Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken token)
        {
            Calls.Enqueue(request);
            if (Mode == "reject" && request.WorkingDirectory.Contains("walker-worker-", StringComparison.Ordinal))
            {
                Interlocked.CompareExchange(ref rejectedRoot, request.WorkingDirectory, null);
                if (request.WorkingDirectory == rejectedRoot) return new(0, "Missing report", "", 1);
            }
            var active = Armed && request.Environment?.Any(p => p.Key.StartsWith("WALKER_ACTIVE_", StringComparison.Ordinal) && p.Value != "0") == true;
            if (!active && (request.Arguments[0] == "build" || (request.Arguments[0] == "test" && request.Arguments[1].EndsWith(".csproj", StringComparison.Ordinal)))
                && Volatile.Read(ref inFlight) > 0) Interlocked.Increment(ref FallbackOverlaps);
            if (active)
            {
                Interlocked.Increment(ref inFlight);
                Active.Enqueue(request);
                if (Active.Count == 2) BothStarted.TrySetResult();
            }
            try
            {
                if (active && Mode == "cancel")
                {
                    var pidFile = Path.Combine(request.Environment!["TMPDIR"]!, "child.pid");
                    return await real.RunAsync(request with { FileName = "/bin/sh", Arguments = ["-c", "sleep 120 & echo $! > '" + pidFile.Replace("'", "'\\''", StringComparison.Ordinal) + "'; wait"] }, token);
                }
                if (active) await Release.Task.WaitAsync(token);
                if (active && Mode == "hang" && request.Environment!.Any(p => p.Key.StartsWith("WALKER_ACTIVE_", StringComparison.Ordinal) && p.Value == "1")) await Task.Delay(Timeout.Infinite, token);
                if (active && Mode == "error" && request.Environment!.Any(p => p.Key.StartsWith("WALKER_ACTIVE_", StringComparison.Ordinal) && p.Value == "1")) return new(0, "Missing report", "", 1);
                return await real.RunAsync(request, token);
            }
            finally { if (active) Interlocked.Decrement(ref inFlight); }
        }
    }
    [Fact]
    public async Task EngineUsesPreparedBatchAndReportsSelectedOrderRegardlessOfCompletionOrder()
    {
        var mutants = new[] { DiscoveryTests.Dummy("a"), DiscoveryTests.Dummy("b"), DiscoveryTests.Dummy("c") };
        var baseline = new FakeEngine(mutants, _ => throw new Exception("Legacy execution must not run"));
        var prepared = new BatchExecutor();
        var report = await new VerificationEngine(baseline, baseline, prepared, baseline)
            .VerifyAsync(new("root", "HEAD", "project", ["tests"], Workers: 2));
        Assert.Equal(new[] { "a", "b", "c" }, report.Results.Select(r => r.Mutant.Id));
        Assert.Equal("failed", report.Status);
        Assert.Equal(2, report.WorkersRequested);
        Assert.Equal(2, report.WorkersUsed);
        Assert.True(prepared.Session!.Disposed);
        Assert.Equal(1, prepared.Session.Batches);
    }

    [Theory]
    [InlineData(false, "incomplete")]
    [InlineData(true, "error")]
    public async Task PartialBatchKeepsStartedEvidenceAndOrdersNeverStartedSkips(bool error, string status)
    {
        var mutants = new[] { DiscoveryTests.Dummy("a"), DiscoveryTests.Dummy("b"), DiscoveryTests.Dummy("c") };
        var baseline = new FakeEngine(mutants, _ => throw new Exception("Legacy execution must not run"));
        using var cancellation = new CancellationTokenSource();
        var executor = new BatchExecutor(() =>
        {
            cancellation.Cancel();
            return [new(mutants[2], MutationOutcome.TimedOut), new(mutants[0], error ? MutationOutcome.TestError : MutationOutcome.Killed)];
        });
        var report = await new VerificationEngine(baseline, baseline, executor, baseline)
            .VerifyAsync(new("root", "HEAD", "project", ["tests"], Workers: 2), cancellation.Token);
        Assert.Equal(status, report.Status);
        Assert.Equal(new[] { "a", "b", "c" }, report.Results.Select(r => r.Mutant.Id));
        Assert.Equal(MutationOutcome.Skipped, report.Results[1].Outcome);
        Assert.Equal(2, report.MutantsExecuted);
        Assert.Equal(1, report.TimedOut);
        Assert.Equal(1, report.Skipped);
        Assert.True(executor.Session!.Disposed);
    }

    private sealed class BatchExecutor(Func<IReadOnlyList<MutationResult>>? results = null) : IMutationExecutor, IBatchMutationPreparer
    {
        public BatchSession? Session { get; private set; }
        public Task<IPreparedMutationSession?> PrepareAsync(VerificationRequest request, IReadOnlyList<Mutant> selected, CancellationToken token)
        {
            Session = new(selected, results);
            return Task.FromResult<IPreparedMutationSession?>(Session);
        }
        public Task<MutationResult> ExecuteAsync(Mutant mutant, VerificationContext context, CancellationToken token)
            => throw new Exception("Legacy execution must not run");
    }
    private sealed class BatchSession(IReadOnlyList<Mutant> selected, Func<IReadOnlyList<MutationResult>>? results) : IPreparedMutationSession, IMutationBatchExecutor
    {
        public int Batches { get; private set; }
        public bool Disposed { get; private set; }
        public int WorkerCount => 2;
        public IReadOnlySet<string> SupportedMutantIds { get; } = selected.Select(m => m.Id).ToHashSet();
        public string? Detail => null;
        public Task<IReadOnlyList<MutationResult>> ExecuteBatchAsync(IReadOnlyList<Mutant> mutants, VerificationContext context, CancellationToken token)
        {
            Batches++;
            if (results != null) return Task.FromResult(results());
            return Task.FromResult<IReadOnlyList<MutationResult>>(mutants.Reverse()
                .Select(m => new MutationResult(m, m.Id == "b" ? MutationOutcome.Survived : MutationOutcome.Killed)).ToArray());
        }
        public Task<MutationResult> ExecuteAsync(Mutant mutant, VerificationContext context, CancellationToken token)
            => throw new Exception("The batch interface must be used");
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }
}
