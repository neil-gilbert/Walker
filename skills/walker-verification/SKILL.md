---
name: walker-verification
description: Verify .NET behaviour with Walker during an AI coding-agent loop. Use after C# production changes and passing ordinary tests, to investigate survivors, verify proposed test improvements, or challenge a requirement or past bug. Do not use for repository-wide mutation scoring or non-.NET work.
---

# Walker verification

Walker is fast, diff-aware mutation verification for AI coding agents. Human output calls a surviving mutant a **Walker**; JSON retains `Survived` and `survived`.

Use targeted mutation verification to answer: **Do the tests distinguish meaningful alternatives to the production behaviour I changed?** A pass is evidence about the selected changed expressions, not proof of correctness or coverage of every changed behaviour.

## When to use

Run after changing C# production behaviour, especially comparisons, equality, null checks, boolean conditions, or numeric calculations, and after the normal build and relevant tests pass. Run before reporting verification complete. Re-run after improving tests or correcting an actual defect revealed by a survivor.

Do not run repeatedly without a change or a concrete investigation. Documentation, formatting, generated files, and test-only changes usually provide no eligible production mutation target. For ordinary test improvement verification, select a base that includes the corresponding production change. With a specific requirement or past bug, an explicit challenge can target unchanged code. Explain when neither scope is available instead of manufacturing a production edit.

Ordinary verification limits work to changed production expressions; explicit challenges can target unchanged production source. Report evidence for selected faults rather than promising all behaviour has been verified.

## Choose optional workflows

Check the installed CLI's `--help` before using these flags. Keep ordinary bounded verification as the default; choose additional work for the task at hand.

| Situation | Optional CLI choice |
| --- | --- |
| Remote/container execution, long or quiet tests, or the user wants live feedback | Add `--progress`. It emits phase changes and flushed heartbeats every 15 seconds to stderr, including elapsed time, remaining budget and active build/test commands. |
| Survivors need investigation | Add `--investigate` for grouped survivor IDs and deterministic test hints. Review each original/replacement; grouping does not prove equivalence or severity. |
| A known contract suggests a focused test improvement | Use `--test-patch <manifest.json>` with native `--isolate` to compare current tests with proposed tests before applying them. |
| A requirement or historical bug needs a specific fault scenario, including unchanged code | Use `--challenge <manifest.json>` with native `--isolate`; choose `--base HEAD` when no production diff is needed. |

For test-patch verification or explicit challenges, read [optional workflow manifests and evidence](references/optional-workflows.md). Both use source mode and share the invocation's time budget. They may be combined. The agent supplies contract reasoning and proposed source changes; the CLI performs execution checks without calling a model.

With `--progress`, preserve stdout as the final JSON report and stream stderr to the caller. Redirecting stderr only to a file hides heartbeats from a remote inactivity watchdog: use the execution tool's separate live stderr/log capture when available. Set `--timeout` within the caller's hard runtime limit, leaving time for cancellation and cleanup. A heartbeat reports liveness, not an ETA or proof that a test is advancing; the timeout still bounds hung work.

## Prepare the scope

1. Inspect repository instructions and the user's constraints. Use the configured `walker.json` when present; explicit CLI arguments override it.
2. Identify the production `.csproj` that compiles the changed files and the test `.csproj` files that exercise that project. Inspect project references rather than guessing from directory names. Use repeated `--tests` for multiple test projects.
3. Choose the base representing the start of the agent's change. If starting a task, record `git rev-parse HEAD` before editing. For an already committed change, use its parent or the agreed branch base. `HEAD~1` is a fallback, not automatically the right scope. Do not create or rewrite commits just to run verification.
4. Inspect existing working-tree changes. The current implementation compares the merge base to the actual tracked working copy, so it includes staged and unstaged edits and preserves the bytes present before each mutation. It cannot distinguish an agent's edits from pre-existing user edits. Untracked new source files are not discovered: report this limitation rather than staging user files without authorization.
5. Ensure normal build and relevant tests passed, then use isolation below. Walker runs a fresh baseline inside the captured snapshot and its budget; baseline failures are execution problems, not kills.

