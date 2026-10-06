using System.Text.Json;
using System.Text.Json.Serialization;
using Walker.Cli;
using Walker.Core;
using Xunit;
namespace Walker.Tests;
public sealed class TextReportTests
{
    [Fact]
    public void SuccessfulRunUsesSafeLanguage()
    {
        var result = Result("passed", MutationOutcome.Killed);
        var text = Render(result);
        Assert.StartsWith("WALKER", text);
        Assert.Contains("1 KILLED", text);
        Assert.Contains("0 WALKERS", text);
        Assert.Contains("SAFE — nothing is still walking.", text);
        Assert.Contains("Completed in 4.8s", text);
        Assert.DoesNotContain("IT'S STILL WALKING.", text);
    }
    [Fact]
    public void SurvivorHasLocationMutationAndInvestigationGuidance()
    {
        var text = Render(Result("failed", MutationOutcome.Survived));
        Assert.Contains("1 WALKER", text);
        Assert.DoesNotContain("1 WALKERS", text);
        Assert.Contains("IT'S STILL WALKING.", text);
        Assert.Contains("Code.cs:1", text);
        Assert.Contains("Original:\n    a == b", text);
        Assert.Contains("Walker:\n    a != b", text);
        Assert.Contains("Investigate the missing behavioural constraint.", text);
        Assert.Contains("does not necessarily mean production code should change", text);
        Assert.Contains("If it still walks, your tests aren't done.", text);
        Assert.DoesNotContain("SAFE", text);
    }
    [Theory]
    [InlineData("incomplete", MutationOutcome.TimedOut, "VERIFICATION INCOMPLETE")]
    [InlineData("incomplete", MutationOutcome.Skipped, "VERIFICATION INCOMPLETE")]
    [InlineData("error", MutationOutcome.TestError, "VERIFICATION ERROR")]
    [InlineData("error", MutationOutcome.CompileError, "VERIFICATION ERROR")]
    public void ErrorsAndIncompleteRunsAreNeverSafe(string status, MutationOutcome outcome, string heading)
    {
        var text = Render(Result(status, outcome));
        Assert.Contains(heading, text);
        Assert.DoesNotContain("SAFE", text);
        Assert.DoesNotContain("IT'S STILL WALKING.", text);
    }
    [Fact]
    public void JsonKeepsTechnicalNamesAndOutcomes()
    {
        var result = Result("failed", MutationOutcome.Survived);
        var json = JsonSerializer.Serialize(result, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase, Converters = { new JsonStringEnumConverter() }
        });
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        Assert.Equal(1, root.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(1, root.GetProperty("survived").GetInt32());
        Assert.Equal("Survived", root.GetProperty("results")[0].GetProperty("outcome").GetString());
        Assert.False(root.TryGetProperty("walkers", out _));
        Assert.False(root.TryGetProperty("horde", out _));
        Assert.DoesNotContain("IT'S STILL WALKING", json);
    }
    [Fact]
    public void TextNamesChangedFilesWithNoCandidatesEvenInPassingRun()
    {
        var result = Result("passed", MutationOutcome.Killed) with
        {
            Files = [new("Empty.cs", 5, 0, 0), new("Covered.cs", 1, 1, 1)]
        };
        var text = Render(result);
        Assert.Contains("No mutation candidates: Empty.cs (5 changed lines; no mutation evidence for this file)", text);
        Assert.DoesNotContain("No mutation candidates: Covered.cs", text);
    }
    private static VerificationResult Result(string status, MutationOutcome outcome) =>
        new(status, "HEAD~1", 1, 1, 1, [new(DiscoveryTests.Dummy("abc123"), outcome)], 4800, new());
    private static string Render(VerificationResult result)
    {
        using var writer = new StringWriter();
        TextReportWriter.Write(result, writer);
        return writer.ToString();
    }
}
