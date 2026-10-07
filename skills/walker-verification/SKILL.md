---
name: walker-verification
description: Verify tests for changed .NET production behaviour with the Walker CLI during an AI coding-agent verification loop. Use after modifying C# production code and passing baseline build/tests, before declaring the change verified; also use to investigate whether new tests constrain a changed boundary, equality, boolean, null, or arithmetic expression. Do not use for repository-wide mutation scoring, non-.NET work, or test-only changes without a corresponding production diff.
---

# Walker verification

Walker is fast, diff-aware mutation verification for AI coding agents. Human output calls a surviving mutant a **Walker**; JSON retains `Survived` and `survived`.

Use targeted mutation verification to answer: **Do the tests distinguish meaningful alternatives to the production behaviour I changed?** A pass is evidence about the selected changed expressions, not proof of correctness or coverage of every changed behaviour.

## When to use

Run after changing C# production behaviour, especially comparisons, equality, null checks, boolean conditions, or numeric calculations, and after the normal build and relevant tests pass. Run before reporting verification complete. Re-run after improving tests or correcting an actual defect revealed by a survivor.

Do not run repeatedly without a change or a concrete investigation. Documentation, formatting, generated files, and test-only changes usually provide no eligible production mutation target. For a test improvement aimed at existing production behaviour, select a base that includes the corresponding production change. Explain when there is no suitable diff instead of manufacturing a production edit.

This tool intentionally limits work to changed production expressions. Do not substitute a full-project mutation score or promise that all behaviour has been verified.

## Prepare the scope

1. Inspect repository instructions and the user's constraints. Use the configured `walker.json` when present; explicit CLI arguments override it.
2. Identify the production `.csproj` that compiles the changed files and the test `.csproj` files that exercise that project. Inspect project references rather than guessing from directory names. Use repeated `--tests` for multiple test projects.
3. Choose the base representing the start of the agent's change. If starting a task, record `git rev-parse HEAD` before editing. For an already committed change, use its parent or the agreed branch base. `HEAD~1` is a fallback, not automatically the right scope. Do not create or rewrite commits just to run verification.
4. Inspect existing working-tree changes. The current implementation compares the merge base to the actual tracked working copy, so it includes staged and unstaged edits and preserves the bytes present before each mutation. It cannot distinguish an agent's edits from pre-existing user edits. Untracked new source files are not discovered: report this limitation rather than staging user files without authorization.
5. Prepare the isolated worktree below, then run `dotnet build <production.csproj>` and `dotnet test <tests.csproj>` for the relevant projects inside that worktree. Fix or report ordinary failures before mutation verification. The verifier also runs a baseline inside its budget to avoid attributing pre-existing failures to a mutant.

For several production projects, verify each project with its relevant tests and record each result. Keep the aggregate runtime within the user's overall limit; the timeout applies separately to each invocation.

## Prepare an isolated worktree

**Run every Walker invocation in a dedicated disposable Git worktree, including retries.** The agent's working checkout is the source of the change being verified; Walker must never mutate it. If isolation cannot be prepared, return the blocker instead of running in the source checkout.

1. Record the source repository's absolute root, current HEAD SHA, resolved base SHA, status, and current tracked diff. Capture a stable snapshot while edits are paused. Create a fresh detached worktree at that HEAD, outside the source checkout; using the default branch or base commit would omit the agent's change.
2. Transfer the current tracked file contents, including staged and unstaged edits, into the worktree. A binary patch against HEAD preserves the combined working-copy state without changing the source index. Keep the snapshot and reports in a separate artifact directory outside both checkouts. For example, from the source repository root:

   ```bash
   source_root=$(git rev-parse --show-toplevel)
   source_head=$(git rev-parse HEAD)
   base_sha=$(git rev-parse --verify "<chosen-base>^{commit}")
   artifact_dir=$(mktemp -d "${TMPDIR:-/tmp}/walker-artifacts.XXXXXX")
   worktree_path="$artifact_dir/worktree"
   git -C "$source_root" diff --binary --no-ext-diff --no-textconv HEAD -- > "$artifact_dir/source.patch"
   git -C "$source_root" worktree add --detach "$worktree_path" "$source_head"
   git -C "$worktree_path" apply --index --binary "$artifact_dir/source.patch"
   ```

   Applying with `--index` in the disposable worktree keeps newly staged source files tracked there; the source index remains untouched. Check each command's success before continuing. Skip patch application when the patch is empty. Resolve the base before switching directories and use `base_sha` for verification so relative refs retain their original meaning.