For several production projects, verify each project with its relevant tests and record each result. Keep the aggregate runtime within the user's overall limit; the timeout applies separately to each invocation.

## Prepare isolation

Prefer native `--isolate` when the installed tool's help lists it. Invoke it from the source checkout: Walker captures HEAD, the resolved base, staged and unstaged bytes, and nonignored untracked inputs into a fresh private worktree. Keep inputs steady until stderr reports “Isolated snapshot ready”; subsequent edits in the source checkout do not affect that run. Every invocation owns a separate snapshot and output directory, including switch-mode source fallback.

Read `isolation` in the final JSON for the snapshot fingerprint, effective scope, durable report/log paths and cleanup state. Normal cleanup removes the owned worktree before emitting the report. Preserve a retained worktree and report its path when cleanup fails. A pending restore journal in the source checkout is a blocker; isolation never recovers or overwrites it.

Native isolation conservatively refuses symlinks, submodules, external or ignored required inputs, explicit wildcard item paths, conditional/custom MSBuild targets/imports, and custom output paths. It isolates repository files and conventional build outputs; arbitrary test code and shared external resources are not sandboxed. Do not silently retry without isolation. For older Walker versions or unsupported layouts, follow [manual isolation](references/manual-isolation.md) or report the blocker.

## Locate the tool

Prefer the repository's installed local tool or an available `walker` command:

```bash
walker --help
# For a configured local .NET tool:
dotnet tool run walker -- --help
```

If the tool has not been installed but its source is available, build the verifier once, then invoke its compiled CLI:

```bash
dotnet build <verifier-repo>/src/Walker.Cli/Walker.Cli.csproj

dotnet <verifier-repo>/src/Walker.Cli/bin/Debug/net10.0/Walker.Cli.dll verify --isolate \
  --base <base-ref-or-sha> \
  --project src/Payments/Payments.csproj \
  --tests tests/Payments.Tests/Payments.Tests.csproj \
  --timeout 60 --max-mutants 20 --format json
```

With native isolation, run from the source checkout. For manual isolation, run from its dedicated worktree. CLI project and test paths are relative to the current directory; configured exclude globs match repository-relative paths. Do not assume a package named `Walker.Cli` on a public feed is this implementation. Use the supplied source or a trusted installed tool.

**Check the tool's capabilities before relying on this skill's options.** Run `verify --help` and compare it with the options in this skill. Without `--filter`, every mutant runs the whole test project, which is usually not viable for integration suites.

Look for every install, not only the first one you find:

- **Global tool:** `dotnet tool list -g`, binary in `~/.dotnet/tools/walker` (often not on `PATH`). It can be older than this skill. For example, an older `walker.cli` 0.1.0 build can lack `--filter`, `--confirm-kills`, `--mutant-mode` or `--workers`.
- **Local tool manifests:** a newer version can be pinned in another repository's `dotnet-tools.json` (for example the Walker source repository). .NET resolves local tools from the current directory. Walker runs from the target checkout for native isolation or its dedicated worktree for manual isolation, so `dotnet tool run walker` cannot find a tool that is pinned in another repository.
- **Source:** the Walker repository's `src/Walker.Cli` and its packed `artifacts/Walker.Cli.<version>.nupkg`.

If the global tool is older than a local or packed version, tell the user. Do not call the tool "outdated" without naming which install you checked. To use the newer version from the target checkout or manual worktree, either:

- run the apphost built from the same trusted source (`<walker-repo>/src/Walker.Cli/bin/Debug/net10.0/Walker.Cli`), or
- with the user's approval, update the global tool from the packed artifact: `dotnet tool update -g walker.cli --add-source <walker-repo>/artifacts --version <version>`.

If .NET, Git, package restore, or the verifier is unavailable, report the blocker. Do not claim mutation verification passed.

