# Three improvements for AI coding agents

These implementation handoffs have now been implemented; the historical problem statements and acceptance plans below remain as the design record. See [the implemented protocol](agent-protocol.md) and [the updated skill](../skills/walker-verification/SKILL.md). They target agents **using Walker to verify a change**. Walker already has versioned JSON, bounded execution, source restoration, and a detailed agent skill; the opportunities below remove decisions and shell choreography that agents still have to reconstruct.

Inspected on 2026-10-07 at commit `a88b62bee100af32fe0ecfc730e8aa41c7018639`. Recheck the named symbols before implementing if the code has moved. Implement each idea independently, in the order below; the flags and fields described below are now available.

| Order | Idea | Agent benefit | Relative scope |
| --- | --- | --- | --- |
| 1 | Structured diagnostics and dependable JSON | Choose a recovery action without parsing English | Small–medium |
| 2 | Rerun specific survivors | Test an improved assertion without repeating unrelated mutations | Medium |
| 3 | Built-in isolated verification | Verify a snapshot with one command while keeping the source checkout available | Larger |

## Shared implementation and verification rules

Start by reading [contracts](../src/Walker.Core/Models.cs), [orchestration](../src/Walker.Core/VerificationEngine.cs), [CLI](../src/Walker.Cli/Program.cs), and the relevant section below. Preserve these existing guarantees:

- Status precedence remains error → incomplete → surviving mutants → passed. Zero eligible mutants supplies no evidence. Compile errors and test infrastructure failures never count as kills.
- Every run that executes mutants first completes a fresh baseline. Survivors must pass every requested test project/framework under the effective test filter. Keep kill confirmation and the distinction between per-mutant hangs and global timeout.
- Keep exact-byte restoration, stale-source rejection, cancellation of child processes, and current bounded selection unless the user explicitly selects particular mutants.
- A survivor requires investigation of the intended contract. A focused pass proves only the reported scope; acceptance or equivalence does not silently convert a failure to success.

Use disposable fixture repositories for mutation experiments. The current [Walker skill](../skills/walker-verification/SKILL.md) requires a disposable worktree for actual verification; continue following it until idea 3 provides the tested equivalent. Preserve existing user changes in this repository.

For each idea, first add the behavioral tests listed below and observe the expected failure. Implement the smallest slice that passes them. Use the .NET SDK selected by `global.json`, available to child processes as well as the parent. Run the focused test command, then the build/test/package checks specified in [CI](../.github/workflows/ci.yml). Record failures caused by unavailable SDKs or dependencies rather than presenting them as passing checks.

Keep evidence in `artifacts/ai-friendly/idea-N/`: exact commands, process exit codes, representative JSON, test results, and before/after observations. Include fixture source hashes for execution changes. Finish with the changed files, acceptance-test results, remaining limitations, and an example invocation another agent can repeat. Wall-clock improvements must be measured; the expected benefits below are hypotheses.

## 1. Give agents typed diagnostics and a reliable JSON contract

### Problem and evidence

`VerificationResult.Error` and `MutationResult.Detail` in [Models.cs](../src/Walker.Core/Models.cs) are free text. The engine catches exceptions and retains `ex.Message`; [CliConfigurationTests](../tests/Walker.Tests/CliConfigurationTests.cs) and parts of [EngineTests](../tests/Walker.Tests/EngineTests.cs) assert English substrings. An agent cannot reliably distinguish an invalid option, an empty test filter, and a baseline timeout without interpreting prose.

There is also an existing format defect in `Program.cs`: the preliminary `--format` scan is overwritten by `config.Format` before normal argument parsing. An error before a later `--format json` can therefore produce text. This command was reproduced against the available Release CLI: `verify --max-mutants nope --format json` returned exit 2 and a `WALKER / VERIFICATION ERROR` text report.

### Proposed interface

Retain existing JSON fields, enum spellings, exit codes, `error`, and `detail`. Add `diagnostics` at report level and on individual mutation results. Every entry has a stable `code`, `phase`, human-readable `message`, and an `actions` array of descriptive action identifiers. For example, the additional fields for a baseline timeout would be:

