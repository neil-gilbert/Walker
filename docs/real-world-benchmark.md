# Real-world benchmark: FluentValidation

Measured on 2026-10-06 on a MacBook Air (Apple M1, 8 GB RAM), macOS arm64,
.NET SDK 9.0.303. The benchmark budget is **120 seconds**, following the initial
60-second experiment. Setup and completed-report latency are measured separately.

## Reproduce

```bash
dotnet build src/Walker.Cli -c Release
python3 scripts/benchmark_real_world.py --timeout 120 --repetitions 3
```

Walker now builds and runs on .NET 10; the pinned target checkout requires the
.NET 9 SDK and its .NET 8/9 test runtimes. Install these alongside .NET 10 in the
same dotnet installation for the harness, which launches both Walker and target
commands with `dotnet`. Build Walker first, or pass an already-built verifier with
`--cli /absolute/path/to/Walker.Cli.dll`. The measurements below predate the .NET 10
migration and describe their original environment.

The script fetches a disposable checkout of
[FluentValidation](https://github.com/FluentValidation/FluentValidation), pinned to
[`f0d51d2109b04aa35f0119a715ef6ab413765263`](https://github.com/FluentValidation/FluentValidation/commit/f0d51d2109b04aa35f0119a715ef6ab413765263).
The base is its parent, `52e214762a7213bcbaed0f61f6df91d2621f89cc`. This is the actual
precision/scale validator refactor, including its accompanying tests; the harness
does not invent production changes or create commits.

The checkout contains **214 tracked C# files**, including **137 core-library files
and 13,068 core-library lines**. The test project references the production library
through the dependency-injection project. Source generators are present. Tests
target both .NET 8 and .NET 9; each target passes 864 tests and skips one benchmark.

The original measurement environment had only the .NET 9 runtime. Those measurements used
`DOTNET_ROLL_FORWARD=Major` for Walker and the test hosts, so the .NET 8 target also
ran on the .NET 9 runtime. To reproduce those historical measurements with the
original .NET 8 Walker binary (this does not replace .NET 10 for the current CLI):

```bash
DOTNET_ROLL_FORWARD=Major python3 scripts/benchmark_real_world.py --timeout 120
```

Full-suite and focused profiles run sequentially in alternating order. Each Walker
invocation uses a fresh detached Git worktree with independent build outputs; the
source checkout is checked for changes and never receives mutations. The focused
filter is `FullyQualifiedName~FluentValidation.Tests.ScalePrecisionValidatorTests`.
Restore and an ordinary full-suite test run warm the checkout before measurement;
each measured Walker run still includes its own discovery, baseline and mutation
execution. Timing ends when the JSON report is written and the process exits.
The harness checks hashes of every tracked C# file after every measured run.

Raw reports, setup logs, phase costs, restoration checks and outcome consistency
are saved under `artifacts/real-world-benchmark/`. `--output`, `--profiles`,
`--max-mutants`, `--timeout` and `--repetitions` customize the experiment. `--checkout`
can reuse a clean source checkout at the pinned commit; the script never resets it.
`--compare-cli` accepts a preserved verifier DLL with its entire output directory;
versions alternate execution order and must produce identical mutant IDs/outcomes
in completed runs. Setup is recorded separately for every worktree. Failed captures
retain their workspace for inspection, with reports outside it.
Incomplete or errored reports remain visible in the summary and are never treated
as completed-under-budget results. The script's exit code indicates capture of
benchmark evidence; inspect each run's `status` and `completeUnderBudget`.

For a comparison (build the updated verifier after preserving the earlier output):

```bash
cp -R src/Walker.Cli/bin/Release/net10.0 artifacts/before-cli
dotnet build src/Walker.Cli -c Release
python3 scripts/benchmark_real_world.py --profiles full --repetitions 3 \
  --compare-cli artifacts/before-cli/Walker.Cli.dll --timeout 120
```

## Completed measurements

| Run | Full suite, 120s budget | Focused suite, 60s budget |
| --- | ---: | ---: |
| 1 | 66.984s | 47.507s |
| 2 | 65.941s | 36.725s |
| 3 | 69.117s | 33.866s |
| Median | **66.984s** | **36.725s** |

All six runs completed all **9 discovered/selected mutants**, with identical IDs
and outcomes: **8 killed, 1 survived**. There were no compilation errors, test
errors, hangs, timeouts or skips. All tracked C# bytes were restored. Exit 1 means
completed verification found a survivor; it is not an infrastructure failure.

The full-suite and focused samples above were collected in separate groups, with
the focused group first, rather than the script's default alternating profile order.
They demonstrate feasibility and the cost of scope on this machine, not a paired
implementation speedup. No Walker execution code changed for this experiment.

For the full-suite group, fetching and checking out a fresh repository took
0.941s; its initial build/tests took 7.783s. Setup plus its first Walker run was
**75.708s**. Packages were already cached locally, so this is not a cold NuGet
restore measurement. Subsequent Walker runs include their own baseline.

Evidence from this session is in `artifacts/real-world/full/summary.json`,
`artifacts/real-world/focused/summary.json`, and their per-run JSON reports.

## Initial 60-second result

The full suite exhausted the budget at **60.289s**: 9 discovered/selected mutants,
7 killed, 1 timed out and 1 skipped. Discovery cost about 1.3s and the baseline
8.9s. Source restoration passed. This is incomplete evidence, not a passing run.

The focused profile completed three times: **47.507s, 36.725s, 33.866s**; median
**36.725s**. All three runs executed the same 9 mutants with identical outcomes:
8 killed and 1 survived, with no errors, hangs, timeouts or skips. Every tracked
C# file was restored exactly. Both target frameworks remained in scope.

## Learnings

- Test scope strongly affects completed-report latency. The focused profile still
  found the meaningful survivor. That observation applies to this change; it does
  not establish that filters preserve evidence for arbitrary changes.
- The full suite meets the revised 120-second target on this machine; the focused
  suite meets the original 60-second target. A 20-mutant ceiling selected only 9
  mutants for this diff, so these timings do not establish a 20-mutant guarantee.
- Discovery is cheap at this size: around a second, including Git/MSBuild source
  evaluation and semantic analysis. No arithmetic or boolean expressions were
  reported unresolved for this diff.
- Repeated graph builds and test-host execution dominate. The transitive project
  reference uses Walker's conservative separate-production-build fallback. In the
  focused runs, median summed explicit mutation builds cost 9.6s and mutation
  test invocations cost 20.6s (including their graph builds). Optimizing Roslyn
  alone would have a small effect on this workload.
- The full-suite runs spent a median 49.6s in mutation test invocations and 8.9s
  in explicit mutation builds. Test selection, repeated graph compilation and
  test-host startup are the next useful areas to investigate for a consistent
  sub-60-second full-suite result.
- A 60-second cancellation budget is not a strict 60-second wall-time guarantee:
  process startup, shutdown, source restoration and report writing add overhead.
  Leave headroom when an external system imposes a hard deadline.
- Results differ noticeably between runs on this machine. Use repeated local
  measurements and report setup/warmth; do not generalize this one diff into a
  repository-wide performance or mutation-coverage claim.

## Survivor investigation

Mutant `7b733c075632d22b7971` changes `scale > 0` to `scale >= 0` in
`PrecisionScaleValidator.Info.Get` (line 105). The existing trailing-zero tests use
nonzero values. For zero with `ignoreTrailingZeros: true`, the mantissa remains
zero, so the replacement permits the trimming loop to continue indefinitely.
The original guard terminates when scale reaches zero. This is a missing zero
boundary test, rather than a reason to change the production guard.

To check that interpretation, a temporary `[Fact]` validated `0m`, `0.00m` and
signed decimal zero with `PrecisionScale(4, 2, true)`. The ordinary focused suite
passed **11 tests per target**, including the new fact (the original focused suite
executes 10 tests per target). Walker then completed in **42.441s**: **8 killed,
1 hung, 0 survived**, exit 0. The former survivor exceeded the per-mutant hang
limit, exactly as predicted for zero. This confirms a distinguishable behavioural
change rather than an equivalent mutant. Hung detection is timeout-based evidence,
not a failing assertion.

The temporary test and every production source file were restored to their
original bytes after the experiment. Its patch and evidence are retained in
`artifacts/real-world/zero-probe/zero-regression.patch`, `baseline.log`, `walker.json`
and `summary.json`. No changes were submitted upstream.

The refactor also left two methods with `[InlineData]` but no `[Theory]`:
`Scale_precision_should_be_valid_nullable` and
`Scale_precision_should_be_valid_when_they_are_equal`. xUnit does not discover
those methods. Passing test totals alone cannot show that every intended case ran.
Their absence was also checked in the ordinary run's TRX results; the new zero
test appeared once per target.

## Optimization follow-up — 2026-10-06

The updated executor completes the **full suite in 47.469s median**,
compared with **59.648s** for the preserved pre-change CLI measured
alongside it: **20.4% less time**. Every updated run completed under
60 seconds, including preferred-scope validation on unmutated source.

| Pair | Preserved CLI | Updated CLI |
| --- | ---: | ---: |
| 1 | 59.063s | 46.713s |
| 2 | 59.648s | 47.469s |
| 3 | 60.349s | 47.483s |
| Median | **59.648s** | **47.469s** |

All six reports have identical mutant IDs and outcomes: 9 executed, 8 killed,
1 survived; no compile errors, test errors, hangs, timeouts or skips. Every tracked
C# byte was restored in each fresh worktree, and the source checkout remained
unchanged. The original missing-zero boundary is still reported as a survivor.
This is a matched implementation comparison, with revision order alternated and
independent warmed build outputs. It does not use a narrower user filter.

Changes:

- Observe each framework's inner baseline build. Its resolved reference metadata
  can prove that the test graph builds the requested single-target production
  project, including transitive references. The full-suite measurement eliminates
  separate mutation production builds: median summed explicit build cost fell
  from 7.159s to zero. Those necessary compilations still occur in test graph builds.
- Run verified test frameworks in order, stopping after actual failed-test counters
  establish a kill. A survivor passes the full requested scope on every framework.
  Unknown/truncated metadata or baseline frameworks without executed tests retain
  the combined invocation. Production multi-targeting and customized references
  retain the conservative separate build.
- Prioritize at most 20 methods that killed earlier mutants, within the original
  filter. Each preferred group first passes on restored, unmutated source; failed,
  empty or hung groups are rejected. This prevents test-order dependencies from
  creating false kills. Baseline test commands under three seconds avoid the extra
  startup. Preferred groups that pass on a mutant always fall back to the complete
  original scope using the assemblies just built for that mutant. History resets
  between verifications. Kill confirmation reruns the actual failing framework.

The initial framework/build change alone measured 59.941s versus 59.852s median,
within noise. .NET 9 had already been running the original frameworks concurrently,
so this alone did not reduce elapsed test work. Prioritized failing methods provide
most of the useful runtime reduction. Final median summed mutation test time,
including preferred-group baseline validation, fell from 44.755s to 38.408s. The
additional inner baseline observation raises baseline median from 6.428s to 7.760s;
this is included in the final wall time.

Validation: Release build with no warnings/errors; **132 tests passed**, covering
first/second-framework kills, all-framework survivors, metadata fallbacks, empty
scopes, compile-error classification, filter preservation, cache reset, restored
source, preferred-subset baseline rejection and confirmation. Source restoration
and outcome equivalence were checked in every paired external run.

Raw evidence: `artifacts/speedup/validated-full/summary.json`, its six full reports,
and `artifacts/speedup/tests-validated.log`. The unvalidated prioritization trial
was stopped and is excluded from these claims. Reproduce with:

```bash
DOTNET_ROLL_FORWARD=Major python3 scripts/benchmark_real_world.py \
  --checkout artifacts/real-world/FluentValidation \
  --compare-cli artifacts/speedup/before-cli/Walker.Cli.dll \
  --profiles full --repetitions 3 --timeout 120
```

These results are for the pinned nine-mutant diff on this MacBook Air, not a
universal runtime guarantee or a claim for 20 executed mutants.

Focused cross-check: one matched pair completed in **25.436s updated versus 28.527s preserved** (10.8% less time), with the same 9 mutant outcomes and exact source restoration. This is one pair, not a median across three samples. Evidence: `artifacts/speedup/validated-focused/summary.json`.

A bounded self-check of the changed `Walker.Execution` source completed in 16.737s:
3/75 discovered mutants selected and executed, 2 killed and 1 survived; no errors
or timeouts, and every snapshot C# byte restored. The survivor changes the
prioritization gate from `tests.TestMs >= 3000` to `> 3000`. At exactly 3000ms it
chooses full-scope execution rather than trying preferred tests first. Both paths
still require the full requested scope for a survivor, so this is accepted as a
performance-policy boundary, not a correctness defect. The unmodified CLI report
retains exit 1; classification does not convert it to a pass. The other selected
mutants flipped the runtime-identifier compatibility guard and verified-build
sharing guard; both were killed by tests.

Self-check evidence and classification: `artifacts/speedup/self/walker.json`,
`classification.json`, and `snapshot.json`. The isolated snapshot used SDK 9 via a
snapshot-only SDK roll-forward adjustment; the source repository SDK pin stayed
unchanged. Its intentional staged patch and copied new test file make the worktree
dirty, so cleanup uses no force and the snapshot is retained at
`/var/folders/l3/7wvcm7vn2xjdmqfgrqmykyhw0000gn/T/walker-speed-self-42kq90i3/worktree` for inspection.