The current Walker CLI requires the .NET 10 runtime; building its source requires the repository's .NET 10 SDK. Target projects retain their own SDK/framework policy, including SDK 8 or 9. When using separate installations, run the CLI apphost (`Walker.Cli`, or `Walker.Cli.exe` on Windows) with `DOTNET_ROOT` pointing to the .NET 10 installation and put the target SDK on `PATH`. On macOS, hosting the DLL through a separate `dotnet` executable can cause child commands to resolve from that executable's directory and miss the target SDK. Treat SDK-resolution failure as infrastructure failure; preserve the target's `global.json`.

## Choose an execution mode

Keep the configured mode unless the user requests a change or a measured comparison justifies one. The default is `--mutant-mode source` (`"mutantMode": "source"`). Source mode automatically uses baseline metadata reuse and adaptive preferred-test planning; they need no extra flags. Preferred groups must pass on original source, and passing preferred attempts fall back to the complete requested scope before a mutant can survive.

- `--mutant-mode switch` / `"mutantMode": "switch"` is experimental. It prepares selected built-in numeric relational boundaries in a scratch copy and verifies the complete requested baseline again with no mutation active. Simple stateless boundaries with assertion-only tests and complete, disjoint per-test coverage can share a fresh-process batch. Missing/ambiguous coverage, changed paths and batch errors retain individual retries; other prepared boundaries run individually. `--confirm-kills` uses individual execution. Consider switching for several eligible boundaries when repeated builds dominate. Preparation and confirmation consume the same timeout. Unsupported operators, decimal/nullable/dynamic operands, expression trees, overloaded conversions, custom source generators or unproved build layouts retain source mutation. Failed preparation also falls back; it is not a batch of compile errors. Keep native isolation or the dedicated manual worktree because source fallback can still mutate there.
- `--compiled-tests` / `"compiledTests": true` is a separate experimental source-mode option. It probes project/DLL baseline parity and can reuse verified DLLs for full retries after preferred tests. Its added setup regressed the measured workloads, so leave it off unless configured or testing that path. Switch mode already uses the assembly runner; combining these options is rejected.

- `--workers 2` / `"workers": 2` is an experimental switch-mode option; the default is one. Use it only when the suite's databases, ports and other external resources support concurrent test hosts. Workers own separate output/content copies, working directories, temp and TRX paths. Ordinary and inactive prepared baselines run concurrently and must match the original test identities. Fewer than two eligible mutants or a rejected parallel baseline retain one worker. Source fallback waits for active workers and stays serial. Keep native isolation or the dedicated manual worktree.

Switching improved the eligible twenty-boundary fixture, but did not improve unsupported workloads and repeatedly slowed Payments. Choose experimental options from measured eligibility and total time, including preparation, rather than assuming they make every run faster. Two workers also increase concurrent memory demand. For optimisation work in Walker's source checkout, read `docs/mutation-optimisation-plan.md` and `docs/performance.md` for the current gates, operator support and measurements. These repository documents are not part of a copied standalone skill. Native isolation gives concurrent invocations separate worktrees; manual invocations must do the same.

## Execute and capture JSON

Default to a 60-second budget and at most 20 mutants unless repository configuration or the user specifies another limit. JSON is the interface for reasoning and automation. Keep process logs out of the report stream.

```bash
artifact_dir=$(mktemp -d "${TMPDIR:-/tmp}/walker-reports.XXXXXX")
base_sha=$(git rev-parse --verify "<chosen-base>^{commit}")
report_path="$artifact_dir/report.json"
log_path="$artifact_dir/process.log"
if walker verify --isolate \
  --base "$base_sha" \
  --project src/Payments/Payments.csproj \
  --tests tests/Payments.Tests/Payments.Tests.csproj \
  --timeout 60 --max-mutants 20 --format json > "$report_path" 2> "$log_path"; then
  verification_exit=0
else
  verification_exit=$?
fi
cat "$report_path"
```

Substitute the compiled CLI or local-tool invocation when necessary. Preserve the exit code from the verifier itself; do not pipe it through a command that hides that code. Read the JSON only after the process exits and source restoration finishes. Preserve the JSON, stderr log, and exit code outside the disposable worktree, including on failure. A missing or invalid JSON report is an execution failure; return the exit code and logs rather than inferring success.

