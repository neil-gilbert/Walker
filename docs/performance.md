# Performance review — 2026-10-06

## Coverage batching — 2026-10-07

Switch mode now batches disjoint, covered stateless boundaries behind a narrow
test-source safety gate. Missing/ambiguous coverage, changed paths, errors and
hangs retain individual retries; confirmation stays individual. Preparation
combines ordinary layout/output queries and consumes output metadata from
single-framework builds when complete. Effective batches avoid a second worker
generation and its parallel probes; sparse coverage retains the existing pool.

Three fresh pairs per frozen revision reduced the twenty-boundary Walker median
from **14.618s to 7.091s**, including preparation, a **51.5%** reduction. Normal
Stryker measured **6.886s** in the final packed-candidate series: Walker is now
within **3.0%** on this fixture. All twenty shared outcomes match; Walker uses one batch for twenty
mutations while Stryker retains forty killed mutations. Source mode remains the
default. See [coverage batching](coverage-batching.md) for exact eligibility,
fallbacks, test evidence and the controls; these numbers do not establish a
general performance ranking.

## Stryker.NET comparison — 2026-10-07

Fresh comparisons against Stryker.NET 5.0.0 use identical disposable source/test
copies, warmed initial builds, alternating execution order and matching
concurrency limits. Payments is effectively tied across five pairs: Walker
**7.384s**, Stryker **7.264s** median, with identical outcomes for all four shared
mutations. Stryker executes one additional killed mutation.

On twenty independent numeric boundaries, three pairs give Walker source mode
**23.745s** versus Stryker **7.555s** with one worker/session. Walker's opt-in
switching and two workers give **15.367s** versus Stryker **7.168s** with two
sessions. Stryker tests forty candidates against Walker's twenty; all shared
mutations are killed by both. Walker therefore still takes **2.14 times as long**
in the faster configuration on this fixture. This fixture omits the existing
Walker-specific fresh-host assertion from both copies, so these measurements
should not be directly combined with the older worker-only comparison below.

See [the comparison report](stryker-comparison.md) for individual timings,
mutation scope, memory, validation and reproduction commands. These small
fixtures establish a remaining performance gap, not a general project ranking.
The [subsequent diagnosis](walker-speed-diagnosis.md) shows that Stryker packs
forty candidates into two coverage-guided test runs, while Walker launches
twenty separate DLL test commands. Disabling mixing removes the observed lead;
the control retains all outcomes. Walker also pays repeated MSBuild queries and
ordinary/ID-zero baseline validation before dispatch.

## Isolated mutation workers — 2026-10-07

Experimental `--mutant-mode switch --workers 2` runs prepared mutants in a bounded
pool. One worker remains the default. Workers own independent ordinary/prepared
output generations, copied content, working directories, temp/TRX paths and
executor state. Parallel ordinary and ID-zero baselines must match the original
full-scope test identities before dispatch. Unsupported source mutants wait for
active workers and execute serially. Confirmation uses the killing worker's
separate validated ordinary DLL, with the actual failing framework/filter.

Three alternating pairs using identical final .NET 10 CLI binaries on an Apple
M1 with 8 GiB RAM gave **19.077s → 15.265s median (20.0% lower)** for twenty
eligible boundaries. Every pair improved by 3.208–3.955s, with all twenty IDs and
kills unchanged and exact source restoration. The fixture has no artificial
delays. All copying and concurrent baseline probes are included: preparation
rose from **4.305s to 5.799s** median. Summed mutant command time rose from
11.400s to 12.102s; overlapping those commands reduces elapsed time, not their
total work.

Median sampled peak process-tree RSS rose from **513.5 MiB to 818.5 MiB**.
The optional harness sampler sums the CLI and current descendants using `ps` at
100ms intervals. This is a sampled RSS sum, not an OS high-water mark; short
peaks, reparented/shared daemons and double-counted shared pages limit it. Both
labels use the same sampling policy. The test framework's own parallelism and
external database/port contention can change this tradeoff in other suites.

Fallback cross-checks, three pairs each, retained one worker for both requested
settings. Payments has zero prepared/four source fallbacks; FluentValidation has
zero prepared/nine fallbacks because its generator context is unsupported.

| Workload | Request one worker | Request two | Sampled peak RSS medians (one / two) |
| --- | ---: | ---: | ---: |
| Payments | 7.810s | 7.689s | 642.5 / 642.3 MiB |
| FluentValidation full | 47.886s | 48.106s | 657.8 / 657.0 MiB |
| FluentValidation focused | 25.960s | 25.433s | 655.9 / 657.7 MiB |

These are variations on the same serial execution path; no concurrency speed or
memory benefit is claimed. All runs preserve identical IDs/outcomes, exact source
restoration and zero errors/hangs/timeouts/skips. FluentValidation retains eight
kills/one survivor and all **214 tracked C# hashes**, with a clean original checkout.
Its target policy remains SDK 9.0.303 with major runtime roll-forward for both
labels; the same .NET 10 apphost/runtime hosts Walker.

