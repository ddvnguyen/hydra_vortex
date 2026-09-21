# Phase 1.5 design note - demand-gated admission (§121/§122)

Status: DRAFT for architect design gate. No Phase 2 code written.
Branch: submodule `feat/moe-demand-admission` (off `5226b502d`).
Author: leader claude-sonnet5-lead-20260921, 2026-09-21.

## 0. Verdict

1. A counter-only gate is not the lever (architect §122 agrees). Source confirms it.
2. The lever is a compute path for non-admitted experts. Three shapes exist; **none is cheap**.
3. **The 3060 cache arm appears to lose to zero-code controls at equal VRAM** (section 5).
   That must be measured before any of the three shapes is built. Recommendation: run controls first.
4. Relaxing `n_routes <= n_slots` is the *small* part. The CPU chain, graph surgery and
   certificate proofs are the expensive part.

## 1. Miss path, traced (`ggml/src/ggml-cuda/moe-cache.cu`, HEAD `5226b502d`)

| Step | Lines | What it does |
|---|---|---|
| commit prior plan | 3005-3041 | writes `slot_for_expert`/`expert_for_slot` for last plan's misses; `frequency++` for every unique expert (hit or miss) |
| step counter | 3049, 3053 | `device_step` += 1 per plan call **on this group's own counter**; `frequency_epoch = step / halflife` |
| route validation | 3078-3082 | fails if `n_routes > n_slots` or `plan_capacity != n_slots` |
| unique + hit/miss | 3102-3213 | miss = unique expert with `slot_for_expert < 0` |
| victim pick | 3233-3290 | per miss: min (effective frequency, last_used) over slots not routed this step |
| completeness | 3292-3295 | any unique with `slot < 0` after this -> `INVALID_STATE` |

Consequences:
- **Every miss is installed.** There is no "serve without installing" branch. `miss_slots[]` feeds the
  H2D gather; `remapped_ids[]` feeds `mul_mat_id` with slot ids only.
- `n_routes <= n_slots` does **not** bind decode-1 (10 <= 42). It binds multi-token verify at small N
  (MTP n=3 at N=36 gives 30, n=4 gives 40 > 36). Relaxing it alone changes nothing for the gate.
- The real hard invariant is 3292-3295 plus the fact that the op is a single `mul_mat_id` node.

## 2. Can `expert_frequency` be the admit counter unchanged? Yes (window unit: see retraction)

`expert_frequency[expert]` (u32) + `expert_frequency_epoch[expert]` already implement "windowed use
counter, halved per epoch" (lazy right shift, 2868-2877). It is incremented for **every** sighting,
so it is a sightings count. Admit rule = `effective_frequency(expert) >= T` at miss time. No new state.

**RETRACTED 2026-09-21 (was: "unit trap, shipped window = 1/3 token, use 768"). Do not use 768.**
The claim came from the `899d8ec7a` commit message ("a decode token is 48 planning steps"). Source
contradicts it: `device_step` lives in `grouped_device_resource`, and one such resource is created per
*group* (`resources[group_index]`, `make_device_resource` at `moe-cache.cu:6614`). A group carries the
gate/up/down tensors of one layer (`group_index` + `role` in `ggml-backend.h:359-366`). So the counter
advances once per plan launch *on that group*, i.e. probably once per decode token per layer, and the
shipped half-life of 16 is then **already 16 decode tokens = pjsgsy's "window 16"**.
**Status: source-derived, not yet measured.** It is settled by the ledger: each record carries the
group id and the `device_step` value read by the kernel, so calls per group per token can be read off
directly. Until then the port default (16) stays and no window is asserted either way.
Env knob `GGML_CUDA_MOE_FREQUENCY_HALFLIFE` (ported from `899d8ec7a`, this branch) sets it.
`899d8ec7a` measured half-life 256/2048 at -6% with LFU *eviction*; under this reading that is 256/2048
*tokens*, consistent with pjsgsy's "short window wins". It still does not test admission.

