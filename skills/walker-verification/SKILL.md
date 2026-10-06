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
5. Run `dotnet build <production.csproj>` and `dotnet test <tests.csproj>` for the relevant projects. Fix or report ordinary failures before mutation verification. The verifier also runs a baseline inside its budget to avoid attributing pre-existing failures to a mutant.

For several production projects, verify each project with its relevant tests and record each result. Keep the aggregate runtime within the user's overall limit; the timeout applies separately to each invocation.

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

dotnet <verifier-repo>/src/Walker.Cli/bin/Debug/net8.0/Walker.Cli.dll verify \
  --base <base-ref-or-sha> \
  --project src/Payments/Payments.csproj \
  --tests tests/Payments.Tests/Payments.Tests.csproj \
  --timeout 60 --max-mutants 20 --format json
```

Run verification from inside the **target** Git repository. CLI project and test paths are relative to the current directory; configured exclude globs match repository-relative paths. Do not assume a package named `Walker.Cli` on a public feed is this implementation. Use the supplied source or a trusted installed tool.

If .NET, Git, package restore, or the verifier is unavailable, report the blocker. Do not claim mutation verification passed.

## Execute and capture JSON

Default to a 60-second budget and at most 20 mutants unless repository configuration or the user specifies another limit. JSON is the interface for reasoning and automation. Keep process logs out of the report stream.

```bash
report_path=$(mktemp "${TMPDIR:-/tmp}/walker-report.XXXXXX")
if walker verify \
  --base <base-ref-or-sha> \
  --project src/Payments/Payments.csproj \
  --tests tests/Payments.Tests/Payments.Tests.csproj \
  --timeout 60 --max-mutants 20 --format json > "$report_path"; then
  verification_exit=0
else
  verification_exit=$?
fi
cat "$report_path"
```

Substitute the compiled CLI or local-tool invocation when necessary. Preserve the exit code from the verifier itself; do not pipe it through a command that hides that code. Read the JSON only after the process exits and source restoration finishes.

For slow test projects, choose a relevant subset with `--filter "FullyQualifiedName~EpsDebitTests"` (or `"filter"` in `walker.json`; CLI overrides config). Both baseline and mutants use exactly that filter. Inspect `testFilter` in JSON and report the limited test scope. A filter matching no executed tests is `TestError`, never success.

Check `schemaVersion` before relying on fields; this implementation uses version 1. Version 1 also contains the additive `hung` count, `Hung` outcome and `unresolvedArithmetic` count (arithmetic candidates whose operand types could not be resolved and were not mutated). Inspect `files` for changed production files with zero `mutantsDiscovered` or zero `mutantsSelected`. Explicitly report these gaps even when the run passes; use normal focused tests/review to investigate them. Text output names files with no candidates. Inspect `status`, `error`, `mutantsDiscovered`, `mutantsSelected`, `mutantsExecuted`, outcome counts, `survivors`, and `results`. Timings show discovery, baseline, build, test, and per-mutant cost. Selection is deterministic and bounded; candidates outside `--max-mutants` are not executed and are not counted as budget-skipped selected mutants.

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

- `Killed`: inspect `failingTests` (capped at 10 names). With slow/flaky integration suites, consider `--confirm-kills` or config `"confirmKills": true`: after exact-byte restoration it rebuilds production and reruns the failed test methods intersected with the same filter. This adds builds/tests within the global budget; parameterized methods may run multiple rows. Repeated failures on unmutated source are `TestError` with `killConfirmed: false`; successful confirmation sets `killConfirmed: true`. Missing/unsupported identities or over 10 failures prevent confirmation and yield `TestError`. A confirmation reduces false kills but does not eliminate flakiness. Without confirmation, executed tests failed with the mutation present. Baseline tests must have passed.
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

Execution modifies one working-copy source file at a time, then restores its exact original bytes in cleanup. The CLI handles Ctrl+C with cancellation; wait for it to return before inspecting restored source. Do not run two verifier instances against the same working tree or edit target source while verification is running. Use isolated workspaces for future parallel execution.

Normal process errors, test failures, cancellation, and budget expiry must restore source. Forced process termination, host failure, and concurrent external edits cannot be made safe by `finally`; prefer graceful cancellation. If interrupted abnormally, inspect the diff and recover from your recorded working-copy content. Never discard user edits with `git checkout`, `git restore`, or a reset.

V1 runs configured test projects for each selected mutant until a project confirms a test failure; survivors must pass all configured projects. The baseline validates all projects before mutation. It has no coverage-based test selection, equivalent-mutant detection, invocation removal, or numeric-constant return mutation. Conditions and resolved boolean return expressions can be negated; non-null patterns are negated only without variable bindings. Overlapping spans prefer high-value operator mutations over broad condition negation. Inspect `unresolvedBoolean` as well as `unresolvedArithmetic`; omitted or unresolved shapes are evidence gaps. Arithmetic mutation is conservative when operand types cannot be resolved. Do not interpret an omitted mutation as evidence that tests cover it.

## Report back

Keep feedback concise: base and project scope; executed/selected/discovered counts; killed, survived, errored, timed out, and skipped counts; runtime; survivor locations and changed behaviour; investigation or limitations. Include ordinary test results separately.

Use wording such as:

> Mutation verification completed: 8/8 selected mutants executed, 7 killed, 1 survived. `PaymentService.CanPurchase` still passes tests when `>=` becomes `>`. I added the equality case required by the purchase contract and re-ran verification; that mutant is now killed.

For incomplete verification, say so explicitly and identify what remains unverified. Never call a survivor a production bug without investigating its contract.
