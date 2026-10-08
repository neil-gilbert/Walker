using System.Text.Json;
using Walker.Core;
using Walker.Execution;
using Xunit;

namespace Walker.Tests;

public sealed class IsolatedVerificationTests
{
    [Fact]
    public async Task SnapshotPreservesWorkingBytesStagingAndUntrackedTests()
    {
        using var fixture = await Fixture.Create();
        fixture.Workspace.Write("Code/Code.cs", "class C { bool M(int a, int b) => a >= b; }\n");
        await fixture.Git("add", "Code/Code.cs");
        var dirty = System.Text.Encoding.UTF8.GetPreamble().Concat(System.Text.Encoding.UTF8.GetBytes("// dirty\r\nclass C { bool M(int a, int b) => a >= b; }\r\n")).ToArray();
        File.WriteAllBytes(Path.Combine(fixture.Root, "Code/Code.cs"), dirty);
        fixture.Workspace.Write("Tests/NewTest.cs", "class NewTest {}\n");
        fixture.Workspace.Write("Code/Untracked.cs", "class Untracked {}\n");
        fixture.Workspace.Write("Code/bin/sentinel", "original build output");
        var before = await fixture.SourceState();
        var report = await new IsolatedVerificationSession(fixture.Runner).VerifyAsync(fixture.Request, fixture.Root, async (request, _, token) =>
        {
            Assert.NotEqual(fixture.Root, request.Root);
            Assert.Equal(dirty, File.ReadAllBytes(Path.Combine(request.Root, "Code/Code.cs")));
            Assert.True(File.Exists(Path.Combine(request.Root, "Tests/NewTest.cs")));
            Assert.False(File.Exists(Path.Combine(request.Root, "Code/bin/sentinel")));
            var tracked = await fixture.Runner.RunAsync(new("git", ["ls-files"], request.Root), token);
            Assert.DoesNotContain("Untracked.cs", tracked.StandardOutput);
            fixture.Workspace.Write("Code/Code.cs", "// a new user edit after capture\n");
            return Passed(request);
        });
        Assert.Equal(0, report.ExitCode);
        Assert.NotNull(report.Isolation);
        Assert.Equal("removed", report.Isolation.CleanupState);
        Assert.Contains("Code/Untracked.cs", report.Isolation.UntrackedProductionFiles);
        Assert.False(Directory.Exists(report.Isolation.WorktreePath));
        Assert.Equal(before.Head, (await fixture.SourceState()).Head);
        Assert.Equal(before.Index, (await fixture.SourceState()).Index);
        Assert.Equal("// a new user edit after capture\n", File.ReadAllText(Path.Combine(fixture.Root, "Code/Code.cs")));
        Assert.Equal("original build output", File.ReadAllText(Path.Combine(fixture.Root, "Code/bin/sentinel")));
        using var saved = JsonDocument.Parse(await File.ReadAllTextAsync(report.Isolation.ReportPath));
        Assert.Equal("removed", saved.RootElement.GetProperty("isolation").GetProperty("cleanupState").GetString());
    }

    [Fact]
    public async Task PendingSourceJournalIsNeverRecovered()
    {
        using var fixture = await Fixture.Create();
        var journal = MutationJournal.PathFor(fixture.Root);
        await File.WriteAllTextAsync(journal, "source recovery sentinel");
        var original = File.ReadAllBytes(Path.Combine(fixture.Root, "Code/Code.cs"));
        var report = await new IsolatedVerificationSession(fixture.Runner).VerifyAsync(fixture.Request, fixture.Root,
            (_, _, _) => throw new Exception("Must not verify"));
        Assert.Equal(2, report.ExitCode);
        Assert.Equal("source_recovery_pending", Assert.Single(report.Diagnostics).Code);
        Assert.Equal("source recovery sentinel", await File.ReadAllTextAsync(journal));
        Assert.Equal(original, File.ReadAllBytes(Path.Combine(fixture.Root, "Code/Code.cs")));
    }

