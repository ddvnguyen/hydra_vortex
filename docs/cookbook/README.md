# cookbook — operational recipes

This directory holds step-by-step recipes for recurring operational tasks: deploy a
build, run live-rig tests, bump a submodule, add a GPU node, and so on. One file per
recipe, named by task (kebab-case, e.g. `deploy-engine-build.md`).

## Format

Each recipe is a concrete "how do I actually run this command" guide — the execution
layer. The policy and process behind each recipe lives in `docs/workflow/`; the cookbook
does not duplicate it. Each recipe should link back to its governing workflow doc.

## What this is NOT

- Not the source of truth for *what shipped* — that's GitHub issues and PRs.
- Not architectural rationale — that's `docs/decisions/`.
- Not a place to accumulate session-level state — that's
  `orchestration/state/latest-status.md`.

## Candidate recipes (backlog)

These are scaffolding only; content to be written when the task is next performed:

- `bump-fork-submodule.md` — how to update the `src/llama-cpp` fork submodule
- `run-live-rig-tests.md` — how to run the live-GPU test tiers against real hardware
- `deploy-engine-build.md` — how to build and deploy llama-engine for a specific GPU arch
- `reconcile-epic-branch.md` — how to bring a stale epic branch up to date
