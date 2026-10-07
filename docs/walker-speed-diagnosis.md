# Why Walker is slower than Stryker.NET — 2026-10-07

## Finding

The main cause on the twenty-boundary fixture is **the number of test runs**.
Stryker uses per-test mutation coverage to pack forty independent mutations into
**two runs**. Walker activates one mutation per process, so it performs **twenty
runs**, even in switching mode. Two workers overlap those runs but still require
roughly ten waves of startup, test discovery and result collection.

Walker also pays substantial fixed setup costs: three build commands, repeated
MSBuild property queries, and baseline checks for its independent output copies.
Default source mode additionally refreshes the project graph for each mutation.
Roslyn discovery and Git diff handling are small parts of the measured time.

This investigation diagnoses the existing implementation. It does not remove
correctness checks or change production execution behaviour.

## Reproduction and minimisation

The fresh ordinary comparison reproduced the symptom: switching with two workers
took **14.924s**, versus **7.009s** for Stryker with two sessions. Both completed
with identical outcomes for all twenty shared mutations.

The diagnostic feedback loop rejects a Walker/Stryker ratio above 1.25 after
checking that the reports completed and common mutation outcomes agree:

```text
python3 artifacts/stryker-diagnosis/check_gap.py --fresh \
  --output artifacts/stryker-diagnosis/fresh-pair
Walker 14924ms; Stryker 7009ms; ratio 2.13
RED: Walker remains more than 25% slower on the same changed behaviour.
```

Exploratory minimisation reduced the disposable fixture to one and two
boundaries. One supported mutation retains one actual worker; two trigger the
two-worker pool and its additional validation. Those exploratory timing samples
precede the binary freeze and are not used as accepted performance evidence.

## Instrumentation

A disposable CLI host copies the real CLI entry point and wraps the **unmodified
production `IProcessRunner`**. It records start/end timestamps, command arguments,
active mutation IDs and TRX test-body durations before normal cleanup. The
profiler uses the saved release at `artifacts/optimisation-5/final-cli`, with an
entry point copied from commit `ede8fa2`. SHA-256 checks confirm that all five
Walker dependency DLLs match that frozen release. Concurrent working-tree
rebuilds were detected; earlier profiles against the mutable Release directory
and the first no-mixing repeated series are excluded from this evidence.
No production logging or executor changes were required.

The pinned instrumented switching run took **15.654s**. Its command trace contains:

| Operation | Commands | Aggregate command time | Meaning |
| --- | ---: | ---: | --- |
| MSBuild property/reference queries | 9 | 2.608s | 0.276s discovery query; 2.332s preparation queries |
| Explicit builds | 3 | 3.145s | Original production, original tests, instrumented tests |
| Baseline/validation tests | 6 | 3.858s | Original baseline, initial ID-zero probe, two ordinary and two ID-zero worker probes |
| Active mutation tests | 20 | 11.958s | New `dotnet test <DLL>` invocation for each mutation |
| Git inventory checks | 23 | 0.763s | Snapshot creation/validation and a check before every prepared attempt |

These are **summed command costs**, not elapsed phases: parallel worker commands
overlap. The twenty mutation test commands span **6.347s** of elapsed time.
Their **400 test-case executions total only 60.354ms of reported test-body time**.
Each command has a median **597ms** duration, while its test bodies average
about **3.018ms** in total. Process/runner startup, discovery and report handling
dominate; the assertions themselves are cheap. TRX durations do not include all
adapter setup/teardown, so the remainder is runner overhead, not a pure process
creation measurement.

Reported phases in that run:

| Phase | Time |
| --- | ---: |
| Git/source scope, parsing and discovery | 0.440s |
| Original baseline | 2.214s |
| Switching and worker preparation | 6.420s |
| Active test-command span | 6.347s |

CLI startup, work around commands, serialization and cleanup account for the
remaining approximately 0.233s. These phase totals show that copying files or
tuning Roslyn discovery alone cannot close the multi-second gap.

## Controlled batching experiment

Stryker's debug log explicitly records:

```text
Mutations will be tested in 2 test runs, instead of 40.
```