    [Theory]
    [InlineData("<Target Name='Sentinel' BeforeTargets='Build'><WriteLinesToFile File='/tmp/walker-must-not-execute' Lines='bad'/></Target>")]
    [InlineData("<ItemGroup><ProjectReference Include='../../External/Code.csproj'/></ItemGroup>")]
    [InlineData("<PropertyGroup><BaseOutputPath>/tmp/external-output</BaseOutputPath></PropertyGroup>")]
    [InlineData("<ItemGroup><Content Include='**/*.json'/></ItemGroup>")]
    public async Task UnsupportedBuildLayoutsFailBeforeVerification(string xml)
    {
        using var fixture = await Fixture.Create();
        fixture.Workspace.Write("Code/Code.csproj", "<Project Sdk='Microsoft.NET.Sdk'><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>" + xml + "</Project>");
        var before = await fixture.SourceState();
        var report = await new IsolatedVerificationSession(fixture.Runner).VerifyAsync(fixture.Request, fixture.Root,
            (_, _, _) => throw new Exception("Must not verify unsupported inputs"));
        Assert.Equal(2, report.ExitCode);
        Assert.Equal("isolation_unsupported", Assert.Single(report.Diagnostics).Code);
        Assert.Equal(before, await fixture.SourceState());
    }

    [Fact]
    public async Task ChangedSnapshotIsRetainedAndCompletedEvidenceSurvivesCleanupFailure()
    {
        using var fixture = await Fixture.Create();
        var original = File.ReadAllBytes(Path.Combine(fixture.Root, "Code/Code.cs"));
        var report = await new IsolatedVerificationSession(fixture.Runner).VerifyAsync(fixture.Request, fixture.Root, (request, _, _) =>
        {
            File.AppendAllText(Path.Combine(request.Root, "Code/Code.cs"), "// unexpected change");
            return Task.FromResult(Passed(request));
        });
        Assert.Equal(2, report.ExitCode);
        Assert.Equal(1, report.Killed);
        Assert.Contains(report.Diagnostics, d => d.Code == "isolation_cleanup_failed");
        Assert.Equal("retained", report.Isolation!.CleanupState);
        Assert.True(Directory.Exists(report.Isolation.WorktreePath));
        Assert.Equal(original, File.ReadAllBytes(Path.Combine(fixture.Root, "Code/Code.cs")));
        await fixture.Runner.RunAsync(new("git", ["worktree", "remove", "--force", report.Isolation.WorktreePath], fixture.Root), default);
    }

    [Fact]
    public async Task GracefulCancellationDuringVerificationProducesDurableIncompleteReport()
    {
        using var fixture = await Fixture.Create();
        using var cancellation = new CancellationTokenSource();
        var before = await fixture.SourceState();
        var report = await new IsolatedVerificationSession(fixture.Runner).VerifyAsync(fixture.Request, fixture.Root, async (_, _, token) =>
        {
            cancellation.Cancel();
            await Task.Delay(Timeout.Infinite, token);
            throw new Exception("Unreachable");
        }, cancellation.Token);
        Assert.Equal(3, report.ExitCode);
        Assert.Equal("cancelled", Assert.Single(report.Diagnostics).Code);
        Assert.Equal("removed", report.Isolation!.CleanupState);
        Assert.True(File.Exists(report.Isolation.ReportPath));
        Assert.Equal(before, await fixture.SourceState());
    }

    private static VerificationResult Passed(VerificationRequest request) => new("passed", request.Base, 1, 1, 1,
        [new(DiscoveryTests.Dummy("fixture"), MutationOutcome.Killed)], 1, new());

    [Fact]
    public async Task SnapshotIncludesStagedAdditionsRenamesAndDeletions()
    {
        using var fixture = await Fixture.Create();
        fixture.Workspace.Write("Code/Old.cs", "class Old {}\n");
        fixture.Workspace.Write("Code/Deleted.cs", "class Deleted {}\n");
        await fixture.Git("add", "."); await fixture.Git("commit", "-m", "extra files");
        await fixture.Git("mv", "Code/Old.cs", "Code/Renamed.cs");
        await fixture.Git("rm", "Code/Deleted.cs");
        fixture.Workspace.Write("Code/Added.cs", "class Added {}\n");
        await fixture.Git("add", "Code/Added.cs");
        var before = await fixture.SourceState();
        var result = await new IsolatedVerificationSession(fixture.Runner).VerifyAsync(fixture.Request, Path.Combine(fixture.Root, "Code"), async (request, process, token) =>
        {
            Assert.True(File.Exists(Path.Combine(request.Root, "Code/Renamed.cs")));
            Assert.True(File.Exists(Path.Combine(request.Root, "Code/Added.cs")));
            Assert.False(File.Exists(Path.Combine(request.Root, "Code/Deleted.cs")));
            Assert.False(File.Exists(Path.Combine(request.Root, "Code/Old.cs")));
            var tracked = await process.RunAsync(new("git", ["ls-files"], request.Root), token);
            Assert.Contains("Code/Added.cs", tracked.StandardOutput);
            return Passed(request);
        });
        Assert.True(result.ExitCode == 0, result.Error);
        Assert.Equal(before, await fixture.SourceState());
    }

