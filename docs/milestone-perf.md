# Milestone M-Perf — Heterogeneous Performance (Tier-1)

> Committed next milestone. Supersedes the old monolithic "M3 Production". Source
> rationale: the heterogeneous-inference research synthesis — see
> `src/docs/REVOLUSION_PLAN_01_JUN.MD`.
>
> **Restructured 2026-06 around `llama-engine`.** P/D split MVP is done (#84 verified).
> The primary track is now the **phase-optimized inference engine** (#161) — a dedicated
> C++ binary linking `libllama` + `libggml` + `common` that Hydra drives entirely over
> binary RPC. Prompt compression (#119/#121/#125/#126) is **deprioritized** — the engine
> is designed extensibly to support future compression, Ring Attention, and other features.

> **Note (post PR #203):** the Python coordinator is deprecated/removed. "Coordinator"
> below means the **coordinator role inside Hydra.Core** — the single C# binary that owns
> routing, sessions, Store, and all policy. All file references point at `src/core/Hydra.Core`.

## Architecture — `llama-engine` (phase-optimized inference)
```
Hydra.Core ── binary control RPC (0x4859) ──▶ llama-engine
   methods: CONFIGURE/INFO · PREFILL · DECODE(stream) · STATE_GET/PUT/META
            SET_EXPERT_MODE · SWAP_QUANT
   (no new CLI params; Hydra drives all per-request behavior over RPC)

Hydra.Core ── HTTP/1.1 + SSE ──▶ llama-engine
   endpoints: /health · /version · /slots · /slots/:id/state/meta · /v1/chat/completions
   (easier testing and interaction; SSE for streaming decode responses)

llama-engine ── ggml --rpc-engine ──▶ P100 rpc-server
   (load-time COMBINED expert split / dense TP)
```
`tools/llama-engine/` is a new CMake target linking `server-context` + `llama-common` +
`llama` (same pattern as `llama-cli`). All Hydra orchestration lives there; the llama.cpp
core stays pristine. Additive core APIs live in `include/llama-hydra.h` + `src/llama-hydra.cpp`.

**HTTP server:** llama-engine exposes HTTP endpoints (default port 8080) for easier testing
and interaction. Uses cpp-httplib (already vendored) with Server-Sent Events (SSE) for
streaming. HTTP/1.1 chosen over HTTP/3 to avoid massive dependencies (nghttp3, quiche,
boringssl) — can add HTTP/2/3 later if needed.

## Tasks — Engine milestones (E0–E3)

### E0 — Engine skeleton + clean-separation scaffold (#161-E0)
New `tools/llama-engine/` target (lib + thin `main.cpp`) linking `server-context` +
`llama-common` + `llama`. Reuse existing slot/batching/inference + state-RPC. Establish
`include/llama-hydra.h` + `src/llama-hydra.cpp` isolation; relocate `llama_io_write_socket`;
rename `--rpc` → `--rpc-engine`. Graceful degradation when peer is down.
**Parity test:** token-for-token identical to `llama-server` (greedy, same seed). Both
sm_120 + sm_60. Dense model parity with small model.

### E1 — Control-RPC plane (Hydra drives the engine) (#161-E1)
Extend `0x4859` RPC with engine-control opcodes: `CONFIGURE`/`INFO`, `PREFILL`
(n_predict=0, returns n_past), `DECODE` (streaming tokens via chunked response).
Reuse `STATE_GET/PUT/META`. Update `specs/rpc-protocol.md`; add client methods to
`Hydra.Shared`. Full prefill → STATE_GET → STATE_PUT → DECODE(stream) cycle over RPC.

**HTTP server (implemented):** llama-engine exposes HTTP endpoints for easier testing:
- `GET /health` — liveness check
- `GET /version` — engine version info
- `GET /slots` — list slot states
- `GET /slots/:id/state/meta` — slot metadata
- `POST /v1/chat/completions` — OpenAI-compatible API with SSE streaming

Uses cpp-httplib (vendored) + SSE for streaming. See `specs/rpc-protocol.md` for details.

### E2 — Per-request expert placement (solo ↔ combined) (#161-E2)
**Spike first:** test two approaches — (A) `ggml_backend_sched_set_tensor_backend()`
(zero core touches), (B) dual tensors + branch in `build_moe_ffn`. Measure switch cost,
same-mode tg, SOLO decode behavior, dual-tensor correctness, RAM/VRAM budget.
Deliverable: `docs/spike-engine-expert-mode.md` + go/no-go.

### E3 — Dynamic quant swap (#200) (#161-E3)
**Spike first:** re-bench swap latency with pre-allocation; correctness round-trip
Q3_K→Q6_K→Q3_K; KV persists; CUDA kernel dispatch picks up new quant type.
Deliverable: `docs/spike-engine-quant-swap.md` + go/no-go.

## Tasks — Prompt compression (deprioritized)

### M-Perf.5.1 — Fork: prompt-token logprobs on `/completion`  (#125)
### M-Perf.5.2 — Surprisal sentence pruning (model-based)  (#119)
### M-Perf.5.3 — Semantic summary compression  (#121)
### M-Perf.5.4 — Compression quality + TTFT harness  (#126)

These are deprioritized in favor of the engine track. The engine is designed to be
extensible — compression, Ring Attention, and other features can be added as new
RPC methods without restructuring the engine.

## M-Perf.9 — Engine model identity + cross-model KV safety (#289)

A KV cache built with one model is incompatible with a different model — restoring a
Mini-built cache into a Balanced-loaded slot silently produces a corrupted response.
The Coordinator therefore tracks the model identity alongside every KV blob.

### Tasks

- **M-Perf.9.1 — Engine PREFILL accepts `model`** (#289 / `EnginePrefill` opcode `0x42`).
  The engine reuses the existing `--rpc-port` (RTX `:9503`, P100 `:9502`) — no new
  binary, no new port. When `model` is present in the PREFILL payload and the engine
  has it in its preset registry, the engine swaps; when absent, no swap; when
  unknown, the engine falls back to the resident model and sets `model_fallback: true`
  in the response. The C++ engine uses the same `common_preset_context::load_from_ini`
  parser llama-server router mode already uses (`common/preset.cpp:315-365`).
- **M-Perf.9.2 — Slot META carries model identity** (opcodes `0x30` STATE_GET and
  `0x32` STATE_META). New fields `model_alias` (the preset alias or filename),
  `model_hash` (64-char hex SHA-256 of the GGUF file), `model_path` (the GGUF file
  the model was loaded from). Hash computed once at model load via
  `llama_hash_file_sha256` (self-contained, no OpenSSL).
- **M-Perf.9.3 — Cross-model KV safety** in `RestoreKvAsync`. After the StatePut
  succeeds, the Coordinator queries the slot META and compares the slot's
  `model_hash` against the stored KV's `model_hash`. On mismatch:
  - `HYDRA_COORD_ALLOW_CROSS_MODEL_KV_REUSE=false` (default) — abort + erase slot +
    re-prefill on the correct model. Metric: `hydra_cross_model_kv_aborted_total`.
  - `HYDRA_COORD_ALLOW_CROSS_MODEL_KV_REUSE=true` — warn + proceed. Metric:
    `hydra_cross_model_kv_warned_total`.
- **M-Perf.9.4 — Engine INFO advertises capabilities** (opcode `0x41`). Returns
  `{engine, version, capabilities, preset_aliases}`. New capabilities: `preset`,
  `model_hash`. Coordinator caches per-worker; controls whether to send `model` in
  PREFILL.
- **M-Perf.9.5 — Stub `EnginePipelineAttach` (0x46)** to wire in #287. Returns
  `HYDRA_STATUS_NOT_IMPLEMENTED` (new status `0x06`) so the Coordinator can
  distinguish "not yet built" from a real error and fall back to solo.
- **M-Perf.9.6 — `X-Hydra-Model` and `X-Hydra-Model-Hash`** response headers so
  clients can correlate responses with the model that produced them.

### Verification

- `pytest tests/system` exercises the live stack: prefill with `model=balanced`
  swaps the RTX preset; prefill with `model=unknown_alias` returns
  `model_fallback: true`; restoring a Mini-built cache into a Balanced-loaded slot
  aborts + re-prefills by default and proceeds with warning when the flag is true.
- `tests/system` for `GET /slots/{id}/state/meta` returns the 3 new fields with
  the SHA-256 hash matching a reference hash of the same GGUF file.
- New metrics visible in Grafana: `hydra_cross_model_kv_aborted_total`,
  `hydra_cross_model_kv_warned_total`, `hydra_model_fallback_total`.

### Back-compat

Old KV blobs in the Store have no `model_hash` in their meta. The cross-model guard
treats `bothEmpty` as "skip" — pre-feature data is always restored. Pre-feature
`SlotMeta` reads default the new fields to empty strings, so a META endpoint on an
older binary returns empty values and the Coordinator behaves as before. No
migration required.

## Owned elsewhere
- **Agent-driven context management (#120 → M5).** The *zero-model* heuristic "what history to
  keep / when to condense" decision is a **semantic, agentic** concern — it moved to the M5
  Agentic milestone. M-Perf keeps only the trivial token-budget trigger.

## Explicitly NOT in scope
- Speculative decoding / MTP as a *prefill* optimization (it only helps decode) — dropped.
- TP-over-TCP (infeasible without forking ggml; loses Blackwell sm_120 + MoE opts).
- Helix MILP / HexGen graph-partition (cluster-scale; 2 nodes don't need it).
- Full Tier-2 PRP/Halda subsystem (only after Tier-1 numbers justify it).

## Verification
- **Engine parity (E0/E1):** token-for-token identical to `llama-server` (greedy); full
  prefill→state→decode over control RPC.
- **Spikes (E2/E3):** switch cost, same-mode tg, solo-decode behavior, swap latency,
  KV persistence — each a committed go/no-go doc.
- **E2E:** `pytest tests/system` against the engine (prefill/decode, state transfer,
  combined-RPC ~544/59, swap-P/D).
- **Build/deploy:** sm_120 + sm_60; bump `src/llama-cpp` submodule; Grafana regression.
- **Quality gate (#187):** HellaSwag P/D (Mini→Balanced) before shipping cross-quant P/D.
