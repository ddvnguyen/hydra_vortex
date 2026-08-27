# TASK ASSIGNMENT — Mini-Fleet smoke tier (architect: consultant, executor: Track-A lead)

Owner directive 2026-08-27: make small-model multi-node smoke tests part of CI/CD
to quickly validate implementation changes. Architect produced the design below;
LEAD implements, then USES it to run smoke against the P100 VM. No RTX anywhere.

## Deliverable PR-1 (branch off epic/697-470-stabilization, base same epic branch)
Title: test(minifleet): mini-fleet smoke tier — real-engine multi-node scenario runner
Closes: open issue first -> [Test] Mini-Fleet smoke tier (label review-finding is for findings;
use plain issue + area/testing).

### Components (exactly these, nothing more)
1. src/core/Tests.MiniFleet/ xUnit project (net10):
   - MiniFleetAppHost (Aspire DistributedApplication): boots sandbox Hydra.Core +
     hydra-head(s) + REAL llama-engine processes as ExecutableResources.
   - Presets: `cpu-2node` (CI: engines ngl=0, threads 3+3, ctx 4096) and
     `gpu-gpu-shared` (P100 VM lane: SAME validated topology as owner proof run:
     node-A :8088 --n-gpu-layers 16 --rpc-port 9513; node-B :8089 --rpc-port 9514
     --n-gpu-layers 8; both -t 3 -c 4096; binary ~/hydra-min-test/llama-engine;
     LD_LIBRARY_PATH=$HOME/hydra-min-test REQUIRED; launched through ssh shim).
   - Reuse ScenarioCatalog specs from Tests.Core/Harness (do NOT fork the catalog);
     driver adapter executes each spec against real HTTP engines instead of fake clients.
   - Scenario assertions: completion status OK, finish_reason present, usage tokens>0,
     store side-effects ignored (out of scope this PR). Emit legacy-vs-v2 trace JSON
     pair to tests/minifleet-artifacts/<preset>/<scenario>.json when both impls run.
   - A/B hooks: env HYDRA_SCHEDULER_IMPL=legacy|v2 drives two passes where feasible.
2. Artifact supply: download-on-demand via hf CLI URL pinned
   https://huggingface.co/unsloth/Qwen3.5-9B-GGUF/resolve/main/Qwen3.5-9B-Q4_K_M.gguf
   sha256=03b74727a860a56338e042c4420bb3f04b2fec5734175f4cb9fa853daf52b7e8
   Env overrides MINIFLEET_MODEL_PATH / MINIFLEET_ENGINE_BIN so rig lanes skip downloads.
   CI caches under actions/cache keyed by the sha256.
3. scripts/minifleet/vm-run.sh: idempotent start/status/stop verbs over ssh hydra-p100;
   MUST NOT touch processes beyond its own pids (never pkill patterns wider than
   our exact cmdline string "Qwen3.5-9B-Q4_K_M.*qwen-2node").
4. .github/workflows/minifleet.yml: workflow_dispatch + pull_request type, but the
   pull_request trigger filtered paths src/core/** src/head/** src/llama-cpp/** or
   label ci:minifleet; single job ubuntu-latest; timeout-minutes 25.
5. docs/minifleet.md runbook documenting ALL engine quirks + topology diagrams +
   validation evidence table.

### Engine quirks you MUST honor (owner-verified today)
- explicit distinct --rpc-port per node; else engine auto-uses port+1 and collides.
- LD_LIBRARY_PATH must point at the engine build prefix dir ($ORIGIN absent).
- /health returns {"status":"ok"}; inference probe /v1/chat/completions.
- Qwen3.5-9B is a REASONING model: reserve >=120 completion tokens or content=="" while
  thinking fills reasoning_content — treat that as PASS for smoke purposes.
- mmap page-cache sharing means two nodes reading one GGUF cost ~zero extra RAM;
  VRAM only pays offloaded layers (+~150MB cuda ctx per proc).

### Acceptance criteria
AC1 dotnet run/test MiniFleet locally (cpu-2node) green end-to-end on host WITHOUT GPU.
AC2 gpu-gpu-shared preset green against live VM using staged binaries; evidence logs +
   trace JSONs committed under docs/minifleet/evidence/.
AC3 VM hygiene verified post-run: nvidia-smi compute apps shows ONLY
   {1620 upstream :8090, 2899 prod engine :8086} + your own ephemeral pids gone after stop.
AC4 CI workflow yaml lint-passes and runs dispatch manually once (Lane1, allowed on CI infra;
   do NOT wait for a full PR run to prove it).
AC5 Docs complete; DevelopmentRunBook.md gains one line pointing to the new tier.
VERIFY: dotnet test src/core/Tests.MiniFleet --filter "Tier=MiniFleet" && bash scripts/minifleet/vm-run.sh status

### Process rules (unchanged from charter)
Zero-trust reporting (no narration-only claims), state files updated, scoped commits,
PR -> base epic/697-470-stabilization, merge forbidden without owner. Consultant reviews
the diff BEFORE the PR description is finalized ("consultant gate").

Out of scope (defer): expert-split dual-RTX preset (needs RTX window), EngineParity merge,
DFlash2/DSpark anything (#703 lane), golden changes (#695 pending owner merge call).
