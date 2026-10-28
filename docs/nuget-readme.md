# Walker: C# mutation testing for .NET

Walker is a .NET command-line tool for fast, diff-aware C# mutation testing,
built for developers, AI coding agents and CI pipelines.

Walker reads the Git diff, makes small behavioural changes to changed production
code, and runs your tests against them. Surviving mutations identify behaviour
that your tests do not defend. Runs have explicit time and mutation budgets and
support JSON output for agent workflows.

## Requirements

- .NET 10 SDK to install and run Walker.
- Git on `PATH` and a Git repository containing the code to verify.
- The SDKs and test runtimes required by that repository and its `global.json`.

The package is exercised on Linux, macOS and Windows, against .NET 8 and .NET 10
test projects. Walker itself runs on .NET 10.

## Install

Install the current preview globally:

```sh
dotnet tool install --global Walker.Cli --prerelease
walker --help
```

For reproducible team and CI setup, use a repository-local manifest:

```sh
dotnet new tool-manifest
dotnet tool install Walker.Cli --version 0.1.0-preview.3
```

Skip `dotnet new tool-manifest` if the repository already has a manifest. Commit
the manifest; subsequent checkouts run `dotnet tool restore`.

## Run

From the target Git repository:

```sh
dotnet tool run walker -- verify --isolate --base HEAD~1 \
  --project src/Payments/Payments.csproj \
  --tests tests/Payments.Tests/Payments.Tests.csproj \
  --timeout 60 --max-mutants 20 --format json
```

With a global install, use `walker verify` with the same arguments.
`--isolate` runs verification in a private worktree and preserves your checkout.

Exit codes: `0` passed, `1` surviving mutations, `2` infrastructure error,
`3` incomplete. A pass covers only the selected mutations and test scope.
Compilation errors never count as detected mutations.

## Why mutation testing?

Code coverage shows which code your tests execute. Mutation testing checks
whether those tests notice deliberate behavioural faults, such as replacing
`balance >= price` with `balance > price`. A surviving mutation can reveal a
missing boundary assertion even when every test is green.

Walker focuses on changed production code in your Git diff. It provides bounded
verification of the selected code and tests, rather than a whole-repository
mutation score. Investigate surviving mutations before changing production code.

## Update or uninstall

```sh
dotnet tool update --global Walker.Cli --prerelease
dotnet tool uninstall --global Walker.Cli
```

For a repository-local tool, omit `--global` and commit the updated manifest
after upgrading. This is a CLI tool: install it with `dotnet tool install`,
rather than adding a `PackageReference` to your application.

See the [project website](https://neil-gilbert.github.io/Walker/),
[full documentation](https://github.com/neil-gilbert/Walker#readme),
[agent protocol](https://github.com/neil-gilbert/Walker/blob/main/docs/agent-protocol.md)
and [agent skill](https://github.com/neil-gilbert/Walker/tree/main/skills/walker-verification).
Read the [release notes](https://github.com/neil-gilbert/Walker/releases)
or [report an issue](https://github.com/neil-gilbert/Walker/issues).
