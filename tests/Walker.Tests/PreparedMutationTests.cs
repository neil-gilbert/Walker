using Walker.Core;
using Walker.Execution;
using Walker.Roslyn;
using System.Text;
using Xunit;

namespace Walker.Tests;

public sealed class PreparedMutationTests
{
    [Theory]
    [InlineData("external")]
    [InlineData("ignored")]
    [InlineData("conditional")]
    public async Task BuildConfigurationMissingFromSnapshotRequiresSourceFallback(string layout)
    {
        using var workspace = new Workspace();
        workspace.Write(layout == "external" ? "Directory.Build.props" : "repo/Directory.Build.props", layout == "conditional"
            ? "<Project><PropertyGroup><DefineConstants Condition=\"$([System.String]::Copy('$(MSBuildProjectDirectory)').Contains('repo'))\">ORIGINAL</DefineConstants></PropertyGroup></Project>"
            : "<Project><PropertyGroup><CheckForOverflowUnderflow>true</CheckForOverflowUnderflow></PropertyGroup></Project>");
        workspace.Write("repo/.gitignore", "bin/\nobj/\n" + (layout == "ignored" ? "Directory.Build.props\n" : ""));
        workspace.Write("repo/Code/Code.csproj", "<Project Sdk='Microsoft.NET.Sdk'><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup></Project>");
        workspace.Write("repo/Code/Code.cs", layout == "conditional" ? """
            public static class C {
            #if ORIGINAL
                public static bool A(int v) => v >= 0;
            #else
                public static bool A(int v) => true;
            #endif
            }
            """ : "public static class C { public static bool A(int v) => v >= 0; }");
        workspace.Write("repo/Tests/Tests.csproj", """
            <Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup>
              <ItemGroup><ProjectReference Include="../Code/Code.csproj"/>
                <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.11.1"/>
                <PackageReference Include="xunit" Version="2.9.2"/>
                <PackageReference Include="xunit.runner.visualstudio" Version="2.8.2"/>
              </ItemGroup></Project>
            """);
        workspace.Write("repo/Tests/Tests.cs", "using Xunit; public class Tests { [Fact] public void Equality() => Assert.True(C.A(0)); }");
        var root = Path.Combine(workspace.Root, "repo");
        if (OperatingSystem.IsMacOS() && root.StartsWith("/var/", StringComparison.Ordinal)) root = "/private" + root;
        var runner = new ProcessRunner();
        foreach (var args in new[] { new[] { "init" }, ["config", "user.email", "test@example.invalid"], ["config", "user.name", "Test"], ["add", "."], ["commit", "-m", "baseline"] })
            Assert.Equal(0, (await runner.RunAsync(new("git", args, root), default)).ExitCode);
        var request = new VerificationRequest(root, "HEAD", "Code/Code.csproj", ["Tests/Tests.csproj"]);
        var executor = new SwitchingMutationExecutor(runner);
        await executor.VerifyAsync(request, default);
        var text = File.ReadAllText(Path.Combine(root, "Code/Code.cs"));
        var mutant = new Mutant("boundary", "Code/Code.cs", 1, "C.A", MutationOperator.ConditionalBoundary,
            "v >= 0", "v > 0", text.IndexOf("v >= 0", StringComparison.Ordinal), "v >= 0".Length, Mutant.Hash(text));
        await using var session = await executor.PrepareAsync(request, [mutant], default);
        Assert.Null(session);
        Assert.Equal(MutationOutcome.Killed, (await executor.ExecuteAsync(mutant, new(request, new AllTestsSelector(request.Tests)), default)).Outcome);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PreparedBoundariesRunWithoutMoreBuildsAndRestoreDirtyInputs(bool multiFramework)
    {
        using var workspace = new Workspace();
        workspace.Write(".gitignore", "bin/\nobj/\n");
        workspace.Write("Code/Code.csproj", "<Project Sdk='Microsoft.NET.Sdk'><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup></Project>");
        workspace.Write("Code/Rules.cs", "namespace Example; public static class Rules { public static bool A(int a, int b) { var first = a >= b; var second = a <= b; return first && second; } public static bool B(int a, int b) => a >= b; }");
        workspace.Write("Tests/Tests.csproj", """
            <Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup>
              <ItemGroup><ProjectReference Include="../Code/Code.csproj"/>
                <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.11.1"/>
                <PackageReference Include="xunit" Version="2.9.2"/>
                <PackageReference Include="xunit.runner.visualstudio" Version="2.8.2"/>
              </ItemGroup></Project>
            """);
        if (multiFramework)
        {
            var project = Path.Combine(workspace.Root, "Tests/Tests.csproj");
            File.WriteAllText(project, File.ReadAllText(project).Replace("<TargetFramework>net8.0</TargetFramework>", "<TargetFrameworks>net8.0;net8.0-windows</TargetFrameworks>", StringComparison.Ordinal));
            workspace.Write("Facade/Facade.csproj", "<Project Sdk='Microsoft.NET.Sdk'><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup><ItemGroup><ProjectReference Include='../Code/Code.csproj'/></ItemGroup></Project>");
            File.WriteAllText(project, File.ReadAllText(project).Replace("../Code/Code.csproj", "../Facade/Facade.csproj", StringComparison.Ordinal));
        }
        workspace.Write("Tests/Tests.cs", """
            using Xunit;
            namespace Example;
            public class Tests {
                [Fact] public void A() { Assert.True(Rules.A(1,1)); Assert.False(Rules.A(0,1)); }
                [Fact] public void B() {
            #if WINDOWS
                    Assert.True(Rules.B(1,1));
            #else
                    Assert.True(Rules.B(2,1));
            #endif
                }
            }
            """);
        var runner = new RecordingRunner();
        async Task Git(params string[] args)
        {
            var result = await runner.RunAsync(new("git", args, workspace.Root), default);
            Assert.True(result.ExitCode == 0, result.StandardError);
        }
        await Git("init"); await Git("config", "user.email", "test@example.invalid"); await Git("config", "user.name", "Test");
        await Git("add", "."); await Git("commit", "-m", "baseline");
        var path = Path.Combine(workspace.Root, "Code/Rules.cs");
        var encoding = new UTF8Encoding(true);
        File.WriteAllBytes(path, [..encoding.GetPreamble(), ..encoding.GetBytes("// dirty input\r\n" + File.ReadAllText(path) + "\r\n")]);
        var original = File.ReadAllBytes(path);
        workspace.Write("required-untracked.txt", "dirty content");
        var root = OperatingSystem.IsMacOS() && workspace.Root.StartsWith("/var/", StringComparison.Ordinal) ? "/private" + workspace.Root : workspace.Root;
        var request = new VerificationRequest(root, "HEAD", "Code/Code.csproj", ["Tests/Tests.csproj"], ConfirmKills: true);
        var trace = new List<string>();
        var executor = new SwitchingMutationExecutor(runner, trace.Add);
        await executor.VerifyAsync(request, default);
        var selected = (await new RoslynMutationDiscoverer().DiscoverAsync(workspace.Root, [new("Code/Rules.cs", [new(1, 3)], true)], default)).Mutants;
        await using var session = await executor.PrepareAsync(request, selected, default);
        Assert.True(session != null, string.Join("\n", trace));
        Assert.Equal(3, session.SupportedMutantIds.Count);
        var preparedRoot = Assert.Single(runner.Calls, c => c.Arguments[0] == "build" && c.WorkingDirectory.Contains("walker-prepared-", StringComparison.Ordinal)).WorkingDirectory;
        Assert.Equal("dirty content", File.ReadAllText(Path.Combine(preparedRoot, "required-untracked.txt")));
        var context = new VerificationContext(request, new AllTestsSelector(request.Tests));
        runner.Calls.Clear();
        var aMutant = Assert.Single(selected, m => m.Member.EndsWith(".A", StringComparison.Ordinal) && m.Original.Contains(">=", StringComparison.Ordinal));
        var a = await session.ExecuteAsync(aMutant, context, default);
        Assert.Equal(MutationOutcome.Killed, a.Outcome);
        Assert.True(a.KillConfirmed);
        Assert.Equal(original, File.ReadAllBytes(path));
        runner.Calls.Clear();
        var b = await session.ExecuteAsync(Assert.Single(selected, m => m.Member.EndsWith(".B", StringComparison.Ordinal)), context, default);
        Assert.Equal(multiFramework ? MutationOutcome.Killed : MutationOutcome.Survived, b.Outcome);
        if (!multiFramework) Assert.DoesNotContain(runner.Calls, c => c.Arguments[0] is "build" or "msbuild");
        else Assert.True(b.KillConfirmed);
        Assert.Contains(runner.Calls, c => c.Arguments[0] == "test" && c.Arguments[1].EndsWith(".dll", StringComparison.Ordinal) && c.Environment is { Count: 1 });
        Assert.Equal(original, File.ReadAllBytes(path));
        // A legacy build cannot overwrite the immutable prepared output used by the next boundary.
        var fallback = await session.ExecuteAsync(Assert.Single(selected, m => m.Operator == MutationOperator.BooleanLogic), context, default);
        Assert.Equal(MutationOutcome.Killed, fallback.Outcome);
        Assert.Equal(MutationOutcome.Killed, (await session.ExecuteAsync(aMutant, context, default)).Outcome);
        Assert.Equal(original, File.ReadAllBytes(path));
        File.AppendAllText(path, "// external edit");
        var stale = await session.ExecuteAsync(selected[0], context, default);
        Assert.Equal(MutationOutcome.TestError, stale.Outcome);
        Assert.Contains("changed", stale.Detail, StringComparison.OrdinalIgnoreCase);
        await session.DisposeAsync();
        Assert.False(Directory.Exists(preparedRoot));
    }

    [Theory]
    [InlineData("batch-build")]
    [InlineData("baseline-report")]
    [InlineData("cancel")]
    public async Task RejectedOrCancelledPreparationCleansScratchWithoutMisclassifyingOrdinaryMutants(string rejection)
    {
        using var workspace = new Workspace();
        workspace.Write(".gitignore", "bin/\nobj/\n");
        workspace.Write("Code/Code.csproj", "<Project Sdk='Microsoft.NET.Sdk'><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup></Project>");
        workspace.Write("Code/Code.cs", "public static class C { public static bool A(int value) => value >= 0; }");
        workspace.Write("Tests/Tests.csproj", """
            <Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup>
              <ItemGroup><ProjectReference Include="../Code/Code.csproj"/>
                <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.11.1"/>
                <PackageReference Include="xunit" Version="2.9.2"/>
                <PackageReference Include="xunit.runner.visualstudio" Version="2.8.2"/>
              </ItemGroup></Project>
            """);
        workspace.Write("Tests/Tests.cs", "using Xunit; public class Tests { [Fact] public void Boundary() => Assert.True(C.A(0)); }");
        var real = new ProcessRunner();
        foreach (var args in new[] { new[] { "init" }, ["config", "user.email", "test@example.invalid"], ["config", "user.name", "Test"], ["add", "."], ["commit", "-m", "baseline"] })
            Assert.Equal(0, (await real.RunAsync(new("git", args, workspace.Root), default)).ExitCode);
        var root = OperatingSystem.IsMacOS() && workspace.Root.StartsWith("/var/", StringComparison.Ordinal) ? "/private" + workspace.Root : workspace.Root;
        using var cancelled = new CancellationTokenSource();
        var roots = new List<string>();
        var runner = new InterceptRunner(real, call =>
        {
            if (call.Arguments[0] == "build" && call.WorkingDirectory.Contains("walker-prepared-", StringComparison.Ordinal))
            {
                roots.Add(call.WorkingDirectory);
                if (rejection == "batch-build") return new(1, "Batch failed", "", 1);
                if (rejection == "cancel") { cancelled.Cancel(); throw new OperationCanceledException(cancelled.Token); }
            }
            if (rejection == "baseline-report" && call.Environment?.Values.Contains("0") == true) return new(0, "No report", "", 1);
            return null;
        });
        var executor = new SwitchingMutationExecutor(runner);
        var request = new VerificationRequest(root, "HEAD", "Code/Code.csproj", ["Tests/Tests.csproj"]);
        await executor.VerifyAsync(request, default);
        var mutant = Assert.Single((await new RoslynMutationDiscoverer().DiscoverAsync(root, [new("Code/Code.cs", [new(1, 1)], false)], default)).Mutants);
        if (rejection == "cancel")
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => executor.PrepareAsync(request, [mutant], cancelled.Token));
        else
        {
            Assert.Null(await executor.PrepareAsync(request, [mutant], default));
            Assert.Equal(MutationOutcome.Killed, (await executor.ExecuteAsync(mutant, new(request, new AllTestsSelector(request.Tests)), default)).Outcome);
        }
        Assert.Single(roots);
        Assert.False(Directory.Exists(roots[0]));
        Assert.Contains("value >= 0", File.ReadAllText(Path.Combine(root, "Code/Code.cs")));
    }
    private sealed class InterceptRunner(IProcessRunner inner, Func<ProcessRequest, ProcessResult?> intercept) : IProcessRunner
    {
        public Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken token)
            => intercept(request) is { } result ? Task.FromResult(result) : inner.RunAsync(request, token);
    }
    private sealed class RecordingRunner : IProcessRunner
    {
        private readonly ProcessRunner runner = new();
        public List<ProcessRequest> Calls { get; } = [];
        public Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken token)
        {
            Calls.Add(request);
            return runner.RunAsync(request, token);
        }
    }
    [Theory]
    [InlineData("success")]
    [InlineData("cancel")]
    [InlineData("empty")]
    [InlineData("baseline-failure")]
    public async Task EnginePreparesOnlyAfterBaselineAndAlwaysDisposesItsSession(string mode)
    {
        using var cancellation = new CancellationTokenSource();
        var candidate = DiscoveryTests.Dummy("candidate");
        var fake = new FakeEngine(mode == "empty" ? [] : [candidate], _ => throw new Exception("Legacy executor must not run"), mode == "baseline-failure");
        var prepared = new PreparingExecutor(cancellation, mode == "cancel");
        var report = await new VerificationEngine(fake, fake, prepared, fake)
            .VerifyAsync(new("root", "HEAD", "project", ["tests"]), cancellation.Token);
        var started = mode is "success" or "cancel";
        Assert.Equal(started ? 1 : 0, prepared.Preparations);
        Assert.Equal(started, prepared.Disposed);
        Assert.Equal(mode == "success" ? "passed" : mode == "baseline-failure" ? "error" : "incomplete", report.Status);
        if (mode == "success")
        {
            Assert.Equal(candidate.Id, Assert.Single(report.Results).Mutant.Id);
            Assert.Equal(1, report.Preparation!.Supported);
        }
    }
    private sealed class PreparingExecutor(CancellationTokenSource cancellation, bool cancel) : IMutationExecutor, IBatchMutationPreparer, IPreparedMutationSession
    {
        public int Preparations { get; private set; }
        public bool Disposed { get; private set; }
        public IReadOnlySet<string> SupportedMutantIds { get; private set; } = new HashSet<string>();
        public string? Detail => null;
        public Task<IPreparedMutationSession?> PrepareAsync(VerificationRequest request, IReadOnlyList<Mutant> selected, CancellationToken token)
        {
            Preparations++;
            SupportedMutantIds = selected.Select(m => m.Id).ToHashSet();
            if (cancel) cancellation.Cancel();
            return Task.FromResult<IPreparedMutationSession?>(this);
        }
        public Task<MutationResult> ExecuteAsync(Mutant mutant, VerificationContext context, CancellationToken token) => Task.FromResult(new MutationResult(mutant, MutationOutcome.Killed));
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }
}
