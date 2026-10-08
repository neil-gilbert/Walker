using System.Text.Json;
using Walker.Execution;
using Xunit;

namespace Walker.Tests;

public sealed class CliProtocolTests
{
    [Theory]
    [InlineData("--max-mutants", "nope", "--format", "json")]
    [InlineData("--format", "json", "--max-mutants", "nope")]
    [InlineData("--unknown", "value", "--format", "json")]
    [InlineData("--format", "json", "--unknown", "value")]
    [InlineData("--format", "json", "--timeout")]
    [InlineData("--format", "text", "--max-mutants", "nope", "--format", "json")]
    [InlineData("--format", "json", "--format")]
    public async Task EffectiveJsonFormatSurvivesEarlierArgumentErrors(params string[] arguments)
    {
        using var workspace = new Workspace();
        workspace.Write("walker.json", "{\"format\":\"text\"}");
        var result = await Run(workspace, arguments);
        Assert.Equal(2, result.ExitCode);
        ReportContract.AssertValid(result.StandardOutput);
        using var report = JsonDocument.Parse(result.StandardOutput);
        Assert.Equal("error", report.RootElement.GetProperty("status").GetString());
        Assert.Equal("invalid_argument", report.RootElement.GetProperty("diagnostics")[0].GetProperty("code").GetString());
        Assert.Equal(1, report.RootElement.GetProperty("schemaVersion").GetInt32());
    }

    [Theory]
    [InlineData("{")]
    [InlineData("{\"unknown\":true}")]
    [InlineData("{\"maxMutants\":1,\"MAXMUTANTS\":2}")]
    public async Task InvalidConfigurationStillReturnsRequestedJson(string configuration)
    {
        using var workspace = new Workspace();
        workspace.Write("walker.json", configuration);
        var result = await Run(workspace, ["--timeout", "60", "--format", "json"]);
        Assert.Equal(2, result.ExitCode);
        ReportContract.AssertValid(result.StandardOutput);
        using var report = JsonDocument.Parse(result.StandardOutput);
        Assert.Equal("invalid_configuration", report.RootElement.GetProperty("diagnostics")[0].GetProperty("code").GetString());
    }

    [Theory]
    [InlineData("text")]
    [InlineData("invalid")]
    public async Task LastCompleteFormatControlsErrorRendering(string finalFormat)
    {
        using var workspace = new Workspace();
        var result = await Run(workspace, ["--format", "json", "--max-mutants", "nope", "--format", finalFormat]);
        Assert.Equal(2, result.ExitCode);
        Assert.StartsWith("WALKER", result.StandardOutput);
    }

    private static Task<Walker.Core.ProcessResult> Run(Workspace workspace, string[] arguments) =>
        new ProcessRunner().RunAsync(new("dotnet", [typeof(Walker.Cli.TextReportWriter).Assembly.Location, "verify", ..arguments], workspace.Root), default);

    [Fact]
    public async Task IsolatedZeroCandidateRunIsIncompleteAndConformsToSchema()
    {
        using var fixture = await IsolatedVerificationTests.Fixture.Create();
        fixture.Workspace.Write("Code/walker.json", "{\"isolate\":true,\"base\":\"HEAD\",\"project\":\"Code.csproj\",\"tests\":[\"../Tests/Tests.csproj\"]}");
        var result = await new ProcessRunner().RunAsync(new("dotnet", [typeof(Walker.Cli.TextReportWriter).Assembly.Location, "verify", "--format", "json"], Path.Combine(fixture.Root, "Code")), default);
        Assert.True(result.ExitCode == 3, result.StandardOutput + result.StandardError);
        ReportContract.AssertValid(result.StandardOutput);
        using var report = JsonDocument.Parse(result.StandardOutput);
        Assert.Equal("no_eligible_expressions", report.RootElement.GetProperty("diagnostics")[0].GetProperty("code").GetString());
        Assert.Equal("Code/Code.csproj", report.RootElement.GetProperty("isolation").GetProperty("scope").GetProperty("project").GetString());
    }
}