For slow test projects, choose a relevant subset with `--filter "FullyQualifiedName~EpsDebitTests"` (or `"filter"` in `walker.json`; CLI overrides config). Both baseline and mutants use exactly that filter. Inspect `testFilter` in JSON and report the limited test scope. A filter matching no executed tests is `TestError`, never success.

**Default filter when none is configured: the test classes changed in the diff.** Preserve configured filters and the requested scope. Otherwise, unless the user asks for a wider scope, derive the filter from `git diff --name-only <base> -- <test dirs>` and join the changed classes with `|` (for example `FullyQualifiedName~IdealWeroBankPreselectTests`). Users expect a run of a few minutes. A broad filter such as `FullyQualifiedName~Ideal` can match 100+ integration tests, and each mutant rebuilds and runs all of them.

**Size the budget from a measured baseline.** The 60-second default is for unit tests. For integration tests, time the filtered baseline (`dotnet test --filter ...`) in the isolated worktree first, then set `--timeout` to about `baseline × (expected mutants + 1) × 1.5`. Example: a 16-test class took ~25 s per mutant, and 7–9 mutants finished in ~4 minutes; a 124-test filter took ~90 s per mutant (~30 minutes in total). Tell the user the expected duration before a long run.

On macOS, wrap long runs in `caffeinate -dimu` so that sleep does not corrupt Testcontainers runs into bogus failures.

Check `schemaVersion` before relying on fields; this implementation uses version 1. Version 1 also contains the additive `hung` count, `Hung` outcome and `unresolvedArithmetic` count (arithmetic candidates whose operand types could not be resolved and were not mutated). Inspect `files` for changed production files with zero `mutantsDiscovered` or zero `mutantsSelected`. Explicitly report these gaps even when the run passes; use normal focused tests/review to investigate them. Text output names files with no candidates. Inspect `status`, `error`, `mutantsDiscovered`, `mutantsSelected`, `mutantsExecuted`, outcome counts, `survivors`, and `results`. Timings show discovery, baseline, build, test, and per-mutant cost. Selection is deterministic and bounded; candidates outside `--max-mutants` are not executed and are not counted as budget-skipped selected mutants.

For switch runs, also read the additive `preparation` object: `durationMs`, `supported`, `fallback` and `detail`. It is normally null in source mode. Supported/fallback counts describe preparation eligibility, not how many mutants completed; use `mutantsExecuted` and outcomes for execution evidence. Zero supported means no switched execution was available; confirm that source mutants actually completed before claiming evidence. Include preparation in reported runtime and record these counts when assessing speed. Zero per-mutant `buildMs` alone does not prove compilation was avoided: source-mode `dotnet test` includes build work in `testMs`.

Also record `workersRequested` and `workersUsed`. Effective coverage batches can use one worker even when two were requested, avoiding the second generation and its parallel probes. Inspect preparation and result detail to distinguish batching from a failed parallel preparation. Batched result `durationMs` and `testMs` repeat the shared batch cost; use overall elapsed time for comparisons rather than summing them. JSON results retain selected order despite concurrent completion. Cancellation drains running children before workspace cleanup; never-started selected mutants are skipped.

## Interpret the result

| Exit | Meaning | Agent action |
| --- | --- | --- |
| 0 | Selected mutants killed; verification passed | Report the bounded evidence alongside ordinary build/test results. |
| 1 | At least one survivor in a completed run | Investigate each meaningful survivor as described below. |
| 2 | Build, test, discovery, or infrastructure error | Inspect `error` and result `detail`. Fix or report the execution problem; do not infer missing assertions. |
| 3 | Verification incomplete | Report missing evidence, completed results, and timeout/skips. Do not call this a pass. |

Use run-level and per-result `diagnostics[].code`, `phase` and `actions` before reading human messages. Unknown codes or actions fall back to status, exit code and outcomes; do not execute action strings as shell commands. `baseline_budget_exhausted` means inspect `timings.baselineMs`; `budget_exhausted` can include isolation setup. For either, inspect the effective scope and choose a justified `--filter` or larger `--timeout`; skipped mutants have no execution evidence. Baseline failure and cancellation also retain elapsed baseline time.

