# COMBINED Engine Mode (2-GPU 5060 Ti + 3060)

> Extracted from CLAUDE.md to keep the handoff file short. Referenced from
> `CLAUDE.md` `## COMBINED engine mode`.

The same-host pair acts as **one logical engine**, layer-split only. The
earlier COMBINED-OT (expert-split) variant for the MoE profile has no model
entry left in `infra/hydra-core/config/models.json` — MoE now routes through
P/D split with the P100 instead (`moe-35b-pd`). The OT-specific plumbing
(`--combined-ot-pattern`, `combined_ot_split`) still exists in code but isn't
exercised by any current model config; don't assume it's wired up without
checking `MultiEngineRouter.cs` and `models.json` first.

## COMBINED (layer-split)

New in #383. Uses `--combined-split-mode layer` + `--combined-tensor-split
r0/r1` to register the peer RPC device BEFORE model load. The stock
tensor_split allocator places whole layers on one device, avoiding
recurrent-layer state corruption across the RPC boundary.

- Peer (3060): `--peer-only` mode — no model, just GPU backend + HTTP health.
- Head (5060 Ti): reaches the peer over `rpc-engine: "localhost:9504"`
  (static in `node-rtx.yaml`); the actual split (`split_mode`, `tensor_split`,
  `rpc_servers`) comes from the per-model `engine_config` in `models.json`,
  pushed dynamically via `PREFILL`/`EngineConfig` (0x40) — not a static CLI
  profile anymore.
- Currently only one COMBINED model exists: `dense-27b-combined`
  (`split_mode: layer`, `tensor_split: [25.0, 40.0]`, `rpc_servers:
  ["rtx3060:9504"]`).
- `MultiEngineRouter.Select` returns a Plan when `cfg.CombinedEnabled` is on
  and the head advertises combined capability; `workers.json` sets
  `rtx.role="head"`, `rtx.peer_worker="rtx3060"`, `rtx.combined_capable=true`,
  `rtx3060.combined_capable=true`, `rtx3060.slots=0` (peer-only, no Core-driven
  model load).

## Selection

Not a manually toggled profile — `AutoRouter` selects `dense-27b-combined`
per-request like any other named model in `models.json`
(`routing.requires_workers: ["rtx3060"]`, `default_eligible: false`, so it's
only picked when something explicitly routes to it, not by default).

Config (single source of truth, unified in #481 Phase 2c — no more
per-profile file pairs):
- `infra/hydra-head/config/node-rtx.yaml` / `node-rtx3060.yaml` — model-agnostic
  head/peer process config (infra only; no per-model split settings).
- `infra/hydra-core/config/workers.json` — single model-agnostic workers config
  (3060 is peer-only, `slots=0`).
- `infra/hydra-core/config/models.json` — single source of truth for all
  per-model runtime config (`split_mode`, `tensor_split`, `rpc_servers`, …).

**Stale, do not use:** `scripts/set-profile.sh {moe|dense}` and
`.env-moe`/`.env-dense` still exist and are still wired into CI/deploy, but
`.env-dense` points at `node-rtx-27b.yaml` / `workers-27b.json` — files that
were deleted as part of the #481 unification above. Running `set-profile.sh
dense` today will not produce a working deploy.
