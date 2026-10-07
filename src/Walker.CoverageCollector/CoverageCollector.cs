using System.Text.Json;
using Microsoft.VisualStudio.TestPlatform.ObjectModel.DataCollection;
using Microsoft.VisualStudio.TestPlatform.ObjectModel.DataCollector.InProcDataCollector;
using Microsoft.VisualStudio.TestPlatform.ObjectModel.InProcDataCollector;

namespace Walker.CoverageCollector;

// In-process events are adapter-dependent. Overlap, missing events and duplicate
// case IDs invalidate the entire report; callers then use ordinary serial runs.
public sealed class CoverageCollector : InProcDataCollection
{
    private readonly object gate = new();
    private readonly Dictionary<string, Case> cases = new(StringComparer.Ordinal);
    private Case? current;
    private bool valid = true;
    private string? environmentName;
    private string? report;
    public sealed record Case(string Id, string Name, string Method)
    {
        public HashSet<int> Hits { get; } = [];
        public string? Outcome { get; set; }
    }
    public void Initialize(IDataCollectionSink sink) { }
    public void TestSessionStart(TestSessionStartArgs args)
    {
        environmentName = Environment.GetEnvironmentVariable("WALKER_COVERAGE_ENV");
        report = Environment.GetEnvironmentVariable("WALKER_COVERAGE_REPORT");
        if (environmentName == null || report == null) { valid = false; return; }
        AppDomain.CurrentDomain.SetData(environmentName + "_coverage", (Action<int>)Hit);
    }
    private void Hit(int id)
    {
        lock (gate)
        {
            if (current == null) valid = false;
            else current.Hits.Add(id);
        }
    }
    public void TestCaseStart(TestCaseStartArgs args)
    {
        lock (gate)
        {
            var test = args.TestCase;
            if (current != null || test == null) { valid = false; return; }
            current = new(test.Id.ToString("D"), test.DisplayName, test.FullyQualifiedName);
            if (!cases.TryAdd(current.Id, current)) valid = false;
        }
    }
    public void TestCaseEnd(TestCaseEndArgs args)
    {
        lock (gate)
        {
            if (current == null || current.Id != args.DataCollectionContext.TestCase?.Id.ToString("D")) valid = false;
            else current.Outcome = args.TestOutcome.ToString();
            current = null;
        }
    }
    public void TestSessionEnd(TestSessionEndArgs args)
    {
        lock (gate)
        {
            if (report == null) return;
            valid &= current == null && cases.Count > 0 && cases.Values.All(c => c.Outcome is "Passed" or "Failed");
            File.WriteAllText(report, JsonSerializer.Serialize(new { Valid = valid, Cases = cases.Values }));
            if (environmentName != null) AppDomain.CurrentDomain.SetData(environmentName + "_coverage", null);
        }
    }
}
