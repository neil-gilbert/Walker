# Walker agent protocol

Use `verify --isolate --format json` from the target checkout. Read one JSON report from stdout after the process exits; stderr contains process logs. Capture both streams and the verifier's own exit code. The [bundled skill](../skills/walker-verification/SKILL.md) describes scope selection and survivor investigation.

## Live feedback and optional workflows

`--progress` / `"progress": true` writes phase changes and a flushed heartbeat every 15 seconds to stderr. It reports elapsed/remaining budget, selected-mutant completion counts and active process operations. It runs during quiet child processes, discovery, baseline, preparation and cleanup. Stream stderr to remote callers; capturing it only to a file conceals activity from inactivity monitors. Heartbeats are liveness signals, not an ETA, and do not extend the global timeout. Progress is off by default and stdout retains the single final JSON report.

Invocation-specific `--investigate`, `--challenge <manifest>` and `--test-patch <manifest>` add nullable `investigation`, `challenges` and `testImprovement` report fields. The last two require native isolation and source mode. Read [workflow manifests](../skills/walker-verification/references/optional-workflows.md) before constructing input. `CustomFault` is an additional operator emitted only for explicit challenges; consumers handling that optional mode need the updated schema. Ordinary operator/outcome meanings and exit codes remain unchanged.

Test improvement evidence preserves the original report in `testImprovement.before`. `verified` requires all original survivors to become confirmed `Killed` results after applying the proposed tests in the private snapshot, with all originally selected faults rechecked and detected. Hangs cannot demonstrate improvement for a survivor. The contract and fault descriptions are supplied by the caller and require review; the CLI does not certify requirement correctness or classify a test failure as an assertion rather than an incidental exception. Full test-file proposals are included in the report for review. Nothing applies those test changes to the caller's checkout.

Workflow diagnostic codes: `workflow_scope_invalid` rejects unsupported source/test paths or changed production scope; `challenge_stale` rejects a mismatched fault hash/span; `test_patch_stale` rejects mismatched original test bytes; `workflow_inputs_changed` rejects changes outside the proposed tests; `no_survivors_to_verify` and `test_improvement_unproven` describe incomplete improvement evidence. Shared-budget cancellation retains the original results when available.

## Version 1 and diagnostics

The [report schema](verification-report.schema.json) describes version 1. Existing fields, outcome spellings and exit codes remain unchanged. `diagnostics`, `selection` and `isolation` are additive; older reports can omit them. Additional fields and unknown diagnostic codes/actions are permitted. Consumers should route known codes, then fall back to status, exit code and result outcomes. Human `error`, `detail` and diagnostic `message` remain useful context, but are not routing keys. Actions are hints, never executable commands.

Run and result diagnostics contain `code`, `phase`, `message` and `actions`. Empty arrays mean no diagnostic. A result can carry completed evidence even when later cleanup makes the run an error. Status precedence remains error → incomplete → survivors → passed. Exit codes are 2, 3, 1 and 0 respectively. Compile/test errors never count as kills; no eligible expressions is incomplete.

| Code | Recovery |
| --- | --- |
| `invalid_argument`, `invalid_configuration` | Correct options or configuration. |
| `git_discovery_failed`, `project_evaluation_failed`, `isolation_setup_failed` | Inspect process logs and restore required Git/build infrastructure. |
| `baseline_build_failed`, `baseline_test_failed`, `test_run_failed` | Repair the ordinary build/test failure before judging mutation evidence. |
| `no_test_projects`, `no_executed_tests` | Inspect project scope, discovery and filter; do not silently broaden it. |
| `test_report_missing`, `test_report_invalid` | Investigate missing or unreadable TRX evidence. |
| `mutation_compile_failed`, `kill_confirmation_failed` | Inspect the affected result; compilation or failed confirmation is not a kill. |
| `baseline_budget_exhausted`, `budget_exhausted` | Inspect timings and setup cost; choose a justified scope or budget. |
| `mutant_hung` | A per-mutant hang was detected; this counts as detection, not a global timeout. |
| `cancelled` | Report completed evidence and unfinished scope. |
| `no_eligible_expressions` | Report the coverage gap; use normal tests/review for unsupported changes. |
| `unknown_mutant`, `mutant_limit_exceeded` | Rediscover IDs or provide a selection within the mutant limit. |
| `source_changed`, `snapshot_changed` | Capture a fresh stable snapshot; preserve subsequent user edits. |
| `source_recovery_pending`, `restore_conflict` | Inspect recovery evidence without overwriting changed files. |
| `isolation_unsupported` | Use supported/manual isolation or report the blocker. |
| `isolation_cleanup_failed`, `isolation_artifact_failed` | Preserve available evidence and retained paths; inspect the failure. |
| `unexpected_error` | Inspect context/logs; status and outcomes remain authoritative. |