    [Fact]
    public async Task EditRacingWithCaptureIsRejectedWithoutLosingTheNewEdit()
    {
        using var fixture = await Fixture.Create();
        var changed = false;
        var runner = new ControlledRunner(async (request, token) =>
        {
            var result = await fixture.Runner.RunAsync(request, token);
            if (!changed && request.FileName == "git" && request.Arguments.Contains("diff"))
            { changed = true; fixture.Workspace.Write("Code/Code.cs", "// new user edit\nclass C {}\n"); }
            return result;
        });
        var report = await new IsolatedVerificationSession(runner).VerifyAsync(fixture.Request, fixture.Root,
            (_, _, _) => throw new Exception("Mixed snapshot must not execute"));
        Assert.Equal(2, report.ExitCode);
        Assert.Equal("snapshot_changed", Assert.Single(report.Diagnostics).Code);
        Assert.Equal("// new user edit\nclass C {}\n", File.ReadAllText(Path.Combine(fixture.Root, "Code/Code.cs")));
    }

    [Fact]
    public async Task CancellationDuringCaptureDoesNotCreateAWorktree()
    {
        using var fixture = await Fixture.Create();
        using var cancellation = new CancellationTokenSource();
        var runner = new ControlledRunner(async (request, token) =>
        {
            var result = await fixture.Runner.RunAsync(request, token);
            if (request.Arguments.Contains("--cached")) cancellation.Cancel();
            return result;
        });
        var report = await new IsolatedVerificationSession(runner).VerifyAsync(fixture.Request, fixture.Root,
            (_, _, _) => throw new Exception("Must not verify"), cancellation.Token);
        Assert.Equal(3, report.ExitCode);
        Assert.Equal("cancelled", Assert.Single(report.Diagnostics).Code);
        Assert.Equal("not_created", report.Isolation!.CleanupState);
        Assert.True(File.Exists(report.Isolation.ReportPath));
    }

    [Theory]
    [InlineData("Required.cs")]
    [InlineData("Required.resx")]
    [InlineData("settings.json")]
    public async Task SymlinkAndIgnoredRequiredSourceAreRefused(string fileName)
    {
        using var fixture = await Fixture.Create();
        if (OperatingSystem.IsWindows()) return;
        File.CreateSymbolicLink(Path.Combine(fixture.Root, "Code/Link.cs"), Path.Combine(fixture.Root, "Code/Code.cs"));
        var session = new IsolatedVerificationSession(fixture.Runner);
        var linked = await session.VerifyAsync(fixture.Request, fixture.Root, (_, _, _) => throw new Exception("Must not verify"));
        Assert.Equal("isolation_unsupported", Assert.Single(linked.Diagnostics).Code);
        File.Delete(Path.Combine(fixture.Root, "Code/Link.cs"));
        File.AppendAllText(Path.Combine(fixture.Root, ".gitignore"), "Code/" + fileName + "\n");
        fixture.Workspace.Write("Code/" + fileName, "ignored required input\n");
        var ignored = await session.VerifyAsync(fixture.Request, fixture.Root, (_, _, _) => throw new Exception("Must not verify"));
        Assert.Equal("isolation_unsupported", Assert.Single(ignored.Diagnostics).Code);
    }

