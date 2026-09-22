# Prefill fast-path prototype — design concept (DRAFT)

**Status:** design concept, pre-implementation. Two mechanisms, one blocking rig defect.
**Scope:** prefill only. Decode is untouched (its levers are the slab `#132` and session-warm ranking).
**Branch reality:** the cached-MoE machinery exists only on `feat/763-reconcile-qwen4exp-mtp` (+4 profiler commits = `feat/profiler-on-763`, the current checkout). `baseline-flash-next` (production build-g3) has **no** `moe-cache.cu`, no `--moe-expert-cache-size`, no `mul_mat_id_cached`. The prototype lands on the 763 lineage.

---

## 0. Executive summary

| # | Mechanism | Expected | Confidence | Cost |
|---|---|---|---|---|
| **A** | Prefill-only k reduction (k=8 during prefill, k=10 decode) | **1.15–1.20×** prefill | medium — depends on the FLOP-share leg | 2-line graph patch + quality leg |
| **B** | Routing-independent prefill expert streaming (union prefetch on the copy stream, GPU compute, layer-ahead double buffer) | **2.4–3.5×** prefill on CUDA0 | medium — depends on the H2D sustained rate | build on existing moe-cache machinery |
| **C** | **Rig defect, blocking:** the 3060's PCIe link runs at **Gen1 x4 / 0.39 GB/s** instead of Gen4 x4 / ~6 GB/s | — | **measured, reproducible** | retrain/reseat — owner action |

A and B compose: B's transfer volume is proportional to the union, and A shrinks the union (0.896× at k=8).

---

## 1. Measured baseline

### 1.1 Geometry (read from the production GGUF, not assumed)

```
qwen4exp.expert_count 512 · expert_used_count 10 · block_count 48
embedding_length 2560 · expert_feed_forward_length 640 · shared 640
attn: 24 heads / 2 KV heads · key_length 256
```

| Quantity | Value | Source |
|---|---|---|
| Expert tensors per layer | `ffn_{gate,up}_exps` (2560×640×512), `ffn_down_exps` (640×2560×512) | GGUF tensor table |
| Per-expert bytes | **1.7838 MiB** | 913.3 MiB/layer ÷ 512 |
| Per-layer bank | **913.3 MiB** (blk.8 = 887.5; layer-dependent quant, e.g. blk.0 gate is Q5_K) | GGUF tensor table |
| All 48 layers | **42.81 GiB** | GGUF tensor table |
| FLOPs per token-expert application | **9.83 MFLOP** (gate+up 6.55 + down 3.28) | 2·(2·2560·640) + 2·(640·2560) |
| FLOPs per layer per 512-token ubatch (k=10) | **50.3 GFLOP** | 512·10·9.83e6 |

### 1.2 The union is the right unit, and it is measured

Computed offline from **64 existing route traces / 428 non-overlapping 512-token prefill windows** (the v2 + v4 packs, `/tmp/opencode/trace-collect/traces/`). `union` = distinct experts touched per layer per ubatch, mean over 48 layers:

| k | union / 512 | expert weight traffic vs k=10 | tokens per expert in the ubatch | expert FLOPs vs k=10 |
|---|---|---|---|---|
| 10 | **292.5** (sd 38.1) | 1.000× | 17.5 | 1.00× |
| 8 | 263.0 | **0.896×** | 15.6 | 0.80× |
| 6 | 226.8 | 0.770× | 12.9 | 0.60× |
| 4 | 181.7 | 0.614× | 10.8 | 0.40× |

Two consequences that constrain both mechanisms:
1. **k-reduction buys far less traffic than it buys FLOPs** (k=8: −10% traffic, −20% FLOPs). Its value therefore depends on prefill being FLOP-bound, not traffic-bound — which is exactly what the D0 leg must establish.
2. **The GEMM is tiny**: ~17 tokens per expert at k=10, i.e. a `[17×2560]×[2560×640]` shape. This is the shape the CPU is being asked to run 292 times per layer per ubatch, and the shape a GPU handles far better.