## 3. Options for serving a non-admitted expert

| | Shape | Reuses | Cost / risk |
|---|---|---|---|
| **A** | #27861 split: zero-slot GPU chain + CPU `mul_mat_id` with skip table + sum | design proven upstream on this model | New CPU op semantics (`src[3]` skip/zero), second graph chain in `qwen4exp.cpp`, sum node. Extra reader of router tensors trips the grouped certificate (#127 blocker 1: logits changed, `graph=unproven`). Every layer becomes a CPU split boundary (ids D2H + sync): CUDA graphs lost for those layers. Plan kernel must emit a zero-slot sentinel and relax 3292-3295. Large. |
| **B** | Layer-granular hybrid: fix `-ot`/cache precedence so `-ot` layers stay CPU, cache the rest | `-ot` placement (linear, 0.203-0.213 tok/s/layer) | Small. But it is static placement plus cache on fewer layers. Static skew is dead, so the cache adds little over pure placement. |
| **C** | #132 hot/cold slab (two tensors, two nodes, scheduler places them) | E1 post-mortem chose it: no intercept, capture-native | Static hot set. Ranking arm dead (0.82x). Not a dynamic admission path. |

E1 (`docs/analysis/e1-postmortem.md`) already built a GPU-hit + CPU-miss + combine kernel at one site.
It died on **reachability** (host-resident site is scheduled to the CPU backend, so the CUDA intercept
never runs) and **capture infeasibility** (sync join). Option A inside the CUDA backend inherits both
problems unless the CPU chain is a real graph node as in A. Reachability precheck rule applies:
prove the site executes with one counter before designing anything.

## 4. Break-even (primary sources; arithmetic mine)

- Cache arm cost model (`b950f4cc2`, r2=0.990, 196 steps, N=42): `ms/token = 25.7 + 0.3003 * misses`,
  mean 261 misses/token of 480 demands, 1.881 MB/miss = 6.27 GB/s. Card not stated in the message;
  the link rate and N=42 point to the 3060 x4 (inferred, not confirmed).
- All-host control (`leader-handoff-state.md` §81): 13.1536 tok/s median, n=6, MTP off, ctx 16384
  = 76.0 ms/token. If fixed cost is the same 25.7 ms: CPU cost per expert ~ (76.0-25.7)/480 = 0.105 ms.
- So serving a miss on CPU (~0.105 ms) vs uploading it (0.3003 ms): **~2.9x cheaper per miss**, if that
  per-expert cost holds at 5.4 experts/layer instead of 10 and the fixed term transfers across depth.
- Back-of-envelope for A: 25.7 + 261*0.105 = 53 ms = ~18.8 tok/s **before** split/sync overhead.
  Treat as an upper bound with wide error, not a prediction. Split boundaries add tens of us each.

## 5. The finding that should gate everything: equal-VRAM controls

Cache N=42 pool = 48 layers x 42 slots x 1.881 MB = 3.79 GB. Static placement of 4 layers =
4 x 937.5 MiB = 3.93 GB (`§81`: P_mid = ncmoe 44). Numbers on file, **different depth/ctx, so not
comparable yet**:

| arm | tok/s | ctx / note |
|---|---|---|
| all-host `--cpu-moe`, no cache | 13.15 | 16384, MTP off (§81) |
| static 4 layers `-ot` (~ equal VRAM) | 14.03 | 16384, MTP off (§81) |
| cache N=42, gate OFF | 9.66-10.83 | 81920, real prompt (`b950f4cc2`, `1ea22696f`); #127 probe 10.43 |

If these hold at matched depth, the cache **loses ~25% to static placement and ~20% to no cache** on
this card, so the gate has to first climb out of a hole. Residency-only gating (option 0) cannot do
that given LFU-with-decay already ships. Only A could, and A's ceiling (~18-19) is +30% over static.

## 6. Recommendation

