# Coverage batching

Switch mode now reduces the number of test launches as well as repeated builds.
The source executor remains the default.

## Eligibility and attribution

The first gate accepts only static expression-bodied numeric comparisons between
a parameter and a literal. Overloads, user fields, properties, constructors and
custom conversions remain individual runs. The test-source gate accepts a narrow
assertion-only grammar: primitive test arguments, direct xUnit assertions and calls
to the proved pure boundaries, including local arrays of those method delegates.
Shared fields, properties, constructors, hooks, inheritance, asynchronous work,
custom helpers, conditional directives and unknown calls reject batching. This
gate prevents a mutation result from being stored by one test and affecting a
later test.

Walker attaches an in-process VSTest collector to the existing inactive prepared
baseline. Test parallelism is disabled for this probe and batched runs. The
collector reports every start/end pair, outcome and visited mutation site. Its
report must match every executed TRX case, and the inactive baseline must retain
the original complete test identities. Overlap, duplicate identities, missing
events and mutation execution outside a test invalidate coverage. Unsupported
adapters retain individual execution.

Deterministic first-fit packing combines only mutations with disjoint covering
test cases across every project/framework. Uncovered and unproved mutations remain
singletons. Each batch starts a new test process with a set of active IDs; all
configured scopes execute. The actual coverage and results must match the baseline
case set. New paths or an unattributable failure invalidate the batch. A failed
test must visit its unique active owner before Walker counts that mutation killed.
Passing owner tests must still visit their active site. Survivors pass every scope.

Missing/malformed reports, runner errors and per-batch hangs cause individual
retries through the existing executor. A batch hang cannot count every member as
detected. Global cancellation returns incomplete evidence; child processes are
drained before cleanup. `--confirm-kills` keeps the existing individual path.
Original input hashes are checked before a batch and again before each retry.

## Preparation and reporting

Snapshot inputs before compiler/context inspection. Copied production sources
must match the semantic model, and the batching policy reads the copied test
sources used by the scratch build. Hash checks invalidate later original-input
changes. This prevents an inspection/copy race from approving a different test
generation.

For ordinary single-framework test projects, one MSBuild evaluation now supplies
both output-layout validation and DLL metadata. An instrumented single-framework
build requests its output metadata after the Build target, removing a separate
query when that observation is complete. Incomplete build observations fall back
to the query. Multi-target inner frameworks, relocated output guards, custom
settings checks and baseline identity comparisons remain in place.

One worker handles effective batches when their run count is no greater than the
ordinary two-worker wave count. This avoids creating a second generation and its
four concurrent validation commands. Sparse/unsupported batching retains the
existing isolated pool. Explicit production builds remain: this change does not
infer that arbitrary test graphs build the production project.

Inspect preparation/result detail and `workersUsed`. A request for two workers can
legitimately use one coverage-batching worker. Every mutation still has its own
outcome and failing-test names. Result duration/test time repeat the shared batch
cost; use total elapsed time for comparisons rather than summing those values.

## Validation

`CoverageBatchTests` compares real batched and individual outcomes, including a
survivor, overlapping covering tests, shared-state rejection, missing/malformed
collector reports, a batch hang, cancellation, stale inputs, an inspection/copy
race, incomplete layout metadata and restored-source confirmation. Existing
instrumentation, prepared multi-framework, source restoration and worker tests
exercise the surrounding guarantees.

Benchmark with the normal Stryker configuration and include baseline/preparation
in elapsed time. The harness freezes all CLI files before each series and checks
completed outcomes, shared mutations and restored source bytes.

The twenty-boundary comparison used three pairs for each frozen revision, with
Walker/Stryker order alternating within each series. SDK 10.0.401, runtime
10.0.12, Stryker.NET 5.0.0, Apple M1 / 8 GiB RAM:

| Twenty boundaries, switching requested with two workers | Walker median | Stryker median |
| --- | ---: | ---: |
| Before | 14.618s | 7.143s |
| Final packed CLI: coverage batching and query consolidation | 7.091s | 6.886s |

Walker used one batch/worker for all twenty mutations; all twenty shared outcomes
matched, and Stryker retained its normal mixing/coverage settings and forty killed
mutations. Walker's wall time fell **51.5%** (2.06× throughput for this fixed work).
Walker is now within **3.0%** of normal Stryker on this fixture. Final ranges are
7.033–7.111s for Walker and 6.808–6.918s for Stryker. This is not a general ranking:
the batching gate is narrow and the two tools enumerate different mutation sets.

The candidate was built from a clean HEAD snapshot with only these optimisation
changes, excluding concurrent unrelated edits. Every CLI file was frozen and
hashed before timing. Raw completed reports and manifests are retained in ignored
`artifacts/batching/before-boundaries/` and `final-boundaries/`. The final candidate
uses the actual packed payload, whose seven Walker DLLs match the tested build.
The original diagnostic gap check reports `GREEN`, with twenty shared outcomes
equal. Earlier `after-*` samples preceded the final safety tightening and are
exploratory controls rather than the final package measurements.

The unsupported decimal Payments control showed no regression: Walker medians
**7.763s before / 7.562s final**, ranges 7.751–7.979s and 7.534–7.647s. Normal
Stryker also decreased, 7.291s → 7.041s, so no unsupported-path speed improvement
is attributed to this change. Every pair retains two kills/two survivors and four
matching shared outcomes. Preparation supports zero mutations and all four use
source fallback. Raw reports are in `artifacts/batching/before-payments/` and
`final-payments/`.

The final clean snapshot passed the full **251-test .NET suite**, including the
previously failing snapshot race; **nine Python tests** and the skill validator
also passed.

The locally installed package also passed the .NET 8 two-framework worker,
baseline-observer deployment and exact source-restoration smoke check. The packed
payload exercised the new collector in the final twenty-boundary benchmark.