### 1.3 The cost model for CUDA0 (production config: 39 CPU-expert layers, 9 on GPU)

- Expert FLOPs on the CPU: 39 × 50.3 GFLOP = **1.96 TFLOP per 512-token ubatch**.
- Measured prefill: 141.6 tok/s → **3.62 s per ubatch** → **≈542 GFLOP/s sustained on the CPU**.
- Expert weight traffic: 39 × 292.5 × 1.7838 MiB = **20.3 GiB per ubatch** → 5.6 GiB/s. Host RAM is not the limit.
- 542 GFLOP/s is ~50–70% of what an i7-12700K (AVX2, no AVX-512) can realistically reach on Q4_K GEMMs of this shape. **[INFERENCE]** prefill on CUDA0 is **CPU expert-GEMM-FLOP-bound**, and the GPU half is largely hidden inside that time.
- **This is the load-bearing inference of the whole design.** D0-L2 (§6) falsifies or confirms it in one cheap leg.

---

## 2. Rig defect (blocking) — the 3060's PCIe link is 15× below its capability

Measured this session with a purpose-built probe (`/tmp/pcibw/bw.cu`, CUDA 13.2, `cudaMemcpyAsync` on pinned + pageable buffers, 512 MiB × 20 reps):

| device | link under 100% load | H2D pinned | H2D pageable | D2H pinned | D2D |
|---|---|---|---|---|---|
| RTX 5060 Ti (`0000:01:00.0`) | **Gen5 x8** (of 16 lanes capable) | **25.80 GB/s** | 13.16 GB/s | 28.63 GB/s | 213.6 GB/s |
| RTX 3060 (`0000:02:00.0`) | **Gen1 x4** ← anomalous | **0.39 GB/s** | 0.39 GB/s | 0.42 GB/s | 189.5 GB/s |

Size sweep (16 MiB / 64 MiB / 256 MiB / 1 GiB): the 3060 reads **0.38–0.40 GB/s at every size**, the 5060 Ti **25.7–25.9 GB/s pinned / 13.0–19.5 GB/s pageable at every size**. Flat across sizes ⇒ a hard link cap, not per-transfer overhead.

Topology (sysfs): the 3060 sits on CPU root port `0000:00:06.0`, whose `max_link_speed` is **16 GT/s (Gen4)** and `max_link_width` is 4. The link negotiated **2.5 GT/s (Gen1)** and stays there under load. x4 is the platform's wiring; **Gen1 instead of Gen4 is the defect** — a further 4× loss on top.

### 2.1 Why this matters beyond the 3060's own numbers

- **It invalidates the recorded PCIe elimination for that card.** `docs/findings-moe-placement-campaign.md:231,245` and `leader-handoff-state.md:4208` exclude bandwidth with "rx 1.5% of the **gen4-x4 ceiling**". The link never negotiated Gen4. Against the *measured* ceiling (390 MB/s), the recorded decode rx (median 119 MB/s, **max 374 MB/s**) is **~30% of the link, and the observed maximum is 96% of the probe's measured peak**. A saturated link is not "1.5% of ceiling".
- **It is the prime suspect for Q5.** The 3060's per-prefill fixed cost is **~57.7 s**, unexplained, non-amortising. At 0.39 GB/s, ~22 GiB of traffic takes exactly 58 s. The record's own 2026-09-18/19 number ("one 0.597 MiB miss-expert H2D copy is 78 µs" ⇒ **8.0 GB/s**) implies the link was running at Gen4 x4 in that era. **A 20× link degradation between the two measurement eras is a live explanation for the "remembered 19.85–22.30 tok/s does not reproduce" mystery** that eight candidate eliminations failed to explain.
- **It blocks Mechanism B on the 3060**: streaming 20.3 GiB/ubatch would take 52 s there — worse than today's 14.7 s marginal.

### 2.2 Owner actions (need root; not yet attempted)

