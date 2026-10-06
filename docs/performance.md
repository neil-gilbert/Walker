# Performance review — 2026-10-06

## Third optimization pass — current result

The same four-mutant Payments sample now completes in **8.325s median**, compared with **10.449s** for the preceding implementation measured alongside it: a **20.3% reduction**. The summed mutation phase fell from about 7.1s to 5.0s per run (about 30%). All six runs had identical mutant IDs and outcomes (four executed, two killed, two survived).

| Measurement | Previous implementation | This pass |
| --- | ---: | ---: |
| Run 1 wall time | 10.342s | 8.206s |
| Run 2 wall time | 10.642s | 8.298s |
| Run 3 wall time | 10.271s | 8.194s |
| Median wall time | 10.449s | 8.325s |

Measured with `scripts/benchmark.py --skip-stryker --compare-cli <snapshot> --repetitions 3`, .NET SDK 10.0.203, macOS arm64, warmed fixture. These are local measurements, not guarantees.

Changes:

- **One `dotnet test` per mutant.** Mutant runs no longer use a separate `dotnet build` and `dotnet test --no-build`. One `dotnet test --no-restore` builds the graph and runs the tests, so each mutant needs one CLI and MSBuild startup fewer. If that run fails without a TRX report, a separate production build still separates `CompileError` from `TestError`. Per-mutant `buildMs` now covers only explicit production builds; the shared build time is in `testMs`.
- **Analyzers are not run** (`-p:RunAnalyzers=false`) in baseline and mutant builds. Analyzers do not change compiled behaviour, and source generators still run. On this small fixture the gain is small; on analyzer-heavy projects it can be large.
- **One Git diff for all files** instead of one for each file. This also fixes rename handling: a per-file pathspec reported a renamed file as entirely new.
- **MSBuild Compile-item evaluation runs at the same time as** the dirty-file Git query, and its result is cached for Roslyn type context.
- TRX reports are streamed with `XmlReader`. Process output keeps its tail in a ring buffer. Build metadata JSON is parsed once. `DOTNET_CLI_TELEMETRY_OPTOUT`/`DOTNET_NOLOGO` are set unless the user already set them.
- Not changed: reducing Roslyn metadata references to the core library. Measured, it saved only about 20–50 ms and would leave more arithmetic operand types unresolved.

## Second optimization pass — historical measurements

A guarded shared-build path reduces the same four-mutant sample to **12.107s median**, compared with **15.142s** for the preceding implementation measured alongside it: another **20.0% reduction**. All six runs had identical mutant IDs and outcomes (four executed, two killed, two survived) and restored the exact source bytes.

| Measurement | Previous implementation | Shared-build path |
| --- | ---: | ---: |
| Run 1 wall time | 15.299s | 13.088s |
| Run 2 wall time | 14.492s | 12.083s |
| Run 3 wall time | 15.142s | 12.107s |
| Median wall time | 15.142s | 12.107s |
| Median mutation builds, summed per run | 6.123s | 3.436s |

The benchmark alternated run order on the same warmed Payments fixture using .NET SDK 8.0.425 on Linux x86-64. The preceding implementation was preserved as a complete binary snapshot. Raw evidence and a fresh text report are in `artifacts/performance-v2/`. No Stryker comparison was repeated in this pass.

During successful baseline builds, Walker captures MSBuild's resolved project-reference metadata. When it confirms that the first selected test project's build includes the same production project, framework, configuration, and platform, the mutation needs one graph build instead of separate production and test builds. The normal `dotnet test` invocation and dependency builds are retained.

The optimization is enabled only after the full baseline passes. Multi-targeting, runtime identifiers, disabled reference builds, custom reference targets/properties, incompatible frameworks/configurations, missing/truncated metadata, or unprepared test selections use the original explicit production build. If a shared build fails, a separate production build distinguishes `CompileError` from `TestError`. No compiled test assemblies are copied or reused behind MSBuild's back.

MSBuild property queries otherwise default to evaluation-only mode; the observed baseline builds explicitly request `-target:Build`. Real CLI integration tests verify that assemblies are produced and that equality coverage kills the mutation through both direct and transitive references.

The full build passes with zero warnings/errors, and **66 tests pass**, including 16 new fast-path/fallback/classification cases. Per-mutant test-host startup and test execution are now the largest remaining mutation-phase cost on this tiny fixture.