The last complete explicit `--format` value determines rendering before fallible configuration/argument parsing, regardless of order. Explicit JSON overrides configured text even for startup errors. An invalid final format produces a text error. Without an explicit format, valid configuration controls the output.

## Focused reruns

Repeat `--mutant <id>` to select current discovered candidates explicitly. Selection validates all IDs before baseline, deduplicates them, and rejects unknown IDs or unique selections beyond `--max-mutants`. It does not fill unused slots with unrelated candidates. Without selectors, existing deterministic bounded selection remains unchanged. Selectors are invocation-specific and are not persisted in `walker.json`.

`selection.kind` is `explicit` or `bounded`; `requestedIds` echoes normalized explicit IDs. `mutantsDiscovered` still counts all current candidates. Each focused run uses a fresh baseline, every requested test project/framework and the effective filter, with the existing confirmation/status rules. Test-only edits preserve candidate identity; changed production expressions or spans can invalidate it. A focused pass proves only those IDs. Recheck the ordinary bounded scope after strengthening tests.

```bash
walker verify --isolate --base <change-base> \
  --project src/Payments/Payments.csproj \
  --tests tests/Payments.Tests/Payments.Tests.csproj \
  --mutant <survivor-id> --timeout 60 --max-mutants 20 --format json
```

## Native isolation

Enable `--isolate` or `"isolate": true`. CLI/config paths resolve against the original invocation directory, including subdirectories. Walker resolves HEAD/base/merge base and captures staged plus unstaged exact bytes, tracked deletions/renames/additions, and nonignored untracked inputs. It verifies the capture against the original checkout before execution. Hold inputs steady until stderr says `Isolated snapshot ready`; later edits to the source checkout do not affect the run. Untracked production files support builds but remain outside Git mutation discovery and are listed explicitly.

Each session owns a detached worktree and durable artifact directory outside the source checkout. `isolation` records run ID, source root/HEAD, resolved base/merge base, fingerprint, project/test/filter/budget scope, setup time, artifact/report/log/worktree paths, cleanup state and untracked production files. The global timeout includes capture and setup, discovery, baseline and execution; child drainage and cleanup can go beyond it.

Artifacts include `session.json`, exact captured `inputs/`, `source.patch`, `process.log`, provisional evidence and final `report.json`. Final stdout is emitted after ownership-checked cleanup. `removed` means cleanup completed; `not_created` means execution never created a worktree; `retained` means inspect the returned path. Cleanup failure becomes exit 2 while preserving completed mutation evidence. Only the session's known worktree with matching metadata/input bytes is removed. Unrelated worktrees are untouched.

Cancellation drains child processes and preserves available evidence. Hard termination can leave readable snapshot metadata and a private worktree without a final report; it never requires source-checkout recovery. A pending source restore journal is rejected without changing it. Never copy residual mutated source back from a retained worktree.

This version conservatively supports conventional built-in SDK projects. It rejects symlinks/submodules, external project inputs, required ignored configuration/source, custom output/cache paths, custom targets/imports and explicit wildcard item paths and conditional or unevaluated MSBuild expressions before verification. This is repository-file/output isolation, not a sandbox for arbitrary build/test code or shared databases, ports and package caches. Unsupported layouts require inspection and [manual isolation](../skills/walker-verification/references/manual-isolation.md), or an explicit blocker; never silently omit `--isolate`.
