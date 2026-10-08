# Optional Walker workflows

Use flags listed in the installed tool's help. These are invocation-specific; ordinary verification remains the default. No model runs inside Walker.

## Investigate a survivor

Add `--investigate` to an ordinary run or focused rerun. `investigation.gaps` groups survivors by file, member and operator and lists hints and mutant IDs. All raw results remain available. Use the hints to find discriminating inputs; determine expected behaviour from the user request, a requirement, an API contract or a developer decision. Equivalent and intentionally unspecified behaviour remain review decisions outside the verdict.

## Verify a proposed test patch

Prepare a JSON manifest **before applying the proposed tests to the source checkout**:

```json
{
  "schemaVersion": 1,
  "contract": "Requirement PAY-12: a balance equal to the price permits purchase.",
  "files": [{
    "file": "Payments.Tests/BoundaryTests.cs",
    "originalHash": null,
    "content": "using Xunit; namespace Payments.Tests; public class BoundaryTests { [Fact] public void ExactBalance() => Assert.True(new PaymentService().CanPurchase(10m, 10m)); }\n"
  }]
}
```

Paths are relative to the Git repository root. `content` contains the complete proposed C# test file and is written as UTF-8 without a BOM. For an existing test file, `originalHash` is the lowercase SHA-256 of its **raw bytes**, including a BOM and newline encoding. Null means the file must be new and under a requested test-project directory. Existing files must be evaluated Compile items of a requested test project; new files are re-evaluated after insertion. Production files and paths outside the repository are rejected.

```bash
walker verify --isolate --progress --base "$base_sha" \
  --project Payments/Payments.csproj --tests Payments.Tests/Payments.Tests.csproj \
  --test-patch /tmp/proposed-tests.json --timeout 120 --max-mutants 20 --format json
```

Include the proposed tests in the chosen test filter. The original run uses the same filter, projects and deadline; a filter with zero original tests cannot establish before/after evidence. Walker executes current tests first, applies the proposal only inside the private snapshot, reruns all originally selected faults with a fresh baseline, and confirms failing test methods on ordinary code. This checks that the proposal also retains previously observed protection. Test files are restored before snapshot cleanup. The caller's checkout is unchanged.

Inspect `testImprovement.status` separately from the ordinary verdict. `verified` requires every original survivor to become `Killed` with `killConfirmed: true` and the complete original selection to pass. Hangs cannot establish improvement for a survivor; compilation failures, unconfirmed kills and incomplete attempts do not establish improvement. `before` preserves original results; `verifiedMutantIds` records demonstrated transitions; `files` preserves the proposal. No original survivors produces incomplete improvement evidence and does not apply the proposed tests.

`verified` describes execution, not semantic correctness. Inspect the failing test code and available process logs to distinguish a meaningful assertion from incidental setup exceptions. Review the supplied contract and avoid inventing expected behaviour from current implementation. After a useful patch is reviewed and applied, run normal tests and ordinary bounded verification again for broader scope. Correct a production defect separately when contract evidence establishes one.

## Challenge a requirement or past bug

Construct one or more narrow replacements describing plausible faults:

```json
{
  "schemaVersion": 1,
  "challenges": [{
    "file": "Payments/PaymentService.cs",
    "sourceHash": "<sha256-of-decoded-source>",
    "spanStart": 123,
    "original": "balance >= price",
    "replacement": "balance > price",
    "concern": "Rejecting an exact-balance payment",
    "expectedBehaviour": "PAY-12 permits a purchase when balance equals price."
  }]
}
```

Calculate `sourceHash` as lowercase SHA-256 of UTF-8 encoded **decoded text**, using BOM-aware reading as .NET `File.ReadAllText` does; omit the encoding BOM from the decoded text and retain existing newlines. `spanStart` is a zero-based .NET/JavaScript UTF-16 character offset, not a byte offset. `original` must match at that offset. Regenerate the manifest after production changes; do not reinterpret stale positions. All source replacements must be deliberate fault hypotheses grounded in the supplied concern.

```bash
walker verify --isolate --progress --base HEAD \
  --project Payments/Payments.csproj --tests Payments.Tests/Payments.Tests.csproj \
  --challenge /tmp/payment-faults.json --investigate --format json
```

Challenges target compiled C# source in the selected production project and can include unchanged files. They replace ordinary diff discovery for this invocation. Every challenge must fit `--max-mutants`; none are silently omitted. Omit `--mutant` and exclusions with explicit challenges. Use source mode without `--compiled-tests`. Supply an explicit base available in the repository; `HEAD` works without a preceding commit.

Read `challenges` and the `CustomFault` results. Concern and expected behaviour are caller-supplied descriptions, not facts certified by Walker. Compile errors and execution errors remain errors. A survivor means current tests passed that fault; it does not by itself establish a production defect. A detected fault establishes protection only against that supplied scenario.

Combine `--challenge` and `--test-patch` when a contract or historical fault needs a new regression test. The same faults are executed before and after the proposal. Keep fault generation and test changes narrow and reviewable rather than maximizing mutation score.