3. Copy required untracked source, tests, and configuration into the same repository-relative paths, preserving contents. Use a NUL-delimited inventory such as `git ls-files --others --exclude-standard -z`. Recreate ignored local configuration only when required for the build; restore dependencies and build outputs inside the worktree. Keep writable files and build paths independent of the source checkout; inspect symlinks, submodules, and external project references for paths back into it. Report unsupported isolation rather than sharing writable source or output directories.
4. Compare the worktree's tracked diff against HEAD with the captured patch and verify copied inputs match the snapshot before running. The source checkout's HEAD, index, and file contents must remain untouched. The CLI still does not discover untracked production files; copying them supports builds but does not add mutation coverage. Report that gap.

Run builds, tests, tool restoration, and Walker from `worktree_path`, using project paths and configuration from that snapshot. Use an absolute path for a compiled verifier located elsewhere. After intentional test or production fixes in the agent's source checkout, prepare a fresh snapshot and worktree for the next run with the same base. Never copy mutation-run source changes back into the agent's checkout.

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

dotnet <verifier-repo>/src/Walker.Cli/bin/Debug/net10.0/Walker.Cli.dll verify \
  --base <base-ref-or-sha> \
  --project src/Payments/Payments.csproj \
  --tests tests/Payments.Tests/Payments.Tests.csproj \
  --timeout 60 --max-mutants 20 --format json
```

Run verification from inside the **isolated target worktree**. CLI project and test paths are relative to the current directory; configured exclude globs match repository-relative paths. Do not assume a package named `Walker.Cli` on a public feed is this implementation. Use the supplied source or a trusted installed tool.

If .NET, Git, package restore, or the verifier is unavailable, report the blocker. Do not claim mutation verification passed.

The current Walker CLI requires the .NET 10 runtime; building its source requires the repository's .NET 10 SDK. Target projects retain their own SDK/framework policy, including SDK 8 or 9. When using separate installations, run the CLI apphost (`Walker.Cli`, or `Walker.Cli.exe` on Windows) with `DOTNET_ROOT` pointing to the .NET 10 installation and put the target SDK on `PATH`. On macOS, hosting the DLL through a separate `dotnet` executable can cause child commands to resolve from that executable's directory and miss the target SDK. Treat SDK-resolution failure as infrastructure failure; preserve the target's `global.json`.

## Choose an execution mode

Keep the configured mode unless the user requests a change or a measured comparison justifies one. The default is `--mutant-mode source` (`"mutantMode": "source"`). Source mode automatically uses baseline metadata reuse and adaptive preferred-test planning; they need no extra flags. Preferred groups must pass on original source, and passing preferred attempts fall back to the complete requested scope before a mutant can survive.

- `--mutant-mode switch` / `"mutantMode": "switch"` is experimental. It prepares selected built-in numeric relational boundaries in a scratch copy, verifies the complete requested baseline again with no mutation active, then activates one mutant per fresh test process over the requested scope. Consider it for several eligible boundaries when repeated builds dominate. Preparation and confirmation consume the same timeout. Unsupported operators, decimal/nullable/dynamic operands, expression trees, overloaded conversions, custom source generators or unproved build layouts retain source mutation. Failed preparation also falls back; it is not a batch of compile errors. Keep the dedicated outer worktree because source fallback can still mutate there.
- `--compiled-tests` / `"compiledTests": true` is a separate experimental source-mode option. It probes project/DLL baseline parity and can reuse verified DLLs for full retries after preferred tests. Its added setup regressed the measured workloads, so leave it off unless configured or testing that path. Switch mode already uses the assembly runner; combining these options is rejected.

- `--workers 2` / `"workers": 2` is an experimental switch-mode option; the default is one. Use it only when the suite's databases, ports and other external resources support concurrent test hosts. Workers own separate output/content copies, working directories, temp and TRX paths. Ordinary and inactive prepared baselines run concurrently and must match the original test identities. Fewer than two eligible mutants or a rejected parallel baseline retain one worker. Source fallback waits for active workers and stays serial. Keep the dedicated outer worktree.

Switching improved the eligible twenty-boundary fixture, but did not improve unsupported workloads and repeatedly slowed Payments. Choose experimental options from measured eligibility and total time, including preparation, rather than assuming they make every run faster. Two workers also increase concurrent memory demand. For optimisation work in Walker's source checkout, read `docs/mutation-optimisation-plan.md` and `docs/performance.md` for the current gates, operator support and measurements. These repository documents are not part of a copied standalone skill. Concurrent CLI invocations still require separate worktrees.

## Execute and capture JSON

Default to a 60-second budget and at most 20 mutants unless repository configuration or the user specifies another limit. JSON is the interface for reasoning and automation. Keep process logs out of the report stream.

```bash
report_path="$artifact_dir/report.json"
log_path="$artifact_dir/process.log"
if (cd "$worktree_path" && walker verify \
  --base "$base_sha" \
  --project src/Payments/Payments.csproj \
  --tests tests/Payments.Tests/Payments.Tests.csproj \
  --timeout 60 --max-mutants 20 --format json) > "$report_path" 2> "$log_path"; then
  verification_exit=0
