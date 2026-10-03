# Option C design — port Strata's miss-handling + GPU-resident adaptive expert cache into the llama.cpp fork

**Task:** t0013 / epic #811 · **Branch:** `docs/811-option-c-design` (off `epic/811-strata-gap-analysis`)
**Date:** 2026-10-03 · **Status:** DESIGN (read-only spike — no build, no rig, no GPU, no commits outside this branch)
**Owner decision being executed:** Option C, chosen 2026-10-03 (`STRATA-INTEGRATION-FEASIBILITY.md:476-480`),
"C, with D as the default until a fork stage lands" (`:490`).

**Claim tags.** `[verified in source]` = the cited file:line was read directly in this task.
`[measured]` = from our own run logs (`docs/evidence/strata-3060-ab/`, `docs/evidence/strata-gap/`).
`[hypothesis]` = reasoned, not yet measured or not yet read. **No number in this document is invented:**
every quantitative figure is either `[measured]`, `[verified in source]`, or an explicitly labelled
*pre-registered threshold* (a choice made now, before the run, not a prediction dressed as data).

**Sources.** Strata `/mnt/WorkDisk/strata/src-v0.1.29` (read-only) · fork `.local/q2g/src`
(export of `hydra-fork`, no `.git`) · evidence `docs/evidence/strata-3060-ab/` and
`docs/evidence/strata-gap/` (both now present in this worktree — this closes open item 3 of
`IMPL-GAP-FORK.md:88-95`).

---

## 0. What Option C is, and the numbers it has to move

Option C, verbatim (`STRATA-INTEGRATION-FEASIBILITY.md:476-480`):

