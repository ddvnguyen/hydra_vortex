# Reconcile a stale epic branch

Governing doc: `docs/workflow/09-epic-branch.md`.

Use this when a local checkout has diverged from `origin/<epic-branch>` (uncommitted
local work + the remote has moved on), or when a branch needs rebasing onto an updated
base (e.g. a fork branch onto its own upstream after churn).

## Case 1: local checkout has drifted from origin

Symptom: `git status` shows local-only commits *and* the remote has commits you don't
have; working tree may also be dirty with stray hand-off files from a prior session.

```bash
git status --short                 # see what's dirty
git log --oneline @{u}..           # commits only local has
git log --oneline ..@{u}           # commits only origin has
```

1. Back up the local state before touching anything: `git branch backup/<name>-<date>`.
2. Stash uncommitted work (exclude broken symlinks if any exist —
   `git stash push -u -m "..." -- . ':!path/to/broken-symlink'`).
3. Hard-reset to origin: `git reset --hard origin/<branch>`.
4. Re-apply anything from the stash/backup branch that's still relevant; commit
   properly this time (not as stray untracked hand-off files — see
   `orchestration/state/latest-status.md` for where that content belongs instead).

## Case 2: rebasing a branch onto a moved-forward base (e.g. a fork branch)

Do this in a **disposable worktree**, not the main checkout — delegate agents and your
own verification both need a clean space to build/test in without racing the primary
tree.

```bash
git worktree add /tmp/<name>-rebase-work <branch>
cd /tmp/<name>-rebase-work
git rebase <new-base>
```

Conflict resolution note: if the branch does a mechanical code *extraction* (moving
functions from file A to file B behind a seam/interface) and the base has since edited
the same functions in file A, expect near-identical conflict shapes across every
extracted region — resolve by taking the base's latest implementation and re-placing it
in the extraction target, not by hand-merging the two conflict sides. **Regex/pattern-
based extraction on a rebase is prone to over-deletion** — after resolving, diff the
result against the base to check nothing meant to be generic (i.e. not part of the
extraction) got silently dropped. Recovering that costs real review time (in one
instance: 561 lines of dropped/missing code across 2 files, from a 4-commit rebase).

## Verify before pushing

```bash
# Standalone build/compile check in the worktree (won't catch link-time issues
# that need a submodule/dependency not present in a bare worktree — note that
# explicitly rather than claiming full verification)
<build command for the language/toolchain>

# Full test suite, not just the tests you expect to be affected
<test command>
```

If a rebase rewrites history that already has a remote copy, pushing needs `--force`.
**Prefer a new branch name over force-pushing over shared history** when the choice is
available — a force-push to a branch others may have already pulled is a
history-rewriting shared-state action; a new branch name (e.g. `<branch>-rebased`) with
a normal push avoids that entirely, at the cost of an extra PR-retarget step.

```bash
git checkout -b <branch>-rebased
git push <remote> <branch>-rebased      # no --force needed, it's a new ref
gh pr create --base <target-branch> --head <remote-owner>:<branch>-rebased ...
```