else
  verification_exit=$?
fi
cat "$report_path"
```

Substitute the compiled CLI or local-tool invocation when necessary. Preserve the exit code from the verifier itself; do not pipe it through a command that hides that code. Read the JSON only after the process exits and source restoration finishes. Preserve the JSON, stderr log, and exit code outside the disposable worktree, including on failure. A missing or invalid JSON report is an execution failure; return the exit code and logs rather than inferring success.

For slow test projects, choose a relevant subset with `--filter "FullyQualifiedName~EpsDebitTests"` (or `"filter"` in `walker.json`; CLI overrides config). Both baseline and mutants use exactly that filter. Inspect `testFilter` in JSON and report the limited test scope. A filter matching no executed tests is `TestError`, never success.

Check `schemaVersion` before relying on fields; this implementation uses version 1. Version 1 also contains the additive `hung` count, `Hung` outcome and `unresolvedArithmetic` count (arithmetic candidates whose operand types could not be resolved and were not mutated). Inspect `files` for changed production files with zero `mutantsDiscovered` or zero `mutantsSelected`. Explicitly report these gaps even when the run passes; use normal focused tests/review to investigate them. Text output names files with no candidates. Inspect `status`, `error`, `mutantsDiscovered`, `mutantsSelected`, `mutantsExecuted`, outcome counts, `survivors`, and `results`. Timings show discovery, baseline, build, test, and per-mutant cost. Selection is deterministic and bounded; candidates outside `--max-mutants` are not executed and are not counted as budget-skipped selected mutants.

For switch runs, also read the additive `preparation` object: `durationMs`, `supported`, `fallback` and `detail`. It is normally null in source mode. Supported/fallback counts describe preparation eligibility, not how many mutants completed; use `mutantsExecuted` and outcomes for execution evidence. Zero supported means no switched execution was available; confirm that source mutants actually completed before claiming evidence. Include preparation in reported runtime and record these counts when assessing speed. Zero per-mutant `buildMs` alone does not prove compilation was avoided: source-mode `dotnet test` includes build work in `testMs`.

Also record `workersRequested` and `workersUsed`. A request for two does not prove the pool was available; inspect preparation detail when only one was used. JSON results retain selected order despite concurrent completion. Cancellation drains running children before workspace cleanup; never-started selected mutants are skipped.

## Interpret the result

| Exit | Meaning | Agent action |
| --- | --- | --- |
| 0 | Selected mutants killed; verification passed | Report the bounded evidence alongside ordinary build/test results. |
| 1 | At least one survivor in a completed run | Investigate each meaningful survivor as described below. |
| 2 | Build, test, discovery, or infrastructure error | Inspect `error` and result `detail`. Fix or report the execution problem; do not infer missing assertions. |
| 3 | Verification incomplete | Report missing evidence, completed results, and timeout/skips. Do not call this a pass. |

If `error` says the baseline exhausted the budget before any mutant started, inspect `timings.baselineMs`. Narrow the relevant suite with `--filter` or increase `--timeout`; skipped mutants have no execution evidence. Baseline failure and cancellation also retain elapsed baseline time.

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
6. If equivalent or intentionally accepted, record the reason and discriminating-input analysis. The CLI has no built-in acceptance flag; classifications live outside execution and do not turn exit 1 into exit 0.
7. Run normal build/tests again, then repeat verification with the same base and comparable bounds.

Example: if equality is explicitly permitted, add a test asserting `CanPurchase(10m, 10m)` is true. Do not weaken or rewrite the production comparison to satisfy the verifier. If equality is intentionally unspecified, record that decision rather than inventing an equality contract.

## Source safety and operating limits

Source-mode execution modifies one worktree source file at a time, then restores its exact original bytes in cleanup. Supported switch mutants execute from prepared scratch outputs; unsupported mutants use the source path. Switch mode checks input hashes and refuses stale prepared execution if build inputs change. The CLI handles Ctrl+C with cancellation; wait for it and its child build/test processes to exit before inspecting source or removing the worktree. Keep the snapshot fixed while verification runs; make intentional fixes in the source checkout between runs. Give concurrent invocations separate worktrees and output paths.

Normal process errors, test failures, cancellation, and budget expiry should restore source. Forced process termination or host failure can leave mutations behind in the disposable worktree. Compare it against the captured snapshot after execution and report restoration failures; never use its residual diff as a proposed fix. Keep an abnormal run's worktree and artifacts for investigation. Recovery must not discard or restore files in the agent's source checkout.

V1 runs configured test projects for each selected mutant until a project confirms a test failure; survivors must pass all configured projects. The baseline validates all projects before mutation. It has no coverage-based test selection, equivalent-mutant detection, invocation removal, or numeric-constant return mutation. Conditions and resolved boolean return expressions can be negated; non-null patterns are negated only without variable bindings. Overlapping spans prefer high-value operator mutations over broad condition negation. Inspect `unresolvedBoolean` as well as `unresolvedArithmetic`; omitted or unresolved shapes are evidence gaps. Arithmetic mutation is conservative when operand types cannot be resolved. Do not interpret an omitted mutation as evidence that tests cover it.

## Report back

Return results to the calling agent on every outcome, including setup failures, survivors, timeout, cancellation, invalid JSON, and restoration failure. If verification is delegated, the worker must return the report contents or an accessible artifact path, process exit code, stderr diagnostics, source HEAD and base SHA, worktree path, and scope to its parent before finishing. The calling agent must read the report and act on it; a report left only in a disposable worktree is not a completed handoff.

Keep feedback concise: base and project scope; effective mode and experimental options; executed/selected/discovered counts; killed, survived, errored, timed out, and skipped counts; runtime; preparation cost and supported/fallback counts when present; survivor locations and changed behaviour; investigation or limitations. Include ordinary test results separately.

Use wording such as:

> Mutation verification completed: 8/8 selected mutants executed, 7 killed, 1 survived. `PaymentService.CanPurchase` still passes tests when `>=` becomes `>`. I added the equality case required by the purchase contract and re-ran verification; that mutant is now killed.

For incomplete verification, say so explicitly and identify what remains unverified. Never call a survivor a production bug without investigating its contract.

After the calling agent has consumed the result, retain the report and logs at the returned artifact paths and remove only the disposable worktree created for this run with `git worktree remove <worktree-path>`. Check its diff against the snapshot first. If removal refuses because it is dirty, retain it and report the path; do not force cleanup or discard changes. Never remove a pre-existing or unrelated worktree.
