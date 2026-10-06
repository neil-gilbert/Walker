# Benchmarking Walker

## Pull request checks

GitHub Actions builds and tests the Release solution on every PR and push to
`main`. The separate **Performance comparison** check benchmarks the PR's exact
base and head commits sequentially on the same Ubuntu runner using .NET 8.
`global.json` selects the latest installed stable .NET 8 SDK feature band; both
benchmark checkouts use that same SDK policy.
It uses the PR's benchmark source for both revisions, so adding a benchmark also
works when the base commit predates the benchmark project.

Open the performance check's Actions run summary for a table of mean execution
times, percentage changes and allocated bytes per operation. Negative time changes
mean faster execution. The `benchmark-comparison-*` artifact contains raw
BenchmarkDotNet JSON/Markdown reports, commit SHAs, SDK details and end-to-end
verification results; artifacts are retained for 30 days. The workflow can also
be run manually with a baseline branch or commit via `base_ref`.

BenchmarkDotNet covers mutation selection with 100/10,000 candidates and Roslyn
discovery with 100/1,000 methods across sparse and full-file diffs. Setup is outside
the measured operations; discovery includes source reads and parsing. Memory
diagnostics report allocations. The CI job uses BenchmarkDotNet's `short` job to
keep PR cost bounded. For longer local measurements:

```bash
dotnet run -c Release --project benchmarks/Walker.Benchmarks -- --filter '*' --exporters json
```

The check also compares the Release CLI revisions using three paired runs of the
Payments fixture below, alternating execution order and rejecting differences in
mutant IDs or outcomes. It measures completed verification latency, including
build/test execution. A deliberate mutation-behaviour change will need the fixture
or comparison policy reviewed before timing can be treated as comparable.

Timing regressions are informational and do not fail the PR based on a percentage
threshold. Invalid measurements, failed builds and non-equivalent end-to-end runs
do fail the check. Hosted runners vary, and base always runs first for the
microbenchmarks; rerun small changes and use longer runs on stable hardware before
claiming an improvement. PR jobs use read-only permissions and publish through
Actions summaries/artifacts, including fork PRs.

## End-to-end fixture

Run the included Payments example in an isolated temporary Git repository:

```bash
dotnet build Walker.sln
# Install a chosen Stryker.NET version from a trusted feed if it is not already available.
python scripts/benchmark.py --stryker /path/to/dotnet-stryker --repetitions 3 --sample-text
```

Use `--skip-stryker` for Walker-only measurements. To compare an earlier implementation, preserve its **entire** CLI output directory before rebuilding, then pass `--compare-cli /path/to/snapshot/Walker.Cli.dll`. The script alternates execution order, reuses the same fixture, and rejects comparisons whose mutant IDs or outcomes differ. Run benchmarks without concurrent builds or tests competing for resources.

For Release measurements, pass `--cli src/Walker.Cli/bin/Release/net8.0/Walker.Cli.dll`
after building with `dotnet build Walker.sln -c Release`.

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
