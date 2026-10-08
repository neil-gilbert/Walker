using System.Text.Json;
using Walker.Core;
using Walker.Execution;
using Xunit;

namespace Walker.Tests;

public sealed class AgentWorkflowIntegrationTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task ProposedEqualityTestCatchesFaultOnlyInPrivateSnapshot(bool explicitChallenge, bool multipleFrameworks)
    {
        using var fixture = await CreateFixture();
        var sourcePath = Path.Combine(fixture.Root, "Payments/PaymentService.cs");
        var source = File.ReadAllText(sourcePath);
        var original = File.ReadAllBytes(sourcePath);
        var testBytes = File.ReadAllBytes(Path.Combine(fixture.Root, "Payments.Tests/PaymentTests.cs"));
        if (multipleFrameworks)
        {
            var project = Path.Combine(fixture.Root, "Payments.Tests/Payments.Tests.csproj");
            File.WriteAllText(project, File.ReadAllText(project).Replace("<PropertyGroup>",
                "<PropertyGroup><TargetFramework></TargetFramework><TargetFrameworks>net10.0;net10.0-windows</TargetFrameworks>", StringComparison.Ordinal));
        }
        if (!explicitChallenge)
        {
            File.WriteAllText(sourcePath, source.Replace("balance >= price", "balance > price"));
            await Git(fixture, "add", ".");
            await Git(fixture, "commit", "-m", "starting contract");
            File.WriteAllBytes(sourcePath, original);
        }
        using var manifests = new Workspace();
        var patch = new TestPatchManifest(1, "Exact balance permits purchase", [new("Payments.Tests/BoundaryTests.cs", null,
            "using Xunit; namespace Payments.Tests; public class BoundaryTests { [Fact] public void ExactBalance() => Assert.True(new PaymentService().CanPurchase(10m, 10m)); }")]);
        manifests.Write("tests.json", JsonSerializer.Serialize(patch, VerificationReportJson.Options));
        var challenge = new ChallengeManifest(1, [new("Payments/PaymentService.cs", Mutant.Hash(source), source.IndexOf("balance >= price", StringComparison.Ordinal),
            "balance >= price", "balance > price", "Rejecting exact balance", "Exact balance permits purchase")]);
        manifests.Write("faults.json", JsonSerializer.Serialize(challenge, VerificationReportJson.Options));
        var result = await Run(fixture, ["--isolate", "--progress", "--investigate", "--test-patch", Path.Combine(manifests.Root, "tests.json"),
            ..(explicitChallenge ? new[] { "--challenge", Path.Combine(manifests.Root, "faults.json") } : Array.Empty<string>())]);
        Assert.True(result.ExitCode == 0, result.StandardOutput + result.StandardError);
        ReportContract.AssertValid(result.StandardOutput);
        using var json = JsonDocument.Parse(result.StandardOutput);
        var evidence = json.RootElement.GetProperty("testImprovement");
        Assert.Equal("verified", evidence.GetProperty("status").GetString());
        Assert.Equal(1, evidence.GetProperty("before").GetProperty("survived").GetInt32());
        Assert.Single(evidence.GetProperty("verifiedMutantIds").EnumerateArray());
        Assert.True(json.RootElement.GetProperty("results")[0].GetProperty("killConfirmed").GetBoolean());
        Assert.Contains("ExactBalance", json.RootElement.GetProperty("results")[0].GetProperty("failingTests")[0].GetString());
        Assert.Equal(explicitChallenge ? "CustomFault" : "ConditionalBoundary", json.RootElement.GetProperty("results")[0].GetProperty("mutant").GetProperty("operator").GetString());
        Assert.Contains("[walker progress]", result.StandardError);
        Assert.DoesNotContain("[walker", result.StandardOutput);
        Assert.Empty(json.RootElement.GetProperty("investigation").GetProperty("gaps").EnumerateArray());
        Assert.Equal(original, File.ReadAllBytes(sourcePath));
        Assert.Equal(testBytes, File.ReadAllBytes(Path.Combine(fixture.Root, "Payments.Tests/PaymentTests.cs")));
        Assert.False(File.Exists(Path.Combine(fixture.Root, "Payments.Tests/BoundaryTests.cs")));
        var isolation = json.RootElement.GetProperty("isolation");
        Assert.Equal("removed", isolation.GetProperty("cleanupState").GetString());
        ReportContract.AssertValid(File.ReadAllText(isolation.GetProperty("reportPath").GetString()!));
    }

    [Fact]
    public async Task QuietBaselineEmitsLiveHeartbeatAndKeepsFinalJsonClean()
    {
        using var fixture = await CreateFixture();
        var source = File.ReadAllText(Path.Combine(fixture.Root, "Payments/PaymentService.cs"));
        fixture.Write("Payments/PaymentService.cs", source.Replace("balance >= price", "balance > price"));
        fixture.Write("Payments.Tests/QuietTest.cs", "using Xunit; namespace Payments.Tests; public class QuietTest { [Fact] public async System.Threading.Tasks.Task Wait() => await System.Threading.Tasks.Task.Delay(120000); }");
        var result = await Run(fixture, ["--isolate", "--progress", "--timeout", "18"]);
        Assert.True(result.ExitCode == 3, result.StandardOutput + result.StandardError);
        ReportContract.AssertValid(result.StandardOutput);
        Assert.Contains("[walker heartbeat]", result.StandardError);
        Assert.Contains("phase baseline", result.StandardError);
        Assert.Contains("active: test", result.StandardError);
        Assert.DoesNotContain("[walker", result.StandardOutput);
        Assert.Equal(source.Replace("balance >= price", "balance > price"), File.ReadAllText(Path.Combine(fixture.Root, "Payments/PaymentService.cs")));
        using var report = JsonDocument.Parse(result.StandardOutput);
        Assert.Equal("removed", report.RootElement.GetProperty("isolation").GetProperty("cleanupState").GetString());
    }

    [Theory]
    [InlineData("--challenge")]
    [InlineData("--test-patch")]
    public async Task OptionalSourcePatchesRequireExplicitIsolationBeforeReadingManifest(string option)
    {
        using var fixture = await CreateFixture();
        var result = await Run(fixture, [option, "/missing.json"]);
        Assert.Equal(2, result.ExitCode);
        using var report = JsonDocument.Parse(result.StandardOutput);
        Assert.Contains("require --isolate", report.RootElement.GetProperty("error").GetString());
        Assert.DoesNotContain("[walker", result.StandardError);
    }

    private static Task<ProcessResult> Run(Workspace fixture, string[] extra) => new ProcessRunner().RunAsync(new("dotnet",
        [typeof(Walker.Cli.TextReportWriter).Assembly.Location, "verify", "--base", "HEAD", "--project", "Payments/Payments.csproj",
            "--tests", "Payments.Tests/Payments.Tests.csproj", "--max-mutants", "1", "--timeout", "120", "--format", "json", ..extra], fixture.Root), default);

    private static async Task<Workspace> CreateFixture()
    {
        var workspace = new Workspace();
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root != null && !File.Exists(Path.Combine(root.FullName, "Walker.sln"))) root = root.Parent;
        Assert.NotNull(root);
        workspace.Write("Directory.Build.props", File.ReadAllText(Path.Combine(root.FullName, "Directory.Build.props")));
        foreach (var file in new[] { "Payments/Payments.csproj", "Payments/PaymentService.cs", "Payments.Tests/Payments.Tests.csproj", "Payments.Tests/PaymentTests.cs" })
            workspace.Write(file, File.ReadAllText(Path.Combine(root.FullName, "examples/Payments", file)));
        workspace.Write(".gitignore", "bin/\nobj/\nTestResults/\n");
        await Git(workspace, "init"); await Git(workspace, "config", "user.email", "tests@example.invalid");
        await Git(workspace, "config", "user.name", "Tests"); await Git(workspace, "add", "."); await Git(workspace, "commit", "-m", "fixture");
        return workspace;
    }
    private static async Task Git(Workspace workspace, params string[] args)
    {
        var result = await new ProcessRunner().RunAsync(new("git", args, workspace.Root), default);
        Assert.True(result.ExitCode == 0, result.StandardError);
    }
}
