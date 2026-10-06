using BenchmarkDotNet.Attributes;
using Walker.Core;
using Walker.Roslyn;

namespace Walker.Benchmarks;

[MemoryDiagnoser]
public class DiscoveryBenchmarks
{
    private string root = null!;
    private SourceChange[] changes = null!;
    private readonly RoslynMutationDiscoverer discoverer = new();

    [Params(100, 1000)]
    public int MethodCount { get; set; }

    [Params(false, true)]
    public bool SparseDiff { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        root = Path.Combine(Path.GetTempPath(), "walker-bdn-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var lines = new List<string> { "class Fixture {" };
        for (var i = 0; i < MethodCount; i++)
            lines.Add($"bool M{i}(int value) => value >= {i};");
        lines.Add("int Fee(int amount) => amount * 2;");
        lines.Add("}");
        File.WriteAllLines(Path.Combine(root, "Fixture.cs"), lines);
        changes = [new("Fixture.cs", SparseDiff
            ? [new(2, 2)] : [new(2, MethodCount + 2)], false)];
    }

    [Benchmark]
    public Task<DiscoveryResult> Discover() => discoverer.DiscoverAsync(root, changes, default);

    [GlobalCleanup]
    public void Cleanup() => Directory.Delete(root, recursive: true);
}
