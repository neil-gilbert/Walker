# Walker

Fast mutation verification for AI coding agents.

**If it still walks, your tests aren’t done.**

Your agent wrote the code.  
Your tests passed.  
Now see what survives.

Walker creates targeted mutations in code changed by a coding agent and checks whether the tests can detect them. It is designed for fast verification inside agentic coding loops rather than full-project mutation scoring.

Traditional mutation testing asks: **How effective is the test suite for this project?**

Walker asks: **Do the tests actually verify the code my coding agent just changed?**

```text
Agent changes code
        ↓
Build
        ↓
Tests
        ↓
Walker
        ↓
Targeted mutations
        ↓
Tests kill them?
     ↙       ↘
   YES        NO
    ↓          ↓
  Done       WALKER
               ↓
        Agent investigates
```

Walker analyses the Git diff, mutates changed production code, prioritises high-value mutations, operates within configurable time and mutant budgets, and reports survivors to the coding agent. It is designed to complete in seconds where practical; the straightforward V1 build-and-test executor can take longer.

**Walker is not trying to maximise a repository-wide mutation score. It is an adversarial verification step for agent-generated changes.**

A **Walker** is a mutant that survived the tests. **Killed** means the tests detected a mutation. **Horde** refers to the current mutation candidates. The theme is confined to documentation and human output; API concepts and JSON fields remain technical.

```bash
dotnet build Walker.sln
dotnet test Walker.sln

# Run from the target Git repository; use the absolute path to the verifier DLL.
dotnet /path/to/verifier/src/Walker.Cli/bin/Debug/net8.0/Walker.Cli.dll verify \
  --base HEAD~1 \
  --project src/Payments/Payments.csproj \
  --tests tests/Payments.Tests/Payments.Tests.csproj \
  --timeout 60 --max-mutants 20 --format json
```

Use repeated `--tests` arguments for multiple test projects. `--format text` produces concise feedback; `--verbose` includes timings in text output. JSON always contains timings and uses `schemaVersion: 1`.

The reusable [agent skill](skills/walker-verification/SKILL.md) explains when to run the tool, how to choose scope, and how to investigate survivors. Copy its directory into your agent's skills directory (for Codex, `.agents/skills/walker-verification/`) to make it discoverable, or explicitly ask the agent to follow the file.

## Local/global tool

The CLI project is configured as a .NET tool with package ID `Walker.Cli`, title `Walker`, and command `walker`. These are provisional metadata, not claims that names are available on public feeds. No package is published; check naming and ownership before any publication.

```bash
dotnet pack src/Walker.Cli -c Release -o artifacts
# In the target repository:
dotnet new tool-manifest # only if there isn't one already
dotnet tool install Walker.Cli --add-source /path/to/verifier/artifacts --version 0.1.0
dotnet tool run walker -- verify --project src/Payments/Payments.csproj \
  --tests tests/Payments.Tests/Payments.Tests.csproj --format json
```

For a global install, use the same trusted local package source with `--global`; the command is `walker verify`. Do not assume an unrelated public package is this implementation.

## Configuration

An optional `walker.json` in the current directory:

```json
{
  "base": "origin/main",
  "project": "src/Payments/Payments.csproj",
  "tests": ["tests/Payments.Tests/Payments.Tests.csproj"],
  "maxMutants": 20,
  "timeoutSeconds": 60,
  "exclude": ["**/*.Designer.cs", "**/Generated/**"]
}
```

CLI arguments override config; repeated tests/excludes replace their configured lists. Paths are relative to the current directory; exclusion globs match repository-relative paths. Unknown configuration fields and CLI options are errors.

## Behaviour and exit codes

| Exit | Status | Meaning |
| --- | --- | --- |
| 0 | passed | All selected mutants were killed. |
| 1 | failed | A completed verification has surviving mutants. |
| 2 | error | Discovery, build, test, or infrastructure error. |
| 3 | incomplete | Timeout, cancellation, skips, or no eligible expressions. |

