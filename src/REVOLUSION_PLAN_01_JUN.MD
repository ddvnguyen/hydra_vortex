# Hydra — Restructured Roadmap + Tier-1 Performance Track (Executable Plan)

## Context
Goal: **learn/build the system**, scope reconsidered deliberately. After auditing
plan-vs-implementation (M0–M2 done, M3 ~40%, `main` CI red, an uncommitted revert
stripping obs metrics) and synthesizing the heterogeneous-inference research
(spec-decoding, P/D disaggregation, prima.cpp/Halda), you committed to the **Full
Tier-1 performance track** and restructured the production roadmap. This plan
captures that decision and the immediate executable steps.

Central insight: Hydra's existing "prefill-on-RTX → migrate KV → decode-on-P100"
path is already proto P/D-disaggregation, and the P100's 720 GB/s HBM2 (> RTX's
448 GB/s) makes it a strong decode/draft node. The performance track *extends what
exists* rather than rebuilding it.

---

## Restructured Roadmap (supersedes old M3)

| MS | Theme | Scope |
|----|-------|-------|
| **Phase 0** | Stabilize | Green CI/CD (in flight), **reverse** the obs-stripping revert, restore slot-wait. Foundation for everything. |
| **M-Perf** | Heterogeneous Performance (**committed next**) | Tier-1: DeviceProfiler spike → speculative decoding → streaming chunked-prefill KV → Pipeline scaffolding. |
| **M3** | Persistence & Real Obs | NVMe write-behind persistence **re-spec'd in C#**; harden monitoring/obs "for real"; CI/CD. |
| **M4** | Model Management & Multi-Modal | Model distribution from Store; dynamic model load/swap; vision + embedding + audio model support. |
| **M5** | LLM Obs & Agentic | Langfuse tracing; A/B testing; agentic system. |

Old M3.3/M3.4 (Langfuse, model-dist, systemd) are re-homed into M3/M4/M5 above.
Action: update `PROJECT_PLAN.md` milestone table + `CLAUDE.md`; add
`docs/milestone-perf.md`, refresh `docs/milestone-3-production.md` (C# persistence),
add `docs/milestone-4-models.md`, `docs/milestone-5-agentic.md`.

---

## Phase 0 — Stabilize (do first, ~1 week)
1. **Reverse the uncommitted revert.** You want obs "for real," so restore the
   stripped instrumentation in `src/coordinator/state_manager.py`
   (`agent_save_duration`, `agent_restore_duration`, `migrations_total`,
   `migration_latency`, `from_node_name`) and the `WaitForIdleSlotAsync` slot
   polling + debug-endpoint crash-restart in `src/Hydra.Agent/AgentServer.cs` /
   `LlamaClient.cs`. Decide per-item: keep (default) vs. intentionally drop.
2. **Green `main`.** Triage CI failures #67 (build-test), #68 (E2E), #69
   (integration); fix-forward. CI/CD is already being built — fold these in.
3. Close stale process debt: update auto-memory (`reviews/` workflow is gone;
   findings are GitHub issues now).

## M-Perf — Tier-1 Performance Track (committed, ~6–8 weeks)

### M-Perf.1 — Measurement spike: `DeviceProfiler` (weeks 1–2) — BUILD FIRST
Every downstream choice is gated on numbers you don't have. Agent-side (C#)
microbench surfaced via the debug endpoint + Prometheus, plus a Coordinator
aggregator. Measure at 1K/8K/32K/80K context:
- P100 decode tok/s with **35B-A3B target + 0.5B draft both resident** → gate ≥15–20.
- Draft acceptance rate (Qwen3-0.5B vs MoE+SSM target) on representative prompts → gate ≥30–40%.
- NAT-bridge bandwidth via `iperf3` → <500 MB/s ⇒ streaming essential.
- SSM `--cache-prompt` bug repro + scope → decides if layer-streamed KV is safe.
**Deliverable:** a go/no-go decision doc; results feed every step below.
Files: new `src/Hydra.Agent/DeviceProfiler.cs`, extend `AgentMetrics.cs`,
`infra/llama-rtx-node/init/start.sh`; new `src/coordinator/profiler.py`.

### M-Perf.2 — Heterogeneous speculative decoding (Tier 1.1) — no fork
Contingent on M-Perf.1 gates. Launch RTX `llama-server` with `--model-draft`
(Qwen3-0.5B) hosted on P100 via llama.cpp RPC backend; add `SpecOrchestrator` in
Coordinator for acceptance reporting + `ngram-mod` fallback. Research-verified
1.4–1.8× lossless decode; ends P100-idle-during-decode waste.
Files: `src/Hydra.Agent/AgentConfig.cs` + llama-server launch (`start.sh`);
new `src/coordinator/spec_orchestrator.py`; wire into `router.py`/`proxy.py`.
**Scope note:** this changes the P100's role (draft host via RPC vs. today's
full Agent+`llama-server`); reconcile the P100 Agent's responsibilities.