1. **Do not build A yet.** Run three zero-code arms on the 3060, same ctx 81920, MTP off, n>=3:
   all-host; `-ot` static 4 layers; cache N=42 gate-OFF. Cost: rig time only (granted).
2. Port the per-step miss ledger (`b950f4cc2`) first. Phase 3's yield table needs uploads/bytes and
   HEAD has only a cumulative hit/miss/evict line (`moe-cache.cu:14616`). It conflicts hard (749 lines)
   because it sits on look-ahead staging; needs a re-derivation, not a cherry-pick.
3. Decision rule after (1): cache >= static -> proceed to a residency-gate spike (option 0) with
   the shipped half-life (see section 2 retraction). Cache < static by >15% -> the choice is A (large, +30% ceiling) vs shelving the cache
   on the 3060. That is an architect/owner call, made on measured numbers.

## 7. Phase 1 status and asset notes

- **Not in HEAD and not in gs:** `1ea22696f` and 9 look-ahead commits under it, incl. `899d8ec7a`
  (half-life knob), `b950f4cc2` (per-step ledger), `ca8ee3d6d` (demand trace for a Belady bound).
  All live only on `feat/moe-lookahead-p1` (local + `hydra-fork`). A rebase to gs/moe-cache would not
  carry them.
- **Deviation from handoff:** the named counters (`admitted_experts`, `admit_evictions`,
  `admit_skipped`, `staged_mib_total`) belong to predicted admission and staging. They read 0 without
  `04e85a745` (1005 lines + ggml-cuda.cu). Harvesting them alone is dead weight. I took the demand-side
  instruments instead. `GGML_MOE_EVICT_POLICY` is not ported: `1ea22696f` measured recent1 vs LFU
  at 10.8316 vs 10.8320 tok/s, so it is not a lever.
- Ported so far: `899d8ec7a` (manual, 1 hunk). Build check pending.

## 8. Unverified / open

- Card behind the `b950f4cc2` fit (3060 inferred).
- Whether the §81 numbers are 3060-solo (line 3076 confirms it only for the MTP proven config).
- Per-expert CPU cost at lower experts-per-layer; depth transfer of the 25.7 ms fixed term.
- `pjsgsy` gate numbers are a single unreviewed comment on one card.

## 9. Findings while running section 6 (2026-09-21) - read before trusting any 3060 number

1. **The 3060 link is Gen1 (2.5 GT/s) x4 and delivers 0.39 GB/s H2D** (pinned = pageable = 0.39-0.42, 35 s
   of sustained load, link never upshifts; 5060 Ti in the same probe: 25.9 pinned / 13.6 pageable, upshifts).
   Card bus 02:00.0 (the NVMe adapter slot), root port 00:06.0 reports max 16 GT/s x4; no AER errors.
   Probe: `scripts/moe-controls/h2d_bw.cu`. Retrain needs root (owner action). The on-file 0.3003 ms/miss
   (= 6.27 GB/s) is from a healthy-link era. `docs/design-prefill-fastpath-DRAFT.md` (foreign draft) found the same.
2. **Ledger smoke (cache N=42, 64 tokens, ctx 8192, cold cache)** on binary ab8424b1b:
   `ms/token = 41.2 + 3.109 * misses`, r2 = 0.995, 177 misses/token; 3.11 ms/miss = 1.887 MB / 0.61 GB/s.
   Slope is the link, not the kernel: 10.4x the on-file slope.