Other recovery codes: `invalid_argument`/`invalid_configuration` require correcting scope; `no_executed_tests` requires checking the filter and test discovery; `unknown_mutant` requires rediscovery; `source_changed`/`snapshot_changed` requires a fresh snapshot. `restore_conflict`, `source_recovery_pending`, and `isolation_cleanup_failed` require inspecting retained evidence without overwriting user changes. `isolation_unsupported` requires supported isolation or a blocker. Never broaden a test filter automatically to conceal missing evidence.

A zero-candidate run is incomplete because it provides no mutation evidence. A timeout or cancellation is incomplete even if completed mutants were killed. An infrastructure error takes precedence over incompleteness; incomplete runs may also contain survivors worth investigating.

Outcome meanings:

- `Killed`: inspect `failingTests` (capped at 10 names). With slow/flaky integration suites, consider `--confirm-kills` or config `"confirmKills": true`: it reruns the failed test methods intersected with the same filter. Source and single-worker paths rebuild production after exact-byte restoration; parallel workers use their separate, validated ordinary DLL generation in the actual failing framework. This adds work within the global budget; parameterized methods may run multiple rows. Repeated failures on unmutated source are `TestError` with `killConfirmed: false`; successful confirmation sets `killConfirmed: true`. Missing/unsupported identities or over 10 failures prevent confirmation and yield `TestError`. A confirmation reduces false kills but does not eliminate flakiness. Without confirmation, executed tests failed with the mutation present. Baseline tests must have passed.
- `Survived`: executed relevant tests still passed.
- `CompileError`: the production mutation did not build. This is not a kill.
- `TestError`: tests did not execute normally, had no executed tests, or a test-project build failed. This is not a kill.
- `Hung`: build and tests exceeded the per-mutant hang limit (3× baseline + 5s), usually an infinite loop. Counted as detected, like `Killed`; the run continues.
- `TimedOut`: current execution was cancelled or exceeded its available budget.
- `Skipped`: a selected mutant was not executed, usually because the budget expired or the baseline failed.

## Investigate survivors

**Never change production code merely to kill a mutant. Never instruct an agent to “kill all mutants.”** A survivor is a question about the intended contract, not a diagnosis of a defect.

For each survivor:

1. Read the reported file, line, member, original expression, and replacement. Inspect surrounding production code and tests.
2. Identify an input that distinguishes the two expressions. For `balance >= price` becoming `balance > price`, the discriminating input is `balance == price`.
3. Determine the intended behaviour from the user request, requirements, existing contract, and domain rules. Do not invent a rule just because it would kill the mutant.
4. Decide whether the cause is missing test coverage, a weak assertion, an untested boundary condition, an equivalent mutation, intentionally unspecified behaviour, or incorrect production behaviour.
5. Add a focused test with a meaningful assertion when the contract specifies a missing behaviour. Correct production code only when evidence establishes that it violates that contract.
   - **Log-only survivors:** a mutant that changes only *whether or what* is logged (for example the condition around a warning, or a value placed in a log field) cannot be killed by tests that assert only HTTP responses and PSP payloads. Classify it as log-only. Do not add test-only production hooks. Offer a test-side log-capture helper (for example an `ILoggerProvider` registered in the test host) as a separate decision for the user.
   - **Compare scopes to classify survivors:** if a wider filter run (or an earlier, cancelled run) killed a mutant that survives the changed-class filter, the gap is in the changed tests, and other suites already constrain the behaviour. Usually the fix is a focused test in the changed class for that contract (for example, the toggle-off path).
6. If equivalent or intentionally accepted, record the reason and discriminating-input analysis. The CLI has no built-in acceptance flag; classifications live outside execution and do not turn exit 1 into exit 0.
7. Run normal build/tests again. If help lists `--mutant`, focus the rerun with repeated `--mutant <survivor-id>` using the same base, project and test scope. Each run still performs a fresh baseline and full requested tests for survival. IDs come from current discovery: test-only edits preserve them; production span/expression changes can invalidate them. Unknown IDs and unique selections exceeding `--max-mutants` fail before baseline. Do not reinterpret an old ID as a line number. Older versions without this flag require an ordinary bounded rerun.

