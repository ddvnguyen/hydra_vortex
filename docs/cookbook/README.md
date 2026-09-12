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

## Written recipes

**Operational (hardware/deploy):**

- `build.md` — build Hydra.Core (C#), Hydra Head (Go), and llama-engine (C++) for each
  GPU arch, with the environment quirks and gotchas
- `deploy.md` — deploy the hydra-system pod + P100 head: token env, deploy scripts, pod
  lifecycle, stale-image and port-collision traps
- `test-lane.md` — the isolated 2-core hydra-test rig: compose bring-up, bare P100
  engines, VM hygiene contract
- `monitoring.md` — start Grafana/Prometheus/Loki/OTel, metrics endpoints, dashboards,
  log pipeline prerequisites

**Process:**

- `reconcile-epic-branch.md` — reconcile a diverged local checkout, or rebase a branch
  onto a moved-forward base, in a disposable worktree
- `paseo-delegate-and-verify.md` — spin up a Paseo delegate, catch a degenerate loop,
  and independently verify its "done" report before trusting it

## Candidate recipes (backlog)

These are scaffolding only; content to be written when the task is next performed:

- `bump-fork-submodule.md` — how to update the `src/llama-cpp` fork submodule
- `run-live-rig-tests.md` — how to run the live-GPU test tiers against real hardware
