# Walker versus Stryker.NET — 2026-10-07

## Measurements

Completed-report latency includes process startup, each tool's own baseline,
mutation discovery, preparation/builds, test execution and JSON reporting.
Package installation and an initial restore/build/passing test run for each copy
are outside timing. These are small fixtures, not a general repository ranking.

| Fixture / settings | Pairs | Walker median | Stryker median | Executed mutations, Walker / Stryker |
| --- | ---: | ---: | ---: | ---: |
| Payments, source / concurrency 1 | 5 | 7.384s | 7.264s | 4 / 5 |
| Twenty boundaries, source / concurrency 1 | 3 | 23.745s | 7.555s | 20 / 40 |
| Twenty boundaries, switch + two workers / concurrency 2 | 3 | 15.367s | 7.168s | 20 / 40 |

Stryker has a clear lead on twenty independent numeric boundaries. Walker takes
**3.14 times as long** in its default source mode and **2.14 times as long** with
opt-in switching and two workers, while Stryker tests twice as many candidates.
All twenty shared boundary mutations are killed by both tools. Stryker's twenty
additional mutations change `>=` to `<`; its full forty-candidate set has no
survivors, ignored candidates, compile errors, runtime errors or timeouts.
Walker actually uses two workers in the switching series, prepares all twenty
mutations and has zero source fallbacks.

Payments is effectively tied: Walker's median is 1.7% higher, with overlapping
ranges of 7.219–7.502s and 7.136–7.392s. Both tools detect the same two test gaps:
the missing equal-balance purchase case and the weak positive-fee assertion.
All four shared mutations have identical outcomes. Walker kills two and retains
two survivors; Stryker kills three and retains two survivors, with one additional
ignored block-removal candidate. Its extra tested mutation changes `>=` to `<`.
Mutation scores therefore have different denominators.

Individual completed-report timings, in pair order:

| Series | Walker seconds | Stryker seconds |
| --- | --- | --- |
| Payments | 7.384, 7.502, 7.219, 7.439, 7.275 | 7.264, 7.214, 7.392, 7.136, 7.328 |
| Boundaries, source / 1 | 23.674, 23.817, 23.745 | 7.555, 7.275, 7.570 |
| Boundaries, switch / 2 | 14.891, 15.367, 15.826 | 7.168, 7.128, 7.389 |

Median sampled peak process-tree RSS:

| Series | Walker | Stryker |
| --- | ---: | ---: |
| Payments | 562.5 MiB | 406.1 MiB |
| Boundaries, source / 1 | 455.2 MiB | 404.1 MiB |
| Boundaries, switch / 2 | 756.8 MiB | 529.7 MiB |

The remaining gap warrants profiling test-host startup and isolation/preparation
before implementing another optimisation. The follow-up
[speed diagnosis](walker-speed-diagnosis.md) attributes the main fixture advantage
to Stryker packing forty mutations into two coverage-guided test runs; Walker
still performs twenty runs. Disabling mixing removes Stryker's lead in the
diagnostic control, with unchanged mutation outcomes. Walker's switching series spends a
median **2.285s** on its baseline and **5.808s** on preparation, then starts fresh
test hosts for mutation attempts. These phase costs are included above. This
comparison identifies a gap; it does not isolate the cost of each architectural
difference or justify dropping correctness checks. A larger production-project
comparison remains necessary before making a general speed claim.

## Measurement policy

- Walker CLI: Release build, commit `a88b62bee100af32fe0ecfc730e8aa41c7018639`.
  Main CLI SHA-256: `dd77385adbbbfee47a4bbbb47818afac5333e2b43fba32f75629e847aa14bdae`.
- Stryker.NET: locally installed, pinned `dotnet-stryker` 5.0.0.
- Hardware: Apple M1, eight logical CPUs, 8 GiB RAM; macOS 26.5.1, arm64.
- Both tools and target projects use the repository's local .NET SDK 10.0.401
  and .NET runtime 10.0.12. Target projects use their default Debug configuration.
- Each pair creates separate fresh disposable Git repositories with identical
  production and test source. Each copy is restored, built and tested before
  timing. The tools never share mutable `bin` or `obj` directories.
- Order alternates: Walker first in odd pairs, Stryker first in even pairs.
  No other benchmark or .NET build/test is run concurrently.