```json
{
  "diagnostics": [{
    "code": "baseline_budget_exhausted",
    "phase": "baseline",
    "message": "The verification budget expired during the baseline.",
    "actions": ["increase_timeout", "review_test_scope"]
  }]
}
```

Actions are suggestions, not commands to execute automatically. Reviewing test scope requires the agent to preserve relevance; an empty filter must never trigger an automatic unrelated filter. Unknown diagnostic codes remain usable through the existing status, exit code, and message.

Start with explicit codes for invalid CLI arguments, invalid configuration, Git discovery failure, project evaluation failure, baseline build failure, baseline test failure, no executed tests, baseline budget exhaustion, cancellation, no eligible expressions, and an unexpected error fallback. Add codes for failures currently surfaced during mutant execution without changing their outcomes. Keep diagnostic phases separate from mutation outcomes.

### Implementation slices

1. Fix format resolution before refactoring diagnostics. Determine the explicit requested format independently of option validation and configuration loading. The last complete `--format <value>` wins; when it is `json`, it governs errors regardless of position, including malformed configuration. A missing trailing value is an argument error rendered using the last complete format or the config/default; an invalid final format value is an argument error rendered as text. Preserve current text defaults and successful behavior.
2. Add the diagnostic contract in Core. At the point a failure becomes known, create a typed failure/result with its code and phase. Map it through CLI and engine handling. Inspect [Git discovery](../src/Walker.Git/GitChangeProvider.cs), [project evaluation](../src/Walker.Execution/MsBuildSourceScope.cs), [executor](../src/Walker.Execution/DotnetMutationExecutor.cs), and [TRX parsing](../src/Walker.Execution/TrxReport.cs). Avoid classifying failures by matching exception message text. Unexpected exceptions use the fallback code with their actual phase.
3. Publish `docs/verification-report.schema.json` and a short `docs/agent-protocol.md` defining diagnostic codes, action meanings, compatibility, and unknown-code handling. Test emitted reports against the schema and representative version-1 consumer expectations. Additive optional fields can retain `schemaVersion: 1`; changes to existing meanings require a separate version decision. Keep JSON stdout to one report and progress/process diagnostics on stderr.
4. Update the existing skill's result interpretation to prefer codes, with text fallback for older Walker versions. Keep detailed protocol reference behind a link in the repository documentation; a copied standalone skill must still work without repository-only files.

### Acceptance tests and completion gate

Add `CliProtocolTests` and extend `CliConfigurationTests`/`EngineTests`:

- Put `--format json` before and after an unknown option, invalid integer, and missing-value error. Test malformed/duplicate configuration, a config requesting text, and repeated format options in both orders. Whenever the effective explicit format is JSON, an error yields one parseable report, exit 2, and an appropriate stable code.
- Distinguish baseline budget exhaustion, external cancellation, baseline failure, zero executed tests, and zero eligible expressions without reading `message`.
- Preserve completed survivors and skipped results on incomplete runs; diagnostics must not change status precedence or counts.
- Vary human wording while asserting unchanged machine codes. A small consumer routes by code and falls back to status for an unknown code.
- Validate passed, failed, error, and incomplete real CLI reports against the schema. Confirm old report fields and outcome spellings remain intact.

Focused command:

```bash
dotnet test tests/Walker.Tests/Walker.Tests.csproj -c Release --filter 'FullyQualifiedName~CliProtocolTests|FullyQualifiedName~CliConfigurationTests|FullyQualifiedName~EngineTests'
```

**Done:** the consumer can select the documented response for every fixture without parsing prose, and an effective JSON request never becomes text because an earlier argument failed. This improves agent reliability; it does not claim faster mutation execution.

## 2. Let an agent rerun the exact survivors it is investigating

### Problem and evidence

`VerificationEngine.VerifyAsync` always calls `Select(mutants, request.MaxMutants)`. The CLI's `--filter` selects tests, not mutants. After adding a missing assertion, an agent must repeat general selection even if it only needs feedback on one known survivor.

The necessary identity already exists: `Mutant.Id`. [Roslyn discovery](../src/Walker.Roslyn/RoslynMutationDiscoverer.cs) hashes file, member, span, operator, original expression, and replacement. Test-only edits normally preserve it; production edits that shift spans can invalidate it. `SourceHash` separately protects application of a freshly discovered mutation.

