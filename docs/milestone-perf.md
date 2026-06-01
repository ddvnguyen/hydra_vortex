# Milestone M-Perf — Heterogeneous Performance (Tier-1)

> Committed next milestone (~6–8 weeks). Supersedes the old monolithic "M3
> Production". Source rationale: the heterogeneous-inference research synthesis
> (spec-decoding, P/D disaggregation, prima.cpp/Halda) — see
> `src/REVOLUSION_PLAN_01_JUN.MD`.

## Why
Hydra's existing "prefill-on-RTX → migrate KV → decode-on-P100" path is already
proto prefill/decode disaggregation. The P100's 720 GB/s HBM2 (> RTX's 448 GB/s)
makes it a strong **decode/draft** node. This track *extends what exists* to close
the biggest waste (P100 idle during RTX decode) and cut long-context TTFT.

Realistic expectation: **1.5–2×**, not the papers' 5–17× (that win hides disk-load
latency Hydra doesn't have — 64 GB RAM + 32 GB VRAM).

## Tasks

### M-Perf.1 — `DeviceProfiler` measurement spike — BUILD FIRST
Every downstream choice is gated on numbers we don't have. Agent-side (C#)
microbench surfaced via the debug endpoint + Prometheus, plus a Coordinator
aggregator. Measure at 1K/8K/32K/80K context:
- P100 decode tok/s with **35B-A3B target + 0.5B draft both resident** → gate ≥15–20.
- Draft acceptance rate (Qwen3-0.5B vs MoE+SSM target) on representative prompts → gate ≥30–40%.
- NAT-bridge bandwidth via `iperf3` → <500 MB/s ⇒ streaming essential.
- SSM `--cache-prompt` bug repro + scope → decides if layer-streamed KV is safe.

**Deliverable:** a go/no-go decision doc; results feed every step below.
**Files:** new `src/Hydra.Agent/DeviceProfiler.cs`, extend `AgentMetrics.cs`,
`infra/llama-rtx-node/init/start.sh`; new `src/coordinator/profiler.py`.
**Done when:** the 4 measurements are recorded and gates evaluated.

### M-Perf.2 — Heterogeneous speculative decoding (no fork)
Contingent on M-Perf.1 gates. Launch RTX `llama-server` with `--model-draft`
(Qwen3-0.5B) hosted on the P100 via llama.cpp's RPC backend; add a
`SpecOrchestrator` in the Coordinator for acceptance reporting + `ngram-mod`
fallback. Research-verified 1.4–1.8× lossless decode.
**Files:** `src/Hydra.Agent/AgentConfig.cs` + llama-server launch (`start.sh`);
new `src/coordinator/spec_orchestrator.py`; wire into `router.py`/`proxy.py`.
**Scope note:** changes the P100's role (draft host via RPC vs. today's full
Agent+`llama-server`); reconcile the P100 Agent's responsibilities.
**Done when:** decode tok/s ≥1.4× single-RTX baseline, lossless.

### M-Perf.3 — Streaming chunked-prefill KV / P/D
Evolve the Store/Agent save-restore from "full blob after prefill" to
"layer-chunked KV streamed as each layer completes," hiding transfer behind RTX
prefill compute. Target TTFT on 80K prompts ↓≥3×.
**⚠️ Fork required:** the fork today exposes only *full*-state endpoints
(`/slots/{id}/state`). Layer-granular streaming needs **new fork endpoints (or a
small C extension)** for per-layer state. Plus the SSM-bug workaround: stream
attention-KV layer-by-layer, ship the small SSM state once at the end.
**Files:** `src/llama-cpp` (new layer-state endpoints), new
`src/Hydra.Agent/KVStreamer.cs`, new `src/coordinator/kv_router.py`, extend
`Hydra.Store` chunk API from session-level → layer-level.
**Done when:** TTFT on 80K prompts down ≥3× vs the current sequential path.

### M-Perf.4 — Pipeline scaffolding
Refactor Coordinator from load-balancer → asyncio dataflow of `Stage`s; migrate
the existing session-migration path onto it. Foundation for any future Tier-2
(prima.cpp PRP / Halda) — which stays **deferred** until measurements justify it.
**Files:** new `src/coordinator/pipeline.py`; refactor `router.py`/`state_manager.py`.

## Explicitly NOT in scope
- TP-over-TCP (infeasible without forking ggml; loses Blackwell sm_120 + MoE opts).
- Helix MILP / HexGen graph-partition (cluster-scale; 2 nodes don't need it).
- Full Tier-2 PRP/Halda subsystem (only after Tier-1 numbers justify it).
- draft-on-CPU (i7-12700K AVX2-only; P100 is the better draft host).

## Verification
- Benchmark harness: before/after decode tok/s + TTFT at 1K/8K/32K/80K, recorded.
- Acceptance-rate + draft-hit + save/restore/migration latency as Prometheus
  metrics on the existing Grafana dashboard.
- Go/no-go gates from M-Perf.1 written to a decision doc before M-Perf.2+ start.
