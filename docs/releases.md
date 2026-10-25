# Releasing Walker

Walker ships as one portable .NET tool package, `Walker.Cli`, with command
`walker`. The supporting libraries, MSBuild logger and coverage collector are
bundled in the tool. The NuGet owner is `neil-gilbert`, matching CodePunk.

## One-time GitHub setup

Sign into NuGet.org as `neil-gilbert` and create a
[trusted-publishing policy](https://www.nuget.org/account/trustedpublishing):

- Package owner: `neil-gilbert` (the same account as CodePunk).
- Repository owner: `neil-gilbert` (the GitHub login, not a display name).
- Repository: `Walker` (the name only, not the repository URL).
- Workflow file: `release.yml` (the filename only).
- Environment: `nuget`.
- Scopes: create new packages and update package versions, for `Walker.Cli`.

The publish job requests a short-lived NuGet credential with `NuGet/login@v1`
and GitHub OIDC. No long-lived API-key secret is required. See
[NuGet's trusted-publishing documentation](https://learn.microsoft.com/en-us/nuget/nuget-org/trusted-publishing).
Use a newly created policy within its initial activation window (normally seven
days); successful publication activates it permanently.

The release job uses the GitHub environment `nuget`. Any environment protection
rules configured there apply before publication.

## Check a release without publishing

Run **Release to NuGet** manually in the Actions tab, supplying a version such
as `0.1.0-preview.1`. Manual runs only build, test and upload artifacts; they
never publish. This also works from a development branch.

The workflow:

1. Validates the version.
2. Builds and runs the solution and Python tests on Linux.
3. Packs the CLI once, including its README, MIT licence and repository metadata.
4. Installs that exact package with an empty package cache on Linux, macOS and
   Windows, then verifies .NET 10 and .NET 8 target projects. Checks include
   baseline observation, compiled tests, isolated worktrees, focused reruns and
   optional agent workflows.
5. Keeps the `.nupkg`, test results and verification evidence as Actions artifacts.

Pull requests and main builds use the same checks. They use the default version
from `Walker.Cli.csproj` and never receive publishing credentials.

## Publish

After the release commit has landed on `main`:

```sh
git switch main
git pull --ff-only
git tag v0.1.0-preview.1
git push origin v0.1.0-preview.1
```

The `v` prefix is removed to get the package version. Release versions must use
`major.minor.patch` with an optional prerelease suffix such as `-preview.1`.
Build metadata and noncanonical numeric versions are rejected. The tag commit
must be an ancestor of `origin/main`.

Tagged releases rerun all checks, publish the checked package to NuGet.org, and
create a GitHub release with generated notes and the `.nupkg` attached. Preview
tags create GitHub prereleases. The workflow passes the tag version to both
build and pack; no source version bump is required for each release.

NuGet versions are immutable: fix a published release with a new version/tag.
If publication succeeds but GitHub release creation fails, rerun the failed
job: an existing NuGet version is skipped and GitHub release creation retries.
Check NuGet indexing before announcing availability.

## Consumer setup

For a global preview install:

```sh
dotnet tool install --global Walker.Cli --prerelease
```

For teams and CI, commit a local tool manifest with an explicit version and use
`dotnet tool restore` on subsequent checkouts. Run `dotnet tool update Walker.Cli
--version <version>` when deliberately upgrading a local manifest, or add
`--global` for a global installation. See the README for verification examples.