### Proposed interface

Add repeatable `--mutant <id>` to `verify`:

```bash
walker verify --base <same-base-sha> --project <production.csproj> \
  --tests <tests.csproj> --mutant <survivor-id> \
  --timeout 60 --max-mutants 20 --format json
```

The agent supplies the same relevant scope/filter as before, including any newly added tests. It still investigates the survivor's intended behavior before deciding what to change. After the focused feedback loop, it runs ordinary bounded verification again to assess the broader changed scope.

### Implementation slices

1. Add `MutantIds` to `VerificationRequest` and collect repeated CLI values. Keep this selector invocation-specific in the first version, avoiding a persistent configuration that unexpectedly narrows future runs.
2. Perform ordinary Git and Roslyn discovery. Resolve requested IDs against **all current candidates before applying the mutant budget**. Deduplicate IDs; reject unknown/out-of-scope IDs and requests whose unique ID count exceeds `MaxMutants`, with exit 2 before baseline/execution. A partially valid request must not silently verify only its valid subset.
3. Select the matching candidates using existing deterministic ordering. With no selector, retain current selection exactly. Always run a fresh baseline and preserve all requested test projects, frameworks, filter, confirmation, timeout, and restoration behavior.
4. Add report metadata such as `selection: { "kind": "explicit", "requestedIds": [...] }`; ordinary runs report `kind: "bounded"`. Keep total discovery and per-file counts, but selected/executed counts describe the requested set. Make the restricted evidence visible in text output and the skill too.

Use current discovery as the authority. This feature does not deserialize executable patches from saved reports, reuse previous test results, suppress survivors, or promise IDs that survive arbitrary edits. An ID is a lookup key, not a snapshot identity: if it still matches after other production changes, this run verifies the current source with fresh baseline evidence. Existing hash checks must still reject changes made between discovery and execution.

### Acceptance tests and completion gate

Add `TargetedVerificationTests`, and extend the existing [integration test](../tests/Walker.Tests/IntegrationTests.cs) `PurchaseBoundarySurvivesThenIsKilledAfterEqualityTestAndSourceIsRestored`:

- With many candidates and `MaxMutants = 1`, request a candidate outside the ordinary first selection. Execute exactly that one; retain full discovery counts.
- Duplicate IDs execute once. Unknown IDs, mixed known/unknown IDs, and an oversized explicit selection cause zero baseline/executor calls. Normal no-selector ordering is unchanged.
- Capture a real survivor ID, add only the equality test required by the purchase contract, then rerun that ID with `--confirm-kills`. Assert a confirmed kill and identical dirty source bytes before/after each run.
- Shift production spans so the old ID disappears. The request must error and direct the agent to rediscover; it must not retarget by line number. Separately preserve stale-source rejection after discovery.
- Exercise source mode and supported switch mode. A targeted survivor still has to pass all configured projects/frameworks. Cover baseline failure, cancellation after a completed result, and precedence when errors and survivors coexist.
- JSON and text explicitly distinguish focused selection; a focused pass never implies other discovered mutants executed.

Focused command:

```bash
dotnet test tests/Walker.Tests/Walker.Tests.csproj -c Release --filter 'FullyQualifiedName~TargetedVerificationTests|FullyQualifiedName~IntegrationTests|FullyQualifiedName~EngineTests|FullyQualifiedName~ExecutionTests'
```

**Done:** the real weak-test → new assertion → confirmed kill loop runs only the requested mutants and preserves the ordinary verification guarantees. Compare general and focused runs on the same fixture: report executed counts, baseline cost, and total duration. Fewer mutant attempts is the primary measurable benefit; a fresh baseline still costs time.

## 3. Move disposable-worktree setup into Walker

### Problem and evidence

The [agent skill](../skills/walker-verification/SKILL.md) already requires agents to capture HEAD/base, copy staged and unstaged edits, include required untracked inputs, run in a disposable worktree, retain reports, verify restoration, and clean up. The README quick start and CLI do not perform that procedure. Every agent has to reproduce it correctly.

