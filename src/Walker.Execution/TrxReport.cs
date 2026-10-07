using System.Xml;
namespace Walker.Execution;

// Read counters and bounded failure identities without loading large TRX result bodies into memory.
internal sealed record TrxReport(int Executed, int Failed, string? Error,
    IReadOnlyList<TrxFailure> Failures, bool FailureSelectionComplete, string? Identity = null)
{
    public static TrxReport Read(string path, bool captureIdentity = false)
    {
        var identities = new List<(string? Id, string? Name, string? Outcome)>();
        var identityComplete = captureIdentity;
        var failures = new List<(string Name, string? Id)>();
        var resultCount = 0;
        (int Executed, int Failed)? counters = null;
        var settings = new XmlReaderSettings { IgnoreComments = true, IgnoreWhitespace = true, XmlResolver = null };
        using (var reader = XmlReader.Create(path, settings))
        {
            while (reader.Read())
            {
                if (reader.NodeType != XmlNodeType.Element) continue;
                if (captureIdentity && reader.LocalName == "UnitTestResult")
                {
                    if (identities.Count < 100_000) identities.Add((reader.GetAttribute("testId"), reader.GetAttribute("testName"), reader.GetAttribute("outcome")));
                    else identityComplete = false;
                }
                if (reader.LocalName == "UnitTestResult" && reader.GetAttribute("outcome") == "Failed")
                {
                    resultCount++;
                    if (failures.Count < 10) failures.Add((reader.GetAttribute("testName") ?? "Unnamed failed test", reader.GetAttribute("testId")));
                }
                if (reader.LocalName == "ResultSummary" && reader.GetAttribute("outcome") is "Aborted" or "Error")
                    return new(0, 0, "Test execution aborted or reported infrastructure errors.", [], false);
                if (reader.LocalName != "Counters" || counters != null) continue;
                int Count(string name) => int.TryParse(reader.GetAttribute(name), out var value) ? value : 0;
                var executed = Count("executed"); var failed = Count("failed");
                if (executed != Count("passed") + failed || Count("error") > 0 || Count("timeout") > 0 || Count("aborted") > 0)
                    return new(0, 0, "Test execution aborted or reported infrastructure errors.", [], false);
                counters = (executed, failed);
            }
        }
        if (counters == null) return new(0, 0, "Malformed test report.", [], false);
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        var wanted = failures.Where(f => f.Id != null).Select(f => f.Id!).ToHashSet(StringComparer.Ordinal);
        if (captureIdentity) wanted.UnionWith(identities.Where(r => r.Id != null).Select(r => r.Id!));
        if (wanted.Count > 0)
        {
            // Definitions may appear before or after Results. Ordinary execution maps only failures;
            // the opt-in compatibility probe also needs every executed method and theory case.
            using var reader = XmlReader.Create(path, settings);
            while (reader.Read())
            {
                if (reader.NodeType != XmlNodeType.Element || reader.LocalName != "UnitTest"
                    || reader.GetAttribute("id") is not { } id || !wanted.Contains(id)) continue;
                using var definition = reader.ReadSubtree();
                while (definition.Read())
                {
                    if (definition.NodeType != XmlNodeType.Element || definition.LocalName != "TestMethod") continue;
                    if (definition.GetAttribute("className") is { Length: > 0 } type && definition.GetAttribute("name") is { Length: > 0 } method)
                        names[id] = type + "." + method;
                }
            }
        }
        var identity = identityComplete && identities.Count > 0
            && identities.Count(r => r.Outcome is "Passed" or "Failed") == counters.Value.Executed
            && identities.All(r => r.Id != null && names.ContainsKey(r.Id) && r.Name != null && r.Outcome is "Passed" or "Failed" or "NotExecuted")
            ? Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(
                System.Text.Json.JsonSerializer.Serialize(identities.Select(r => new[] { names[r.Id!], r.Name!, r.Outcome! }).OrderBy(r => System.Text.Json.JsonSerializer.Serialize(r), StringComparer.Ordinal))))) : null;
        return new(counters.Value.Executed, counters.Value.Failed, null,
            failures.Select(f => new TrxFailure(f.Name, f.Id != null ? names.GetValueOrDefault(f.Id) : null)).ToArray(),
            resultCount == counters.Value.Failed && resultCount <= 10, identity);
    }
}
internal sealed record TrxFailure(string Name, string? FullyQualifiedName);
