# Five optimisations for faster mutation verification

Implementation handoff, reviewed 2026-10-06. Based on commit `08a1c04` **plus the existing working-tree changes**, including framework execution and preferred-test validation. The checklist below records implementation progress; unchecked items remain proposals. Historical measurements in the evidence section predate this work.

**Objective:** reduce the wall time from launching Walker to receiving a complete report, with the same selected mutant IDs, requested test scope, outcomes, and source-restoration guarantees.

## Start here

| Rank by expected opportunity | Optimisation | Cost removed | Difficulty | Implement in this order |
| --- | --- | --- | --- | --- |
| 1 | Compile selected mutants once, activate one per test process | Recompilation and graph builds between supported mutants | High | Fourth; uses 3 and 4 |
| 2 | Make preferred tests relevant to the mutant and stop ineffective attempts | Unrelated tests, growing preferred groups, repeated subset validation | Medium | Second |
| 3 | Run verified, already-built test assemblies directly | MSBuild startup/evaluation on executions that need no build | Medium | Third; uses 4 |
| 4 | Collect framework metadata during the original baseline build | Extra inner-framework build invocations made just to inspect metadata | Medium | First |
| 5 | Execute mutants in a bounded pool of isolated workers | Serial waiting when spare CPU/memory is available | High | Fifth; uses 1 and 3 |

The ranking is an engineering judgement, not a measured comparison of these proposals. **Implementation sequence: 4 → 2 → 3 → 1 → 5.** Finish the tests and performance gate for one item before starting the next. Items 1 and 5 are architectural changes: implement their slices individually.

## Evidence and scope

The saved, alternating FluentValidation comparison has these medians across three runs of each version:

| Measurement | Preserved CLI | Existing updated CLI |
| --- | ---: | ---: |
| Completed-report wall time | 59.648 s | 47.469 s |
| Baseline | 6.428 s | 7.760 s |
| Sum of explicit mutation builds | 7.159 s | 0 s |
| Sum of mutation test-command time | 44.755 s | 38.408 s |
| Outcomes | 8 killed, 1 survived | 8 killed, 1 survived |