`Program.cs` currently calls `MutationJournal.RecoverAsync(root)` before discovery, and source execution mutates the supplied root. [MutationWorkspace](../src/Walker.Execution/MutationWorkspace.cs) provides useful copying/path checks for experimental prepared execution, but source fallback still exists. It is not isolation for the whole verification.

### Proposed interface and first-version limits

Add opt-in `walker verify --isolate`, compatible with the existing scope options and idea 2. It owns a fresh disposable worktree and durable artifact directory. Defaults for existing invocations remain unchanged; update the recommended agent invocation after the feature passes its tests.

Initially support ordinary repositories whose build inputs and writable outputs can be confined to the snapshot. Refuse unsupported symlinks, submodules, external imports/references, parent build configuration, and custom output arrangements before running their build logic. Reuse conservative checks where appropriate, and state their limitations. This is checkout isolation, not a sandbox for arbitrary build/test code or shared databases and ports.

### Implementation slices

1. Add an `IsolatedVerificationSession` in Execution owning snapshot creation, path mapping, artifacts, and disposal behind a small interface. Branch into it **before source-root journal recovery**. If the source has a pending restore journal, return a diagnostic while leaving both journal and source untouched. Keep the existing recovery route for explicitly non-isolated execution.
2. Resolve source root, relative invocation directory, HEAD, and base SHA before creating a detached worktree at that HEAD. Capture the combined tracked working contents against HEAD, including staged additions, renames, deletions, and unstaged edits. Use `GIT_OPTIONAL_LOCKS=0` for source inventory reads. Apply the binary patch with `--index` only inside the disposable worktree. Preserve source staging and file contents; worktree registration may update Git administrative metadata.
3. Copy nonignored untracked build inputs using a NUL-delimited inventory, without staging them. Preserve bytes, relevant permissions, relative paths, and configuration resolution when invoked from a subdirectory. Rebuild outputs inside isolation. Untracked production remains outside mutation discovery and must be reported as an evidence gap. Unsupported required ignored inputs must cause an explicit limitation/error rather than silently changing build semantics.
4. Verify the snapshot against captured hashes and detect edits racing with capture. Run discovery, baseline, mutation, switch preparation, and source fallbacks only against the snapshot. Do not automatically fall back to mutating the source checkout when isolation fails. Once capture succeeds, later source edits are allowed and never overwritten; the report describes the captured snapshot.
5. Include `runId`, source HEAD, resolved base/merge base, snapshot fingerprint, effective scope, artifact paths, and cleanup state in the report. Persist session metadata, provisional results, and logs outside the worktree before cleanup. After cleanup, atomically persist the final report with its outcome and emit that report once on stdout. Count setup in the invocation's global budget; child work receives only the remaining time. Cleanup and stopping children may run past that deadline, as cleanup does today.
6. Drain child processes before disposal on normal completion or graceful cancellation. Remove only this session's worktree after restoration and comparison with its captured inputs. A dirty captured snapshot is expected: any removal of it requires verified ownership and matching captured bytes, with artifacts already durable. Retain on unexpected differences or failed cleanup and report the path. Never broadly prune worktrees. Timeout/graceful cancellation yields incomplete/exit 3; unsupported isolation, setup failure, or cleanup failure yields error/exit 2, retaining any collected mutation results. Hard termination cannot promise drained children or a final report; preserve the source checkout and previously persisted session artifacts.

### Acceptance tests and completion gate

Add `IsolatedVerificationTests` using real temporary Git repositories plus injected failures for lifecycle cases:

