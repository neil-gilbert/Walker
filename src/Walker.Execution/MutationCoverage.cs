using System.Text.Json;
using System.Xml.Linq;
using Walker.Core;

namespace Walker.Execution;

internal sealed record CoveredCase(string Id, string Name, string Method, HashSet<int> Hits, string Outcome);
internal sealed record MutationCoverage(bool Valid, IReadOnlyList<CoveredCase> Cases)
{
    internal static MutationCoverage? Read(string path, IReadOnlyList<TrxCase>? results)
    {
        try
        {
            if (results == null || !File.Exists(path) || new FileInfo(path).Length > 16 * 1024 * 1024) return null;
            var coverage = JsonSerializer.Deserialize<MutationCoverage>(File.ReadAllText(path));
            if (coverage == null || !coverage.Valid || coverage.Cases == null || coverage.Cases.Count == 0 || coverage.Cases.Count > 100_000
                || coverage.Cases.Any(c => c == null || c.Hits == null || c.Hits.Any(id => id < 1)
                    || c.Name == null || c.Method == null || c.Outcome is not ("Passed" or "Failed"))
                || coverage.Cases.Select(c => c.Id).Distinct().Count() != coverage.Cases.Count
                || results.Select(c => c.Id).Distinct().Count() != results.Count) return null;
            var recorded = coverage.Cases.OrderBy(c => c.Id).Select(c => (Guid.Parse(c.Id), c.Name, c.Outcome));
            var actual = results.OrderBy(c => c.Id).Select(c => (Guid.Parse(c.Id), c.Name, c.Outcome));
            return recorded.SequenceEqual(actual) ? coverage : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException or FormatException or ArgumentException) { return null; }
    }

    // Deterministic first-fit packing; unknown/uncovered IDs are always singletons.
    internal static IReadOnlyList<Mutant[]> Pack(IReadOnlyList<Mutant> selected, IReadOnlyDictionary<string, int> ids,
        IReadOnlySet<int> batchable, IReadOnlyList<MutationCoverage>? coverage)
    {
        var batches = new List<List<Mutant>>();
        var used = new List<HashSet<string>>();
        foreach (var mutant in selected)
        {
            var tests = new HashSet<string>(StringComparer.Ordinal);
            if (coverage != null && ids.TryGetValue(mutant.Id, out var id) && batchable.Contains(id))
                for (var scope = 0; scope < coverage.Count; scope++)
                    foreach (var test in coverage[scope].Cases.Where(c => c.Hits.Contains(id))) tests.Add(scope + ":" + test.Id);
            var index = tests.Count == 0 ? -1 : used.FindIndex(set => set.Count > 0 && !set.Overlaps(tests));
            if (index < 0) { batches.Add([mutant]); used.Add(tests); }
            else { batches[index].Add(mutant); used[index].UnionWith(tests); }
        }
        return batches.Select(b => b.ToArray()).ToArray();
    }
}

internal sealed class CoverageCapture : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "walker-coverage-" + Guid.NewGuid().ToString("N"));
    internal string Settings { get; }
    internal string Report { get; }
    internal IReadOnlyDictionary<string, string?> Environment { get; }
    internal CoverageCapture(string environmentName, string active)
    {
        Directory.CreateDirectory(root);
        Settings = Path.Combine(root, "coverage.runsettings");
        Report = Path.Combine(root, "coverage.json");
        Environment = new Dictionary<string, string?> { [environmentName] = active,
            ["WALKER_COVERAGE_ENV"] = environmentName, ["WALKER_COVERAGE_REPORT"] = Report };
        var collectorPath = Path.Combine(Path.GetDirectoryName(typeof(CoverageCapture).Assembly.Location)!, "Walker.CoverageCollector.dll");
        var collectorName = "Walker.CoverageCollector.CoverageCollector, " + System.Reflection.AssemblyName.GetAssemblyName(collectorPath).FullName;
        new XDocument(new XElement("RunSettings",
            new XElement("RunConfiguration", new XElement("MaxCpuCount", 1)),
            new XElement("xUnit", new XElement("ParallelizeAssembly", false), new XElement("ParallelizeTestCollections", false)),
            new XElement("NUnit", new XElement("NumberOfTestWorkers", 0)),
            new XElement("MSTest", new XElement("Parallelize", new XElement("Workers", 1), new XElement("Scope", "ClassLevel"))),
            new XElement("InProcDataCollectionRunSettings", new XElement("InProcDataCollectors",
                new XElement("InProcDataCollector", new XAttribute("friendlyName", "WalkerCoverage"),
                    new XAttribute("uri", "InProcDataCollector://Walker/Coverage/1.0"),
                    new XAttribute("assemblyQualifiedName", collectorName),
                    new XAttribute("codebase", collectorPath)))))).Save(Settings);
    }
    public void Dispose()
    {
        try { Directory.Delete(root, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