## First optimization pass — historical measurements

Walker completed the four-mutant Payments fixture in **15.449 seconds median**, compared with **25.800 seconds** before optimization: **40.1% lower latency**, or approximately **1.67× throughput** for this fixed workload. All six executions produced identical mutant IDs and outcomes: four executed, two killed, two survived. Every execution restored the exact source bytes.

### Measurements

Three paired runs used the same temporary Git repository, SDK 8.0.425, Debug CLI builds, Linux x86-64, warmed packages and initial fixture build/tests. Execution order alternated. The compiler server was warm from verification tests. No other builds or tests ran concurrently. Times include Walker's baseline and the complete final report.

| Measurement | Before | After |
| --- | ---: | ---: |
| Run 1 wall time | 25.800s | 15.449s |
| Run 2 wall time | 25.776s | 15.652s |
| Run 3 wall time | 26.153s | 15.081s |
| Median wall time | 25.800s | 15.449s |
| Median per-mutant builds, summed per run | 14.734s | 6.361s |
| Median baseline | 4.849s | 3.217s |
| Median mutation test execution, summed per run | 5.068s | 4.682s |

The two meaningful survivors were the equal-balance purchase boundary (`>=` → `>`) and the weak positive-fee assertion (`*` → `/`).

A separate Stryker.NET 4.6.0 run took **11.500s**, executed five mutants, killed three, and found the same two survivors (plus one ignored candidate). This tiny fixture does **not** demonstrate a speed advantage over Stryker. Walker's advantage remains a bounded diff scope; larger-repository and smaller-diff measurements are needed to test the original hypothesis. These are local measurements, not performance guarantees.

Raw evidence is in `artifacts/performance/summary.json`, `walker-N.json`, `before-N.json`, `stryker.json`, and `sample.txt`. Artifacts are ignored by version control. The benchmark script can reproduce paired measurements using a preserved CLI output directory; see [benchmark instructions](benchmark.md).

### Changes and review findings

- **Build overhead dominated.** Mutant builds now use `--no-restore` after the successful baseline and use the standard .NET compiler server. MSBuild still processes project dependencies and refreshes test outputs. Skipping dependent builds was deliberately avoided because downstream assemblies must observe the changed code.
- **Inherited output handles could block indefinitely.** A long-lived compiler server can retain a redirected output pipe after the build exits. ProcessRunner now drains buffered output for a bounded interval after exit and cancels pending reads. Truncated output is explicitly flagged; Git/MSBuild JSON consumers reject incomplete data. Cancellation still terminates the active process tree before source restoration.
- **Roslyn repeated unnecessary work.** Discovery now prunes unchanged syntax subtrees, merges changed spans, hashes each source at most once, and lazily creates semantic models only for changed arithmetic expressions. The tiny fixture includes arithmetic and cannot meaningfully quantify these large-file benefits.
- **No-change runs evaluated projects unnecessarily.** Generated/excluded-only or empty C# diffs now return before MSBuild source evaluation. Git no longer computes an unused diff-stat; merge-base semantics are preserved. Directory/project classification and exclusion patterns are reused within discovery.
- **Extra test projects could continue after a confirmed kill.** Every configured project passes the baseline, but mutant execution now stops at the first project reporting actual failed tests. Survivors still require all selected projects to pass. Runner errors and zero-test runs remain errors, not kills.
- **Skipped-result accounting scanned repeatedly.** A set of completed IDs replaces repeated linear searches when marking remaining selected mutants skipped.

### Verification and remaining cost

At the end of this first pass, the full solution built with zero warnings/errors and all **50 tests passed**. Coverage includes changed-line selection, stable IDs, overlapping/multiline changed regions, no-change discovery, baseline and mutant outcomes, time-budget cancellation, inherited daemon pipes, exact dirty-byte restoration, technical JSON, themed text, and direct/transitive project-reference end-to-end runs.

The inherited-pipe regression reproduces the hang with a child that keeps stdout open after its parent exits. The real CLI integration tests show that a boundary mutant survives weak tests, then is killed by equality coverage, through both direct and transitive project references.

Remaining runtime is predominantly separate builds and test-host startup/execution for each mutant. The next substantial improvement needs reliable test-impact selection or compile-once mutation activation, as planned in the original architecture. Neither concurrent working-tree mutation nor unsafe reuse of stale test assemblies was introduced.