### M-Perf.3 — Streaming chunked-prefill KV / P/D (Tier 1.2)
Evolve the existing Store/Agent save-restore from "full blob after prefill" to
"layer-chunked KV streamed as each layer completes," hiding transfer behind RTX
prefill compute. Target TTFT on 80K prompts ↓≥3×.
**⚠️ Scope gate (fork required):** the fork today exposes only *full*-state
endpoints (`/slots/{id}/state`). Layer-granular streaming needs **new fork
endpoints (or a small C extension)** for per-layer state — this is NOT pure
Python. Plus the SSM-bug workaround: stream attention-KV layer-by-layer, ship the
small SSM state once at the end. Gate this step on M-Perf.1's SSM + bandwidth findings.
Files: `src/llama-cpp` (new layer-state endpoints), new
`src/Hydra.Agent/KVStreamer.cs`, new `src/coordinator/kv_router.py`, extend
`Hydra.Store` chunk API from session-level → layer-level.

### M-Perf.4 — Pipeline scaffolding (Tier 1.3)
Refactor Coordinator from load-balancer → asyncio dataflow of `Stage`s; migrate
the existing session-migration path onto it. Foundation for any future Tier-2
(prima.cpp PRP / Halda) — which stays **deferred** until measurements justify it.
Files: new `src/coordinator/pipeline.py`; refactor `router.py`/`state_manager.py`.

## Explicitly NOT now (scope discipline)
- TP-over-TCP (infeasible without forking ggml; loses Blackwell sm_120 + MoE opts).
- Helix MILP / HexGen graph-partition (cluster-scale; 2 nodes don't need it).
- Full Tier-2 PRP/Halda subsystem (only after Tier-1 numbers justify it).
- draft-on-CPU (i7-12700K AVX2-only; P100 is the better draft host).

## Expectation-setting (honest)
- Realistic gain **1.5–2×**, not the paper's 5–17× (that win hides disk-load
  latency Hydra doesn't have — 64 GB RAM + 32 GB VRAM).
- MoE+SSM draft acceptance is **unverified** (all cited benches are dense) — the
  spike must measure it; `ngram-mod` is the zero-VRAM fallback.

## Verification
- Benchmark harness: before/after decode tok/s + TTFT at 1K/8K/32K/80K, recorded.
- Acceptance-rate + draft-hit + save/restore/migration latency as Prometheus
  metrics on the existing Grafana dashboard (this is the "obs for real" payoff).
- Go/no-go gates from M-Perf.1 written to a decision doc before M-Perf.2+ start.
- E2E test extended to cover the spec-decode and P/D-streaming paths; `main` green.

## First concrete actions on approval
1. Phase 0 step 1–2: reverse the revert (restore obs + slot-wait), triage CI #67/#68/#69.
2. Scaffold `DeviceProfiler` + benchmark harness; run the 4 gating measurements.
3. Write the go/no-go decision doc; proceed to M-Perf.2 if gates pass.