1. Retrain the link without a reboot: `setpci -s 00:06.0 CAP_EXP+30.w=0x0004:0x000f` (target speed = Gen4) then `setpci -s 00:06.0 CAP_EXP+10.w=0x0020:0x0020` (retrain), re-read `current_link_speed`.
2. If it returns to Gen1, reseat the card / replace the riser or adapter — an M.2-derived x4 port with a marginal riser is the classic Gen1-negotiation signature.
3. Re-run `/tmp/pcibw/bw 1 512 0` after any change; expected ≥ 5 GB/s H2D if Gen4 x4 negotiates.
4. Until it is fixed, **every 3060 measurement in the record carries an uncontrolled 20× I/O handicap**, and the 3060 legs should be treated as era-confounded.

---

## 3. Mechanism A — prefill-only k reduction

**Idea.** Run k=8 (or 6) while `n_tokens > 1`, keep k=10 for decode. Routing stays the model's own; only the count changes, and only in the phase whose FLOPs we are paying for.

**Why it is not the same as the rejected A1 ablation.** A1 measured *global* k reduction (prefill and decode both degraded) and priced it: k=8 = 1.112× t/s for +5.1% PPL; k=6 = 1.30× for +13.5%; k=4 = 1.54× for +40.1%. Prefill-only at k=8 is strictly *less* damage than global k=8 — decode keeps full fidelity — so **+5.1% PPL is an upper bound on its cost**, and the speedup is concentrated where the FLOPs are.

**Patch surface (verified in the current checkout).**
- `src/llama-graph.cpp:1545` (current checkout) / `:1469` (production `baseline-flash-next`) — the single place k is chosen:
  `n_expert_used (cparams.warmup ? hparams.n_expert : hparams.n_expert_used())`.
  This is a ctor member-init, and every MoE arch consumes the *member*, not the hparams.
- **Mandatory companion edits** — the tail view-loop derives its own bound from the hparams, so it must use the same effective k or the views are malformed: current checkout `src/llama-graph.cpp:2406` (`n_expert_used_il =`) and `:2422` (`if (n_expert_used_il == 1)`); production `:2276`, `:2286`, `:2292`. Verified firsthand on both lineages.
- Gating: `ubatch.n_tokens > 1` **and** `cparams.ctx_type == LLAMA_CONTEXT_TYPE_DEFAULT`. The second gate is not optional — the MTP draft head shares the model object, the hparams and this ctor member, and `common/speculative.cpp:1741` decodes multi-token draft batches, which would otherwise be silently reduced too. (Precedent for ctx_type gating: the moe-lookahead path.)
- Knob: one new `llama_cparams` field (`n_expert_used_prefill`, 0 = off) surfaced as a CLI flag; the ctor already receives `cparams`, so no plumbing is needed beyond the flag.
- CUDA-graph safety: prefill graphs and decode graphs are built separately (`llama-context.cpp:2842` passes `ubatch.n_tokens > 1`), so a phase-dependent k cannot destabilise decode capture.

**Expected gain.** If D0-L2 confirms FLOP-bound behaviour, the expert path is ~100% of prefill time and k=8 removes 20% of its FLOPs → **~1.25×** ceiling, ~1.15–1.20× realistically (union traffic and non-expert work do not shrink). The union model says the traffic component only falls 10%, so do not expect the full 1.25×.

**Quality gate.** Same corpus and harness that produced A1's PPL ladder, prefill-only arm: ship line = PPL within +5% of k=10 **and** ≥1.10× prefill. Prefill writes the KV cache for the whole context, so this is a *poisoning* risk, not a per-token noise risk — the corpus measure is the right instrument, not a spot check.

**Risks.** (i) Draft head — handled by the ctx_type gate. (ii) The `--override-kv` alternative is *not* suitable for the prototype: it writes `n_expert_used_arr` at model load only (`llama-model.cpp:1236-1238`) and is phase-blind; it is however the ideal **diagnostic** for D0-L2, and it needs no patch.

