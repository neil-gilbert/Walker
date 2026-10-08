# Differentiating Walker from Stryker

Research date: 8 October 2026. Primary-source desk research: official documentation, maintainer discussions, user issue reports, and competing tools' own repositories. This is a qualitative sample, not a representative survey. It does **not** establish what a majority of developers wants or willingness to pay. GitHub search snapshots can lag; reported defects below are evidence of needs, not proof they affect every current release. Recommendations below are hypotheses, clearly separated from reported user evidence.

## The strongest needs visible in public evidence

### 1. Feedback soon enough to change a pull request

A Stryker.NET user describes a roughly 20,000-line application with 1,300 tests taking eight hours for a full run, exceeding GitHub Actions' six-hour job limit. Even two changed files reportedly took about an hour. Their requested outcome was feedback on changed lines; the maintainer explicitly described this as the second report of `since` being too slow for PRs. The user's observation that late feedback gets ignored explains why wall-clock speed matters: it determines whether findings influence a change. These are measurements from that user's 2024 environment, **not** contemporary benchmark claims. [Stryker.NET discussion #3013](https://github.com/stryker-mutator/stryker-net/discussions/3013)

An older independent JavaScript user reported 942 tests normally running in 73 seconds, but an estimated mutation run exceeding 72 hours. Their configuration disabled coverage analysis, so this is evidence of confusing setup/performance tradeoffs, not a fair evaluation of StrykerJS defaults. [StrykerJS issue #3320](https://github.com/stryker-mutator/stryker-js/issues/3320)

**Inference for Walker:** a bounded, useful PR result matters more than being faster on one benchmark. Distinguish completed checks, deferred work, and incomplete measurement; never turn a time budget into a deceptively complete score. Changed-code scope alone is not novel (see capability check).

### 2. Results developers and agents can verify

A 2026 Stryker.NET user supplies a CSLA reproduction in which equivalent canary mutations intermittently appear killed under the MTP runner. They report approximately 9–18 false kills among 200 canaries at normal concurrency, and none when concurrency is one or the code is stateless. They explicitly request per-mutant process isolation. The precise leak mechanism remains the reporter's inference; the issue is open in the fetched page. [Stryker.NET issue #3742](https://github.com/stryker-mutator/stryker-net/issues/3742)

A separate user manually removed a repository call and saw two tests fail, yet Stryker reported survivors. Maintainers proposed checking test identification, initial failures, coverage analysis and execution context. This demonstrates the practical need to explain surprising results; the discussion does not establish a confirmed root cause. [Stryker.NET discussion #3237](https://github.com/stryker-mutator/stryker-net/discussions/3237)

Additional MTP reports describe multiple assemblies losing coverage and test selection silently falling back to the full suite. **Both issues are closed in the fetched pages**; #3753 links a fixing PR. They show recurring integration failure modes and active upstream repair, not durable competitive gaps. [Stryker.NET issue #3753](https://github.com/stryker-mutator/stryker-net/issues/3753), [Stryker.NET issue #3754](https://github.com/stryker-mutator/stryker-net/issues/3754)

**Inference for Walker:** bundle a survivor's exact change, covering tests, baseline status, isolated replay, and the evidence behind its verdict. For AI-written fixes, independently prove that the new test passes on the original and fails on the challenged version. Useful explanations should be grounded in actual execution rather than a model's confidence.

### 3. Less time chasing unkillable or low-value survivors

Stryker's own documentation says equivalent mutants cannot be definitively detected and recommends accepting less than 100% or changing the source. This is an acknowledged limitation of mutation testing, not a Stryker-specific bug. [Official equivalent-mutants guidance](https://stryker-mutator.io/docs/mutation-testing-elements/equivalent-mutants/)

A current Stryker.NET wrapper already offers likely-equivalent classification for logging/attribute contexts, with an annotation mode that leaves the score alone and an explicit suppression mode. This independently demonstrates demand for report triage but also shows that heuristic filtering is already available around Stryker. Its README is a product claim; this research did not execute it. [marmorkrebs repository](https://github.com/anagnorisis2peripeteia/marmorkrebs)

**Inference for Walker:** group survivors by missing behaviour/assertion, prioritise meaningful risks and suggest concrete inputs. Label equivalence as a reviewable hypothesis unless proven under a defined method; a surviving test run alone cannot prove equivalence. Logging, messages and metadata can be contractual behaviour, so do not automatically discard them. Demand for this exact AI workflow is plausible, but the sources here do not quantify adoption.

### 4. Adoption that survives real build and test environments

A Stryker.NET user asks for a run to finish with useful results rather than letting one unsupported generated partial class abort the whole project. Their report covers Blazor/Razor and WPF compilation failures, unsuccessfully attempted exclusions, and no report produced. Maintainer discussion identifies compiler/generator and design-time-build issues, with work underway; this is an environment-specific complaint, not universal incompatibility. [Stryker.NET issue #3813](https://github.com/stryker-mutator/stryker-net/issues/3813)

There is also an explicit 2026 request to show progress during lengthy coverage analysis, particularly `perTestInIsolation`, instead of requiring debug logs to see activity. [Stryker.NET issue #3728](https://github.com/stryker-mutator/stryker-net/issues/3728)

**Inference for Walker:** preflight diagnosis and honest partial reports reduce adoption friction. This is necessary product quality rather than an AI differentiator by itself. A model can explain a deterministic diagnostic; it should not decide silently that failed execution counts as protection.

## Capability reality check: do not claim these are missing

Stryker.NET already documents per-test coverage, concurrent execution, mutation levels, score gates, selective files and character spans, changed-code runs (`since`), baseline result reuse, JSON/HTML reporters and mutation exclusions. Its MTP runner is marked preview and users are told to verify results. Therefore diffs, caching, machine-readable output and filtering are not sufficient differentiators. [Official Stryker.NET configuration](https://stryker-mutator.io/docs/stryker-net/configuration/)

StrykerJS separately has incremental mutation testing. Do not transfer a NET limitation onto the JavaScript product. [Official StrykerJS incremental documentation](https://stryker-mutator.io/docs/stryker-js/incremental/)

Stryker's report schema already includes `coveredBy`, `killedBy`, source, replacement and `statusReason`. Adding JSON or test relationships alone does not create a new product category. [Official report schema](https://github.com/stryker-mutator/mutation-testing-elements/blob/master/packages/report-schema/src/mutation-testing-report-schema.json)

Editor integration is also already moving upstream. The official VS Code plugin uses a JSON-RPC Mutation Server Protocol; StrykerJS release history records MSP support in 9.1.0 and stdio transport in 9.3.0. Do not position an IDE extension or persistent agent transport as globally unique. [Official VS Code announcement](https://stryker-mutator.io/blog/vscode-plugin/), [StrykerJS release history](https://github.com/stryker-mutator/stryker-js/releases)

## AI and workflow competitors already exist

- **agentic-stryker:** a publicly released research implementation wraps StrykerJS with MCP and an LLM agent that writes/improves tests using mutation feedback. Its tools include mutation testing, mutant details and test execution; a benchmark harness provides validation. This shows feasibility and weakens any claim that “Stryker plus AI test generation” is novel. It does not establish a mature commercial product or widespread adoption. [Repository and methodology](https://github.com/eefscheef/agentic-stryker)
- **Chaos-MCP:** its README claims several mutation engines, changed-line scope, survivor grouping/severity/hints, prior-run verification, runtime budgets and persistent suppressions. These are claims, not features independently benchmarked here. They weaken differentiation based only on MCP, concise hints or bounded runs; its listed engines are not Stryker.NET. [Repository](https://github.com/AraneaDev/Chaos-MCP)
- **marmorkrebs:** its README claims PR scoping across engines, Stryker.NET likely-equivalent classification, evidence-preserving gates and explicit incomplete/error handling. Again, implementation and adoption were not independently assessed. [Repository](https://github.com/anagnorisis2peripeteia/marmorkrebs)

## What the evidence supports saying

The recurring practical themes in this sample are **quick feedback, trustworthy execution, actionable survivors and lower setup/triage effort**. The evidence is strongest for runtime and execution friction. A native .NET loop from a behavioural concern or survivor to a small **execution-verified test improvement** is a promising product hypothesis. It should compete on the useful result and strength of evidence, not the presence of AI, JSON, a percentage, or an MCP server.

The next validation step is interviews and trials with teams already running Stryker.NET: measure time from survivor to accepted test, how many findings they reject, PR turnaround, unsupported builds, and whether generated tests assert intended behaviour rather than copy current implementation. Ask users to compare actual results on their repository; interest in a feature is weaker evidence than repeatedly using it.

## Research precedent beyond Stryker

Google's industrial study identifies both computational expense and developer attention as adoption constraints. It reports that developers initially judged 85% of findings unproductive, with later filtering increasing productive findings from 15% to 89%. Those results belong to Google's deployment, not Stryker or Walker. The implication is to measure useful findings and time spent resolving them, rather than optimize the mutation count alone. [Google study, 2021](https://arxiv.org/html/2102.11378v2).

Meta's ACH generates concern-specific faults and uses survivors to guide test generation. Its December 2024 Messenger/WhatsApp test-a-thons accepted 140 of 191 reviewed tests (73%); 36% of relevance-rated tests were judged privacy relevant. This supports practical value for verified test patches, while showing that relevance needs review. Its LLM equivalence detector is imperfect: model judgement cannot be treated as proof. These results concern Meta's Kotlin systems and selected reviewers; they are not a forecast for Walker's .NET users. [Meta ACH paper](https://arxiv.org/html/2501.12862v1).

LLM-generated mutants are already established research: GitHub Next's LLMorpheus generates mutants and evaluates them using a custom StrykerJS fork. AI mutation generation alone is therefore not a novel category. [LLMorpheus repository](https://github.com/githubnext/llmorpheus).

## Walker's starting point

Inspected the working tree on 2026-10-08, including existing uncommitted changes. No builds or tests were run for this research. Code presence is not a release or correctness claim.

- [README](../README.md) defines a bounded, changed-code verifier with deterministic mutation selection, machine output, baseline checks and survivor investigation guidance.
- [Current CLI](../src/Walker.Cli/Program.cs) already contains `--mutant`, `--isolate` and typed error output. [Models](../src/Walker.Core/Models.cs) include scope, diagnostics and snapshot metadata. [Isolation implementation](../src/Walker.Execution/IsolatedVerificationSession.cs) captures source inputs and persists run artifacts. These are a useful foundation for an agent investigation workflow, rather than new roadmap suggestions.
- The recorded [Stryker comparison](stryker-comparison.md) has near-equal latency on Payments, but Walker is slower on twenty boundaries, even with switching and two workers. That fixture evidence does not support a general faster-than-Stryker claim.
- The [existing improvement handoff](ai-agent-improvements.md) describes earlier proposals; portions now have code in the working tree. Recheck implementation and validation before treating those proposals as unfinished.

## Recommended product direction

**Positioning hypothesis: Walker helps a coding agent turn an untested behaviour into a reviewed regression test, backed by recorded execution evidence.**

Start with .NET agents. Keep execution and verdicts deterministic; let the user's coding agent supply reasoning and proposed tests. An embedded model service is optional, not a prerequisite. The differentiator must be the quality and completeness of the workflow, because generic hints, JSON, MCP, diff scope and AI test generation already have competition.

### 1. Build the survivor-to-test workflow first

Proposed capability, not implemented here:

1. Export a compact investigation packet: original/replacement, enclosing member, relevant test context, source fingerprint, effective scope, and a behaviour hypothesis.
2. Ground the expected behaviour in a requirement, public API contract, existing specification test or a developer decision. If intent is unknown, report that explicitly. A passing original implementation alone does not establish correctness.
3. Let the agent propose a small test patch using the project's conventions. Freeze production inputs during this verification so the result can be attributed to the test change.
4. Demonstrate that the original test scope passed the faulty version, the new test passes ordinary code, and the new test fails on that same faulty version. Record the actual assertion failure and repeat ordinary execution to detect observed instability. Repetition reduces flake risk; it cannot prove permanent stability.
5. Recheck the broader agreed scope and return a reviewable patch with the requirement reference, exact fault, test name, commands, hashes, outcomes and remaining unverified code.

Example report: `Equal balance must permit purchase. Existing tests also passed when >= became >. New test CanPurchase_WithExactBalance passes ordinary code and fails the mutated code at the purchase assertion.` This is much more useful than a survivor ID plus an invitation to investigate.

A production defect should become a separate finding, with its own fix and evidence, rather than an instruction to preserve current behaviour. Infrastructure failures, compilation failures and hangs remain distinct from an assertion catching the intended fault.

### 2. Add requirement-driven and incident-driven challenges

Proposed capability: accept a requirement or historical bug and replay a narrowly scoped fault against the current tests. Start with one domain, such as payments or access control.

Examples: duplicate processing of the same idempotency key; removal of tenant scoping from a query; applying a fee before rather than after a cap; an expiry decision on the wrong side of the boundary. These represent developer concerns beyond generic operator substitution, although some may also be expressible through Stryker's existing operators.

Require a rationale, exact patch, affected contract and observed behaviour difference. Compile and run the proposed fault; an LLM's plausible explanation is not execution evidence. Introduce support for validating explicit external fault patches before building open-ended fault generation. Bound patch scope and retain artifacts for replay.

Historical bug replay has a useful oracle: the incident description and known regression example. Requirements provide intent when available. Neither guarantees complete coverage of the business rule, so report challenged scenarios rather than a claim that the requirement has been proved.

This is the more distinctive long-term direction. Demand for it remains a hypothesis, supported by industrial precedent rather than a representative survey of Stryker users.

### 3. Make triage and evidence part of both workflows

Group related survivors into behavioural gaps while keeping every raw result accessible. Prefer a concrete input or test that distinguishes original and faulty behaviour over an explanation alone. Offer classifications such as missing assertion, missing scenario, likely equivalent, unspecified behaviour and execution problem, with reasons and uncertainty.

Store accepted dispositions with source/contract fingerprints, author and rationale; invalidate them when relevant inputs change. AI classification must not silently turn a survivor into a pass. A change in production must invalidate an earlier test-only repair comparison unless equivalence of the relevant inputs is established.

Report tested, excluded, unsupported and budget-unexecuted scope explicitly. The review artifact is evidence for selected challenges, not a certificate that an entire change is safe.

## What to deprioritize

- Rebuilding Stryker's broad reporting dashboard or chasing a larger operator catalogue before validating the workflow.
- Marketing diff selection, JSON, concurrency, editor integration or MCP as unique features.
- Promising automatic equivalent-mutant elimination or calling LLM risk rankings measured severity.
- Optimizing for 100% mutation score or generating large volumes of tests merely to raise it.
- Expanding to many languages immediately. Later, an optional Stryker report/execution adapter could serve teams already using Stryker while Walker owns investigation and test-change evidence. Validate an adapter before assuming protocol compatibility.

## Validate with users before committing to a large roadmap

Run a proposed pilot with 5–10 .NET teams using AI coding agents, including some existing Stryker users. Observe real change verification rather than asking only which feature sounds appealing.

Compare three approaches on matched real changes: their existing workflow; an agent using Stryker's JSON directly; and the same agent using Walker investigation/verification packets. Keep model, relevant test scope and resource budgets comparable. This measures whether Walker adds value beyond better prompting around an existing engine.

Measure time to the first useful finding, time to an accepted test patch, reviewer acceptance, redundant/brittle tests, triage effort, observed false results, runtime and model cost, and faults caught from known past regressions. Ask reviewers to assess intended behaviour independently of the model's explanation. Track failed and inconclusive attempts as well as successful patches.

Suggested decision rule: build the investigation and verified-test slice first; proceed to concern-driven faults if reviewers accept useful patches and the workflow reduces their total effort. If only runtime wins matter to pilot users, prioritize trustworthy execution and selection instead. These are proposed validation steps, not research already conducted.
