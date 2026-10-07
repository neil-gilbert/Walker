using System.Text.Json;
using Walker.Execution;
using Xunit;

namespace Walker.Tests;

public sealed class CliConfigurationTests
{
    [Theory]
    [InlineData("{\"maxMutants\":1,\"maxMutants\":20}")]
    [InlineData("{\"maxMutants\":1,\"MaxMutants\":20}")]
    public async Task DuplicateConfigurationSettingsAreErrors(string configuration)
    {
        var error = await ConfigurationError(configuration);
        Assert.Contains("duplicate", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task UniqueConfigurationSettingsRemainCaseInsensitive()
    {
        var error = await ConfigurationError("{\"MaXMuTaNtS\":0,\"PrOjEcT\":\"Code.csproj\",\"TeStS\":[\"Tests.csproj\"]}");
        Assert.Contains("--max-mutants and --timeout must be positive", error);
    }

    [Theory]
    [InlineData("{\"mutantMode\":\"unknown\"}", "Mutant mode must be source or switch")]
    [InlineData("{\"mutantMode\":\"switch\",\"compiledTests\":true}", "omit --compiled-tests")]
    [InlineData("{\"mutantMode\":\"switch\"}", "Provide --project")]
    public async Task MutationModeConfigurationIsValidated(string configuration, string expected)
    {
        Assert.Contains(expected, await ConfigurationError(configuration));
    }

    [Theory]
    [InlineData("{\"workers\":0,\"mutantMode\":\"switch\"}", "Workers must be 1 or 2")]
    [InlineData("{\"workers\":3,\"mutantMode\":\"switch\"}", "Workers must be 1 or 2")]
    [InlineData("{\"workers\":2}", "Multiple workers require --mutant-mode switch")]
    public async Task WorkerConfigurationIsValidated(string configuration, string expected)
    {
        Assert.Contains(expected, await ConfigurationError(configuration));
    }

    [Fact]
    public async Task ExplicitWorkerOptionOverridesConfiguration()
    {
        Assert.Contains("Provide --project", await ConfigurationError("{\"workers\":3,\"mutantMode\":\"switch\"}", "--workers", "1"));
    }

    private static async Task<string> ConfigurationError(string configuration, params string[] arguments)
    {
        using var workspace = new Workspace();
        workspace.Write("walker.json", configuration);
        var cli = typeof(Walker.Cli.TextReportWriter).Assembly.Location;
        var result = await new ProcessRunner().RunAsync(new("dotnet", [cli, "verify", "--format", "json", ..arguments], workspace.Root), default);
        Assert.True(result.ExitCode == 2, result.StandardOutput + result.StandardError);
        using var report = JsonDocument.Parse(result.StandardOutput);
        Assert.Equal("error", report.RootElement.GetProperty("status").GetString());
        Assert.Equal(0, report.RootElement.GetProperty("mutantsExecuted").GetInt32());
        return report.RootElement.GetProperty("error").GetString()!;
    }
}