---

## 4. Mechanism B — routing-independent prefill expert streaming

**Idea (FreeToken's prefill regime, adapted).** During prefill the transfer is *routing-independent*: you can move a whole layer's experts (or its per-ubatch union) to VRAM without any predictor, any ids readback, or any per-miss decision. Stream layer L+1's experts on a dedicated copy stream while layer L computes, then compute the expert GEMMs on the GPU against the resident bank. Decode is untouched.

**What already exists** (in `feat/profiler-on-763`; `baseline-flash-next` has none of it):

| Primitive | Location | State |
|---|---|---|
| Cached/grouped expert compute entry | `ggml-cuda.cu:3291` `ggml_cuda_mul_mat_id_cached` | present |
| Graph plan with per-layer groups + authorities | `moe-cache.cu:9611-9640` (`pure_prefill` → `GROUP_REASON_PREFILL` records, `record.prefill = 1`) | present — **prefill groups are already planned** |
| Outcome enum | `moe-cache.cuh:318-321` (`PREFILL_LEGACY`, `DECODE_GROUPED`, `DECODE_LEGACY`, `ERROR`) | present |
| Dedicated copy stream + batched H2D | `moe-cache.cu` (`cudaMemcpyBatchAsync`, `copy_stream`, LFRU guard) | present, **gated on `DECODE_GROUPED`** |
| Phase classification, paging/residency telemetry | `moe-cache.cu` (`phase=prefill\|decode`), `server-context.cpp:803-805` (needs `--moe-expert-cache-size` + `--experimental-logs`) | present |
| Prefill-resident auxiliary banks + witnesses | `moe-cache.cu:11564` `prefill_add_id_source` / `:11663` `finish_prefill_add_id` | present (auxiliary staging only, not the expert banks) |
| Scheduler-level used-expert slice copy | `ggml-backend.cpp:1801-1900` | present — the only prefill weight-streaming code today |
| Loader hook for a cached expert buffer | `moe-cache.cu:17547` `ggml_backend_cuda_moe_cached_buffer_type`, `--moe-expert-cache-size` | present |

**The gap (what the prototype adds).**
1. **Prefill is routed to the legacy compute path.** `moe-cache.cu:9610-9611` sets `PREFILL_LEGACY` for `pure_prefill`, and `ggml-cuda.cu:3803` then calls `ggml_cuda_mul_mat_id_impl` (legacy). For a CPU-resident expert tensor that means the **CPU backend executes it**: `ggml_backend_cuda_device_offload_op` (`ggml-cuda.cu:8249-8252`) keys on `op->ne[2]`, which for `mul_mat_id` is **k = 10**, against `GGML_OP_OFFLOAD_MIN_BATCH` = 32 — the GPU offload rule never fires, regardless of how many tokens are in the ubatch.
2. **No prefill prefetch.** The copy engine is gated on `DECODE_GROUPED` (`moe-cache.cu:4436,4466,4473,4485`).
3. **No layer-ahead double buffer.** Decode prefetches within a step; prefill needs banks alternating across layers.
4. **No per-ubatch union computation.** The ids exist (the legacy path needs them), so the union is a small kernel or a host pass — but it does not exist yet.

**Transfer arithmetic (CUDA0, union-sized banks, 39 CPU layers).**

| Variant | Volume per 512-tok ubatch | @25.8 GB/s (pinned) | @13.2 GB/s (pageable) | vs today's 3.62 s |
|---|---|---|---|---|
| **B-union** (slot-indirected bank, ~292 slots) | 20.3 GiB | **0.79 s** | 1.54 s | **2.4–4.6×** |
| B-bank (whole layer, no indirection) | 34.8 GiB | 1.35 s | 2.64 s | 1.4–2.7× |

B-union wins on volume and reuses the cache's existing slot indirection; the cost is the union pass. GPU compute (1.96 TFLOP at these shapes) is ~0.1–0.2 s and hides under the copy with a double buffer.

**VRAM.** Union-sized double buffer = 2 × 292 × 1.7838 MiB = **1.04 GiB**; whole-bank double buffer = 1.79 GiB. Both fit beside the production placement (9 resident layers ≈ 7.8 GiB of the 16 GB card) if the prefill banks are **phase-scoped**: the static decode placement is not needed during prefill, and re-loading it afterwards is ~70 ms at 25.8 GB/s. This is the single most attractive part of the design — the two phases want different VRAM layouts and neither pays for the other.

**Prerequisite: pinned expert weights.** 25.8 GB/s is the pinned number; pageable is 13.2 GB/s (halves the win but keeps it). Today a host-buffer override is silently downgraded: `llama-model-loader.cpp:1322-1330` rewrites any host buffer type to the plain CPU buffer when `use_mmap` is set. So pinned experts need `--load-mode none` (measured cost on decode: −4.9%, i.e. nil) or a small loader opt-out. This is a real decision, not a detail: it is the difference between a 4.6× and a 2.4× ceiling.

**Why this is not the rejected decode-lookahead design.** That design predicted per-token misses and paid a per-miss fetch + an in-step readback that banned CUDA graphs. This one has **no predictor, no per-token decision, no readback beyond what the base path already needs**, and it is prefill-only — where graphs are not captured. The 36.3 µs/invocation break-even that killed the decode mechanisms does not apply: the unit here is a whole-layer batch transfer amortised over 512 tokens.

**Known hazards to check before building.** The decode-era cached path carries two recorded blockers (the authority invariant refusing pools; a router-census perturbation that changes logits). Whether the prefill group path inherits them is unknown and must be tested with a numerics check, not assumed.

---

## 5. What is *not* in scope (and why)

- **Decode overlap / prefetch flags** — measured nil, Amdahl-capped at 2.5% (drafting = 262 ms of a 10,283 ms wall). Prefill double-buffering is not this: it changes *where compute runs*, not *when a fetch is issued*.
- **Subject-ranked expert selection** — measured +2–3%, coherence test falsified; retained as a telemetry dimension only.
- **Prefill ranking as a decode predictor** — dead (h_prefill 0.2383 vs h_global 0.3363).
- **Increasing `n_ubatch` alone** — worth folding into D0 as a free variable (`-b 512 -ub 512` is needed for per-ubatch instrumentation anyway); the union saturates near the bank, so larger ubatches only amortise the transfer, they do not shrink it.

---

## 6. Measurement plan — D0 first, before any build

All legs are **zero-patch**: the knobs already exist. `-b 512 -ub 512` makes each `llama_decode` exactly one ubatch, which makes the existing `PROF prefill` line and the server's `prompt_ms` per-ubatch-accurate without touching the profiler (whose `t_dev` is otherwise the *last* ubatch only).

| Leg | Config | Question it answers |
|---|---|---|
| **L0** ✅ done | `/tmp/pcibw/bw {0,1} 512 0` | link state + real H2D/D2H per card (§2) |
| **L1** | CUDA0, production config, fixed 512-token prompt, `-b 512 -ub 512`, ×5 | baseline per-ubatch prefill time, variance |
| **L2** | + `--override-kv qwen4exp.expert_used_count=int:1` | **the FLOP-share discriminator.** ≥1.6× faster ⇒ FLOP-bound ⇒ A and B both live. ≤1.3× ⇒ the expert FLOP path is not the cost ⇒ stop and re-diagnose |
| **L3** | + `int:8` | validates the union model's prediction (0.896× traffic, 0.80× FLOPs) against a real number |
| **L4** | `-ncmoe` sweep (0, 1, 2 extra layers on GPU) | per-layer expert-placement slope **for prefill** (decode's is 0.32–0.42 tok/s/layer) |
| **L5** | two identical prefills in one process, both cards | re-pays the ~57.7 s fixed cost? (the Q5 recipe) — and whether it tracks the link |
| **L6** | L1 while sampling `nvidia-smi dmon` + `/proc` (minflt/majflt, pgpgin/out) | sustained PCIe rate + fault traffic during prefill; confirms or kills B's transfer model |

Instruments that exist today: the one-shot `PROF prefill` line (`llama-context.cpp:3809-3814`), server `prompt_ms` + the cumulative `prompt processing` progress line (`server-context.cpp:735-747`), `llama_perf_context` counters, `n_reused`. **Absent from this tree:** `HYDRA_EXPERT_STATS` and the e0 per-(layer,site) rings (they live only in the stamped `build-e0-eager` / `build-g3` binaries).

---

## 7. Gates and kill criteria

| Gate | Threshold | Consequence if failed |
|---|---|---|
| D0-L2 FLOP share | ≥1.6× at k=1 | Mechanism A capped below its price; B's premise weakens — re-diagnose before building |
| D0-L6 sustained H2D | ≥20 GB/s on CUDA0 | B-union degrades to B-bank/pageable arithmetic or dies |
| Mechanism A quality | PPL ≤ +5%, prefill ≥1.10× | drop A (B does not depend on it) |
| Mechanism B same-session A/B | ≥1.15× at matched quality **and** VRAM | kill B |
| 3060 link | ≥5 GB/s H2D after retrain | all 3060 legs stay era-confounded; B is CUDA0-only |

---

## 8. Next actions

1. **Owner:** retrain the 3060's link (§2.2) — it is a 15× rig defect and the cheapest large win available.
2. **Rig (D0):** L1→L3 on CUDA0; L5 on both cards. Decides whether A and B are worth building.
3. **Zero-rig, in parallel:** (a) union-per-ubatch numbers are already computed here — fold them into `#771`'s telemetry contract; (b) sketch the A patch against `llama-graph.cpp:1545` + `:2406`; (c) locate the prefill gate in `moe-cache.cu:9610` and enumerate what a `PREFILL_GROUPED` outcome would need to satisfy the plan/authority checks.
4. **Decision needed before B is built:** pinned expert weights (`--load-mode none`) or pageable — 4.6× vs 2.4× ceiling, at a measured −4.9% decode cost.

---

### Provenance

- Geometry: `gguf` reader over `/mnt/SSD/qwen3.8-flash-next-apex-mini/Qwen3.8-Flash-Next-APEX-I-Mini-*.gguf`.
- Union table: `tools/atlas/trace_io.py`-compatible parse of 64 `.route` traces (`/tmp/opencode/trace-collect/traces/{v2,v4}`), 428 windows of 512 prefill positions, `prefill_tokens` from the per-trace sidecars. **Rank-order truncation is verified at the source**: `examples/route-trace/route-trace.cpp` (commit `75c879787`) dumps the `ffn_moe_topk-<il>` tensor in its natural k-order, and `ggml_top_k` sorts descending (`cmp_top_k`: `data[a] > data[b]`, `ggml/src/ggml-cpu/ops.cpp:8600`). So the first k entries of a row *are* the router's top-k at that k.
- **Caveat on the same table:** the recorder evaluates the prompt **one token per decode** (`route-trace.cpp`: "prompt is evaluated one token at a time so every row maps to a single position"). Routing is per-position, so the union model is structurally sound, but it models batched-prefill routing rather than measuring it. D0-L3 is the test: predicted 0.896× traffic / 0.80× FLOPs at k=8 against the measured speedup.
- Link probe: `/tmp/pcibw/bw.cu` (CUDA 13.2, sm_86 + sm_120), 512 MiB × 20 reps, pinned and pageable; link state sampled with `nvidia-smi` during the run.
- Code facts: three read-only branch reads against `hydra-fork/{baseline-flash-next, feat/profiler-on-763, feat/moe-lookahead-p1}`; file:line references are to the current checkout (`feat/profiler-on-763` @ `5226b502d`) unless stated.
