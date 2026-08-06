# 9. Epic branch (large, multi-PR issues)

**Goal:** land many PRs against one integration point instead of `main`, so pending
work doesn't drift and block on each other while waiting on live-GPU / E2E verify.

## When to suggest this

**Proactively suggest an epic branch** — don't wait to be asked — when an issue looks
like it will spawn **3 or more PRs**, or will stay open for multiple days/sessions
while depending on real-GPU or live-rig verification that can't run on every PR (the
pattern that produced #470's merged-decode work: cross-repo fork + Hydra PRs, wire
protocol still unstable, each PR individually mergeable but the set only makes sense
together). Signs it's epic-shaped:
- The issue body already lists multiple workstreams / sub-tasks.
- The change spans `src/llama-cpp` (fork) + `src/core` (C#) + `src/head` (Go) —
  cross-repo coordination alone usually means it's not a single-PR change.
- You can't reasonably run "E2E verify" (`03-test-verify.md`) until several
  interdependent pieces land together.

If in doubt, ask the user — don't unilaterally create the branch structure change,
this doc only covers what to do once they've agreed.

## Setup

1. Branch name: `epic/{issue-id}-{short-slug}` off the latest `main` — lowercase,
   matching the repo's existing `feat/`, `fix/`, `perf/`, `ci/` convention (not
   `Epic-#N-...`). Example: `epic/470-merged-decode`.
2. Branch-protect it the same way as `main`: require the `Build & Test` CI check
   before merging into it. **Do not** require the live-hardware / E2E-verify gate per
   PR into the epic branch — that stays a once-per-epic step (see Close-out below).
   This is the whole point: cheap, fast integration for the day-to-day PRs.
3. Note the epic branch on the tracking issue (a comment is enough) so anyone picking
   up a sub-task knows where to branch from.

## Working the epic

- **Step 1 (pick up):** sub-tasks still get their own issue/board item, but branch
  from and PR into the epic branch, not `main`:
  `gh issue develop N --base epic/470-merged-decode --name fix/470-...`
- **Step 2 (implement):** unchanged — same submodule push-before-PR rule, same
  `CLAUDE.md` design decisions apply.
- **Step 4 (commit & PR):** `gh pr create --base epic/470-merged-decode ...`. These
  PRs merge on normal CI-green + review — no live-rig verify required per PR, and no
  need to wait for the user's merge confirmation on *every* one if they've already
  green-lit "merge into the epic branch freely" (still ask before merging the final
  epic → main PR).
- **Keep the epic branch synced with `main` regularly** (merge `main` into the epic
  branch every few days, or before branching a new sub-task off it) — `git merge`,
  not rebase, so history stays honest. Resolve drift in small pieces as it happens;
  don't let it all pile up for the final merge.

## Close-out (epic → main)

1. Open **one PR**, `epic/470-merged-decode` → `main`.
2. Run the full verification tier on this PR: `dotnet test src/core/Tests.Shared/ &&
   dotnet test src/core/Tests.Core/`, plus a real E2E / live-GPU verify
   (`03-test-verify.md`) — this is the point where that gate belongs, not on every
   sub-PR.
3. **Merging this PR into `main` requires the user's explicit green light**, same as
   any other merge (`04-commit-pr.md`) — a prior "merge freely into the epic branch"
   approval does not extend to this step.
4. Close the tracking issue per `07-issue-and-close.md` once this PR merges.

→ Back to `01-pickup.md` for the next task.