    [Fact]
    public async Task ConcurrentSessionsAndExistingWorktreeKeepSeparateOwnership()
    {
        using var fixture = await Fixture.Create();
        using var unrelated = new Workspace();
        var existing = Path.Combine(unrelated.Root, "worktree");
        await fixture.Git("worktree", "add", "--detach", existing, "HEAD");
        var before = await fixture.SourceState();
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var arrivals = 0;
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        async Task<VerificationResult> Verify(VerificationRequest request, IProcessRunner process, CancellationToken token)
        {
            if (Interlocked.Increment(ref arrivals) == 2) ready.SetResult();
            await ready.Task.WaitAsync(token);
            return Passed(request);
        }
        try
        {
            var reports = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ => new IsolatedVerificationSession(fixture.Runner)
                .VerifyAsync(fixture.Request, fixture.Root, Verify, cancellation.Token)));
            Assert.All(reports, report => Assert.True(report.ExitCode == 0, report.Error));
            Assert.NotEqual(reports[0].Isolation!.WorktreePath, reports[1].Isolation!.WorktreePath);
            Assert.NotEqual(reports[0].Isolation!.ReportPath, reports[1].Isolation!.ReportPath);
            Assert.True(File.Exists(Path.Combine(existing, "Code/Code.cs")));
            Assert.Equal(before, await fixture.SourceState());
        }
        finally { await fixture.Git("worktree", "remove", existing); }
    }

    [Fact]
    public async Task SetupConsumesTheSameGlobalBudget()
    {
        using var fixture = await Fixture.Create();
        var runner = new ControlledRunner(async (request, token) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(3), token);
            return await fixture.Runner.RunAsync(request, token);
        });
        var report = await new IsolatedVerificationSession(runner).VerifyAsync(fixture.Request with { TimeoutSeconds = 1 }, fixture.Root,
            (_, _, _) => throw new Exception("Expired setup must not verify"));
        Assert.Equal(3, report.ExitCode);
        Assert.Equal("budget_exhausted", Assert.Single(report.Diagnostics).Code);
        Assert.Equal("not_created", report.Isolation!.CleanupState);
        Assert.InRange(report.Isolation.SetupMs, 700, 5000);
    }

    [Fact]
    public async Task GitCleanupFailureRetainsEvidenceAndOwnedPath()
    {
        using var fixture = await Fixture.Create();
        var runner = new ControlledRunner((request, token) => request.FileName == "git" && request.Arguments.Contains("remove")
            ? Task.FromResult(new ProcessResult(1, "", "injected cleanup failure", 1))
            : fixture.Runner.RunAsync(request, token));
        var report = await new IsolatedVerificationSession(runner).VerifyAsync(fixture.Request, fixture.Root,
            (request, _, _) => Task.FromResult(Passed(request)));
        Assert.Equal(2, report.ExitCode);
        Assert.Equal(1, report.Killed);
        Assert.Equal("retained", report.Isolation!.CleanupState);
        ReportContract.AssertValid(await File.ReadAllTextAsync(report.Isolation.ReportPath));
        Assert.Contains(report.Diagnostics, diagnostic => diagnostic.Code == "isolation_cleanup_failed");
        await fixture.Git("worktree", "remove", "--force", report.Isolation.WorktreePath);
    }

    [Fact]
    public async Task HardTerminationLeavesReadableSnapshotAndOriginalCheckoutUntouched()
    {
        using var fixture = await Fixture.Create();
        fixture.Workspace.Write("Code/Code.cs", "class C { bool M(int a, int b) => a >= b; }\n");
        await fixture.Git("add", "Code/Code.cs");
        File.AppendAllText(Path.Combine(fixture.Root, "Code/Code.cs"), "// unstaged sentinel\n");
        var before = await fixture.SourceState();
        var bytes = File.ReadAllBytes(Path.Combine(fixture.Root, "Code/Code.cs"));
        var start = new System.Diagnostics.ProcessStartInfo("dotnet")
        {
            WorkingDirectory = fixture.Root, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (var argument in new[] { typeof(Walker.Cli.TextReportWriter).Assembly.Location, "verify", "--isolate", "--base", "HEAD",
            "--project", "Code/Code.csproj", "--tests", "Tests/Tests.csproj", "--timeout", "120", "--format", "json" }) start.ArgumentList.Add(argument);
        start.Environment["MSBUILDDISABLENODEREUSE"] = "1";
        start.Environment["DOTNET_CLI_USE_MSBUILD_SERVER"] = "0";
        using var process = System.Diagnostics.Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        string? artifacts = null;
        try
        {
            using var wait = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            while (await process.StandardError.ReadLineAsync(wait.Token) is { } line)
            {
                const string prefix = "Isolated snapshot ready. Artifacts: ";
                if (line.StartsWith(prefix, StringComparison.Ordinal)) { artifacts = line[prefix.Length..]; break; }
            }
            Assert.NotNull(artifacts);
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            using var metadata = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(artifacts!, "session.json")));
            Assert.Equal("verifying", metadata.RootElement.GetProperty("state").GetString());
            Assert.Equal(bytes, File.ReadAllBytes(Path.Combine(artifacts!, "inputs/Code/Code.cs")));
            Assert.Equal(bytes, File.ReadAllBytes(Path.Combine(fixture.Root, "Code/Code.cs")));
            Assert.Equal(before, await fixture.SourceState());
            Assert.True(File.Exists(Path.Combine(artifacts!, "process.log")));
        }
        finally
        {
            if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); }
            await stdout;
            if (artifacts != null) await fixture.Git("worktree", "remove", "--force", Path.Combine(artifacts, "worktree"));
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CancellationDuringBaselineOrAppliedMutationRestoresBeforeCleanup(bool duringBaseline)
    {
        using var fixture = await Fixture.Create();
        fixture.Workspace.Write("Code/Code.cs", "class C { bool M(int a, int b) => a >= b; }\n");
        var before = await fixture.SourceState();
        var bytes = File.ReadAllBytes(Path.Combine(fixture.Root, "Code/Code.cs"));
        using var cancellation = new CancellationTokenSource();
        var applied = false;
        var report = await new IsolatedVerificationSession(fixture.Runner).VerifyAsync(fixture.Request, fixture.Root, async (request, _, token) =>
        {
            var discovered = await new Walker.Roslyn.RoslynMutationDiscoverer().DiscoverAsync(request.Root,
                [new("Code/Code.cs", [new(1, 1)], true)], token);
            var fake = new FakeEngine(discovered.Mutants, _ => throw new Exception("Use real source executor"));
            var process = new ControlledRunner(async (_, childToken) =>
            {
                applied = !bytes.SequenceEqual(File.ReadAllBytes(Path.Combine(request.Root, "Code/Code.cs")));
                cancellation.Cancel();
                await Task.Delay(Timeout.Infinite, childToken);
                throw new Exception("Unreachable");
            });
            var executor = new DotnetMutationExecutor(process);
            IBaselineVerifier baseline = duringBaseline ? executor : new CompletedBaseline();
            var result = await new VerificationEngine(fake, fake, executor, baseline).VerifyAsync(request, token);
            Assert.Equal(bytes, File.ReadAllBytes(Path.Combine(request.Root, "Code/Code.cs")));
            Assert.False(File.Exists(MutationJournal.PathFor(request.Root)));
            return result;
        }, cancellation.Token);
        Assert.Equal(3, report.ExitCode);
        Assert.Equal(!duringBaseline, applied);
        Assert.Equal(duringBaseline ? MutationOutcome.Skipped : MutationOutcome.TimedOut, Assert.Single(report.Results).Outcome);
        Assert.Equal("removed", report.Isolation!.CleanupState);
        ReportContract.AssertValid(await File.ReadAllTextAsync(report.Isolation.ReportPath));
        Assert.Equal(bytes, File.ReadAllBytes(Path.Combine(fixture.Root, "Code/Code.cs")));
        Assert.Equal(before, await fixture.SourceState());
    }

    private sealed class CompletedBaseline : IBaselineVerifier
    {
        public Task VerifyAsync(VerificationRequest request, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class ControlledRunner(Func<ProcessRequest, CancellationToken, Task<ProcessResult>> run) : IProcessRunner
    {
        public Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken token) => run(request, token);
    }

    internal sealed class Fixture : IDisposable
    {
        public Workspace Workspace { get; } = new();
        public string Root => Workspace.Root;
        public ProcessRunner Runner { get; } = new();
        public VerificationRequest Request => new(Root, "HEAD", Path.Combine(Root, "Code/Code.csproj"), [Path.Combine(Root, "Tests/Tests.csproj")], TimeoutSeconds: 120);
        public static async Task<Fixture> Create()
        {
            var fixture = new Fixture();
            fixture.Workspace.Write(".gitignore", "bin/\nobj/\n");
            fixture.Workspace.Write("Code/Code.csproj", "<Project Sdk='Microsoft.NET.Sdk'><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");
            fixture.Workspace.Write("Code/Code.cs", "class C { bool M(int a, int b) => a > b; }\n");
            fixture.Workspace.Write("Tests/Tests.csproj", "<Project Sdk='Microsoft.NET.Sdk'><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup><ItemGroup><ProjectReference Include='../Code/Code.csproj'/></ItemGroup></Project>");
            await fixture.Git("init"); await fixture.Git("config", "user.email", "tests@example.invalid"); await fixture.Git("config", "user.name", "Tests");
            await fixture.Git("add", "."); await fixture.Git("commit", "-m", "baseline");
            return fixture;
        }
        public async Task<string> Git(params string[] arguments)
        {
            var result = await Runner.RunAsync(new("git", arguments, Root, Environment: new Dictionary<string, string?> { ["GIT_OPTIONAL_LOCKS"] = "0" }), default);
            Assert.True(result.ExitCode == 0, result.StandardError);
            return result.StandardOutput;
        }
        public async Task<(string Head, string Index, string Status)> SourceState() =>
            (await Git("rev-parse", "HEAD"), await Git("ls-files", "--stage", "-z"), await Git("status", "--porcelain", "-z", "--untracked-files=all"));
        public void Dispose() => Workspace.Dispose();
    }
}