Each mutation is covered by one theory case; different boundary methods have
disjoint covering cases. Stryker groups the twenty `<` mutations into one run
and the twenty `>` mutations into another. Each group has twenty members, with
no shared covering test between its members. This behaviour agrees with
[Stryker's documented mixing policy](https://stryker-mutator.io/docs/stryker-net/configuration/#disable-mix-mutants-flag).

Changing **only** `disable-mix-mutants` in Stryker's configuration changes the
instrumented diagnostic pair from **Walker 15.654s / Stryker 7.244s** to
**Walker 14.596s / Stryker 14.632s**. Stryker's log then records forty mutation
requests rather than two. All forty candidates remain killed, and all twenty
shared mutations retain the same outcomes. The lost batching benefit is much
larger than ordinary run-to-run variation in the original repeated comparison.

This control attributes a large benefit to batching. It does **not** imply that
disabling Stryker's optimisation is a useful competitive configuration, or that
the two complete mutation sets are equivalent. Its purpose is to isolate the
cause of the normal performance gap.

An additional uninstrumented run through the updated harness validates the
mixing-control option and binary freezing: **Walker 14.985s / Stryker 14.561s**,
with twenty matching shared outcomes and all **86 copied CLI files** retaining
their recorded SHA-256 hashes. This run is saved in `pinned-no-mix-benchmark/`.
These are diagnostic samples, not a new multi-project performance ranking.

## Source-mode cost

A separate trace of the default source path took **23.552s**. Its twenty mutation
commands cost **20.801s** in total, median **1.030s** each, while their test bodies
totalled **60.417ms**. Each command is `dotnet test <project.csproj>` with restore
disabled and build enabled; the source changes before each call. Project
evaluation and compilation are included in the reported **`testMs`** field.
`buildMs: 0` therefore does not mean that those attempts did not build.

Switching removes those per-mutation graph refreshes and reduces an individual
attempt to roughly 0.6s. It still invokes a new test process for each active ID.

## Relevant implementation

- `src/Walker.Execution/DotnetMutationExecutor.cs`, `RunTestScope`: creates TRX
  directories and invokes `dotnet test` for each project or prepared DLL scope.
- `src/Walker.Execution/PreparedMutationSession.cs`, `ExecuteAsync`: validates
  snapshot inputs, activates one ID and runs the original full test filter.
  Prepared attempts do not use the source executor's learned preferred groups.
- `src/Walker.Execution/MutationWorkerPool.cs`, `CreateAsync`: copies ordinary
  and instrumented generations, then validates each worker's ordinary and
  ID-zero tests in two parallel rounds. These checks establish equivalence and
  detect resource conflicts; simply deleting them would change guarantees.
- `src/Walker.Execution/SwitchingMutationExecutor.cs`, `PrepareAsync`/`Layout`:
  evaluates original, copied outer and copied framework-specific layouts through
  separate MSBuild processes before building and checking the instrumented graph.
- `src/Walker.Execution/MutationWorkspace.cs`, `Unchanged`: re-enumerates Git
  inputs and hashes their contents before each prepared attempt.

## Priorities for the next implementation

1. **Coverage-guided batches have the largest measured opportunity.** Collect
   per-test coverage for supported mutation sites, pack only mutations with
   disjoint covering tests, and run one fresh process per batch. The current
   single-active-ID runtime and per-mutation result model both need changes.
   Require baseline identity equivalence, comparison against serial outcomes,
   and serial fallback when coverage, shared/static state or attribution is
   ambiguous. A process pool alone does not reduce the number of test runs.
2. **Consolidate preparation metadata queries.** Reuse or combine evaluated
   properties for the same project/framework/generation. The 2.332s query cost
   is an opportunity ceiling, not an established saving. Preserve validation of
   original versus relocated outputs, custom settings and multi-target graphs;
   do not reuse values across different roots or generations blindly.
3. **Reduce repeated baseline/build work where graph proof permits it.** Prefer
   one ordinary test graph build that also proves the production project was
   built. Preserve the explicit production-build fallback for custom references.
   Review overlapping ID-zero probes using identical test identities and output
   hashes; retain the parallel checks that detect cross-worker resource conflicts.
4. **Profile the runner launch path before adopting a long-lived host.** Direct
   test-platform invocation may reduce SDK dispatch cost while keeping fresh test
   processes, but it has not been measured in this investigation. Host reuse
   changes static-state isolation and requires its own compatibility gate.

For each change, use the normal Stryker configuration as the performance control,
include preparation in wall time, and retain Payments plus a production-project
fallback workload. Prepared mode needs coverage/selection work as well as build
sharing; increasing worker count alone is unlikely to reproduce Stryker's gain.

## Evidence and reproduction

SDK 10.0.401, runtime 10.0.12, Stryker.NET 5.0.0, Apple M1 / 8 GiB RAM.
The disposable profiler/probes and raw traces are retained under clearly named,
ignored `artifacts/stryker-diagnosis/`. `pinned-analysis.json` records phase costs and
counts; `pinned-switch-2/commands.jsonl`, `pinned-no-mix/commands.jsonl` and
`pinned-source-1/commands.jsonl` contain the timed real command calls. All
completed probes preserve fixture C# hashes and expected mutation outcomes.

The reusable harness now supports the batching control:
It snapshots the complete Walker CLI output before timing, records every copied
file's hash and validates the snapshot again after each pair. This prevents a
concurrent repository build from changing the measured binary generation.

```bash
python3 scripts/benchmark_stryker.py \
  --dotnet .dotnet/dotnet \
  --cli artifacts/optimisation-5/final-cli/Walker.Cli.dll \
  --stryker artifacts/stryker-comparison/tool/dotnet-stryker \
  --fixture boundaries --mutant-mode switch --workers 2 \
  --stryker-concurrency 2 --disable-stryker-mixing --repetitions 3 \
  --output artifacts/stryker-diagnosis/no-mix-new
```

Omit `--disable-stryker-mixing` and use a fresh output directory for the normal
control. Tool installation/build and the original normal repeated series are
documented in [the comparison report](stryker-comparison.md).