- Compare native isolation with the existing manual isolated workflow on the Payments fixture. Candidate IDs, outcomes, effective scope, and restored snapshot bytes must agree.
- Stage one change, add a different unstaged edit, use BOM/CRLF, and add an untracked equality test. Verify actual working bytes and the new test reach the snapshot. Source HEAD, staged entries (`git ls-files --stage -z`), status, tracked/untracked file bytes, and pre-existing build outputs remain unchanged. Avoid asserting raw Git index bytes, which may change through stat-cache refresh.
- Invoke from a subdirectory with relative config paths, staged new production files, renames, and deletions. Confirm the configured scope and base are preserved. Copied untracked production stays explicitly outside mutation coverage.
- Test a pending source journal, unsupported external build input, and symlink back into the source checkout. Refuse without recovering source or executing unsupported build logic; sentinel files remain untouched.
- Gracefully cancel during capture, baseline, and active mutation: exit 3, children drained, final report retained. Inject setup and cleanup failures: exit 2, collected results retained, uncertain worktree path reported. Separately hard-kill a session and verify source preservation and readable previously persisted metadata; do not require a final report from a killed process. Another pre-existing worktree is never removed.
- Change a source file during capture and require rejection/retry before verification. Change it after successful capture and prove that the new edit survives while the report still identifies the earlier snapshot.
- Run source fallback within switch mode and prove it mutates only the owned worktree. Start two isolated sessions with distinct snapshots/artifact paths and verify ownership and cleanup cannot cross between them; shared external test resources remain the caller's responsibility.

Focused command:

```bash
dotnet test tests/Walker.Tests/Walker.Tests.csproj -c Release --filter 'FullyQualifiedName~IsolatedVerificationTests|FullyQualifiedName~IntegrationTests|FullyQualifiedName~ExecutionTests|FullyQualifiedName~RobustnessTests'
```

**Done:** one CLI invocation reproduces the manual workflow's mutation evidence with durable final reports on normal completion and graceful cancellation. Source preservation and recoverable session metadata also hold after hard termination. Update the README and skill to use it, removing duplicated manual setup from the normal path while retaining guidance for older versions. Measure setup overhead separately; the primary benefit is reliable operation and less orchestration for agents.

## Implementation record (2026-10-08)

All three ideas are implemented. The current interface is documented in [agent-protocol.md](agent-protocol.md) and the [version 1 schema](verification-report.schema.json). The [bundled skill](../skills/walker-verification/SKILL.md) now prefers native isolation and includes focused reruns and typed recovery guidance; older/manual isolation is a separate reference.

- **Diagnostics:** typed failures originate in Git discovery, Compile evaluation, baseline/test execution and restoration; startup JSON format is resolved before fallible parsing. [Protocol tests](../tests/Walker.Tests/CliProtocolTests.cs), [diagnostic tests](../tests/Walker.Tests/DiagnosticTests.cs) and [schema tests](../tests/Walker.Tests/ReportContractTests.cs) verify behavior and legacy compatibility.
- **Focused reruns:** repeatable `--mutant` validates against all discovered candidates before the baseline, deduplicates IDs and retains the full requested test scope. [Selection tests](../tests/Walker.Tests/TargetedVerificationTests.cs) and [real CLI integration](../tests/Walker.Tests/IntegrationTests.cs) exercise the loop, invalidated IDs and native/manual parity.
- **Isolation:** the execution session owns durable capture/log/report artifacts and one disposable worktree, preserves source staging/bytes, shares the global deadline, and checks ownership before cleanup. [Lifecycle tests](../tests/Walker.Tests/IsolatedVerificationTests.cs) cover capture races, staging/renames/deletions, untracked inputs, unsupported layouts, concurrent sessions, cancellation during capture/baseline/applied mutation, cleanup failures and hard termination.

Validation includes a Release build, a 300-test full-suite checkpoint, expanded isolation checks, nine Python tests, skill validation and installed-package checks on .NET 10 and .NET 8 targets. The package workflow now exercises native isolation and a repeated-ID survivor rerun with a fresh baseline and confirmed kill; CI includes this check. Commands, TRX results, source hashes and representative JSON are retained under `artifacts/ai-friendly/`, with the ledger in `verification-notes.md`.

The implementation uses `read-tree` and a binary patch applied to the **private index**, then copies captured raw bytes without checkout filters. This preserves staged additions and discovery semantics while avoiding checkout hooks/smudge conversion. Native isolation intentionally rejects custom/conditional build layouts, explicit wildcard items, links/submodules, external inputs and uncertain ignored inputs outside conventional build-output directories. It does not sandbox arbitrary build/test code or shared external resources. Untracked production remains a reported mutation-evidence gap. Hard termination guarantees source independence and previously persisted metadata, not a final report or child drainage. Measured package snapshots recorded setup overhead separately; no general speedup is claimed.
