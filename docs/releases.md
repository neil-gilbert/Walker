# Releasing Walker

Walker ships as one portable .NET tool package, `Walker.Cli`, with command
`walker`. The supporting libraries, MSBuild logger and coverage collector are
bundled in the tool. The NuGet owner is `neil-gilbert`, matching CodePunk.

## One-time GitHub setup

Add an Actions secret named `NUGET_API_KEY` to
[neil-gilbert/Walker](https://github.com/neil-gilbert/Walker/settings/secrets/actions).
Use a NuGet.org API key from the `neil-gilbert` account with permission to create
and update `Walker.Cli`. A key scoped only to `CodePunk` will not work.
Renew the key in the Actions secret when it expires.

The release job uses the GitHub environment `nuget`. Any environment protection
rules configured there apply before publication.

## Check a release without publishing

Run **Release to NuGet** manually in the Actions tab, supplying a version such
as `0.1.0-preview.3`. Manual runs only build, test and upload artifacts; they
never publish. This also works from a development branch.

The workflow:

1. Validates the version.
2. Builds and runs the solution and Python tests on Linux.
3. Packs the CLI once, including its installation README, icon, MIT licence,
   search keywords, release-note link and repository metadata.
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
git tag v0.1.0-preview.3
git push origin v0.1.0-preview.3
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

## Project website and search discovery

The public project page is https://neil-gilbert.github.io/Walker/. Its source is
in `website/`, with shared artwork in `docs/assets/`. Changes to either are
published by `.github/workflows/pages.yml` through GitHub Pages. Preview the
same static output locally by copying those files into `artifacts/site/` as in
the workflow, then serving that directory with `python3 -m http.server`.

The page has a canonical URL, a description, social preview images, software
structured data and `sitemap.xml`. Keep its installation guidance and links
accurate. The global and local installation examples resolve the current
preview, so they do not require an edit for every release.

NuGet controls its own search-engine indexing policy. Its listing may serve a
`noindex` tag for a new package even when installation and NuGet search work.
The project website provides a separate indexable explanation and installation
guide. Search engines still decide whether and when to index it.