Source: [real-world benchmark follow-up](real-world-benchmark.md#optimization-follow-up--2026-10-06); the locally available raw summary is `artifacts/speedup/validated-full/summary.json`. Artifacts are ignored by Git and may be absent in another checkout. These are **existing measurements**, inspected for this plan, not new benchmark runs. Independent medians need not add to the wall-time median.

`testMs` includes builds inside `dotnet test` and preferred-scope preparation. Zero `buildMs` therefore **does not mean compilation is free**. Approximately 81% of the updated wall-time median is in mutation test commands. In `full-2.json`, Git/parsing/discovery together take 959 ms, while the surviving mutant takes 10.813 s. Optimising discovery alone has limited upside for this workload.

Existing improvements to preserve: `--no-restore` for mutations, shared production/test graph builds when proved compatible, analyzers disabled, early exit after a killing project/framework, validated preferred methods, lazy Roslyn semantics, and streamed TRX parsing. See [performance history](performance.md). Re-implementing these is not a new optimisation.

## Common implementation and test loop

1. Read the files named in the selected item. Check `git status --short`; retain the user's current edits. Work from the current source, not an older committed executor.
2. Record a fresh baseline and preserve the **entire** current CLI output directory using the commands below. Historical timings identify opportunities; the preserved binary is the comparison control.
3. Add the smallest regression test for the next slice. Run it and confirm that it fails for the intended missing behaviour. Avoid stopwatch assertions in unit tests; assert builds, process invocations, scopes, identities, and outcomes.
4. Implement only that slice. Run its targeted tests, then its named existing regression suites. Repair failures before continuing.
5. When all slices for that item pass, run the complete suite and paired benchmarks. Keep correctness failures distinct from noisy timings.
6. Record files changed, commands/results, command counts, benchmark medians, outcome equivalence, and remaining fallbacks in `artifacts/optimisation-N/notes.md`. Update the checklist at the end of this document only when the item meets its completion gate.

### Invariants for every item

- A passing baseline covers every requested test project and every applicable framework under the original user filter. Optimised subsets first pass on unmutated code. A survivor still passes the full original scope, including every applicable framework.
- Keep `VerificationEngine.Select`, selected mutant IDs, operator coverage, and the mutation budget unchanged. Faster completion comes from less execution overhead, not fewer selected mutants or a narrower user filter.
- Failed assertions establish kills. Compile errors, empty/missing/malformed TRX, inconsistent counters, and infrastructure failures remain errors. Keep per-mutant `Hung` separate from global-budget `TimedOut`/`Skipped`.
- Preserve `--confirm-kills`, its actual failing framework/filter, and its failure-identity completeness rules. Reordering can change which genuine failing test is observed first; outcome equivalence does not require identical failure-name lists.
- Use one global deadline for discovery, preparation, execution, and confirmation. Stop child processes before cleanup. Record added preparation time; it cannot disappear outside the measured run or timeout.
- Keep exact source bytes, dirty edits, BOM/newlines, stale-source rejection, and crash recovery. Unknown capabilities use the existing executor. A failed optimisation cannot silently become a pass or a skipped selected mutant.
- Reset run-scoped caches between verifications. Retain the conservative behaviour for unsupported build graphs and test adapters.

### Baseline and release-gate commands

Run from the repository root. `global.json` now requires a .NET 10 SDK; the pinned FluentValidation fixture additionally needs its .NET 9 SDK and .NET 8/9 test runtimes. Install/use the required SDKs and runtimes before implementation testing. Runtime roll-forward is not a replacement for a missing SDK.

Put the .NET 10 installation on `PATH` for build/test commands and their child processes. For the FluentValidation harness, use one dotnet installation containing the Walker runtime and the target's SDKs/runtimes. Earlier measurements used a local .NET 8 installation under `.dotnet/` and SDK 9 with major runtime roll-forward; their historical evidence remains unchanged. The actual commands used for item 4 are retained in local `artifacts/optimisation-4/notes.md`.

Before each numbered item, set `OPT_ITEM` to its number. Reuse that value until its comparison is complete. The snapshot command deliberately fails if the destination already exists, protecting earlier evidence.

```bash
OPT_ITEM=4
dotnet --version
dotnet restore Walker.sln
dotnet build Walker.sln -c Release --no-restore
dotnet test Walker.sln -c Release --no-build
python3 -m unittest discover -s scripts/tests
python3 - "$OPT_ITEM" <<'PY'
import shutil, sys
from pathlib import Path
destination = Path('artifacts') / ('optimisation-' + sys.argv[1]) / 'before-cli'
destination.parent.mkdir(parents=True, exist_ok=True)
shutil.copytree('src/Walker.Cli/bin/Release/net10.0', destination)
PY
```

Stop on any failed command. For the red/green loop, `dotnet test` must rebuild the tests; `--no-build` is appropriate only after an explicit successful build:

```bash
dotnet test tests/Walker.Tests/Walker.Tests.csproj -c Release --filter 'FullyQualifiedName~BaselineMetadataTests'
```

Replace the filter with the test class named in the current slice. After completing an item:

```bash
dotnet build Walker.sln -c Release --no-restore
dotnet test Walker.sln -c Release --no-build
python3 -m unittest discover -s scripts/tests
python3 scripts/benchmark.py --skip-stryker --repetitions 3 --timeout 120 \
  --cli src/Walker.Cli/bin/Release/net10.0/Walker.Cli.dll \
  --compare-cli "artifacts/optimisation-$OPT_ITEM/before-cli/Walker.Cli.dll" \
  --output "artifacts/optimisation-$OPT_ITEM/payments"
python3 scripts/benchmark_real_world.py --profiles full focused --repetitions 3 --timeout 120 \
  --cli src/Walker.Cli/bin/Release/net10.0/Walker.Cli.dll \
  --compare-cli "artifacts/optimisation-$OPT_ITEM/before-cli/Walker.Cli.dll" \
  --output "artifacts/optimisation-$OPT_ITEM/fluentvalidation"
```

The real-world harness fetches the pinned project when no `--checkout` is supplied. An existing clean checkout at the required commit can be passed with `--checkout`. These scripts use disposable repositories/worktrees; never benchmark by mutating this working tree. Run comparisons without other builds/tests competing for resources. See [benchmark details](benchmark.md) for setup and SDK qualifications.

Both harnesses now accept repeatable `--walker-arg` arguments, applied **only to the candidate CLI**, and record them in the summary. Argument construction is tested in `scripts/tests`. Add `--walker-arg=--mutant-mode --walker-arg=switch` to benchmark item 1.

**Performance gate:** all comparisons must complete with identical mutant IDs/outcomes and exact source restoration. Payments expects 4 executed / 2 killed / 2 survived; the pinned FluentValidation diff expects 9 / 8 / 1 for each profile. Reject new errors, timeouts, or skips. Compare medians and paired differences; repeat with five pairs if the apparent win is within run-to-run noise. Enable a fast path by default only after repeatable improvement on its target workload and no repeatable material regression in the other profiles. Otherwise keep it opt-in and record that it missed the default-enable gate. Do not invent a percentage speedup in advance.

Add an opt-in execution trace through `IProcessRunner` or an executor diagnostic sink when needed: phase, project/framework, mutant ID, scope kind, build/no-build mode, elapsed time, and outcome. Keep trace output outside JSON stdout. It must distinguish baseline, preferred validation, mutant attempts, full-scope fallback, and confirmation. Current `testMs` cannot separate compilation from test-host work; use a diagnostic MSBuild log for a representative run if that distinction affects a decision, and benchmark with diagnostics disabled.

## 1. Compile selected mutants once and activate one per test process

**Why:** `ExecuteMutationAsync` currently changes source before every mutant, and `RunTestScope` normally builds its test graph again. A runtime selector could turn repeated builds into one prepared build per supported graph/framework. This has the largest structural opportunity, but instrumentation overhead and fallback frequency must be measured.

**Read/edit:** [executor](../src/Walker.Execution/DotnetMutationExecutor.cs), [engine](../src/Walker.Core/VerificationEngine.cs), [contracts](../src/Walker.Core/Models.cs), [Roslyn discovery](../src/Walker.Roslyn/RoslynMutationDiscoverer.cs), [process runner](../src/Walker.Execution/ProcessRunner.cs), and [CLI](../src/Walker.Cli/Program.cs). Proposed new files: `Walker.Roslyn/MutationInstrumenter.cs`, `Walker.Execution/PreparedMutationSession.cs`, and `Walker.Execution/MutationWorkspace.cs` under `src/`.

Mutation switching is an established technique; startup/static state requires a fresh execution environment when the active mutation changes. See [Stryker's explanation](https://stryker-mutator.io/docs/mutation-testing-elements/static-mutants/). The slices below describe the implemented first version; the completion record documents its measured scope and limitations.

Conceptual rewrite, using an internal integer mapped back to the existing public mutant ID:

```csharp
return global::WalkerGenerated.Runtime.IsActive(17)
    ? (balance > price)
    : (balance >= price);
```

### Slice 1A — prepare a session without changing execution

1. Add an optional batch-preparation contract receiving the selected mutants after a successful ordinary baseline. Have `VerificationEngine` await it inside the global budget and dispose the resulting session in `finally`. Keep the legacy per-mutant executor usable by existing fakes and unsupported sessions.
2. Create a disposable snapshot containing the actual build inputs, including dirty source, project/import files and required untracked inputs. Preserve relative paths. Detect external linked inputs, symlinks, or custom absolute output paths that cannot be isolated; use the original executor for those layouts.
3. Keep discovery IDs and reported paths tied to the original repository. Hash original inputs before preparing and check for changes before execution. Give the scratch project its own `bin`/`obj`; never let a scratch build overwrite the developer's outputs.
4. Add `PreparedMutationTests`: no preparation for zero selected mutants or failed baseline; cancellation disposes preparation; dirty bytes are copied exactly; source changes refuse stale execution; paths remain repository-relative.

**Done:** preparation is optional, cancellable, and changes no existing outcomes or files in the user's tree.

### Slice 1B — instrument a deliberately small supported set

1. Start with relational boundary expressions over known built-in numeric values that produce an ordinary `bool`. Instrument only selected, non-overlapping syntax nodes. Use Roslyn node replacement, preserving trivia and original span-to-ID mapping.
2. Add a generated runtime helper with a unique namespace. Read the active ID once per fresh process, before mutated code can run; ID zero means the original branch. Use per-child environment overrides, not process-wide `Environment.SetEnvironmentVariable` in Walker.
3. Keep branch evaluation lazy and evaluate operands once on the selected branch. Route expression trees, variable-binding conditions, constant-only contexts, unresolved/overloaded conversions, and unproved contexts to the legacy path. Preserve checked arithmetic and conditional-compilation semantics when extending support.
4. Build with the real project/MSBuild context, retaining target frameworks, references, defines, nullable settings, source generators and imports. The lightweight `Analysis` compilation in `RoslynMutationDiscoverer` is **not** a faithful production emitter. Projects whose generators/custom targets cannot preserve the instrumented build contract use fallback.
5. Add `InstrumentationTests` that execute original, inactive, and active branches; check side effects, short circuiting, exceptions, and ID isolation. Add negative cases proving unsupported constructs reach fallback instead of vanishing.

**Done:** each supported active branch behaves like its ordinary source-replacement mutant; inactive branches behave like original source in executable fixtures.

### Slice 1C — reuse the prepared outputs

1. Build the instrumented graph once, then run the entire requested baseline with active ID zero. Compare executed test identities/counts with the ordinary baseline for supported adapters. A failed/empty/inconsistent instrumented baseline disables this session and returns to the ordinary path within the remaining budget.
2. For each supported mutant, use item 3's assembly runner with a fresh host and its active ID. Preferred validation and kill confirmation use fresh hosts with ID zero. Initially perform confirmation against separately retained ordinary baseline outputs to preserve its current meaning.
3. Separate immutable prepared outputs from legacy fallback outputs. A fallback source build must never replace the instrumented DLL used by the next supported mutant. Keep the existing restore journal on the legacy path.
4. If a batch cannot compile, fall back for the batch in the first version. Do not classify every member `CompileError`: ordinary per-mutant compilation determines that. Later batch splitting is optional, only if traces justify it.
5. Add integration fixtures for two mutants in the same method, alternating killed/survived results, direct/transitive references, generated source, two TFMs, invalid replacement, confirmation, crash and cancellation. Check all source bytes after every run.

**Done:** after preparation, supported mutants cause zero additional builds, while unsupported mutants retain ordinary execution. Adding supported mutants increases test attempts but not prepared graph-build count. Report preparation cost, supported count and fallback count alongside the paired benchmark; retain fresh test processes so static state cannot leak between mutants.

CLI/config mode `--mutant-mode source|switch` defaults to `source`. Extend supported operators one at a time with the same semantic tests. Default promotion is a separate decision after the common performance gate; small budgets or very few eligible mutants may not repay two baselines.

## 2. Make preferred tests relevant and adaptive

**Why:** before item 2, `preferredMethods` accumulated up to 20 methods for each project/framework, regardless of the mutated member. Every changed union created another validation scope, and the single `tests.TestMs >= 3000` gate used aggregate test time. `PreparePreferred` could also validate later frameworks/projects before an earlier one killed the mutant.

**Read/edit:** [preferred planner](../src/Walker.Execution/PreferredTestPlanner.cs), `PreparePreferred` and `RunTests` in [executor](../src/Walker.Execution/DotnetMutationExecutor.cs), plus [TRX parsing](../src/Walker.Execution/TrxReport.cs). Tests: [planner](../tests/Walker.Tests/PreferredTestPlannerTests.cs), [framework execution](../tests/Walker.Tests/FrameworkExecutionTests.cs) and [kill confirmation](../tests/Walker.Tests/KillConfirmationTests.cs).

### Slice 2A — stable groups and precise validation caching

1. Extract planning from execution. Key learned history by normalised project path, framework, original filter, source file and member. `Mutant.Member` is not an overload-resolved symbol; use this history only as a priority hint.
2. Use stable singletons when the first observed kill identifies one distinct supported method: prefer the same member's method, with the initial project/framework method as the cold-start hint. If the first kill identifies several methods, retain the broader existing history policy, bounded at 20 methods. Canonicalise method ordering and filter escaping; intersect every proposal with the original filter. Do not discard ambiguous multi-method evidence just to force a singleton: the unconditional version failed the real-project gate.
3. Store validation/rejection by the **exact canonical scope** plus unmutated input identity, not one mutable filter string per project/framework. Current keys include project, working root, framework, original filter, selected source file/hash and canonical preferred filter, within a baseline epoch. Passing two singleton validations does not validate their union. Freeze singleton groups; broader fallback groups require their own exact validation whenever membership changes.
4. Initially validate at most one new group, for the first project/framework that will be attempted. Later scopes can use already-validated groups or run the full original filter. This avoids eager validation of scopes that may never execute.
5. Add `PreferredTestPlannerTests`: same-member preference, different-member fallback, trait filter intersection, parameterised methods, deterministic ties, exact group cache keys, and history reset on a new baseline.

**Done:** repeated identical scopes validate once per unchanged run; a new group never inherits another group's baseline evidence. The source hash protects the selected file and caches reset at each baseline; this is not a fingerprint of arbitrary external files changed by a test suite.

### Slice 2B — stop paying for poor predictions

1. Record actual attempts, kills, misses, elapsed cost and validation cost for each group. A miss is a preferred attempt that passes or executes no tests and therefore needs the full scope. Count all overhead in end-to-end timing.
2. Replace the aggregate three-second eligibility decision with per-project/framework timing where the baseline proves it. Current combined TRX output does not reliably identify each framework, so retain the conservative three-second rule using each project's actual baseline test-command time. Never infer a framework from a randomly named report or add unrelated projects' costs. Keep quick scopes and filters without a matching baseline on the full-scope path.
3. Give an eligible new group a bounded trial. Initially stop trying it for that scope after two consecutive misses; a new verification resets the decision. Keep this threshold in one named constant and test it. Tune from paired measurements, not intuition.
4. Continue to rebuild fresh mutated outputs before the first attempt; full-scope fallback may reuse only those outputs. A passing subset or a predictor with no history can never establish survival.
5. Add tests for rejection on unmutated failure/empty results/hang; two misses disabling extra startups; original scope on fallback; every-framework survival; later scopes not prevalidated needlessly; and confirmation on the actual killing scope.

**Done:** on synthetic hit-heavy tests, later mutants skip full suites; on miss-heavy tests, wasted preferred startups are bounded. Run `PreferredTestPlannerTests`, `FrameworkExecutionTests`, and `KillConfirmationTests`, then the common gate. Add a fixture with several unrelated changed members so the benchmark can expose over-broad history.

Per-test coverage could eventually seed these hints before the first kill. It is not required for this item: the repo has no reliable per-test map today. Keep full-scope fallback until such a collector has its own correctness evidence; file names and aggregate line coverage are not sufficient evidence to omit tests.

## 3. Run already-built test assemblies directly

**Why:** baseline tests and full-scope fallbacks call `dotnet test <project> --no-build`, which still enters the project/MSBuild route. An assembly invocation avoids that route when the exact outputs are already known. Item 1 makes this available for the supported mutation loop as well.

**Read/edit:** `Build` and `RunTestScope` in [executor](../src/Walker.Execution/DotnetMutationExecutor.cs), [build coverage](../src/Walker.Execution/BuildCoverage.cs), and [process runner](../src/Walker.Execution/ProcessRunner.cs). Proposed new file: `src/Walker.Execution/CompiledTestRunner.cs`.

Microsoft documents that `dotnet test` forwards DLL/EXE inputs to VSTest, whereas project inputs go through MSBuild. It also supports filtering, TRX results and per-test-process environment variables. See [dotnet test with VSTest](https://learn.microsoft.com/en-us/dotnet/core/tools/dotnet-test-vstest). Verify the exact argument set on the repository's .NET 10 SDK; do not copy project-only MSBuild switches into assembly commands.

### Slice 3A — discover exact test outputs

1. Extend item 4's metadata to include `TargetPath`, framework identity, configuration, platform/RID, applicable test settings/adapter locations, and a build-generation identity. Keep this distinct from proof that a test build covers production.
2. Resolve one actual test assembly per verified framework from successful builds. Include `.deps.json`, `.runtimeconfig.json`, test adapters and content dependencies in the output contract; preserve the current working directory and environment semantics.
3. Add `CompiledTestRunnerTests` for custom output paths, spaces, direct/transitive references, multiple frameworks, missing metadata/files and stale build identities. Unknown customised test targets/settings retain project execution.

**Done:** the runner selects real, fresh outputs; it never guesses `bin/Debug/net8.0` or treats a file's existence as proof that it contains the current mutant.

### Slice 3B — replace only invocations that already need no build

1. Initially use assembly execution only for baseline scopes and the full-filter retry after a preferred attempt built the current mutant. Keep combined project build/test invocations for ordinary source mutations requiring a build; splitting them into two commands may be slower.
2. Probe adapter compatibility during the baseline. Require the same discovered/executed test scope and outcomes before enabling the alternate runner. Preserve framework coverage and original filters, including parameterised rows and traits.
3. Reuse `TrxReport` outcome rules, unique results directories, process-tree cancellation and bounded output draining. Missing/malformed reports and empty test execution retain their current errors.
4. Add real integration checks comparing project and assembly execution for filtered/full xUnit scopes, content files, customised settings fallback, second-framework kills, and confirmation. Then let item 1 call this runner for prepared mutants.

**Done:** verified no-build paths launch tests without project evaluation and yield equivalent outcomes. Trace command counts and compare Payments plus both FluentValidation profiles. This optimisation removes MSBuild overhead; it still launches a fresh test host. Persistent host reuse needs separate state-isolation proof and is outside this item.

## 4. Collect framework metadata during the original baseline build

**Why:** the baseline branch of `RunTests` builds a multi-target test project once, then calls `Build(... framework: ...)` again for each framework to obtain `_MSBuildProjectReferenceExistent`. The extra graph invocations exist solely to inspect already-built inner projects. The saved comparison shows baseline cost increased while mutation execution improved.

**Read/edit:** `Build`, `ReadFrameworks`, and the baseline branch of `RunTests` in [executor](../src/Walker.Execution/DotnetMutationExecutor.cs); [build coverage](../src/Walker.Execution/BuildCoverage.cs). Collector assets: [invocation-scoped reader](../src/Walker.Execution/BuildObservation.cs), [packaged MSBuild logger](../src/Walker.BuildLogger/BuildObservationLogger.cs), and [logger project](../src/Walker.BuildLogger/Walker.BuildLogger.csproj).

Implementation uses an MSBuild logger rather than replacing or chaining user import hooks. It observes successful inner builds and the SDK's resolved reference task inputs. This preserves existing project imports. Query mode evaluates the root before logger attachment, so the observed build omits property-query switches and uses the old query route if capture fails.

### Slice 4A — collect without removing the old path

1. Attach the packaged logger to a normal test-graph build. Record evaluated settings, resolved reference task inputs, absolute project path and actual framework only after a successful inner `Build`/project completion. Reject settings assigned by targets, conflicting reference metadata and unsupported message languages.
2. Pass the logger to the whole graph without editing project files or import hooks. Use `System.Text.Json` serialization. Attach only for a visible multi-framework declaration; single-framework and imported/unknown declarations retain the existing route. Diagnostic event capture is needed for task inputs; keep console output quiet and measure the cost.
3. Write separate, atomic records keyed by project/framework/configuration/platform/RID in a unique run directory. Parallel inner builds must not append to one shared file. Accept records only from this invocation after its successful completion.
4. Package the observer/task so a packed Walker tool can locate it outside the source checkout. Do not depend on development-relative paths.
5. Add `BaselineMetadataTests`: multiple records, malformed/truncated/missing/incomplete record, duplicate identity, wrong project/schema, failed observer, cancellation and a fresh directory on each baseline. Use a real two-framework transitive fixture to compare captured fields with the original inner-build query and prove custom import/property-change fallbacks.

**Done:** records match the original inner-build query for supported layouts while the old execution path still works.

### Slice 4B — remove redundant observation commands

1. Feed successful records through the existing `BuildCoverage` compatibility checks. Skip additional inner `Build` calls only for frameworks with complete valid records. Retain the existing query fallback for missing records, then conservative build execution if compatibility remains unknown.
2. Preserve the initial production baseline build in this item. Preserve the test graph build, full original baseline test run, and the rule that every separately scheduled framework actually executed baseline tests. This slice removes metadata-only repetition.
3. Keep single-target, multi-target production, runtime-specific, custom-property and truncated-metadata fallbacks. Update command-count tests that intentionally asserted the old observation route, preserving their semantic assertions.
4. Run `BaselineMetadataTests`, `BuildReuseTests`, `FrameworkExecutionTests`, and `PerformanceRegressionTests`. Validate the packed collector with the portable smoke check below. It installs the local package into a temporary tool directory, runs a two-framework fixture and checks exact source bytes. On macOS/Linux it also asserts exactly two build calls and one logger attachment.

```bash
dotnet pack src/Walker.Cli/Walker.Cli.csproj -c Release --no-build -o artifacts/optimisation-4/package
python3 scripts/check_packed_tool.py --package artifacts/optimisation-4/package/Walker.Cli.0.1.0.nupkg
```

Use `--dotnet .dotnet/dotnet` with the local SDK installation in this workspace.

**Done:** a supported two-framework test project produces its metadata in one test-graph build, removing the two follow-up framework build invocations. Baseline test counts and build-coverage decisions stay equivalent. The common performance gate includes collector I/O/serialization overhead. Expected benefit is mostly startup latency, particularly for small mutation budgets; do not claim all 7.760 seconds of baseline time is removable.

## 5. Add a bounded pool of isolated mutation workers

**Why:** `VerificationEngine` currently awaits selected mutants sequentially. Once immutable prepared outputs exist, separate test processes can overlap some execution. This can reduce wall time when resources permit, but adds memory demand and can make small machines slower. The existing real-world measurements used an M1 with 8 GB RAM.

**Read/edit:** [engine](../src/Walker.Core/VerificationEngine.cs), [contracts](../src/Walker.Core/Models.cs), [CLI](../src/Walker.Cli/Program.cs), item 1's workspace/session, and item 3's runner. Proposed new file: `src/Walker.Execution/MutationWorkerPool.cs`.

### Slice 5A — two isolated workers, explicit opt-in

1. Add proposed `--workers`/`workers`, default `1`; initially accept `1` and `2`. Validate config/CLI precedence and expose the effective setting in diagnostics. Enable the pool only for supported prepared sessions at first.
2. Give workers independent output/content copies, temporary/results directories, active-mutant environment, process handles and mutable planner state. Never run the current source-mutating executor concurrently in one root: its source paths, `bin`/`obj`, and restore journal are shared.
3. Read-only immutable compiled inputs may be copied from preparation. Source-mode fallback mutants execute serially in a separate workspace. Preserve dirty input snapshots and inspect linked/custom external paths before enabling workers.
4. Shared external databases, ports and other services are not isolated by copying directories. Keep parallel execution opt-in for suites whose resources support concurrent runs. Prove each worker's unmutated baseline is valid under the chosen concurrency before attributing failures to mutants; this validation also uses the global budget.
5. Add `MutationWorkerTests` with a barrier that proves two attempts overlap without wall-clock thresholds. Check different active IDs, source/output/temp isolation, no double scheduling, one-worker fallback and one worker's failure leaving the other intact.

**Done:** parallelism is observable in tests, but workers cannot overwrite each other's mutable execution state.

### Slice 5B — deterministic reporting and cancellation

1. Schedule from the existing selected list. Collect by selected index and emit results in that order, regardless of completion order. At a timeout, running attempts become timed out and never-started attempts become skipped, using the existing status precedence.
2. Check the global deadline before every dispatch. Cancel all active children and await their exit/restoration before disposing workers. A single hung mutant does not exhaust the whole queue unless the global deadline also expires.
3. Validate kill confirmation in the killing worker on ordinary unmutated outputs. Do not share mutable `DotnetMutationExecutor` dictionaries across workers. Initially keep learned history local; merging hints is later optional work.
4. Run `MutationWorkerTests`, `EngineTests`, `RobustnessTests`, `ExecutionTests` and `KillConfirmationTests`, including a real child-process cancellation fixture. Check JSON/text ordering and error precedence.
5. Benchmark `workers=1` versus `2` with identical candidate binaries. Pass `--mutant-mode switch` to both labels using independent `--compare-walker-arg` / `--walker-arg` settings; otherwise the control silently stays in source mode. Include preparation/copy cost and sampled process-tree RSS using `--measure-memory`; account for test frameworks' own parallelism. The twenty-boundary fixture provides an eligible batch. Keep normal default at one unless the evidence supports a safe automatic policy.

**Done:** all mutants have equivalent completed outcomes, processes and workspaces are cleaned, and the two-worker target workload improves after setup cost. A run that is quicker only because it times out or executes fewer mutants fails the gate. Parallelism promises no fixed 2× speedup.

## Completion checklist and handoff record

### Item 4 completed — 2026-10-06

The baseline now captures SDK-resolved framework/reference metadata during its original test-graph build, using the packaged MSBuild logger. A supported two-framework project uses **two build commands total** (production + test graph), instead of four. It preserves the full baseline test run and the existing build-coverage checks. The direct/transitive real fixture verifies captured fields against the original query and proves that an imported target still runs; targets that change tracked settings use the original queries.

Validation: **144 .NET tests passed**, including 12 metadata cases; **4 Python harness tests passed**. The installed-tool smoke check runs outside the checkout, checks two frameworks, counts exactly two build commands and verifies exact source bytes. No SDK policy was changed; this workspace uses ignored SDK 8.0.425 under `.dotnet/`. FluentValidation used the existing SDK 9.0.303 with `DOTNET_ROLL_FORWARD=Major`, identically for both CLIs.

Three alternating pairs per workload used the entire preserved pre-item CLI directory. These are new local measurements, including logger/event/serialization overhead, independent of the historical table above.

| Workload | Before wall median | After wall median | Baseline medians | Wall improvement |
| --- | ---: | ---: | ---: | ---: |
| FluentValidation full | 47.447s | 45.497s | 7.837s → 6.485s | 4.1% |
| FluentValidation focused | 25.660s | 24.273s | 4.789s → 3.347s | 5.4% |
| Payments (single framework) | 6.890s | 6.835s | 2.006s → 1.964s | Within noise |

All FluentValidation runs executed the same nine selected IDs with **8 killed / 1 survived**, zero errors/hangs/timeouts/skips, and exact restoration of every tracked C# file. Payments retained the same four IDs and **2 killed / 2 survived**. Every full-profile pair improved (1.696s, 2.107s, 2.536s), as did every focused pair (1.436s, 1.712s, 1.329s). No timing improvement is claimed for Payments.

Enabled by default for visible multi-framework project declarations with supported English/invariant MSBuild events. Imported/unknown declarations, missing collector assets and unsupported languages retain the original route. Missing/ambiguous/invalid records trigger original framework queries; a failed outer observation can also require another graph build. Alias-path identities conservatively fall back. This item does not add assembly execution, mutation switching or workers.

Raw evidence: `artifacts/optimisation-4/payments/summary.json`, `artifacts/optimisation-4/fluentvalidation/summary.json`, preserved `before-cli/`, and `notes.md`. The raw artifacts are ignored; reproduce them with the commands above. The next item after this checkpoint was **2A**, completed below.

### Item 2 completed — 2026-10-07

[PreferredTestPlanner](../src/Walker.Execution/PreferredTestPlanner.cs) now separates learned hints from execution. When the first observed kill identifies one distinct supported method, the planner keeps stable singleton groups keyed to source file/member, project, framework and original filter. Other members can learn their own method without invalidating a useful earlier group. Validation/rejection uses the exact canonical filter, working root, project/framework, original filter and selected source file/hash within the baseline epoch. Every proposal intersects the original filter; commas are escaped and method ordering is deterministic.

Only the first attempted project/framework can receive a new unmutated validation. Later scopes use an exact cached validation or the full original filter. An exact group stops after **two consecutive passing/empty misses**, and a preferred kill resets that streak. New baselines reset all history. Eligibility uses each project's baseline test-command time rather than summing unrelated projects; unresolved per-framework identity retains the conservative three-second project rule. Attempt, kill, miss and elapsed/validation costs are tracked internally and included in report timing.

The initial unconditional singleton policy failed the real-project gate: full FluentValidation rose from **45.866s to 63.198s median** despite equivalent outcomes. It discarded useful methods from multi-method kills. The final policy retains the broader existing history, bounded at 20 methods, when the first observed kill is ambiguous. Each changed broader group still requires exact unmutated validation. This fallback is deliberate; do not remove it without a new paired performance gate. The rejected binary and results are retained locally under `singletons-cli/` and `fluentvalidation-singletons/`.

Validation: **158 .NET tests and four Python tests passed**. New executor-boundary tests cover member reuse, separate exact scopes, original-filter isolation, source-hash changes, generic comma escaping/canonical ordering, ambiguous history, failed/empty/hung unmutated validation, bounded misses and streak reset. Framework tests cover avoiding later-scope validation, every-framework survival and confirmation on the actual preferred killing framework/filter. Fresh mutant builds, full-filter fallback and exact byte restoration remain intact.

Three alternating pairs per workload compared the final CLI against the complete preserved pre-item-2 CLI, including item 4:

| Workload | Before wall median | After wall median | Result |
| --- | ---: | ---: | --- |
| PreferredTests, repeated member A among unrelated members | 45.357s | 37.220s | **17.9% lower** |
| FluentValidation full | 45.610s | 45.446s | Within noise |
| FluentValidation focused | 23.852s | 23.666s | Within noise |
| Payments | 7.122s | 6.996s | Within noise |

Every PreferredTests pair improved (6.842s, 7.518s, 8.304s), with the same seven IDs and seven kills. Its serial 500ms member tests and 2s unrelated test are deliberate synthetic costs; they do not predict gains in other repositories. Baseline medians were effectively unchanged (6.128s → 6.123s); summed mutation test time fell from 38.489s to 30.514s. All FluentValidation runs retained nine IDs, eight kills/one survivor and every tracked C# hash; Payments retained four IDs and two kills/two survivors. There were no errors, hangs, timeouts or skips. No timing improvement is claimed for those existing workloads.

Enabled by default with the conservative ambiguous-history fallback. The final comparisons used SDK 8.0.425 for both synthetic fixtures and SDK 9.0.303 with major runtime roll-forward for FluentValidation. Raw results, binary/source snapshots and exact commands are in ignored `artifacts/optimisation-2/`. Reproduce the new fixture with:

```bash
python3 scripts/benchmark.py --skip-stryker --fixture preferred-tests --repetitions 3 \
  --cli src/Walker.Cli/bin/Release/net10.0/Walker.Cli.dll \
  --compare-cli artifacts/optimisation-2/before-cli/Walker.Cli.dll \
  --output artifacts/optimisation-2/preferred-tests
```

Use the local SDK 8 PATH described above. At this checkpoint, switching and
parallel workers had not yet been implemented; their subsequent records follow.

### Item 3 completed as opt-in — 2026-10-07

Experimental `--compiled-tests` / `"compiledTests": true` enables the assembly runner. It is **off by default**. [CompiledTestOutput](../src/Walker.Execution/CompiledTestOutput.cs) evaluates `TargetPath`, the framework moniker, configuration/platform, test settings and import paths after a successful build. It requires the actual assembly, `.deps.json` and `.runtimeconfig.json`, then hashes the evaluated properties and project/build inputs. It uses the original output location, preserving adapters and copied content, and the same working root/environment. Output metadata remains separate from `BuildCoverage`.

Implementation adjustment to 3A: output/settings queries are separate evaluations on the opt-in path. The item-4 observer and its six tracked coverage properties remain unchanged; adding properties assigned during targets to that contract could invalidate otherwise useful coverage records. The output query cost is included in wall time and baseline/mutation timing. This version does **not** remove every MSBuild evaluation: a preferred miss requires another output query. Reusing a generation prepared once belongs to item 1.

The ordinary project baseline still runs in full. Assembly probes then run the same original filter for each proved framework with a fresh host. Streaming TRX parsing compares sorted report signatures containing every method, displayed theory case and outcome, including multiplicity. Equal counters alone are insufficient. Missing identities, incomplete reports, mismatched scope, probe errors/empty execution/local hangs or unknown capabilities disable the alternate path; global cancellation still propagates. Probe costs remain inside the verification budget.

After a preferred invocation has successfully built the current mutant, the executor evaluates outputs again and compares their build-input identity with the validated baseline. Only then can the full original filter run from the DLL. Changed output paths/configuration/project inputs or missing runtime files require a fresh project build. Ordinary mutants and restored-source confirmation retain combined project build/test execution. Baseline epochs reset all output certificates.

This first version supports conventional VSTest test projects on .NET Core with AnyCPU and no RID. Custom settings/adapter paths, collectors/blame/options, testing-platform applications, custom VSTest binaries, project/directory targets, arbitrary explicit imports, active Common/xUnit import hooks and unknown package build imports use the existing project route. Standard SDK, VSTest, xUnit and test-SDK coverage imports are recognised. No guessed output paths, relocated assemblies, shared hosts or parallel workers are introduced.

Validation: **184 .NET tests and four Python tests passed**. [CompiledTestRunnerTests](../tests/Walker.Tests/CompiledTestRunnerTests.cs) exercises full-scope compatibility rejection, custom output paths/spaces, output/input changes, missing runtime files, unsupported settings/imports and source restoration. Real xUnit fixtures cover copied content, filtered/full scopes, direct/transitive references, two frameworks, an actual preferred miss followed by a fresh mutated DLL kill, and confirmation on restored source in the killing framework. The packed tool also passed outside the source checkout with `compiledTests: true` in configuration: its trace proves two graph builds, one observer attachment, two output evaluations and two DLL probes, followed by equivalent mutation outcomes and restored bytes.

Three alternating pairs per workload compared the complete preserved pre-item-3 CLI against the candidate with `--compiled-tests`:

| Workload | Before wall median | Opt-in wall median | Change |
| --- | ---: | ---: | ---: |
| Payments | 6.888s | 7.753s | 12.6% slower |
| FluentValidation full | 45.949s | 54.061s | 17.7% slower |
| FluentValidation focused | 24.133s | 26.091s | 8.1% slower |

Every pair regressed. Payments baseline medians increased **1.995s → 2.792s**. FluentValidation full baseline increased **6.492s → 14.715s**, while summed mutation test time fell **38.357s → 37.575s** and output queries added **0.521s** median explicit mutation work. Focused baseline increased **3.316s → 5.324s**. Independent medians need not add to wall time. The extra baseline executions dominate the modest benefit of full DLL retries.

All Payments runs retained four IDs, two kills/two survivors and restored bytes. All FluentValidation runs retained nine IDs, eight kills/one survivor, exact hashes of all **214 tracked C# files**, a clean original checkout, and zero compile/test errors, hangs, timeouts or skips. SDKs remained 8.0.425 for Payments and 9.0.303 with major runtime roll-forward for FluentValidation.

**Performance gate failed for default enablement.** Keep this correctness-tested runner opt-in. Do not present the earlier proposal as a speed gain on these workloads. Item 1 now reuses outputs from a single prepared generation and includes/amortises this setup in its own gate, recorded below.

Reproduce candidate-only opt-in measurements using `--walker-arg=--compiled-tests` with either benchmark harness. This flag is forwarded only to the candidate CLI; the preserved control remains unchanged. Raw evidence and complete before/after CLI directories are in ignored `artifacts/optimisation-3/`.


### Item 1 implementation — 2026-10-07

Implemented slices 1A–1C behind `--mutant-mode switch` / `"mutantMode": "switch"`.
The default remains source mutation. Engine preparation starts only after the
ordinary baseline succeeds, consumes the same global budget, and disposes its
session on success, rejection or cancellation. JSON/text report preparation
duration and supported/fallback counts.

`ProjectCompilation` reads the actual production MSBuild compile items,
references, defines and language/nullable/overflow settings for semantic analysis.
`MutationInstrumenter` supports selected, non-overlapping built-in numeric
relational boundaries (excluding decimal/char). Constants, nullable/dynamic or
unresolved operands, expression trees, variable binding, overloaded operators and
user-defined conversions retain source mutation. The rewritten lazy branch
preserves operand evaluation, exceptions and short-circuit behaviour. A generated
helper reads a unique child-process environment key once; every attempt starts a
fresh test host. Public IDs and reported paths stay tied to original source.

`MutationWorkspace` copies tracked and non-ignored untracked files with dirty
bytes/encoding intact, rejects symlinks, external/absolute build paths, custom
targets/imports and reference-property overrides, and checks all original input
hashes before using prepared outputs. External/ignored ancestor build configuration
and conditional user build settings also require fallback, including transitive
projects; otherwise moving a project could change compiler flags while ID zero
still passes. The first version conservatively rejects
production multi-targeting, source attributes/generators and unknown test
settings/analyzers. Evaluated scratch outputs must stay inside the snapshot.
Unsupported contexts use the original executor; a failed batch build or ID-zero
baseline never turns the whole batch into `CompileError`.

The scratch graph is built once per requested test project. ID zero then executes
the complete requested filter in every framework; TRX method/theory-case/outcome
signatures must match the ordinary baseline. Supported mutants run these immutable
outputs with no additional builds or output queries. Legacy fallback builds use
the original tree and cannot overwrite prepared outputs. Confirmation deliberately
rebuilds and tests restored original source in the failing framework, rather than
trusting the instrumented baseline. Its extra cost remains in the budget.

Validation includes executable semantic
tests cover inactive/active/unknown IDs, side effects, short circuiting, exceptions
and process isolation. Real prepared fixtures cover dirty BOM/CRLF bytes,
untracked input, two boundaries in one method, survival/kill alternation,
transitive references, a kill only in the second framework, restored-source
confirmation, interleaved legacy fallback, stale-input refusal, batch/baseline
rejection, external/ignored/directory-dependent build settings, and cancellation cleanup. CLI configuration and candidate-only benchmark
arguments are validated. Final validation and timing evidence are recorded below.

Final validation: **216 .NET tests and seven Python tests passed**. The installed
package passed switching under both SDK **10.0.401** and **8.0.425**, each with two
frameworks. Traces assert two ordinary baseline builds plus one prepared graph
build, one observer attachment, four DLL attempts (ID-zero baseline and mutant in
both frameworks), one supported mutant/no fallback, zero mutant build time and
unchanged source bytes.

Source and switch are compared using **identical complete .NET 10 CLI binaries
within each pair**, with switch arguments applied only to the candidate. This
avoids confounding the concurrent SDK migration with switching. The archived
pre-migration `before-cli` is not the control. All setup and ID-zero probes are
included in wall time; restores/initial fixture tests are warmed.

| Workload | Source wall median | Switch wall median | Change | Preparation median | Supported / fallback |
| --- | ---: | ---: | ---: | ---: | ---: |
| Twenty boundaries, three alternating pairs | 23.471s | 19.057s | 18.8% faster | 4.336s | 20 / 0 |
| Payments, three alternating pairs | 7.151s | 7.628s | 6.7% slower | 0.441s | 0 / 4 |
| FluentValidation full, five pairs | 47.197s | 47.046s | Within noise; no gain claimed | 0.389s | 0 / 9 |
| FluentValidation focused, three pairs | 25.022s | 25.188s | 0.7% slower; small difference | 0.375s | 0 / 9 |

The boundary fixture has twenty equality tests and a fresh-host static-state
assertion, with **no artificial delays**. All three pairs improved; summed mutant
test time fell from **20.670s to 11.295s** median. A final paired check after the
additional isolation guards retained twenty switched kills and restored bytes:
**23.667s → 19.529s (17.5% faster)**, including **5.057s** preparation. The original
three-pair snapshot remains intact, and the final binaries are saved separately.

Payments' decimal boundary and other operators fall back; FluentValidation's
source generator requires fallback. Both return before snapshot creation, so the
later workspace guards do not change their execution paths. The last two full
FluentValidation pairs use the final guarded binaries; five pairs resolve the
tiny apparent improvement as noise. Every comparison preserves selected IDs and
outcomes: twenty kills on the boundary fixture, four IDs/two kills/two survivors
on Payments, and nine IDs/eight kills/one survivor on both FluentValidation
profiles. All **214 tracked FluentValidation C# hashes** are restored, the source
checkout stays clean, and no errors, hangs, timeouts or skips appear.

**Default-enable gate missed.** Keep `source` as the default because Payments
regresses in every pair and unsupported workloads cannot amortise preparation.
The synthetic eligible workload establishes a bounded benefit, not a general
repository speedup. Walker runs on **.NET 10.0.12**; synthetic targets use SDK
10.0.401, while FluentValidation retains SDK 9.0.303 and major runtime roll-forward
for both labels. Its apphost avoids macOS executable-directory SDK resolution.

Raw evidence, complete CLI snapshots, combined five-pair full results, package and
commands are under ignored `artifacts/optimisation-1/`; `notes.md` records the
failed SDK-host launch excluded from timing and the final guard verification.
Reproduce mode comparisons with the commands in [benchmark.md](benchmark.md#compile-once-switching-fixture).
Item 5 follows with source fallback kept serial and worker count defaulting to
one. Operator support remains unchanged; concurrency has its own correctness and
timing gates.

### Item 5 implementation — 2026-10-07

Slices 5A–5B are implemented behind `--mutant-mode switch --workers 2` /
`"workers": 2`. Default worker count remains one; the CLI accepts only one or two
and rejects multiple workers in source mode. Engine preparation returns an
optional batch executor and reports additive `workersRequested` / `workersUsed`.
Final results are ordered by the unchanged selected list, including never-started
skips; infrastructure errors retain precedence over incomplete execution.

[MutationWorkerPool](../src/Walker.Execution/MutationWorkerPool.cs) copies the
prepared scratch tree into two independent generations. Each worker also receives
the exact original dirty input snapshot and frozen ordinary baseline output/content
directories. Linked or external output paths reject parallel preparation. Each
worker owns its executor, working directory, temp environment and TRX paths.
Ordinary and inactive prepared generations are validated in separate parallel
phases under the original filter, every project/framework and the global budget;
sorted TRX identity signatures must match the original baseline. A failed/empty/
incompatible parallel baseline drains all probes and retains single-worker switch
execution. Fewer than two eligible mutants avoid worker setup entirely.

The pool dispatches at most two prepared attempts from contiguous supported
segments. It waits for all active attempts before each unsupported source mutant,
then resumes prepared work. Serial fallback uses the original disposable tree;
worker generations cannot be overwritten by its builds. Worker errors and local
hangs preserve the other worker and continue the queue. Global cancellation stops
dispatch, drains both process trees, retains started outcomes and cleans worker
roots before returning. The engine adds skipped outcomes for unstarted mutants.
Workers confirm kills on their independently validated ordinary DLL generation,
using the actual failing framework and original/failed-method filter intersection.
The source and single-worker confirmation paths retain explicit restored builds.
External databases, ports and test frameworks' own parallelism remain the suite's
responsibility; the pool stays opt-in.

Validation: **230 .NET tests and nine Python tests passed**. Barrier tests prove
overlap without stopwatch assertions, independent active IDs/output/content/temp/
results paths, dirty BOM/CRLF and untracked input preservation, no double dispatch,
second-framework kills through transitive references, worker-local confirmation,
interleaved source fallback and resumed prepared execution, baseline rejection,
small-batch fallback, error/hang continuation, real child cancellation/cleanup and
partial selected-order reporting/status precedence. The installed package passed
SDK 10.0.401/net10.0 and SDK 8.0.425/net8.0 targets with two frameworks: three graph
builds, one observer attachment, 14 DLL attempts, zero per-mutant builds and exact
source restoration. Walker uses .NET 10.0.12 for both target policies.

The twenty-boundary fixture's three alternating pairs compare the **same final
binary** with switch mode and workers one/two. Wall median **19.077s → 15.265s
(20.0% lower)** includes preparation **4.305s → 5.799s**. Every pair improves by
3.208–3.955s and retains all twenty IDs/kills with restored source. Sampled peak
process-tree RSS rises **513.5 MiB → 818.5 MiB**. Summed mutation test-command time
rises **11.400s → 12.102s**; overlapping commands reduce wall time, not total work.
The sampler uses `ps` every 100ms, includes the CLI and current descendants, can
miss short/reparented peaks and can double-count shared pages. Both labels use the
same sampler on an Apple M1 with eight logical CPUs and 8 GiB RAM. The fixture has
no artificial delays.

Payments retained one worker for both requested counts (zero prepared/four source
fallback) and equivalent four IDs/two kills/two survivors: **7.810s → 7.689s**
median, within noise; no concurrency benefit is claimed. Both sampled peaks are
about **642 MiB**. FluentValidation's three pairs per profile also retained one
worker (zero prepared/nine fallback): full **47.886s → 48.106s**, focused
**25.960s → 25.433s**. These are variations on the same serial path, not concurrency
gains; no fallback-workload speedup is claimed. Full sampled peaks are
**657.8 / 657.0 MiB**, focused **655.9 / 657.7 MiB**. All twelve reports retain nine
identical IDs, eight kills/one survivor, all **214 tracked C# hashes**, a clean source
checkout and zero errors/hangs/timeouts/skips. Walker's .NET 10 apphost hosts both
labels while the target retains SDK 9.0.303 with major runtime roll-forward.

**Item-5 gate passed for the eligible workload; retain explicit opt-in.** Increased
memory and shared external resources prevent a general automatic concurrency
policy. Raw evidence, package, exact commands and hashed complete snapshots are
in ignored `artifacts/optimisation-5/`. Use the worker
comparison commands in [benchmark.md](benchmark.md#compile-once-switching-fixture).

- [x] 4 — baseline metadata captured during existing builds; packed-tool and fallback tests pass; measured result recorded above.
- [x] 2 — stable member scopes with ambiguous-history fallback; bounded misses; scope/confirmation tests pass; paired measurements recorded above.
- [x] 3 — verified assembly probes/retries and runner parity tests pass; paired measurements recorded; failed speed gate keeps it opt-in.
- [x] 1 — prepared mutation switching; semantic/fallback/restoration/package tests pass; setup and fallback costs measured; remains opt-in after the default-enable gate missed.
- [x] 5 — opt-in isolated workers; deterministic reporting/cancellation/package tests pass; eligible wall-time improvement and sampled memory cost recorded; fallback cross-checks preserve outcomes/source.

After each item, report: **implemented slice → targeted tests → complete tests → equivalent outcomes/source bytes → timing evidence → enabled or opt-in → next slice**. If a fast path fails its correctness or performance gate, retain the conservative path, record the reason, and continue with independent work from the sequence.

All five items are implemented and validated. **4 and 2** are enabled by default;
**3 and 1** remain opt-in after missing their default-enable gates. **5** improves
the eligible workload and stays opt-in because concurrency requires appropriate
resources and test isolation. The earlier measurements used local SDK 8.0.425;
the [.NET 10 migration](dotnet-10.md) updates the current SDK policy. Items 1 and 5
compare identical .NET 10 binaries and target policies within each pair. Evidence
is retained under ignored `artifacts/optimisation-{1,2,3,4,5}/`; it is local evidence,
not a portable substitute for the test source and reproduction commands.