- Both tools target the same two-commit change. Walker uses `--base HEAD~1`;
  Stryker uses `--since:<resolved-parent-SHA>`. Stryker 5.0.0 rejected the literal
  `HEAD~1` revision in the initial probe, so the harness resolves it first.
- Stryker retains its normal mutation, coverage and execution defaults. Explicit
  overrides select the diff base, concurrency, JSON reporter and output location,
  and skip the online version check. No previous mutation results are reused.
  See [Stryker configuration](https://stryker-mutator.io/docs/stryker-net/configuration/).
- Walker selects at most twenty mutations; both fixtures fit within that limit.
  Counts and outcomes must remain stable within each tool's repeated series.
  Every Walker mutation must also appear in Stryker's report with the same file,
  source line, replacement and outcome. The complete mutation sets still differ.
- The boundary fixture removes the `FreshHost` static-counter assertion from
  **both disposable test copies**. It checks Walker's process lifecycle, not
  production behaviour, and is excluded to keep the benchmark focused on the
  production assertions. This is not evidence about Stryker's host-reuse policy. All
  twenty equality tests remain. Repository examples are unchanged; neither
  fixture introduces artificial delays.
- The harness checks all production/test C# hashes after both tools. It rejects
  timeouts, incomplete reports, runtime errors and unstable mutation outcomes.
  Walker's survivor exit code is expected; Stryker's default score threshold
  accepts survivors. Exit codes alone are not compared as test outcomes.
- Both tools use the same optional 100ms process-tree RSS sampler. It excludes
  reparented/shared daemons and may double-count shared pages or miss short peaks;
  measurements are sampled RSS sums, not OS high-water marks.

## Reproduce

Install the pinned tool and build Walker using the intended SDK:

```bash
.dotnet/dotnet tool install dotnet-stryker --version 5.0.0 \
  --tool-path artifacts/stryker-comparison/tool
.dotnet/dotnet build Walker.sln -c Release --nologo

python3 scripts/benchmark_stryker.py \
  --dotnet .dotnet/dotnet \
  --cli src/Walker.Cli/bin/Release/net10.0/Walker.Cli.dll \
  --stryker artifacts/stryker-comparison/tool/dotnet-stryker \
  --fixture payments --repetitions 5 --measure-memory \
  --output artifacts/stryker-comparison/payments-new

python3 scripts/benchmark_stryker.py \
  --dotnet .dotnet/dotnet \
  --cli src/Walker.Cli/bin/Release/net10.0/Walker.Cli.dll \
  --stryker artifacts/stryker-comparison/tool/dotnet-stryker \
  --fixture boundaries --mutant-mode source --workers 1 \
  --stryker-concurrency 1 --repetitions 3 --measure-memory \
  --output artifacts/stryker-comparison/boundaries-source-new

python3 scripts/benchmark_stryker.py \
  --dotnet .dotnet/dotnet \
  --cli src/Walker.Cli/bin/Release/net10.0/Walker.Cli.dll \
  --stryker artifacts/stryker-comparison/tool/dotnet-stryker \
  --fixture boundaries --mutant-mode switch --workers 2 \
  --stryker-concurrency 2 --repetitions 3 --measure-memory \
  --output artifacts/stryker-comparison/boundaries-switch-new
```

`--dotnet` also controls child `PATH` and `DOTNET_ROOT`. Substitute another SDK
installation consistently for both tools when reproducing on another machine.
Use a fresh output directory: the harness refuses to overwrite an old series.
New runs preserve a complete hashed CLI snapshot under `walker-cli/`, outside
timing, so later repository builds cannot replace the measured assemblies.

Each `pair-N` contains warm test logs, both process logs, both full JSON reports
and `common-mutations.json`. `summary.json` records commands, SDK/runtime/tool
versions, CLI hash, source hashes, individual timings, counts, preparation and
memory data. Only series marked `completed: true` qualify for the tables.

Local raw evidence is under ignored `artifacts/stryker-comparison/`.
Accepted series are `payments-source-1-measured/`, `boundaries-source-1/` and
`boundaries-switch-2/`; exploratory incomplete runs are excluded. All eleven
completed pairs preserve source/test hashes and stable outcomes, with no runtime
errors or timeouts. Stryker emits framework-detection warnings, then successfully
selects/builds/tests `net10.0`; the original warnings remain in the logs.
Nine existing Python harness tests pass, and the new harness is exercised through
the actual twenty-two completed tool executions.
