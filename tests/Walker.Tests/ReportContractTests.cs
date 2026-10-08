using System.Text.Json;
using System.Text.Json.Nodes;
using Json.Schema;
using Walker.Core;
using Xunit;

namespace Walker.Tests;

public sealed class ReportContractTests
{
    [Theory]
    [InlineData("passed", MutationOutcome.Killed)]
    [InlineData("failed", MutationOutcome.Survived)]
    [InlineData("error", MutationOutcome.TestError)]
    [InlineData("incomplete", MutationOutcome.Skipped)]
    public void VersionOneConsumersKeepExistingFieldsAndIgnoreNewOnes(string status, MutationOutcome outcome)
    {
        var result = new VerificationResult(status, "HEAD", 1, 1, 1, [new(DiscoveryTests.Dummy("id"), outcome)], 1, new())
        { Diagnostics = [VerificationDiagnostic.Create("a_future_code", "a_future_phase", "Human explanation", "a_future_action")] };
        var json = JsonSerializer.Serialize(result, VerificationReportJson.Options);
        ReportContract.AssertValid(json);
        using var report = JsonDocument.Parse(json);
        Assert.Equal(result.ExitCode, report.RootElement.GetProperty("exitCode").GetInt32());
        Assert.Equal(outcome.ToString(), report.RootElement.GetProperty("results")[0].GetProperty("outcome").GetString());
        // A consumer routes known codes and falls back to status; human messages are never interpreted.
        Assert.Equal(status, Route(report.RootElement));
        var legacy = JsonNode.Parse(json)!.AsObject();
        foreach (var field in new[] { "diagnostics", "selection", "isolation" }) legacy.Remove(field);
        ReportContract.AssertValid(legacy.ToJsonString());
    }

    [Fact]
    public void SchemaRejectsContradictoryExitCodeAndMalformedDiagnostics()
    {
        var node = JsonSerializer.SerializeToNode(new VerificationResult("passed", "HEAD", 0, 0, 0, [], 0, new()), VerificationReportJson.Options)!;
        node["exitCode"] = 2;
        Assert.False(ReportContract.Schema.Evaluate(node).IsValid);
        node["exitCode"] = 0;
        node["diagnostics"] = JsonNode.Parse("[{\"code\":42}]");
        Assert.False(ReportContract.Schema.Evaluate(node).IsValid);
    }

    private static string Route(JsonElement report) => report.GetProperty("diagnostics").EnumerateArray().Any(d => d.GetProperty("code").GetString() == "baseline_budget_exhausted")
        ? "review budget" : report.GetProperty("status").GetString()!;
}

internal static class ReportContract
{
    internal static readonly JsonSchema Schema = JsonSchema.FromText(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "verification-report.schema.json")));
    internal static void AssertValid(string json)
    {
        var validation = Schema.Evaluate(JsonNode.Parse(json));
        Assert.True(validation.IsValid, "Verification report failed JSON Schema validation: " + json);
    }
}
