# Benchmarking Walker

Run the included Payments example in an isolated temporary Git repository:

```bash
dotnet build Walker.sln
# Install a chosen Stryker.NET version from a trusted feed if it is not already available.
python scripts/benchmark.py --stryker /path/to/dotnet-stryker --repetitions 3 --sample-text
```

Use `--skip-stryker` for Walker-only measurements. To compare an earlier implementation, preserve its **entire** CLI output directory before rebuilding, then pass `--compare-cli /path/to/snapshot/Walker.Cli.dll`. The script alternates execution order, reuses the same fixture, and rejects comparisons whose mutant IDs or outcomes differ. Run benchmarks without concurrent builds or tests competing for resources.

The fixture changes four production expressions: a purchase boundary, boolean logic, a null check, and fee arithmetic. Tests intentionally miss equal-balance behaviour and assert only that a fee is positive. Both are actionable survivors. Boolean and null mutations should be killed.

Reports go to `artifacts/benchmark` (override with `--output`):

- `walker-N.json`: complete schema-versioned verification results for each run.
- `before-N.json`: equivalent results from an optional pre-change binary.
- `summary.json`: wall times, median timing, phase costs, counts, and survivors.
- `sample.txt`: actual themed text output when requested.
- `stryker.json` and `stryker.log`: Stryker's report and process output when requested.

Timing starts at process launch and ends when the complete actionable report is available. Package restores and the initial fixture build/test are warmed before measurement; Walker's own baseline is still included. Compiler-server warmth can affect the first run. This measures completed-report latency, not the instant the first survivor is internally detected. Stryker runs the whole tiny production fixture with one test session; Walker targets the diff with at most 20 selected mutants. Their mutation sets and execution strategies differ.

The script never benchmarks against or mutates the developer's working tree. It makes commits only in its disposable fixture repository. It verifies exact source-byte restoration after every Walker execution and uses graceful cancellation followed by a bounded stop for benchmark timeouts.

A small fixture cannot establish performance across repositories. Record SDK, tool versions, hardware/environment, warm/cold state, run count, and outcome equivalence. An incomplete or errored run is not evidence of a speed improvement. See [the measured review](performance.md) for this workspace's results.
