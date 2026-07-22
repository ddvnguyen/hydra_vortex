# COMBINED Engine Mode (2-GPU 5060 Ti + 3060)

> Extracted from CLAUDE.md to keep the handoff file short. Referenced from
> `CLAUDE.md` `## COMBINED engine mode`.

The same-host pair can act as **one logical engine** in two modes:

## COMBINED-OT (expert-split, 35B MoE profile)
FFN expert tensors route to the peer's ggml-RPC backend using `--combined-ot-pattern`.
Used by the MoE profile (default). Requires the 3060 to load the model with partial
CPU offload.

- C#: `MultiEngineRouter.Select` returns a Plan when `estTokens > 4096`,
  `cfg.CombinedEnabled` is on, and the head advertises combined capability.
  `workers.json` sets `rtx.role="head"`, `rtx.peer_worker="rtx3060"`,
  `rtx.combined_capable=true`, `rtx.combined_ot_split="blk\\.([0-9]+)\\.ffn_.*_exps\\.weight=CPU"`.
- Env vars: `HYDRA_LLAMA_ENGINE=true`, `HYDRA_COORD_COMBINED_ENABLED=true`,
  `HYDRA_COORD_PIPELINE_ENABLED=true`, `HYDRA_COORD_MULTI_ENGINE_POLICY=combined`,
  `HYDRA_COORD_MULTI_ENGINE_THRESHOLD=4096`.

## COMBINED-static (layer-split, Dense profile)
New in #383. Uses `--combined-split-mode layer` + `--combined-tensor-split r0/r1`
to register the peer RPC device BEFORE model load. The stock tensor_split allocator
places whole layers on one device, avoiding recurrent-layer state corruption across
the RPC boundary.

- Peer (3060): `--peer-only` mode — no model, just GPU backend + HTTP health
- Head (5060 Ti): `--combined-split-mode layer --combined-tensor-split 21/44`
- Every request uses COMBINED (threshold=0 via `HYDRA_COORD_MULTI_ENGINE_THRESHOLD=0`)
- Profile switching: `bash scripts/set-profile.sh {moe|dense}`

## Profiles
Two profiles can be switched via env vars or the helper script:

| Profile | Model | Routing | 3060 role |
|---------|-------|---------|-----------|
| **moe** | Qwopus3.6-MoE-35B-A3B-v1-APEX-I-Mini | COMBINED-OT + P/D split | Peer with model (partial CPU offload) |
| **dense** | Qwopus3.6-Dense-27B-Coder-Compat-MTP | COMBINED-static layer-split | `--peer-only` backend (no model) |

Profile files (after #481 Phase 2c — single model-agnostic profile):
- `infra/hydra-head/config/node-rtx.yaml` / `node-rtx3060.yaml` — model-agnostic head/peer node configs
- `infra/hydra-core/config/workers.json` — single model-agnostic workers config (3060 is peer-only, slots=0)
- `infra/hydra-core/config/models.json` — single source of truth for all per-model runtime config (split_mode, tensor_split, rpc_servers, …)

(Pre-#481 used `.env-moe` / `.env-dense` profile pairs and `node-rtx-27b.yaml` / `workers-27b.json` variants — all removed because per-model config moved into models.json.)