3. **Half-life unit SETTLED (measured):** 3024 plan calls = 48 groups x 63 tokens; every group's
   `device_step` runs 0..62. One `device_step` = one decode token per layer, so the shipped half-life 16
   = 16 tokens (pjsgsy's "window 16"). The earlier "1/3 token" claim stays retracted.
4. **Cold, short sample only (not representative, redo at depth):** misses by prior sighting count
   0/1/2/3+ = 9247/1748/81/0 (83.5/15.8/0.7/0 %). Evictions 9060 of 11076 misses, mean evicted freq 1.04.
5. `--decode-overlap` fails with any host-resident expert ("rebuilt graph requires another backend"), so it is
   dropped from every control arm. Overlap/no-overlap and gs/demand binaries gave the same 1.7 tok/s on the smoke.
6. Page-cache residency of the 50 GB model is a covariate (21 GB resident before prewarm); harness now prewarms
   and records it, plus the H2D probe, per arm.

**Consequence:** cache-vs-static tok/s on the 3060 cannot decide anything until the link is at Gen4. Routing
statistics (misses/token, bins) are link-independent and are being collected now (run3, flagged degraded).

## 10. Control arms at depth (run3/run4, 2026-09-21/22) - link 0.39 GB/s (Gen1) for all rows

Depth: prompt_n 13946, ctx 81920, MTP off, 200 out, 1 boot per arm, reps 2-3 warm (cache_n 13942). No `--decode-overlap`
except `cache42OL`. Binary `ab8424b1b`. Page cache prewarmed (~68 GB) per arm.

| arm | tok/s reps | VRAM after load / peak MiB | note |
|---|---|---|---|
| cache N=42 (`cache42L`) | 1.50 / 1.56 / 1.53 | 10461 / 10883 | link-bound, not a valid cache figure |
| cache N=42 + overlap (`cache42OL`) | 1.64 / 1.57 / 1.57 | 10573 / 11005 | overlap does not change the intercept |
| static `ncm4` (n-cpu-moe 44) | 12.42 / 12.53 / 12.60 | 10947 / 10983 | equal VRAM with cache (peak within ~100 MiB) |
| static `ot4` (-ot 4 layers) | 12.28 / 12.57 / 12.67 | 10947 / 10983 | same as ncm4: flag flavours agree |
| all-host (`--cpu-moe`) | 11.48 / 11.92 / 9.85 | 7205 / 7241 | rep 3 host-contended (load1 3.5, server CPU 219% vs 270%) |

Ledger at depth (link-independent routing): ~181-194 misses/token of 480, hit rate ~60%. Misses by prior sighting
count 0/1/2/3+ = ~72/25/3/0 %. 95% of misses evict an expert with mean windowed freq 1.13. Fit ms = I + 3.11-3.16*miss,
**I = 45-57 ms** (overlap off 57/52/57, overlap on 45/54/52), r2 0.99. The on-file I = 25.7 is not reproduced.
CPU-serve cost per expert (all-host vs ncm4, 40 experts): 0.11-0.19 ms, noisy.

Option A ceiling = 1000/(I + up*0.3003 + cpu*0.105), gate T=1 (up 55, cpu 135 per token): I=52 -> 12.0 tok/s,
I=41 -> 13.9, I=25.7 -> 17.7. Static = 12.5 (14.03 on file at ctx 16384). With the measured I the ceiling does not
clearly beat static before split/sync overhead. Caveat: I was measured at Gen1; whether the link changes I is untested.

## 11. Architect rulings (section 125, 2026-09-22)

1. Link defect confirmed. Idle sysfs proves nothing (both cards read Gen1 idle); the loaded 35 s probe is the instrument.
   The accepted constraint is x4 width; Gen1 speed was never accepted (~6% of the Gen4 x4 rate).
2. **Do not accept the Gen1 fixed cost I=45-57 as the break-even input. Neither build Option A nor shelve.** At Gen1 the slope
   term is ~587 ms/token against I~52, so the intercept is barely identified. The ceiling is an undefined input, not a range.
3. Prefill collapse reopened only as a free rider: re-measure prefill in the post-fix rerun.
4. Record drift: `PROJECT_STATUS.md:378` (idle only) and `:408` (card now on 02:00.0, not 07:00.0) annotated 2026-09-22.

Leader follow-up: identify I at Gen1 with a near-zero-miss decode (repetitive continuation) so the slope term is small.