Validation: **230 .NET tests and nine Python tests passed**, plus the installed
two-worker tool under SDK 10.0.401/net10.0 and SDK 8.0.425/net8.0, each with two
frameworks. Package traces establish three graph builds, one observer attachment,
14 DLL attempts, no per-mutant builds and unchanged source bytes. Tests cover
barrier-proved overlap, dirty BOM/CRLF and untracked content, transitive references,
second-framework kills, worker-local confirmation, interleaved source fallback,
rejected parallel baselines, one-worker fallback, error/hang continuation, real
child cancellation/cleanup and selected-order partial reporting/status precedence.

Keep two workers opt-in for suites that support concurrent external resources.
Use `workersRequested`, `workersUsed` and preparation counts/detail to check that
the pool ran. Raw evidence and complete hashed CLI snapshots are in ignored
`artifacts/optimisation-5/`; reproduction commands are in [benchmark.md](benchmark.md#compile-once-switching-fixture).

## Compile-once switching — 2026-10-07

Experimental `--mutant-mode switch` compiles selected numeric boundaries in an
isolated copy and activates one per fresh test process. Source mutation stays the
default. Three alternating mode comparisons on identical .NET 10 CLI binaries
gave a **23.471s → 19.057s median (18.8% reduction)** on twenty eligible boundaries,
including **4.336s** preparation. All twenty IDs and kills matched. The fixture
contains no artificial delays. A final check after stricter isolation guards
retained the gain: **23.667s → 19.529s (17.5%)**, one pair.

| Cross-check | Source median | Switch median | Interpretation |
| --- | ---: | ---: | --- |
| Payments, three pairs | 7.151s | 7.628s | 6.7% slower; all four use fallback |
| FluentValidation full, five pairs | 47.197s | 47.046s | Within noise; no gain claimed; all nine use fallback |
| FluentValidation focused, three pairs | 25.022s | 25.188s | Small 0.7% regression; all nine use fallback |

Payments excludes decimal and non-boundary operators from switching;
FluentValidation's source generator requires source execution. Preparation costs
approximately 0.4s on these unsupported workloads, with no build savings. The
default-enable gate therefore missed. These measurements establish a benefit on
the eligible synthetic fixture, not a general repository speedup.

All comparisons retained identical IDs/outcomes, restored source bytes and zero
new errors, hangs, timeouts or skips. All 214 FluentValidation C# hashes matched
and its original checkout stayed clean. Final validation: **216 .NET tests and
seven Python tests pass**, plus installed-tool switching with two frameworks under
SDK 10 and SDK 8. Traces verify one prepared graph and zero additional mutation
builds. Dirty/untracked input, stale source, batch rejection, framework-specific
kills, confirmation, legacy fallback isolation and external/ignored/conditional
build settings have regression coverage.

Walker runs on .NET 10.0.12. Synthetic targets use SDK 10.0.401; FluentValidation
retains SDK 9.0.303 for both modes. The first snapshot precedes the final workspace
guards; those unsupported cross-checks return before workspace creation, and the
last two full pairs plus the final synthetic check use the final binaries.
See the [plan's implementation record](mutation-optimisation-plan.md#item-1-implementation--2026-10-07)
for the exact qualifications and safeguards. Raw results and complete snapshots
are in ignored `artifacts/optimisation-1/`. The subsequent worker measurements
appear above.

## Real-project execution optimization

The [FluentValidation follow-up](real-world-benchmark.md#optimization-follow-up--2026-10-06)
measures the full suite at **47.469s median versus 59.648s** for the preserved CLI
in three alternating paired runs: **20.4% less time**, with all nine mutant IDs
and outcomes unchanged. A focused cross-check also improved (25.436s versus
28.527s, one pair). This now meets the original 60-second target for that diff on
the MacBook Air.

The executor observes inner-framework reference metadata to share verified builds,
stops after a framework establishes a kill, and prioritizes methods that killed
earlier mutants on slower suites. Preferred groups must first pass on unmutated
source; passing groups always fall back to the full requested scope. Validation
cost is included in the measured time. Unknown metadata and unsupported layouts
retain conservative execution. Release tests: **132 passed**. See the linked
report for the full measurements, safeguards, self-check survivor classification,
and raw artifact paths.

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


## Baseline framework metadata collection — 2026-10-06

A supported two-framework test project now needs two baseline build invocations (production plus the test graph), rather than four. A packaged MSBuild logger captures the resolved inner-framework reference metadata during the original build. Existing build-coverage checks, the full baseline scope, source restoration and conservative query fallbacks remain in place. Single-framework and imported/unknown declarations use the existing route.

Three alternating pairs against the preserved pre-change CLI gave these new local medians:

| Workload | Before wall | After wall | Before baseline | After baseline |
| --- | ---: | ---: | ---: | ---: |
| FluentValidation full | 47.447s | 45.497s | 7.837s | 6.485s |
| FluentValidation focused | 25.660s | 24.273s | 4.789s | 3.347s |
| Payments, single framework | 6.890s | 6.835s | 2.006s | 1.964s |

Full and focused FluentValidation wall medians improved by 4.1% and 5.4%, respectively; every pair improved. No meaningful change is claimed for Payments. All compared runs retained the same selected IDs and outcomes (FluentValidation: 8 killed, 1 survived; Payments: 2 killed, 2 survived), with exact source bytes restored and no new errors, hangs, timeouts or skips. Measurements include observation overhead. Payments used SDK 8.0.425; FluentValidation used SDK 9.0.303 and runtime major roll-forward identically for both CLIs.

All 144 .NET tests and four Python harness tests passed. A real two-framework transitive fixture compares captured metadata with the original queries and checks existing imports/property-change fallbacks. The locally installed package smoke test also asserts two build calls and collector deployment outside the checkout. Reproduce it with [check_packed_tool.py](../scripts/check_packed_tool.py); see [the plan and completion record](mutation-optimisation-plan.md#completion-checklist-and-handoff-record) for commands and limitations. Raw paired evidence is in ignored `artifacts/optimisation-4/` directories. Item 2 was completed subsequently, below; items 3, 1 and 5 remain pending.

## Preferred-test planning — 2026-10-07

Stable member-specific singletons reduce the new unrelated-member fixture from
**45.357s to 37.220s median (17.9%)** in three alternating pairs. Every pair
improved, with the same seven selected IDs, seven kills and exact source bytes
restored. The fixture deliberately uses serial 500ms member tests and a 2s
unrelated test; this is synthetic evidence, not a general speedup claim.

The planner validates exact scopes on unmutated input, prepares only the first
attempted project/framework, and stops an exact preferred group after two
consecutive passing/empty misses. A preferred kill resets the streak. Original
filters, full-scope survival, fresh mutant builds and kill confirmation remain
intact. Eligibility uses individual project baseline costs and retains the
three-second rule when framework timing cannot be proved.

For ambiguous first kills naming several methods, the broader history is
retained. Forcing a singleton in that case regressed full FluentValidation
from 45.866s to 63.198s median, so that experiment was rejected. The final
policy keeps existing workloads within noise:

| Workload | Before wall median | After wall median |
| --- | ---: | ---: |
| FluentValidation full | 45.610s | 45.446s |
| FluentValidation focused | 23.852s | 23.666s |
| Payments | 7.122s | 6.996s |

All compared IDs/outcomes match (FluentValidation: eight killed/one survived;
Payments: two killed/two survived), with no errors, hangs, timeouts or skips.
All 158 .NET tests and four Python tests passed. The final policy is enabled
by default with the ambiguous-history fallback. See the
[implementation record](mutation-optimisation-plan.md#item-2-completed--2026-10-07)
and [fixture command](benchmark.md#preferred-test-planner-fixture).
Raw evidence is in ignored `artifacts/optimisation-2/`; both synthetic fixtures
used SDK 8.0.425, and FluentValidation used SDK 9.0.303 with major runtime
roll-forward identically for the control and candidate. No timing improvement
is claimed for the existing workloads.

## Verified assembly runner — opt-in, 2026-10-07

`--compiled-tests` / `"compiledTests": true` runs project-vs-DLL compatibility probes during the ordinary baseline, then permits verified DLL full-filter retries after preferred project invocations build a mutant. Actual output paths, framework monikers, required runtime files and hashed build inputs are checked. Ordinary source mutations and restored-source confirmation keep their project builds. Unsupported settings/targets/imports or incomplete metadata retain project execution. Fresh processes, TRX outcome rules, framework coverage and byte restoration remain in place.

The complete suite passed **184 .NET tests and four Python tests**, including real xUnit content/filter/transitive/multi-framework probes and a preferred miss that kills via the freshly built DLL and confirms via a restored project build.

Three alternating pairs against the preserved CLI containing items 4 and 2 produced these wall medians:

| Workload | Before | Opt-in assembly runner |
| --- | ---: | ---: |
| Payments | 6.888s | 7.753s |
| FluentValidation full | 45.949s | 54.061s |
| FluentValidation focused | 24.133s | 26.091s |

Every pair was slower. All IDs/outcomes matched: Payments two kills/two survivors; FluentValidation eight kills/one survivor, all 214 tracked C# hashes restored and no errors/hangs/timeouts/skips. Full FluentValidation's baseline increased 6.492s → 14.715s; mutation test-command time fell 38.357s → 37.575s, with 0.521s median output-query work. Full-suite probing costs more than the retries save on these runs.

**The runner remains off by default.** It is a tested capability for future compile-once mutation switching, which must include and amortise preparation costs in its own benchmark gate. Output/settings evaluations are separate from item 4's existing coverage logger; a preferred miss still pays an evaluation. No default speed gain is claimed. See the [implementation plan](mutation-optimisation-plan.md#item-3-completed-as-opt-in--2026-10-07) for fallbacks and the next slice. Local raw comparisons, source/binary snapshots and command contracts are under ignored `artifacts/optimisation-3/`.
