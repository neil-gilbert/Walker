using BenchmarkDotNet.Attributes;
using Walker.Core;

namespace Walker.Benchmarks;

[MemoryDiagnoser]
public class SelectionBenchmarks
{
    private Mutant[] mutants = null!;

    [Params(100, 10000)]
    public int CandidateCount { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        mutants = Enumerable.Range(0, CandidateCount).Select(i => new Mutant(
            i.ToString("D8"), $"src/File{i % 20}.cs", i + 1, "Fixture.Method",
            (MutationOperator)(i % 6), "a >= b", "a > b", i, 6, "fixture")).ToArray();
    }

    [Benchmark]
    public Mutant[] Select() => VerificationEngine.Select(mutants, 20);
}
