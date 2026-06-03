# Milestone M-Perf — Heterogeneous Performance (Tier-1)

> Committed next milestone. Supersedes the old monolithic "M3 Production". Source
> rationale: the heterogeneous-inference research synthesis — see
> `src/docs/REVOLUSION_PLAN_01_JUN.MD`.
>
> **Restructured 2026-06 around prompt compression.** The original spec-decoding track
> (M-Perf.1 DeviceProfiler #82, M-Perf.2 heterogeneous spec-decode #83) is **dropped** —
> see "Why the pivot" below.

## Why the pivot — attack prefill, not decode
The headline pain is **prefill**: an 80K-token prompt takes ~12 min on the P100 because
attention prefill is **O(N²)** in prompt length N. Speculative decoding (and MTP, already
enabled via `--spec-type draft-mtp`) only accelerates the **decode** loop — it does
**nothing** for prefill. So spec-decode can't touch the 12-min wall.

The lever that *does*: **reduce N**. A small "gatekeeper" model shortens the prompt
**before** the big model prefills it → N drops → prefill drops quadratically. This is
prompt compression / context distillation (LLMLingua / Selective-Context family).

**Key advantage over spec-decode:** compression works in **text space** — the gatekeeper
scores importance in its own tokens, we emit *text*, and the big model re-tokenizes. So
there is **no draft↔target vocab-parity requirement** (the blocker that sank #83).

Realistic expectation: large TTFT cuts on long prompts (the dominant agentic/RAG case),
bounded by a quality gate.

## Architecture — the compression gatekeeper
```
Client → Coordinator
  ├─ token-budget trigger: prompt over budget?  (trivial threshold; the "what to keep"
  │                                               policy is agentic — see M5/#120)
  ├─ if over budget → gatekeeper llama-server (small model on P100) scores per-token
  │     surprisal via the /completion prompt_logprobs flag (M-Perf.5.1)
  ├─ prune low-information sentences (5.2) and/or summarize the middle (5.3)
  └─ send the shortened prompt → big model (RTX) prefill + decode
```
Cross-cutting rules for every compression step:
- **Text-space, no vocab parity** — any small model works as the gatekeeper.
- **Deterministic** (greedy, fixed params): identical input → identical compressed output,
  or Hydra's KV-cache reuse breaks (every request would prefill a fresh sequence).
- **System prefix kept verbatim** → the existing prefix-checkpoint cache still hits;
  compress only the document/history *middle*.
- **No new container.** The gatekeeper is a small-model `llama-server` instance (a systemd
  unit on the P100 — bare metal, not Docker). Pruning/summary **policy lives in the
  Coordinator**. The only llama.cpp fork change is the `prompt_logprobs` flag (5.1).
- **Gatekeeper model:** a small model (Qwen3.5-0.8B / 2B), used purely as compressor.

## Tasks

### M-Perf.5.1 — Fork: prompt-token logprobs on `/completion`  (#125)
The **only** fork change, kept minimal + upstream-mergeable. Add a `prompt_logprobs` flag
to `/completion` that returns per-prompt-token logprob/surprisal from one forward pass
(mirrors vLLM `prompt_logprobs`). No compression logic in the server — it exposes data; the
Coordinator owns policy. **Files:** `src/llama-cpp/tools/server/server.cpp` (~50–80 lines).

### M-Perf.5.2 — Surprisal sentence pruning (model-based)  (#119)
Coordinator uses 5.1's prompt logprobs from the gatekeeper to prune the lowest-information
**sentences/segments** down to the token budget; keep system + first user msg + last-K turns
verbatim. No new RPC opcode, no Agent hop (HTTP-direct to the gatekeeper, like `proxy.py`).
**Files:** new `src/coordinator/compression.py` (pruning); wire into `router.py`/`proxy.py`.

### M-Perf.5.3 — Semantic summary compression  (#121)
For document-heavy prompts where pruning is insufficient, replace the compressible middle
with a short gatekeeper-generated summary (deterministic; sentinel marker). Quality-gated by
5.4. **Files:** extend `src/coordinator/compression.py`.

### M-Perf.5.4 — Compression quality + TTFT harness  (#126)
Replaces the dropped DeviceProfiler's measurement/gating role. Measure TTFT before/after,
token savings, perplexity delta, answer-match, and the gatekeeper's own forward-pass cost at
1K/8K/32K/80K; Prometheus + Grafana; a go/no-go decision doc with recommended defaults.
**Files:** new `src/coordinator/compression_bench.py`; extend metrics.

### M-Perf.6 — Streaming chunked-prefill KV / P/D  (#84) — *deprioritized*
A **complementary** prefill lever (hide KV transfer behind RTX prefill compute), independent
of compression. ⚠️ Fork-heavy (needs per-layer state endpoints). Revisit after the
compression TTFT numbers (5.4) land.

### M-Perf.7 — Pipeline scaffolding  (#85) — *deprioritized*
Refactor Coordinator from load-balancer → asyncio `Stage` dataflow; foundation for any future
Tier-2 work. Later.

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
- TTFT before/after at 1K/8K/32K/80K recorded (5.4); net win = `prefill_saved −
  compressor_cost > 0` demonstrated per context size.
- Compression ratio + ppl delta + answer-match + save/restore latency as Prometheus metrics
  on the existing Grafana dashboard.
- Quality gate (5.4) passed before aggressive compression ships; decision doc committed.