```bash
walker verify --isolate --base "$base_sha" \
  --project src/Payments/Payments.csproj \
  --tests tests/Payments.Tests/Payments.Tests.csproj \
  --mutant <survivor-id> --timeout 60 --max-mutants 20 --format json
```

When present, read `selection.kind` and `selection.requestedIds`. A focused pass proves only those selected IDs. After improving tests, run the ordinary bounded selection again to check the broader changed scope.

Example: if equality is explicitly permitted, add a test asserting `CanPurchase(10m, 10m)` is true. Do not weaken or rewrite the production comparison to satisfy the verifier. If equality is intentionally unspecified, record that decision rather than inventing an equality contract.

## Source safety and operating limits

Source-mode execution modifies one worktree source file at a time, then restores its exact original bytes in cleanup. Supported switch mutants execute from prepared scratch outputs; unsupported mutants use the source path. Switch mode checks input hashes and refuses stale prepared execution if build inputs change. The CLI handles Ctrl+C with cancellation; wait for it and its child build/test processes to exit before inspecting source or removing the worktree. Keep the verification snapshot fixed; native isolation permits intentional source-checkout edits after capture. Give concurrent invocations separate worktrees and output paths.

To stop a run early (for example, to narrow the filter), send SIGINT to the Walker CLI process itself, not to a wrapper such as `caffeinate` or the shell: `kill -INT <walker-pid>`. Wait for the process to exit; it restores source and writes a JSON report with exit code 3 (incomplete). Keep that report: its completed results are still useful evidence for classifying survivors. For manual isolation, confirm the worktree matches the snapshot before you start again. For native isolation, inspect the returned cleanup state and start a fresh session.

Normal process errors, test failures, cancellation, and budget expiry should restore source. Forced process termination or host failure can leave mutations behind in the disposable worktree. Compare it against the captured snapshot after execution and report restoration failures; never use its residual diff as a proposed fix. Keep an abnormal run's worktree and artifacts for investigation. Recovery must not discard or restore files in the agent's source checkout.

V1 runs configured test projects for each selected mutant until a project confirms a test failure; survivors must pass all configured projects. The baseline validates all projects before mutation. It has no coverage-based test selection, equivalent-mutant detection, invocation removal, or numeric-constant return mutation. Conditions and resolved boolean return expressions can be negated; non-null patterns are negated only without variable bindings. Overlapping spans prefer high-value operator mutations over broad condition negation. Inspect `unresolvedBoolean` as well as `unresolvedArithmetic`; omitted or unresolved shapes are evidence gaps. Arithmetic mutation is conservative when operand types cannot be resolved. Do not interpret an omitted mutation as evidence that tests cover it.

## Report back

Return results to the calling agent on every outcome, including setup failures, survivors, timeout, cancellation, invalid JSON, and restoration failure. If verification is delegated, the worker must return the report contents or an accessible artifact path, process exit code, stderr diagnostics, source HEAD and base SHA, worktree path, and scope to its parent before finishing. The calling agent must read the report and act on it; a report left only in a disposable worktree is not a completed handoff.

Keep feedback concise: base and project scope; effective mode and experimental options; executed/selected/discovered counts; killed, survived, errored, timed out, and skipped counts; runtime; preparation cost and supported/fallback counts when present; survivor locations and changed behaviour; investigation or limitations. Include ordinary test results separately.

Use wording such as:

> Mutation verification completed: 8/8 selected mutants executed, 7 killed, 1 survived. `PaymentService.CanPurchase` still passes tests when `>=` becomes `>`. I added the equality case required by the purchase contract and re-ran verification; that mutant is now killed.

For incomplete verification, say so explicitly and identify what remains unverified. Never call a survivor a production bug without investigating its contract.

Retain the returned report and logs. Native isolation already performs ownership-checked cleanup: `removed` needs no further removal; `retained` requires inspection and must not be force-deleted. Forced termination can leave a private worktree and readable `session.json`, captured inputs and process log without a final report; source checkout bytes remain independent. For manual isolation, follow its reference cleanup instructions.
