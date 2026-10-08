## Prepare an isolated worktree

**Run every Walker invocation in a dedicated disposable Git worktree, including retries.** The agent's working checkout is the source of the change being verified; Walker must never mutate it. If isolation cannot be prepared, return the blocker instead of running in the source checkout.

1. Record the source repository's absolute root, current HEAD SHA, resolved base SHA, status, and current tracked diff. Capture a stable snapshot while edits are paused. Create a fresh detached worktree at that HEAD, outside the source checkout; using the default branch or base commit would omit the agent's change.
2. Transfer the current tracked file contents, including staged and unstaged edits, into the worktree. A binary patch against HEAD preserves the combined working-copy state without changing the source index. Keep the snapshot and reports in a separate artifact directory outside both checkouts. For example, from the source repository root:

   ```bash
   source_root=$(git rev-parse --show-toplevel)
   source_head=$(git rev-parse HEAD)
   base_sha=$(git rev-parse --verify "<chosen-base>^{commit}")
   artifact_dir=$(mktemp -d "${TMPDIR:-/tmp}/walker-artifacts.XXXXXX")
   worktree_path="$artifact_dir/worktree"
   git -C "$source_root" diff --binary --no-ext-diff --no-textconv HEAD -- > "$artifact_dir/source.patch"
   git -C "$source_root" worktree add --detach "$worktree_path" "$source_head"
   git -C "$worktree_path" apply --index --binary "$artifact_dir/source.patch"
   ```

   Applying with `--index` in the disposable worktree keeps newly staged source files tracked there; the source index remains untouched. Check each command's success before continuing. Skip patch application when the patch is empty. Resolve the base before switching directories and use `base_sha` for verification so relative refs retain their original meaning.
3. Copy required untracked source, tests, and configuration into the same repository-relative paths, preserving contents. Use a NUL-delimited inventory such as `git ls-files --others --exclude-standard -z`. Recreate ignored local configuration only when required for the build; restore dependencies and build outputs inside the worktree. Keep writable files and build paths independent of the source checkout; inspect symlinks, submodules, and external project references for paths back into it. Report unsupported isolation rather than sharing writable source or output directories.
4. Compare the worktree's tracked diff against HEAD with the captured patch and verify copied inputs match the snapshot before running. The source checkout's HEAD, index, and file contents must remain untouched. The CLI still does not discover untracked production files; copying them supports builds but does not add mutation coverage. Report that gap.

Run builds, tests, tool restoration, and Walker from `worktree_path`, using project paths and configuration from that snapshot. Use an absolute path for a compiled verifier located elsewhere. After intentional test or production fixes in the agent's source checkout, prepare a fresh snapshot and worktree for the next run with the same base. Never copy mutation-run source changes back into the agent's checkout.


After the calling agent has consumed the result, retain the report and logs at the returned artifact paths and remove only the disposable worktree created for this run. Never remove a pre-existing or unrelated worktree.

**The disposable worktree is always "dirty" by design.** `git apply --index` stages the snapshot, so plain `git worktree remove <path>` always refuses. Do not treat this refusal as a sign of residual mutations. Instead:

1. Compare the worktree's tracked diff with the captured snapshot: `git -C <worktree> diff --binary --no-ext-diff --no-textconv HEAD -- | cmp - <snapshot.patch>`.
2. If they are identical, forced removal discards nothing except the snapshot copy. Ask the user once for approval, then run `git worktree remove --force <worktree-path>`.
3. If they differ, keep the worktree and report the path: it can contain a mutation that was not restored.

**Use a new worktree path for each run** (for example `<artifact_dir>/run<N>/worktree`), and keep each run's snapshot, report, log and exit code in its own `run<N>` directory. Then a retry never collides with a worktree that you have not removed yet.

**Check each setup step explicitly.** `set -e` does not stop at a failing command inside an `&&` chain, and `worktree add` into an existing path fails. If you hide its output, the next `git apply` can then run against the old worktree. Test the exit status of `worktree add`, `apply` and the snapshot `cmp` before you build or run Walker.

Tracked-patch equality alone is insufficient: also verify copied untracked inputs match the snapshot and inspect unexpected files before removal. Native isolation already checks ownership and captured inputs; these manual cleanup steps apply only to this reference workflow.
