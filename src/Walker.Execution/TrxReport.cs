using System.Xml;
namespace Walker.Execution;

// Read counters and bounded failure identities without loading large TRX result bodies into memory.
internal sealed record TrxReport(int Executed, int Failed, string? Error,
    IReadOnlyList<TrxFailure> Failures, bool FailureSelectionComplete)
{
    public static TrxReport Read(string path)
    {
        var failures = new List<(string Name, string? Id)>();
        var resultCount = 0;
        (int Executed, int Failed)? counters = null;
        var settings = new XmlReaderSettings { IgnoreComments = true, IgnoreWhitespace = true, XmlResolver = null };
        using (var reader = XmlReader.Create(path, settings))
        {
            while (reader.Read())
            {
                if (reader.NodeType != XmlNodeType.Element) continue;
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
        if (wanted.Count > 0)
        {
            // Definitions may appear before or after Results. A second streaming pass maps only failed IDs.
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
        return new(counters.Value.Executed, counters.Value.Failed, null,
            failures.Select(f => new TrxFailure(f.Name, f.Id != null ? names.GetValueOrDefault(f.Id) : null)).ToArray(),
            resultCount == counters.Value.Failed && resultCount <= 10);
    }
}
internal sealed record TrxFailure(string Name, string? FullyQualifiedName);
