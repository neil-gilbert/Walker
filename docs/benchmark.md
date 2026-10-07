# Benchmarking Walker

For a pinned GitHub project, use the [FluentValidation benchmark](real-world-benchmark.md).
It measures full-suite and focused verification against a real production change,
with a default 120-second budget and separate setup timings.

For repeated comparisons with Stryker.NET, use
[the Stryker comparison](stryker-comparison.md) and `scripts/benchmark_stryker.py`.
It alternates execution order, gives each tool its own fresh warmed repository,
and verifies outcomes for mutations shared by both tools. The older harness below
runs Stryker only once, even when Walker repetitions are requested.

## Pull request checks

GitHub Actions builds and tests the Release solution on every PR and push to
`main`. The separate **Performance comparison** check benchmarks the PR's exact
base and head commits sequentially on the same Ubuntu runner using the .NET 10 SDK.
`global.json` selects the latest installed stable .NET 10 SDK feature band; both
benchmark checkouts use that same SDK policy.
The runner also installs .NET 8 for older baselines. Each revision retains its
own target framework, and CLI output paths are resolved through MSBuild. A
comparison across this migration therefore includes the runtime change.
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

For Release measurements, pass `--cli src/Walker.Cli/bin/Release/net10.0/Walker.Cli.dll`
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

## Preferred-test planner fixture

Use `--fixture preferred-tests --skip-stryker` to measure the fixture in
`examples/PreferredTests`. It changes seven expressions in four members and
interleaves boundary mutations with repeated Boolean mutations in member A.
Each member's test deliberately takes 500ms; unrelated work takes 2s, with test
parallelism disabled. This exposes the cost of growing preferred groups and
repeated validation. These fixed delays are synthetic and do not represent a
real project's speedup. All seven mutations must be killed; the harness checks
those counts, identical selected IDs/outcomes and exact restored source bytes.

```bash
python3 scripts/benchmark.py --fixture preferred-tests --skip-stryker \
  --cli src/Walker.Cli/bin/Release/net10.0/Walker.Cli.dll \
  --compare-cli artifacts/optimisation-2/before-cli/Walker.Cli.dll \
  --repetitions 3 --output artifacts/optimisation-2/preferred-tests
```

The default remains Payments. Use the .NET 10 SDK for either fixture.

## Candidate-only experimental options

Both Walker benchmark harnesses accept repeatable `--walker-arg` values. Use `--walker-arg=--compiled-tests` to enable assembly probes/retries only for the candidate; the preserved `--compare-cli` remains unchanged. The effective arguments are recorded in summaries. For example, add this argument to the paired Payments or FluentValidation commands in the [mutation optimisation plan](mutation-optimisation-plan.md).

The 2026-10-07 item-3 comparisons include all setup within completed-report wall time. Three pairs per workload retained identical IDs/outcomes and restored source, but the opt-in medians regressed: Payments **6.888s → 7.753s**, FluentValidation full **45.949s → 54.061s**, focused **24.133s → 26.091s**. Assembly execution remains off by default. Local raw evidence is under ignored `artifacts/optimisation-3/payments/` and `artifacts/optimisation-3/fluentvalidation/`; the plan documents the correctness checks and fallbacks.

## Compile-once switching fixture

For a worker comparison, use the same complete CLI directory for both labels and
pass switch mode to both. `--compare-walker-arg` applies only to the control;
`--walker-arg` applies only to the candidate:

```bash
python3 scripts/benchmark.py --skip-stryker --fixture prepared-mutants \
  --repetitions 3 --timeout 120 --measure-memory \
  --cli src/Walker.Cli/bin/Release/net10.0/Walker.Cli.dll \
  --compare-cli src/Walker.Cli/bin/Release/net10.0/Walker.Cli.dll \
  --compare-walker-arg=--mutant-mode --compare-walker-arg=switch \
  --compare-walker-arg=--workers --compare-walker-arg=1 \
  --walker-arg=--mutant-mode --walker-arg=switch \
  --walker-arg=--workers --walker-arg=2 \
  --output artifacts/optimisation-5/prepared-mutants
```

Both harnesses support these arguments and `--measure-memory`. The optional
sampler records `memory.peakTreeRssBytes` and sample counts using the summed RSS
of the CLI and its current descendants every 100ms. This is a sampled process-tree
measurement, not an OS high-water mark: short peaks and reparented/shared compiler
daemons can be missed, and shared pages can be counted more than once. Use the
same sampling policy for both labels; report failures as unavailable memory data.
Record preparation time and `workersUsed` alongside total wall time. Payments
and FluentValidation currently lack eligible switch batches and retain one
worker even when two are requested. Parallel test hosts can contend with the
test framework's own parallelism or shared services; keep two workers opt-in.

`examples/PreparedMutants` changes twenty independent numeric boundaries. Equality
tests kill all twenty mutants, and a static counter asserts a fresh test process.
There are no artificial delays. Use the same complete CLI snapshot for control
and candidate to isolate source versus switch mode, including all preparation:

```bash
python3 scripts/benchmark.py --fixture prepared-mutants --skip-stryker \
  --cli artifacts/optimisation-1/final-cli/Walker.Cli.dll \
  --compare-cli artifacts/optimisation-1/final-cli/Walker.Cli.dll \
  --walker-arg=--mutant-mode --walker-arg=switch \
  --repetitions 3 --timeout 120 --output artifacts/optimisation-1/prepared-mutants
```

Run the Payments and both FluentValidation profiles as cross-checks. Summaries
include `preparation` with elapsed time, supported count and fallback count;
check these counts before attributing a result to switching. For a pinned target
using an older SDK, `benchmark_real_world.py --walker-dotnet /path/to/dotnet`
can host Walker on .NET 10 while target builds/tests retain the SDK on `PATH`.
On macOS, add `--walker-apphost` to avoid resolving child `dotnet` commands
from the host executable's directory; this needs each snapshot's apphost binary.
Use identical installations and target policies for both labels. A faster
synthetic fixture alone does not justify enabling switching by default.
