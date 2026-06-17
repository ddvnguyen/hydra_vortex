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

llama-engine ── ggml --rpc-engine ──▶ P100 rpc-server
   (load-time COMBINED expert split / dense TP)
```
`tools/llama-engine/` is a new CMake target linking `server-context` + `llama-common` +
`llama` (same pattern as `llama-cli`). All Hydra orchestration lives there; the llama.cpp
core stays pristine. Additive core APIs live in `include/llama-hydra.h` + `src/llama-hydra.cpp`.

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