> **C** — Port Strata's miss-handling / GPU-resident adaptive expert cache into the fork. The ranked-gap
> analysis says the win comes from *where expert GEMMs run* + *publish-first plan/GPU-reach pipeline* +
> *pre-fill chunk DMA ring*, not from the packaging (`IMPL-GAP.md:23-34`, gaps #1/#2/#4/#8). Target: fork
> `--moe-expert-cache-size` thrash (F1 = 9.28 vs F0 = 15.30) and `ggml_backend_sched` per-layer split/sync
> (gap #2, est. 15–25 ms/token). 15–25 agent-days, staged. Medium risk.

### 0.1 The gap to close (all `[measured]`, t0006, RTX 3060, ctx 16384, IQ3_S, 3 timed reps/cell)

| Metric | Strata S0 | fork F0 | ratio | source |
|---|---:|---:|---:|---|
| decode 4K (primary, spec-off) | **32.44** [32.37–32.50] | **15.30** [15.12–15.48] | **2.12×** | `strata-3060-ab/README.md:47-53` |
| decode 1K / 12K | 33.14 / 33.73 | 15.34 / 15.23 | 2.16× / 2.21× | same |
| cold prefill 12K | **605.9** | **201.0** | **3.01×** | `README.md:47-53` |
| cold prefill 4K / 1K | 450.8 / 167.2 | 202.6 / 137.4 | 2.23× / 1.22× | same |
| prefix TTFT turn1/2/3 (s) | 19.12 / 5.74 / 5.86 | 48.50 / 9.97 / 11.25 | fork worse, 1.92× at t3 | `README.md:55-60` |
| VRAM after load (MiB) | n/a | F0 **6271**, F1(N=22) **8045**, F2 **8891** | — | `logs/*-vram_after_load.txt` |

Per-token: Strata ≈ 30.8 ms, fork ≈ 65.4 ms → **Δ ≈ 34 ms/token** (`IMPL-GAP.md:19`).

### 0.2 Constraints this design obeys (owner rules, not negotiable here)

| Rule | Source | Consequence for Option C |
|---|---|---|
| **k stays 10** | owner 2026-10-03 | prefill-fastpath **Mechanism A** (prefill-only k=8) is out of scope entirely. |
| **No `--override-kv qwen4exp.expert_used_count`** (waiver only for timing arms) | phase15 §25; owner memory | the D0-L2 FLOP-share discriminator of `design-prefill-fastpath-DRAFT.md:6` (`--override-kv … int:1`) **cannot be used as a gate**; it is re-based on `--profile-decode` (§3, Stage 0). |
| **N parked 38–42** | `leader-handoff-state.md:1092` | any stage that resizes the pool stays inside 38–42 unless the owner re-opens it; §3 shows why that window is the *only* one that fits on 12 GB. |
| **One GPU = one compute task** | `CLAUDE.md` Key Design Decisions | every gate in §3 runs on the 3060 **solo**. No stage introduces a second device into one task; CUDA1/COMBINED legs are out of scope. |
| **Upstream params untouched; our own flag family owns expert placement** | `leader-handoff-state.md:1102-1175` (§21a) | new behaviour is gated behind fork-owned flags; with our flags unset the engine stays upstream-bit-identical. |
| **Option D stays the default until a fork stage lands** | `STRATA-INTEGRATION-FEASIBILITY.md:490` | this document is not a commitment to build; each stage is separately go/no-go (§5). |

---

## 1. Mechanism-by-mechanism map: Strata → fork

Read each row as: *what Strata does* → *what the fork does at the equivalent point* → *gap verdict*.

### 1.1 Three-way miss classification + publish-first plan (gap #1/#8 core)

| | Strata | fork |
|---|---|---|
| Where | `src/core/expert_source.cpp:816-820` — comment: *"the GPU's share, decided and published FIRST so the GPU starts while the CPU works"*; classification into `kind[] = {-1 CPU, 0 VRAM, 1 PCIe}` in the same block; the PCIe tier is `last pcie_num/256` of the misses (`expert_source.cpp:784-899`), so a **fraction** of misses stay on the GPU and the rest go to the CPU pool. | **Absent.** The only decision is hit-vs-miss inside the plan kernel; there is no CPU-compute tier when the cache is on. |
| Measured | per layer-window: `CPU experts 3.79`, `VRAM hits 6.14`, `PCIe 0.10` (counts of K=10) — `generate.cpp:4212-4220` [verified], values `[measured]` (`IMPL-GAP.md:9-12,24`). | F0: 10/10 on the CPU backend (`IMPL-GAP-FORK.md:10-19` [verified]); F1 (cache on): **all** traffic through the GPU LRU, `h2d_mib=6520` per run, L1 hit ≈73% `[measured]`. |
| Verdict | **MISSING on the fork** — but only reachable once the fork has *both* a GPU tier and a CPU tier for the same layer. See §1.4 and Stage 3. |

### 1.2 Residency: demand admission with per-layer quotas, **no eviction**

| | Strata | fork |
|---|---|---|
| Where | `src/core/expert_cache.cpp:227-246` `admit()` — `layer_slot_range()` per-layer quota, `return kNotResident` when that layer's range is full, `next_free_ >= slots_` → no admission; explicit comment *"Same 'no eviction' rule"* and *"which is what confined the measured hit rate to 2.97%"* for the single-counter version [verified]. Quota ranges: `expert_cache.hpp:213-220`. | `ggml/src/ggml-cuda/moe-cache.cu` — LRU eviction on every miss [verified]: victim = min(effective frequency, last_used) over slots not routed this step (phase15 §1 cites `:3233-3290`); *"Every miss is installed. There is no 'serve without installing' branch"* (`design-moe-demand-admission-phase15.md:14-20`). |
| Consequence | the pool never thrashes because **admission is decoupled from the miss**: a miss is *served*, not *installed*. | a single sighting installs + evicts ⇒ measured thrash: F1 `h2d_mib=6520`/run, thousands of H2D copies, `unique experts up to 120/512 per hot tensor` `[measured]` (`strata-3060-ab/README.md:127-131`). |
| Verdict | **MISSING** — the fork's plan kernel has no "serve without installing" branch. This is the literal meaning of *"port Strata's miss-handling"*. |

### 1.3 Adaptive residency: `adapt()` — decayed usage, gain-gated swap, separate stream

| | Strata | fork |
|---|---|---|
| Cadence | `adapt_every = 4` rounds, `adapt_swaps` cap (`src/program/generate.cpp:260-262` [verified]); invoked on its **own thread beside commit+draft** (`generate.cpp:4895-4900` [verified]). | No equivalent. Eviction happens inline in the plan kernel at miss time (§1.2). |
| Candidate | missing expert with decayed usage `u[e] >= 2.0f` (`generate.cpp:4779-4786` [verified]) — i.e. routed ≥2 after decay. | Frequency counter exists: `expert_frequency` + `expert_frequency_epoch`, lazy right-shift, half-life 16, env `GGML_CUDA_MOE_FREQUENCY_HALFLIFE` (`moe-cache.cu:2777-2799`, decay at `:3119-3125` [verified]). |
| Victim | the layer's **least-routed resident** (`generate.cpp:4787-4790` [verified]). | LRU-by-frequency, computed at miss time rather than between rounds. |
| Gate + action | swap only if `cand >= vict + 1.5f` (`generate.cpp:4793-4796` [verified]); `cudaMemcpyAsync` on `adapt_stream`, `host_res[out] = kNotResident` immediately (*"evicted now: the CPU computes it meanwhile"*), `pending` applied when the event lands, `usage *= 0.7f` after the round (`:4797-4824` [verified]). | None. |
| Verdict | **PARTIAL** — the fork has the *ranking input* (decayed frequency) but none of the *policy* (gain gate, between-rounds cadence, stream, publish-on-event, CPU-computes-meanwhile). Porting the policy is cheap; it only pays off once §1.1/§1.2 exist. |

### 1.4 CPU pool serving misses (the "10.5 ms" half)

| | Strata | fork |
|---|---|---|
| Pool | dedicated **physical-core pinned** thread pool, siblings dropped, affinity set, first core reserved; row-parallel across **all** experts of a layer at once (`pool.cpp:494-506,361,398`); activation quant fused to `block_q8_K` on the main thread (`expert_source.cpp:919-925`). | `ggml_compute_forward_mul_mat_id` `ggml-cpu.c:1542`, `n_tasks = n_threads` `:2357-2361` [verified]; single-thread pre-pass then a work-stealing chunk loop (`chunk_size=16`) **within one expert at a time**, `continue` on unrouted experts (`:1663-1673` [verified]). |
| Threads | all physical cores, pinned. | `-t` (`common/arg.cpp:1519-1527` [verified]) → **F0 ran `n_threads = 6`** `[measured, logs/F0-2-engine.log]`, unpinned, no repack for IQ3_S (`repack.cpp:4528-4565`), IQP multi-token path skipped at decode (`IQP_MIN_BATCH_ID = 8`). |
| Measured | `CPU 10.20 ms/window` for 48 layers at ~3.8 experts/layer `[measured]`. | residual `[hypothesis]` **25–40 ms** of the 65.4 (`IMPL-GAP-FORK.md:96-101`). |
| Verdict | **PARTIAL, and the estimate is suspect.** 10.5 ms × (10/3.8) = 27.6 ms ≈ the fork's own bracket — i.e. the CPU-time difference is **arithmetically explained by placement (§1.1), not by kernel quality** `[hypothesis]`. Banked counter-evidence: prefill is *thread*-insensitive over 8–20 threads (O1, `leader-handoff-state.md:863,1004-1006`), and decode moved only 23.00 → 23.99 tok/s from `-t16` → `-t18` with a spare-capacity cliff at 0 spare (`:764-791`). ⇒ **thread tuning is a cheap Stage-0 arm, not a stage.** |

### 1.5 Two-phase `hits(Launch)` / `hits(Combine)` overlap (gap #2's Strata twin)

| | Strata | fork |
|---|---|---|
| Where | `include/strata/core/hit_hook.hpp:1-41` [verified]; call sites `src/core/session.cpp:674-698` [verified]: `hits(Launch)` → `pool()` → `cudaMemcpyAsync(misses)` → `hits(Combine)` → `cudaGraphLaunch(posts[l])`. | No equivalent seam. |
| Why it matters | measured: a single post-pool call moved the drain **18.2 → 10.2 ms** but *"the token did not move at all"* — the GPU's half had been moved *behind* the CPU's instead of beside it; a second buffer + one `add_inplace` bought the overlap back (`hit_hook.hpp:18-24` [verified]). | — |
| Counter-evidence (do not over-credit it) | `IMPL-GAP-FORK.md:76-86` [verified] proves Strata's *host* wall is still **additive per layer** (`verify = Σ wait + Σ pool + stage`; 15.76 + 10.52 + 0.29 ≈ 26.6 ≈ 28.18): the ping-pong is causal (layer l+1 needs `resid_l`). The publish-first benefit is **prefetch-ahead-of-pool**, not pool-hidden-behind-GPU. | — |
| Verdict | **PARTIAL.** What is portable is the *shape* (one graph launch + one mapped-memory spin per layer, tiny per-layer overhead) rather than a hidden-CPU-win. The fork pays the same shape 97× per token with `cudaStreamSynchronize` at each boundary (§2). |

### 1.6 Prefill: routing-independent expert streaming + chunk + DMA ring (gap #4)

| | Strata | fork |
|---|---|---|
| Ring | `src/prefill/prefill.cpp:77-88` [verified] — `ring_slots()`: **384** slots when pinned share ≥0.9 (measured 1153 → 1294 tok/s at 8192-token chunks), else **96**; `STRATA_PREFILL_RING` overrides. | No prefill ring: the copy engine is gated on `DECODE_GROUPED` only (`moe-cache.cu:4589,4618,4626,4638` [verified]). |
| Issuer / sync | dedicated issuer thread `cudaMemcpyAsync` ring (`prefill.cpp:1131-1177`); **one** `cudaStreamSynchronize` per MoE layer per chunk for host grouping + ordering (`prefill.cpp:1503-1512` [verified]); per-entry `cudaStreamWaitEvent` for streamed experts (`prefill.cpp:1690-1694`). | Per-ubatch chain; expert path runs on the CPU backend (§2 "PREFILL_LEGACY"), so the GPU never sees prefill expert work. |
| Chunk | auto ladder `{8192,6144,4096,3072,2048,1024,512,256}` chosen against cache-slot budget + lend pct 90/85 (`generate.cpp:3062-3085` [verified]); print `prompt chunk auto: %lld` (`generate.cpp:3105` [verified]). | `n_batch`/`n_ubatch` only. |
| Compute | experts **stay quantized**; MMQ int8 tensor-core GEMM (`src/prefill/moe_mmq.cu:125-151`); cold cache still streams every blob each chunk — yet 3× the fork (`IMPL-GAP.md:67-71`). | plain `vec_dot` IQ3_S on the CPU, no repack (`IMPL-GAP-FORK.md:30-37` [verified]). |
| Verdict | **MISSING, and the most portable of the four.** It needs *no residency at all* (streaming, not caching) — which is why it can ship without the decode-side cache (Stage 1). |

### 1.7 Telemetry needed to make the gates readable (gap closed by this map)

| Signal | Strata | fork |
|---|---|---|
| Per-window decode split | `strata decode timing: … verify (GPU-reach wait + per-layer host [plan actq jobs CPU] + stage) + commit/emit + draft; per layer-window: CPU experts / VRAM hits / PCIe` — `generate.cpp:4212-4220` [verified] | `--profile-decode` → `PROF … t_wall t_cpu t_dev t_sync` per 64-step window (`common/arg.cpp:1977-1983` [verified], `llama-context.cpp:726,847-853`); **`t_dev` is submit-to-complete wall, not GPU-busy** (`leader-handoff-state.md:4105` §107 caveat). |
| Split count | n/a | `GGML_SCHED_DEBUG` → `## SPLIT #n` (`ggml-backend.cpp:1986-1989` [verified]). |
| Graph reuse | n/a | `graphs reused = %d` (`llama-context.cpp:6501` [verified]). |
| Verdict | **PRESENT on both sides** — Stage 0 needs no new code. |

---

## 2. Fork: has vs missing (file:line, consequence)

| # | Capability | Fork location | State | Consequence `[measured]` |
|---|---|---|---|---|
| 1 | GPU expert cache flag | `common/arg.cpp:2876-2885` → `params.n_moe_expert_cache_slots` | **PRESENT** | F1 runs; that is the whole point of the arm. |
| 2 | Buft override **prepended** (first-match-wins beats `--cpu-moe`/`-ncmoe`) | `src/llama.cpp:318-355`, `set_slots_fn` `:347`, pattern `\.ffn_(up\|down\|gate\|gate_up)_(ch\|)exps` `:345-346`, warning `:361-364` | **PRESENT, all-or-nothing** | cache>0 ⇒ *every* expert tensor on the GPU LRU, regardless of `-ncmoe` ⇒ **no per-layer or per-expert hybrid is expressible today**. This single line is why Option C's §1.1 cannot be reached by configuration alone. |
| 3 | GPU slot pool + LRU eviction + plan kernel | `ggml/src/ggml-cuda/moe-cache.cu` (14,993 lines) | **PRESENT, wrong policy** | every miss installs (phase15 §1) ⇒ thrash ⇒ F1 **9.28 vs F0 15.30** (−39%). |
| 4 | Decayed demand frequency (half-life 16, env-tunable) | `moe-cache.cu:2777-2799`, lazy shift `:3119-3125` | **PRESENT** | ranking *input* exists; ranking *policy* (§1.3) does not. |
| 5 | Demand **admission** (serve without installing) | — | **MISSING** | root cause of `h2d_mib=6520`. |
| 6 | Between-rounds `adapt()` swap, gain ≥1.5, separate stream, publish-on-event | — | **MISSING** | residency cannot follow the conversation. |
| 7 | CPU-compute tier for misses (three-way kinds) | — | **MISSING** | the fork cannot serve a miss except by H2D (cache on) or by putting the *whole* tensor on the CPU (cache off). |
| 8 | Pinned-host L2 tier | `moe_cache_l2_init` `moe-cache.cu:422-447` (`cudaMallocHost`), flag `common/arg.cpp:2888-2895` | **PRESENT, default 0** | storage, not a compute tier; does not help F0/F1 as configured. |
| 9 | Copy stream + batched H2D | `moe-cache.cu` (`cudaMemcpyBatchAsync`, `copy_stream`) | **PRESENT but decode-only** | gated on `DECODE_GROUPED` (`:4589,4618,4626,4638` [verified]) ⇒ **prefill never prefetches**. |
| 10 | Prefill grouped expert path | `pure_prefill` → `GGML_CUDA_MOE_GRAPH_OUTCOME_PREFILL_LEGACY` (`moe-cache.cu:9768-9770`, also `:9883,10360-10362` [verified]) | **MISSING (forced legacy)** | prefill expert GEMMs land on the CPU backend; GPU offload cannot rescue them because the batch key is `op->ne[2]` = **k = 10** against `GGML_OP_OFFLOAD_MIN_BATCH = 32` (`ggml-cuda.cu:8243-8252`, `:8486-8489` [verified]) ⇒ **F1 prefill 206 vs F0 201 — flat**, i.e. the cache buys prefill nothing. |
| 11 | Cross-backend boundary: split on backend change | `ggml-backend.cpp:1345-1349` [verified] | **PRESENT (this is gap #2)** | ~96–97 boundaries/token `[hypothesis, derived; count unverified]`. |
| 12 | Boundary drain is **host-blocking** | events NULL ⇒ `ggml_backend_synchronize(prev)` (`ggml-backend.cpp:1774-1781` [verified]); sync copy path `:1900-1917` [verified]; `n_copies = 1` when not parallel `:1998` [verified], events only created at `:2032` | **PRESENT, blocking** | every boundary pays `cudaStreamSynchronize` + blocking activation copy ⇒ **zero cross-backend overlap**; estimate 10–25 ms/token `[hypothesis]` (`IMPL-GAP-FORK.md:96-99`). |
| 13 | `pipeline_parallel` (the feature that would give real events) | **force-disabled by our own flags**: `llama-context.cpp:1289-1294` requires `!model.has_tensor_overrides()` [verified] | **PRESENT but off** | `-ncmoe`/`-ot` create overrides ⇒ `n_copies=1` ⇒ no events. Turning it on is *not* a config change. |
| 14 | `cudaLaunchHostFunc` wait for a non-CUDA event | `ggml-cuda.cu:7179-7190` — `#if 0 // untested` [verified] | **MISSING (disabled)** | the device-side wait alternative is compiled out. |
| 15 | CUDA graphs | compiled in, default ON; graphs wrap the **GPU splits only** (sched splits first); compat veto `ggml-cuda.cu:4404-4412` [verified] | **PRESENT** | graphs do **not** hide the ~97 boundaries (`IMPL-GAP-FORK.md:27-29`); and any change to node/backend assignment risks a rebuild failure — see risk R1. |
| 16 | CPU kernel | `ggml-cpu.c:1542`, `:1663-1673`, `:2357-2361` [verified]; no IQ3_S repack; IQP skipped at decode (`GGML_IQP_MIN_BATCH_ID = 8`) | **PRESENT** | plain `vec_dot` at T=1; but see §1.4 — placement, not kernel, dominates. |
| 17 | Pinned experts for a future streaming path | downgraded under `use_mmap` (`llama-model-loader.cpp:1324-1330` [verified]); `--load-mode none` restores pinning | **PRESENT, gated** | `--load-mode none` measured **−4.9%** decode, i.e. nil (`leader-handoff-state.md` §106 D2) `[measured]`. |
| 18 | Per-step miss ledger / counters | `moe-cache.cu:14616` cumulative hit/miss/evict line only (phase15 §7) | **PARTIAL** | enough for gates (bytes + hit rate), not for a per-step yield table. |

**Residual arithmetic** `[hypothesis]`: 65.4 ≈ GPU 16 (Strata-class floor) + CPU experts 25–40 + boundaries 10–25.
Brackets the observation; **unmeasured until Stage 0**.

---

## 3. Minimal staged plan

**Ordering principle:** gain ÷ (cost × risk), using only measured anchors. Every stage is independently
shippable, independently revertible (one flag), and independently measurable on the existing t0006 harness
(`docs/evidence/strata-3060-ab/harness/`, now in-tree).

### 3.0 Gates reused by every stage

| Gate | Definition | Threshold | Basis |
|---|---|---|---|
| **G-D** decode | spec-off 4K decode, 3 timed reps, median, ABBA order, same harness | vs the stage's own same-session control | protocol `strata-3060-ab/README.md:24-34,75-77` |
| **G-P** prefill | cold 12K prefill (and 4K), same protocol | same | same |
| **G-T** prefix | TTFT turn1/2/3 on the 9,163-tok prefix cell | same | same |
| **G-K1 identical-token** | temp 0, 256 new tokens, p1k/p4k/p12k: token sequence vs same-binary feature-off control | **100% identical** for stages that must not change numerics (S0, S2, S4) | pre-registered |
| **G-K2 KL** | teacher-forced KLD over `scripts/eval/wikitext-2-raw/wiki.test.raw` (the `kl-gate.sh` corpus), stage binary vs control | **≤ 2 × the S0-measured build-vs-itself floor** | the record has **no** build-vs-itself floor yet (`leader-handoff-state.md:2233` §37a); S0 measures it, so no absolute number is invented here |
| **G-V VRAM** | MiB after load | **≤ 11 000 MiB** (≥1.2 GiB below the 12 288 MiB ceiling) | chosen from `[measured]` F0 6271, F1 8045, and the N=64 OOM |
| **G-L PCIe validity** | pinned H2D probe pre/post + 1 Hz link sampling | probe **6.00–6.30 GB/s**; **`gen1 ∧ util≥20` = 0 samples** | verbatim from `strata-3060-ab/README.md:82-86` `[measured 6.10–6.13]` |
| **G-R non-regression** | any *other* axis than the one the stage targets | **no axis regresses > 3%** vs the stage's control | pre-registered (3% > the ~1–2% session noise `[measured]`) |
| **G-G graphs** | `graphs reused` counter + no `post_decode() failed` | ≥ control, zero HTTP 500 | risk R1 |

---

### Stage 0 — measure before building (zero code, 1 rig session) · 1–2 agent-days

**Why first:** every fork-side magnitude in §2 is `[hypothesis]`. Gap #2 alone is "the single biggest unknown"
(`IMPL-GAP.md:83`). Building on an unmeasured split is how the last three mechanism attempts died
(`leader-handoff-state.md:1194-1204`: every failure = "the mechanism cost more than the prize").

**What runs (all knobs already exist):**
1. F0 control re-measured + `--profile-decode` → `t_wall / t_cpu / t_dev / t_sync` per 64-step window.
2. `GGML_SCHED_DEBUG=1` → count `## SPLIT` per token (settle **97 vs ~193**, `IMPL-GAP-FORK.md:91`).
3. Two **zero-code** config arms: `-t 12` and `-t 16` unpinned at the same spare-capacity rule (≥2 spare;
   `leader-handoff-state.md:764-791`), plus record `spare` explicitly. F0 ran at `n_threads = 6`
   `[measured]`.
4. Correctness floor: build-vs-itself KL on the wikitext corpus → the G-K2 denominator.
5. **Close gap #7 for free:** the t0006 logs are now in-tree, so apply the F-B decision table
   (`IMPL-GAP-FORK.md:49-56`) to F0-2/F0-3 to locate the ~4–5 s turn-2 fixed cost
   (`prompt eval time ≈ 10000/731` vs `≈ 6000/731` vs `N ≠ 731`).
6. Record the recurrent-state question for Stage 2 (risk R2): whether DeltaNet/PLE conv-history tensors
   participate in the cross-backend copies at each boundary.

**Gates (pre-registered):**
- **G-0a attribution** — the three sources (profile windows, split count, config arms) must explain
  **65.4 ± 10% ms/token**. *Fail ⇒ stop and re-diagnose; no stage 1–4 is authorised on an unexplained budget.*
- **G-0b instrument** — G-K2 floor established (a number must exist, whatever it is).
- **G-0c cheap win** — if `-t 16` alone gives **≥ +6%** decode with G-R/G-V/G-L passing, **ship it as a
  config change immediately** and drop any later CPU-engine work.
- G-L, G-V, G-G apply.

**Ships:** config defaults only (if G-0c fires) + a measurement report.
**Reverts:** flag-off. **Risk:** none (no code).

---

### Stage 1 — prefill: routing-independent GPU expert streaming (gap #1 + #4) · 5–7 agent-days

**Why here:** the largest *measured* multiple (3.01× at 12K) for the smallest architectural blast radius —
prefill does not touch decode, is not CUDA-graph-captured, and needs **no residency** (it streams).
Gain/cost beats the decode stages: 1.5–2.0× ceiling vs 10–15%.

**Design (Strata's prefill regime, adapted):**
- Compute prefill expert GEMMs on the GPU from host-resident expert weights, streamed on a dedicated copy
  stream, layer-ahead double buffer; decode untouched.
- **Must not require `--moe-expert-cache-size > 0`.** That flag currently costs **40% decode** (F1 9.28 vs
  F0 15.30) `[measured]`, and it is a *load-time* buft override (`src/llama.cpp:329`) so it cannot be
  enabled for prefill only. ⇒ a prefill-only streaming path over the host-resident weights (Strata's shape:
  pinned arena + device alias, `expert_source.cpp:505-537`), **not** a reuse of the decode LRU.
  Fallback if that proves too large: reuse `moe-cache` staging with the pool sized for a *phase-scoped*
  double buffer only (~1 GiB: 2 × 292 × 1.7838 MiB `[hypothesis]`, `design-prefill-fastpath-DRAFT.md:4`).
- Concretely, on the fork: give `pure_prefill` a grouped outcome instead of `PREFILL_LEGACY`
  (`moe-cache.cu:9768-9770`), un-gate the copy engine from `DECODE_GROUPED`
  (`:4589,4618,4626,4638`), add the per-ubatch union pass, and one host sync per layer per chunk
  (Strata's shape, `prefill.cpp:1503-1512`).
- Pinned weights: `--load-mode none` if the streaming path needs pinned source
  (`llama-model-loader.cpp:1324-1330`); cost measured at **−4.9% decode = nil** `[measured]`.

**Gates:**
- **G-P primary:** cold 12K prefill **≥ 1.15 ×** the same-session F0-equivalent control (= ≥ 231 tok/s
  against 201.0). *Kill below 1.15×* — the bar the prefill design itself registered
  (`design-prefill-fastpath-DRAFT.md:7`). **Stretch** (recorded, not gating): ≥ 300 tok/s (1.5×);
  Strata's 605.9 is the ceiling reference, not the gate.
- **G-D / G-R:** decode must not regress > 3% with the new flag **off** (default-off requirement) and
  must not regress > 3% with it **on**.
- **G-K1:** decode token sequences **100% identical** with the flag on vs off (decode is untouched by
  construction — if it is not identical, the design is wrong, stop).
- **G-K2:** prefill numbers may legitimately differ (CPU `vec_dot` → GPU MMQ int8) ⇒ KL ≤ 2× floor.
- **G-V:** VRAM ≤ 11 000 MiB (F0 6271 + ~1 GiB buffer + slack).
- **G-L, G-G** apply. Recorded hazard to check with a numerics test, not an assumption: the decode-era
  cached path carries an *authority invariant refusing pools* and a *router-census perturbation that
  changes logits* (`design-prefill-fastpath-DRAFT.md:4`).

**Known blocker carried from the prefill design:** its D0-L2 premise leg used
`--override-kv … expert_used_count=int:1`, now barred. **Replacement:** use Stage 0's
`--profile-decode` device-vs-host split as the premise evidence; if a waiver for one timing arm is
wanted, it goes to the owner, not to this plan.

**Ships:** `--moe-prefill-stream` (name open; must be in the fork's own flag family per §21a), default off.
**Reverts:** flag-off. **Risk:** medium (touches `moe-cache.cu` plan/authority), blast radius = prefill only.

---

### Stage 2 — decode: boundary cost (gap #2) · 4–6 agent-days

**Why second:** the other *measured-target* leg of Option C's own row ("`ggml_backend_sched` per-layer
split/sync, est. 15–25 ms/token"). Smaller gain than Stage 1 but it is the prerequisite for reading Stage 3
cleanly (boundary noise would otherwise swamp a residency measurement), and it benefits *both* the CPU-only
and the future hybrid path.

**What changes (in increasing order of invasiveness; pick the cheapest that meets the gate):**
1. Restore real events at boundaries so the drain uses `ggml_backend_event_synchronize` instead of
   `ggml_backend_synchronize(prev)` (`ggml-backend.cpp:1774-1781`): i.e. decouple `n_copies`/event creation
   (`:1998`, `:2032`) from `model.has_tensor_overrides()` (`llama-context.cpp:1289-1294`).
2. Make the cross-backend activation copy async (`ggml-backend.cpp:1900-1917` currently falls back to a
   blocking `ggml_backend_tensor_copy` because CPU's `cpy_tensor_async` is NULL and CUDA's requires both
   sides CUDA).
3. Enable the disabled device-side wait (`ggml-cuda.cu:7179-7190`, `#if 0 // untested`).
4. Only if 1–3 are insufficient: reduce the *number* of boundaries (already 1 CPU run per MoE layer).

**Gates:**
- **G-D primary:** decode 4K **≥ +10%** vs same-session F0-equivalent (≥ 16.8 against 15.30)
  — *pre-registered threshold*, derived as the low end of the 10–25 ms estimate (65.4 − 6.5 ms).
  Record the actual ms saved against Stage 0's measured boundary share; if Stage 0 says the boundary share
  is **< 6 ms**, this stage is cancelled before any code (that is its own kill criterion).
- **G-G:** `graphs reused` ≥ control, zero `post_decode() failed`.
- **G-K1:** **100% identical-token** — scheduling-only changes must not move a single token. *Any*
  difference is a bug, not a numerics trade.
- **G-R, G-V, G-L** apply (VRAM must not move: enabling `n_copies>1` adds buffers — if G-V fails, the
  stage is not shippable on 12 GB and stops here).

**Ships:** a fork-owned flag (e.g. `--moe-boundary-events`), default off until G-K1 passes.
**Reverts:** flag-off. **Risk:** medium-high — directly on the ggml sched + CUDA-graph boundary.

---

### Stage 3 — decode: GPU-resident adaptive expert cache with demand admission (gap #8 + §1.1/§1.2/§1.3) · 6–9 agent-days

**Why last:** highest decode upside (CPU share 25–40 ms → ~10.5 ms if it reaches Strata's 6.1-GPU/3.9-CPU
split `[hypothesis]`), highest risk, and it is the stage Option C is *named* for. It is gated on Stage 0
(attribution), Stage 2 (clean timing), and on the pre-existing hot/cold-slab ruling (below).

**Hard constraint this stage must respect — the 12 GB ceiling:**
- `[measured]` unit 80.64 MiB per N (resident), Strata's auto tier = 2 641 slots = **5.06 GiB**;
  **N = 64 (fill-match) → `cudaMalloc(58982616) failed` at load** (`strata-3060-ab/README.md:134-138`,
  `deviations.md` V4). F0 headroom is 12 288 − 6271 ≈ 6 000 MiB.
- ⇒ **the fork cannot reach Strata's residency on this card by raising N.** The port must reduce
  *per-miss cost* (serve cheaply), not chase *miss-prevention* (bigger pool). Any proposal that needs
  N > 42 is a proposal that needs the owner to re-open the parked window **and** a VRAM budget decision —
  not an engineering default (§0.2).

**Work, in the order the prior rulings require:**
1. **Policy port (small, self-contained):** add demand admission to the plan path so a miss is *served*
   not *installed* (`moe-cache.cu` victim-pick/`INVALID_STATE` region, phase15 §1 `:3233-3295`), plus
   Strata's between-rounds `adapt()` (§1.3: candidate `u ≥ 2.0f`, victim least-routed, gain ≥ 1.5,
   separate stream, publish-on-event, `usage *= 0.7f`) reusing the frequency counter that already exists
   (`moe-cache.cu:2777-2799`). Per-layer quotas as in `expert_cache.cpp:236-241`.
2. **Serving tier (the expensive part — this is where the prior rulings bind):**
   - **Not** the two-branch construction: `ggml_mul_mat_id` computing all k experts on each branch = 2k work;
     this measured **0.82×** and is *DO NOT RE-PROPOSE* (`leader-handoff-state.md:1126-1133`).
   - The ruled shape is the **hot/cold expert slab (issue #132)**: split `blk.N.ffn_*_exps` at *load* into a
     hot slab (→ GPU buffer) and a cold slab (→ CPU buffer) = two tensors = two `mul_mat_id` nodes, remapped
     ids + combine; the scheduler places them for free; no intercept, no per-token transfer, no sync, graph
     capture native (`leader-handoff-state.md:2200-2210` §37). Its build case is gated on a
     coverage-per-MiB re-derivation that already **fires BUILD with margin** (`:2259-2334` §38–§39a).
   - If instead a per-expert backend-selection op is pursued (on-device compaction, no host readback), it
     inherits the **ARM 008 budget rule**: per-invocation cost **≤ 34.5 µs** or it is not funded
     (`leader-handoff-state.md:1402,1461`); ≤11.5 µs for 3× margin.
3. **`adapt()` cadence placement:** beside commit+draft on its own thread (Strata `generate.cpp:4895-4900`),
   touching only residency tables that nothing reads until the next window.

**Gates:**
- **G-D primary:** cache-on decode **≥ F0 15.30** (today's F1 is 9.28, i.e. the bar is *stop losing*, then
  gain). Pre-registered bands: **< 15.30 ⇒ stage fails and the cache is shelved on this card (Option D for
  this mechanism); 15.30–17.0 ⇒ ship only if G-R passes and report plainly; ≥ 17.0 ⇒ proceed to the
  hybrid-tuning follow-up.**
- **G-D′ thrash:** `moe-cache` H2D bytes per run **≤ 500 MiB** (vs `[measured]` 6520) and L1 hit ≥ 60%
  (vs `[measured]` ≈73% on a *thrashing* N=22 — hit rate alone is not a win signal, bytes are).
- **G-K1/G-K2:** this stage changes *where* experts compute ⇒ token identity is not expected on the
  slab split; require **G-K2 ≤ 2× floor** *and* the Strata L100-class check explicitly: a wrong-layer /
  wrong-ids combine produced *"finite, fluent, deterministic tokens that were not the model's, and no
  timing test could see it"* (`session.cpp:686-694` [verified]; the ids bug moved KL 9.69e-02 → 1.03e+00,
  top-1 0.867 → 0.333, `hit_hook.hpp:28-33` [verified]). **⇒ G-K2 is a blocking gate for this stage, not a
  formality.**
- **G-P, G-T, G-V (≤ 11 000 MiB with N ≤ 42), G-L, G-G, G-R** apply.

**Ships:** `--moe-expert-home` / `--moe-expert-pin-count` family (§21a flag surface, owner approval pending)
+ admission/adapt behind it, default = upstream behaviour.
**Reverts:** flags unset ⇒ bit-identical upstream (§21a (a)).
**Risk:** high — ggml sched, loader, and CUDA-graph paths all in play.

---

### Stage 4 — prefix turn-2/3 fixed cost (gap #7) · 1–3 agent-days, conditional

**Why conditional:** Stage 0 item 5 either locates the ~4–5 s (`IMPL-GAP.md:74-76`) or proves it is inside
the slot. Only then is there anything to fix.

**Gates:** **G-T:** TTFT turn3 **≤ 8.5 s** vs F0 11.25 `[measured]` (−25%; pre-registered) with G-K1
100% identical (a prefix/LCP decision change must not alter generated tokens) and G-R elsewhere.
**Ships:** config or a small `server-context.cpp` change. **Risk:** low.

---

### 3.5 What is deliberately *not* in this plan

- **Mechanism A (prefill-only k reduction)** — barred by k = 10 (§0.2).
- **Any `-ot`/layer-placement-only proposal** — already measured (0.1459–0.2065 tok/s/layer
  `[leader-handoff-state.md:3827,3685]`) and it *cannot* discriminate hot from cold within a layer
  (`:1197-1198`): layer-granular placement does not test the ranking this option is about.
- **Multi-GPU / COMBINED legs** — one GPU = one compute task (§0.2); out of scope for Option C.
- **Any `--override-kv` gate** — see §0.2; Stage 0's profiler replaces it.
- **Raising N to fill-match Strata** — measured OOM (§3, Stage 3).

---

## 4. Risks and agent-days

### 4.1 Risks

| # | Risk | Why it is real (evidence) | Mitigation / gate |
|---|---|---|---|
| **R1** | **CUDA graphs break when node/backend assignment changes.** | `--decode-overlap` already crashes on this model at the first decode graph rebuild: *"rebuilt graph requires another backend for ffn_moe_gate-0 (MUL_MAT_ID)"* → `post_decode() failed` HTTP 500, reproduced twice `[measured, strata-3060-ab/README.md:108-112 D3]`. Graph compat veto is narrow (`ggml-cuda.cu:4404-4412`). | **G-G** on every stage: `graphs reused` ≥ control, zero 500s. Stage 2 must not assume `--decode-overlap` works — fix D3 or avoid it. |
| **R2** | **Hybrid DeltaNet / recurrent state.** | the served model is **36 DeltaNet + 12 full-attn** layers `[measured, docs/evidence/model-layer-info/gsq-rco-iq3s.summary.txt]`; `qwen4exp.cpp:3-4,38-47` pulls SSM state sizes and uses `llama-memory-hybrid-idx` / `llama-memory-recurrent`; *"the PLE conv history is a row of the recurrent cache"* (`qwen4exp.cpp:142` [verified]). | Stage 0 item 6 establishes whether recurrent/PLE tensors ride the cross-backend copies at each boundary. Any Stage 2/3 change that re-partitions per-layer execution must **not** split or duplicate recurrent state or conv history. `[hypothesis]` until Stage 0 reads it. |
| **R3** | **k = 10 and no `--override-kv`.** | §0.2. Kills Mechanism A and the D0-L2 premise leg; also kills the tempting "just lower k during prefill" shortcut that would otherwise look like a free 1.15–1.20×. | Premise evidence re-based on `--profile-decode`. Any waiver request goes to the owner, explicitly, for **timing arms only**. |
| **R4** | **One GPU = one compute task** + host spare capacity. | §0.2; and the spare-capacity effect is a banked finding — needs ≥2 spare logical CPUs, cliff at 0 spare (`leader-handoff-state.md:764-791`), mechanism still a hypothesis (`:824`). | All gates run 3060-solo. Any `-t` arm records `spare` explicitly and keeps ≥2. If a stage looks like it needs the 5060 Ti *inside the same task*, it stops for an owner decision. |
| **R5** | **12 GB VRAM ceiling makes the Strata residency unreachable.** | `[measured]` N=64 OOM (`cudaMalloc(58982616)`); F0 already at 6271 MiB. | **G-V ≤ 11 000 MiB**; Stage 3 is explicitly *miss-serving*, not *miss-preventing*; N stays ≤ 42. |
| **R6** | **A correct-looking result can be wrong.** | Strata's own history: a wrong-layer combine and a stale-ids hook both produced *"finite, fluent, deterministic tokens that were not the model's"* with plausible timings (`session.cpp:686-694`, `hit_hook.hpp:28-33` [verified]). This model is also numerically nondeterministic at temp 0 (acceptance 0.43 vs 0.80 in the record). | **G-K2 is blocking for Stages 1 and 3**; G-K1 100% for Stages 0/2/4. n=3 interleaved (ABBA), gate on medians, never bests. |
| **R7** | **Measurement instruments lie.** | `t_dev` is submit-to-complete wall, not GPU-busy (`leader-handoff-state.md:4105` §107); NVML-by-CUDA-ordinal sampled the wrong GPU under CVD remap (`:4130`); profiler-on legs without an OFF replicate were previously rejected. | Stage 0 produces one profiler-ON vs OFF replicate before any profiler-derived number is used as a gate. |
| **R8** | **Option D drift.** | Option C is explicitly *not* the default until a stage lands (`STRATA-INTEGRATION-FEASIBILITY.md:490`). | Each stage has its own stop condition (§5); no stage's failure authorises the next. |

### 4.2 Agent-days

| Stage | What | Agent-days | Cumulative |
|---|---|---:|---:|
| 0 | measure + config arms + correctness floor + gap-#7 log pass | **1–2** | 1–2 |
| 1 | prefill GPU expert streaming | **5–7** | 6–9 |
| 2 | decode boundary/sync | **4–6** | 10–15 |
| 3 | admission + `adapt()` + hot/cold slab serving tier | **6–9** | 16–24 |
| 4 | prefix fixed cost (conditional) | **1–3** | 17–27 |

Option C was quoted at **15–25** `[hypothesis]` (`STRATA-INTEGRATION-FEASIBILITY.md:480`). This plan lands
at **17–27**, i.e. +2 at both ends, because it carries a measurement stage the original estimate did not
price, and because gap #7 is now in scope (its logs are in-tree). If Stage 4 is dropped and Stage 3 fails
its gate early, the expected path is **16–18** — inside the original range.

---

## 5. Go / no-go and the first stage

### 5.1 Overall

**CONDITIONAL GO, Option D stays armed.** Option C is authorised to *run Stage 0* now; Stages 1–4 are
individually authorised only by their predecessor's gate passing. There is no commitment to reach Stage 3.

### 5.2 Stage gates (stop conditions)

| Stage | GO if | NO-GO (stop / re-diagnose / fall back to D) if |
|---|---|---|
| **0** | G-0a attribution within 65.4 ± 10%, G-0b floor exists, G-L/G-V/G-G pass | attribution cannot be obtained → **stop the whole option** and report the unexplained budget plainly |
| **1** | cold-12K prefill ≥ 1.15×, decode −R ≤ 3%, decode token identity 100%, KL ≤ 2× floor, VRAM ≤ 11 000 | prefill < 1.15× ⇒ drop the streaming path (Stage 4/2 continue on their own merits) |
| **2** | Stage 0 measured boundary share ≥ 6 ms **and** decode ≥ +10% **and** G-K1 100% **and** `graphs reused` ≥ control | boundary share < 6 ms ⇒ **cancel before code**; else G-K1 any diff ⇒ bug, not a trade |
| **3** | decode ≥ 15.30 **and** H2D ≤ 500 MiB/run **and** KL ≤ 2× floor **and** VRAM ≤ 11 000 at N ≤ 42 | decode < 15.30 ⇒ **shelve the cache on this card** (Option D for this mechanism); N > 42 required ⇒ **owner decision**, not a default |
| **4** | TTFT-t3 ≤ 8.5 s and G-K1 100% | diagnosis says the cost is inside the slot ⇒ no change |

### 5.3 First stage

**Stage 0 — measure before building.** One rig session, zero code, 1–2 agent-days, on the existing t0006
harness. It converts the three load-bearing `[hypothesis]` values in §2 (boundary count 97, boundary cost
10–25 ms, CPU-expert residual 25–40 ms) into `[measured]`, establishes the G-K2 denominator every later
stage needs, closes gap #7 with logs that are now in-tree, and offers a free config win (`-t 6` → `-t 16`)
that can ship before any build.

It is also the cheapest possible refutation: if G-0a fails, the option stops before a single line of fork
code is written — which is exactly what the mechanism-budget rule demands
(`leader-handoff-state.md:1194-1204`: *no mechanism gets built until its cost is estimated against the
budget, in the design brief, not after*).

---

## 6. Assumptions and open questions

1. `.local/q2g/src` is treated as the production fork source. It has no `.git`; every fork line above was
   read from that export. If the export drifts from `hydra-fork`'s `hydra-build.yml` output, line numbers
   move — re-verify before citing in a PR body.
2. All fork-side magnitudes in §2 (`97` boundaries, `10–25` ms, `25–40` ms CPU) remain `[hypothesis]`
   until Stage 0. This document does not upgrade them.
3. Whether a **phase-scoped ~1 GiB** prefill buffer fits beside the production layout on 12 GB is
   `[hypothesis]`; the arithmetic (6271 + ~1040 ≈ 7311 MiB) says yes but has not been run.
4. The N = 38–42 park is cited from `leader-handoff-state.md:1092` (2026-09-19). The t0006 A/B ran N = 22
   and N = 64. **The park's current status should be confirmed with the owner before Stage 3 sizes anything.**
5. Flag names in §3 (`--moe-prefill-stream`, `--moe-boundary-events`) are placeholders; §21a's flag surface
   (`--moe-expert-home`, `--moe-expert-pins`, `--moe-expert-pin-count`) is the approved naming direction
   and is **pending owner approval** (`leader-handoff-state.md:1157-1165`).
6. Gap #7's root cause is still unknown — Stage 0 item 5 is a *log pass*, not a fix.
7. Strata-side line numbers were re-verified directly against `/mnt/WorkDisk/strata/src-v0.1.29` in this
   task; fork-side line numbers likewise against `.local/q2g/src`. Where a prior doc's line number was for
   a different lineage (`design-prefill-fastpath-DRAFT.md` cites the 763 checkout, e.g. `moe-cache.cu:9610`),
   this document cites the `.local/q2g/src` number (`:9768`) instead.
