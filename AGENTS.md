# Agent Instructions

The instructions for this project live in **`CLAUDE.md`**. Read it first.

## ⛔ NEVER delete the runner work directory

`/mnt/containers/actions-runner-work/` holds the GitHub self-hosted runner's
repo checkouts + build cache (incl. the ~1.5GB llama-cpp fork submodule).
Deleting it forces a full multi-minute re-checkout on the next CI run.

- **NEVER** `rm -rf` anything under it, in any cleanup, in any script, in any
  agent turn.
- **`podman system prune` / `podman volume prune` are SAFE** — they only touch
  container storage, never this dir. Use those for disk space.
- The dir carries a sentinel: `/mnt/containers/actions-runner-work/.PROTECTED-DO-NOT-DELETE`
  (read-only). Any cleanup logic that might touch the dir MUST test for it
  first (`bash scripts/protect-runner-work.sh check`).
- If you genuinely need to clear a stale checkout, delete the nested
  `.../hydra_vortex/hydra_vortex/hydra_vortex` inner worktree ONLY, never the
  `actions-runner-work` root or `hydra_vortex` top level, and only with owner
  approval.

Quick map:
- **Build / run / test** → `CLAUDE.md` (## Build Environment Quirks) + `DevelopmentRunBook.md`
- **Task lifecycle** → `CLAUDE.md` (## Task Lifecycle) → `docs/workflow/NN-*.md`
- **Pod management** → `docs/hydra-system-pod.md`