Errors take precedence over incomplete execution; incompleteness takes precedence over survivors. Inspect `results` even when verification was incomplete. Compilation errors are never kills. Test failures are classified using TRX counters, not a generic nonzero process exit. A run with no executed tests is an error.

Discovery uses the merge base from `git diff <base>...HEAD` semantics and compares it to the actual tracked working copy so staged/unstaged edits have correct current line numbers. Untracked source files are not discovered. Deleted-only lines do not authorize mutation of unchanged neighbouring expressions. MSBuild Compile-item evaluation limits candidates to the configured production project, including linked source. Generated files, test projects, bin/obj, Designer and source-generator output are excluded.

Roslyn discovers boundary, equality, boolean, logical, null-pattern and numeric arithmetic mutations only in expressions intersecting changed lines. Boolean constant returns are supported. Arithmetic requires known numeric operand types; analysis is per-file and conservatively skips unresolved operands. Invocation removal, numeric return constants, coverage selection and equivalent-mutant detection are deferred. Unusual operator overloads or project context can still produce a compile error; such results never pass verification.

Selection prioritizes boundary, equality, null handling, boolean logic, returns and arithmetic, then repository path/location/ID. All eligible expressions intersect changed lines. A maximum limits **selected** mutants; unselected candidates are counted as discovered, not budget-skipped selected mutants.

The budget includes discovery, baseline and execution. On expiry, no more mutants start; the running process tree is cancelled and source cleanup completes. Cleanup can exceed the budget slightly. Baseline build/tests run once before mutation because a pre-existing test failure must not be counted as a kill. V1 builds each selected mutation and runs configured test projects sequentially, stopping once a project confirms a test failure. Baseline builds restore dependencies once; mutant builds reuse the restore and standard compiler server. MSBuild still updates dependent assemblies so tests observe the mutated code. Baseline build metadata can prove that a compatible test-project build already includes production; Walker then uses that build instead of launching a redundant production build. Multi-target, runtime-specific, customized or unconfirmed references retain the separate build. Failed shared builds receive a production-only diagnostic build so compile errors remain distinct from test-project errors.

The executor snapshots and restores exact working-copy bytes, including BOM/newlines and existing edits, in `finally`. It rejects stale source hashes. Run one verifier per working tree and avoid editing target files during execution. Graceful Ctrl+C is supported. Forced termination, host failure or simultaneous external edits cannot be guaranteed safe by in-process cleanup. Build outputs may still reflect the last mutant until a normal rebuild; source is restored.

## Agent interpretation

A surviving Walker may indicate:

1. Missing test coverage.
2. A weak assertion.
3. An untested boundary condition.
4. An equivalent mutation.
5. Intentionally unspecified behaviour.
6. Incorrect production behaviour.

The agent should determine which applies. A surviving Walker does **not** necessarily mean production code should change. Never instruct the agent simply to kill the mutant. Record accepted/equivalent survivors externally with their ID and rationale; V1 does not silently suppress them or convert their exit code to success.

## Architecture and tests

- `Walker.Core`: typed contracts, orchestration, deterministic selection, budget and statuses; no Roslyn, Git or process dependencies.
- `Walker.Git`: change discovery and default exclusions.
- `Walker.Roslyn`: changed-expression syntax analysis and stable mutation IDs.
- `Walker.Execution`: process lifecycle, MSBuild source scope, baseline and reversible sequential execution.
- `Walker.Cli`: configuration, command arguments, JSON/text reporting and tool packaging.
- `tests/Walker.Tests`: mutation discovery, selection, statuses, real Git discovery, process cancellation, restoration under failures/cancellation, and a real CLI boundary workflow.

The integration test creates an isolated Git repository: changing `balance > price` to `balance >= price` produces a survivor and exit 1 with weak tests, then a new equality test kills it and yields exit 0. It verifies exact dirty source restoration after both runs.

The [performance review](docs/performance.md) records measured costs and optimizations.

The [benchmark script](scripts/benchmark.py) runs the example against this verifier and Stryker.NET in a temporary repository. It records wall time, executed mutants and survivor results. Results are specific to the change/fixture and do not establish a general speed claim. See [benchmark guidance](docs/benchmark.md).
