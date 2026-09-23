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

## 12. Pre-registered reading of the near-zero-miss legs (section 126), written BEFORE the data

Legs: `scripts`-style `smoke_rep.sh` (shallow, ctx 8192) and `smoke_rep14k.sh` (prompt_n ~13946, ctx 81920), both cache N=42,
ledger on, MTP off, `-ot`/`--cpu-moe` off. Repetitive continuation ("banana" x N). X = decode tok/s at ~0 misses.

1. **Validity gate:** read steady-state misses/token from the ledger (tokens after warm-up). If it is not near 0 (say > 20),
   X is not a ceiling and the leg is reported as such. X0 = 1000 / (ms/token - 3.14 * misses/token) is reported next to X.
2. **X < 12.5 (static)** -> shelve the cache on the 3060; decisive at Gen1 (a ceiling does not improve with the link).
3. **X >= 12.5 does NOT by itself justify a gate.** X = 1000/I is only the no-miss bound: a real gate still has to serve the
   ~190 misses/token, by upload (0.3003 ms at a healthy link) or CPU (~0.105 assumed, 0.11-0.19 measured delta).
   Tighter bound: B = 1000 / (I + M * c_min), M ~ 190, c_min = min(0.3003, measured CPU-serve cost from ncm2), I = 1000/X0.
   Expected from the Gen1 fit: X ~ 19-22 tok/s, B ~ 11.4-13.9. My rule (leader's choice, not the architect's): B <= 13.75
   (static x1.10, allowance for split/sync overhead) -> shelve; B > 13.75 -> the link fix is worth waiting for.
4. The leg also settles the I dispute directly: X at 14K vs the Gen1 fit I = 45-57 ms (X = 17.5-22).

## 13. Per-request cache reset: design or incidental? (section 128, source trace only, no rig)

**Finding.** The cold cache at every request start is a consequence of the legacy/grouped authority handoff, not a policy
choice and not the candidate-table publish path.

- `publish()` / `detach_resources()` are NOT per request. `llama_context::refresh_moe_candidates()` returns early unless
  `moe_candidate_refresh_pending`, which is set only at context creation and by `set_adapters_lora()` (`llama-context.cpp:2557`).
- The per-request reset is `cold_reset_grouped_resource()` (`moe-cache.cu:6574`): it memsets `slot_for_expert`/`expert_for_slot`
  to -1, `last_used`, `expert_frequency`, `expert_frequency_epoch`, `device_step`, `device_clock` and the plan to 0. It is called
  from the group-authority transition (`moe-cache.cu:11593`, `11628`) when `resource->legacy_dirty`.
- `legacy_dirty` is set whenever the LEGACY per-tensor cache takes a lease on the shared backing (`moe-cache.cu:8366`, `8481`,
  `8613`). The legacy path serves prefill and any non-grouped-decode graph, so every request that runs a prefill dirties the
  backing and the next grouped decode starts cold. Commit `a3f252c9e` ("retain MoE cache backing across phases") keeps the
  allocation across phases, not the contents. The dirty flag is a conservative correctness guard: the legacy path moves slots
  behind the grouped metadata's back, so the grouped metadata cannot be trusted afterwards.
- Consequence for agent workloads: a warm multi-turn request (`cache_n` 13942, `prompt_n` 4) still takes the legacy path for
  its 4-token prefill and therefore throws away the decode working set of the previous turn, even though it would be the case
  where carry-over is worth most. Conversely, a real long prefill overwrites the legacy slots with prefill-recency, so
  carry-over would only be meaningful together with a reconcile step (rebuild grouped metadata from the legacy slot map),
  not by simply skipping the memset.

**Simulator.** `sim_policy.py --carry=1` replays all traced requests as one stream per group (residency and frequency survive
request boundaries). It bounds the gain from removing the reset. Caveat: the three traced requests are the same prompt, so
request k+1 re-touches request k's experts and the result is optimistic; a distinct-prompt trace is needed before this number
is used for a decision.

**Exactness of replays (architect section 128).** Residency-only replays (kernel at any half-life, LRU, Belady mandatory
install) are exact: routing is policy-independent at temperature 0 and each group's cache is independent. Bypass replays
(gate T, Belady + bypass) are APPROXIMATE: a bypassed expert would be computed on the CPU, and a different accumulation order
can perturb later logits and so later routing. Read them as bounds, not predictions.

**Architect ruling (section 129): carry-over is parked as a quantified known defect, about 1%.** Cold fill costs ~480 misses at
step 0 against ~190/token steady state, so the excess is ~290 misses per request. On the 128-token turns the multi-turn
harness serves that is 290 / (480 + 127 x 190) = ~1.2% (200-token turns: ~0.75%). It becomes material only for 10-30 token
tool-calling turns. It is not a larger lever than the admission gate, and the distinct-prompt carry-over trace is NOT run for
the decision (only if free alongside a leg already running). Filed future work: make the reset prefix-reuse-aware, which needs a
reconcile step that rebuilds the grouped metadata from the legacy slot map, not just skipping the memset.

**Reporting rule for B.** If B rests on a bypass number (gate or Belady + bypass), it is reported as needing split-execution
confirmation and does not decide alone. Residency-only replays are exact; gate and Belady-with-bypass replays are approximate.

## 14. c_cpu from the static arms (run3/run4, 14K depth, warm reps 2-3) - written BEFORE X

Arms `ncmK` keep the expert tensors of the last K layers on the GPU and serve the rest from the CPU (K = 4, 2, 0 = `allhost`).
Warm decode (`cache_n` 13942), `allhost` rep 3 excluded (contended):

| arm | tok/s (reps 2,3) | ms/token |
|-----|------------------|----------|
| ncm4 | 12.53, 12.60 | 79.59 |
| ncm2 | 12.31, 12.21 | 81.57 |
| allhost | 11.92 (rep 3 9.85 contended) | 83.89 |

Cost of moving one layer's experts from GPU to CPU: 0.990 ms (4 -> 2) and 1.163 ms (2 -> 0). At top-10 that is
**c_cpu = 0.099-0.116 ms per expert per token**, which brackets the architect's assumed 0.105 and tightens the earlier
0.11-0.19. Rep 1 (cold prompt, `cache_n` 0) is 0.3-0.5 tok/s lower on every arm and is not used.
Caveat: these arms serve all 10 experts of a layer on the CPU in one batch. Option A serves only the non-admitted subset
(~4 of 10 per layer), so any per-call fixed cost is amortised over fewer experts and c_cpu is a lower bound on the split
case. Report B over c in {0.105, 0.116}, and flag a bypass-based B as needing split-execution confirmation (section 129).

**Provisional B grid (I unmeasured, X pending):** B = 1000 / (I + M * c), shelve threshold B <= 13.75.

| I (ms) | M=190, c=0.105-0.116 | M=137, c=0.105-0.116 |
|--------|----------------------|----------------------|
| 25.7 (on file) | 20.9-21.9 | 24.0-24.9 |
| 41 (shallow Gen1 fit) | 15.9-16.4 | 17.6-18.1 |
| 52 (14K Gen1 fit mid) | 13.5-13.9 | 14.7-15.1 |
| 57 (14K Gen1 fit high) | 12.7-13.0 | 13.7-14.0 |

Break-even I for B = 13.75: about 52 ms at M = 190 and about 58 ms at M = 137. So the shelve call turns on whether the
measured I is above roughly 52-58 ms, i.e. whether X = 1000/I is below roughly 17-19 tok/s. The Gen1 fit range (45-57)
straddles it, which is why the X legs decide and this table does not.

## 15. First 14K "repetitive" leg FAILED the validity gate (2026-09-22) - not a ceiling, do not read as X

`smoke_rep14k.sh` (old binary, no ids; raw `/completion`, prompt "banana " x 13932, N=42, MTP off, `-ot`/`--cpu-moe` off) did
not stay repetitive: the model broke out into reasoning text (`<think>`), so routing kept changing. The section 12 gate (misses
near 0, say < 20/token) caught it. Ledger, misses/token by request step (3 requests; each starts cold, step 0 = 480):

| steps | req 1 | req 2 | req 3 |
|-------|-------|-------|-------|
| 1-3 | 247 | 255 | 253 |
| 4-19 | 171 | 206 | 210 |
| 20-59 | 185 | 189 | 194 |
| 60-198 | 142 | 138 | 123 |

Decode was 1.85 / 1.82 / 1.95 tok/s (541 / 548 / 512 ms/token) at ~146-154 mean misses/token, VRAM peak 10819 MiB.
Backing out I = ms - 3.14 x misses gives 54-64 ms, but that is exactly the unidentified quantity of section 125: the
slope term (~470 ms) is 8x the intercept, so a 0.5 ms/miss slope error moves I by ~75 ms. It is NOT accepted as I.
Useful by-product: the natural-continuation steady state here is 123-142 misses/token, i.e. in the region of the
architect's M ~ 137 floor, and hl=16 LRU-frequency never gets below it without an oracle.

**Fix:** the continuation is forced with a GBNF grammar `root ::= (" banana")+` (temperature 0, tokens/unit 1.001), so the
routed token stream is constant and misses should collapse; the validity gate is re-applied to the new ledgers. Legs rerun on
the rebuilt binary (`a2c46384b`, ids in the ledger): shallow (ctx 8192) then 14K. The failed run is kept as `rep14k-think/`.

## 16. Shallow forced-repetition leg + within-run fit identify I (2026-09-22) - supersedes the I dispute of section 125

Rebuilt binary `a2c46384b` (ids in the ledger), shallow leg (ctx 8192, prompt "banana " x 60, grammar-forced
`root ::= (" banana")+`, N=42, MTP off, 3 requests, link 0.39 GB/s).

1. **Simulator validated against the real kernel.** `sim_policy.py` `kernel(hl=16)` reproduces the recorded miss set on
   **28,656 / 28,656 records (100.00%)** over the 3 requests x 48 groups x 199 steps. Residency-only replays are exact.
2. **The near-zero-miss leg is not reachable at N=42.** Even with the token stream forced constant, steady state is
   **79 misses/token** (step 0 = 480, steps 1-3 = 175, 4-19 = 107, 20-59 = 73, 60+ = 79): position and recurrent state keep
   moving the routing. The section 12 gate (< 20) fails again, so X = tok/s at ~0 misses is never observed directly; the three
   requests are identical (deterministic) so they are timing replicates, not independent samples.
3. **What replaces X: a within-run regression** (`scripts/moe-controls/fit_step_time.py`, step time from the gap between
   consecutive last-group plan records vs misses summed over the step, steps >= 4). Misses vary 18-207 inside one run, so I is
   an extrapolation of only 18 misses to 0 and does not depend on link state, page cache or thermals between runs:

   | ledger | misses/step | I (ms) | slope (ms/miss) | R2 | 1000/I |
   |--------|-------------|--------|-----------------|----|--------|
   | shallow forced, req 1-3 | 18-207 | 30.9 / 31.2 / 32.4 | 3.084 / 3.068 / 3.057 (+-0.011) | 0.997-0.998 | 30.9-32.4 |
   | 14K natural (section 15, old binary), req 1-3 | 19-344 | 48.4 / 36.9 / 36.4 | 3.154 / 3.257 / 3.209 (+-0.02) | 0.989-0.995 | 20.7-27.5 |

   So **shallow I = 31 +- 1 ms** (the earlier between-run "41" was a confounded fit) and **14K I = 36-48 ms** (req 1 is the
   cold-prompt request; 2-3 agree at 36-37). The slope 3.06-3.26 ms/miss is consistent with 1.887 MB / 0.39 GB/s = 4.8 ms
   only if ~37% of the upload is hidden behind compute (or the probe is pessimistic); see section 18 item 7. It is measured, not assumed. Read the 14K rows as
   provisional: that ledger is from the failed leg (model reasoning, no ids) and the grammar-forced 14K leg is still running.
4. **Provisional B with the identified I** (B = 1000 / (I + M * c), c in {0.105, 0.116} from section 14, static x1.10 = 13.75):

   | I | M=190 | M=137 |
   |---|-------|-------|
   | 31 (shallow) | 18.9-19.6 | 21.5-22.0 |
   | 36 (14K, low) | 17.0-17.5 | 19.3-19.8 |
   | 48 (14K, high) | 14.0-14.5 | 15.8-16.2 |

   Every cell clears 13.75, but the high-I / high-M corner (14.0-14.5) has only ~10-15% margin over the shelve line. At a
   healthy link the ungated cache costs 1000 / (I + M * 0.3003) = 13.0 at I=36, M=137, i.e. no better than static; the gain
   comes from serving non-admitted experts on the CPU, which is a bypass design.
5. **Replays on the (repetitive, so optimistic) shallow trace**, cost per token in ms at c_up 0.3003 and c_cpu 0.105:
   kernel hl=16 23.7, lru 28.4, gate T=1 hl=16 11.7 (installs 18.9 + bypass 57.0), gate T=2 hl=16 9.5,
   Belady mandatory 15.3, Belady + bypass 9.3. Bypass rows are APPROXIMATE (section 129).

**Not yet decided.** Open before B is final: (a) the grammar-forced 14K leg for depth I under controlled conditions, and
(b) a natural-prompt trace WITH ids for the Belady M floor at depth, since the forced trace is easier than real text.

## 17. 14K grammar-forced leg: I at depth, B, and the pre-registered rule applied (2026-09-22)

Leg: ctx 81920, prompt_n 13933 (`cache_n` 0 then 13929 for reps 2-3), N=42, MTP off, no `-ot`/`--cpu-moe`, grammar-forced
continuation, link 0.39 GB/s, VRAM peak 10807 MiB, decode 2.90 / 2.96 / 2.88 tok/s. Ledger has ids.

- **Validity gate:** NOT met as a ceiling (steady state 89-93 misses/token; steps 1-3 231, 4-19 110, 20-59 104). Same
  conclusion as section 16: near-zero misses are unreachable at N=42, so X is replaced by the within-run fit.
- **Simulator:** `kernel(hl=16)` reproduces the recorded miss sets on 28,656 / 28,656 records again (depth 14K).
- **Within-run fit at depth** (`fit_step_time.py`, misses/step 25-283): I = **43.2 / 38.9 / 39.8 ms**, slope 3.02-3.06
  ms/miss, R2 0.995-0.996. Together with the natural 14K ledger of section 16 (36-48 ms) depth I is **39-43 ms**, i.e. 1000/I
  = 23-26 tok/s. The on-file 25.7 ms was a healthy-link, shallower-context value.
- **B = 1000 / (I + M * c)**, c in {0.105, 0.116}; shelve line 13.75 (static 12.5-12.6 x 1.10). M = 54-57 is the Belady floor on
  this (easier, forced) trace, 137 is the architect's natural floor, 190 the observed natural steady state:

  | I | M=54-57 | M=137 | M=190 |
  |---|---------|-------|-------|
  | 38.9 | 22.0-22.4 | 18.3-18.8 | 16.4-17.0 |
  | 43.2 | 20.1-20.5 | 16.9-17.4 | 15.3-15.8 |

- **Rule (section 12, item 3): B > 13.75 in every cell, so do NOT shelve.** Lowest B = 15.3, i.e. +22% over static, highest
  22.4, +79%. Consequence: the fixed cost is not what caps the design; the miss/serve path is.
- **What B is NOT.** It is an upper bound for a design that serves every non-hit expert at c_min. At a healthy link the
  ungated cache is 14.2 / 11.9 / 10.0 tok/s at M = 90.6 / 137 / 190 (I=43.2, 0.3003 ms/upload), so plain admission tuning
  does not beat static; only removing the upload from the critical path (CPU serve of non-admitted experts) does. That
  is a bypass design, so per section 129 B **needs a split-execution confirmation before it decides a build**.
- **Replays (approximate for bypass rows), forced 14K trace, ms/token at c_up 0.3003, c_cpu 0.105:** kernel hl=16 27.2,
  hl=64 25.5, lru 31.8, gate T=1 15.5 (installs 29.8 + bypass 62.7), gate T=2 12.0, Belady mandatory 17.1, Belady + bypass 11.2.
  Half-life 16 -> 64 is worth only ~6%; the policy knob is not the lever, the bypass is.
- **Open:** a natural-prompt 14K trace with ids would replace M = 54-57 (forced) by a real floor. It refines B inside the
  range above; it cannot flip the rule, because B > 13.75 already holds at the M = 190 observed natural rate.

## 18. Architect rulings (section 130, 2026-09-22) - read this before section 17's "do not shelve"

1. **The section 12 gate is RETIRED AS UNREACHABLE, not failed.** At N=42 with position/recurrent routing drift there is no
   near-zero-miss steady state (79 misses/token shallow, 89-93 at 14K even with the token stream forced constant), so the gate
   was specified against a state that does not exist. It is replaced by the within-run regression of section 16, and the
   replacement is justified there. **I is link-independent by construction** (the intercept at zero misses contains no
   upload), so it is a Gen4-valid number extracted from a Gen1-broken rig. That is why this decision did not wait on the
   3060 link retrain (which needs root).
2. **What "do not shelve" means.** B > 13.75 in every cell of section 17, but the ungated cache at a healthy link is
   14.2 / 11.9 / 10.0 tok/s at M = 90.6 / 137 / 190, i.e. NOT better than static. So `--moe-expert-cache-size` as shipped is
   dead on its own merits, Gen4 or not. What survives the rule is **a bypass / split-execution design that does not exist
   yet**. "Do not shelve" is NOT build authorization; it keeps the lever open pending item 3.
3. **c_cpu is the entire remaining decision.** Shelve line B <= 13.75 means I + M*c >= 72.73, so the c that flips a cell is
   c* = (72.73 - I) / M:

   | | M=190 | M=137 | M=57 |
   |---|-------|-------|------|
   | I=43.2 | 0.155 | 0.216 | 0.518 |
   | I=38.9 | 0.178 | 0.247 | 0.593 |

   Measured c_cpu is 0.099-0.116 with whole layers on CPU (10 GEMMs on `-t 6`). The worst cell flips at a **1.34x** penalty
   (1.53x at I=38.9), and serving ~4 experts instead of 10 on 6 threads is expected to cost MORE per expert, not less.
4. **Do not build split-execution in order to measure it.** Stage 1 is a standalone microbenchmark
   (`scripts/moe-controls/moe_cpu_bench.cpp`, driver `run_f1f2.sh`) on the real tensors and quant types of the live model:
   F1 = c(4)/c(10) (pool utilisation at `-t 6`), F2 = c(4, GPU decode running)/c(4, idle). Corrected c_cpu =
   (0.099-0.116) x F1 x F2. Even that is a LOWER bound: the harness lacks the cache pressure from the rest of the model's CPU
   work and the `per_layer_token_embd=CPU` offload.
   **Stage-2 rule, pre-registered before F1/F2 were seen:** authorise a split-execution build ONLY if B > 13.75 at the WORST
   cell (I = 43.2, M = 190, corrected c at its UPPER bound). If the result falls between, escalate to the owner with the
   range; do not decide alone and do not pick a friendlier cell.
5. **The natural-prompt Belady trace is KILLED** (it cannot flip the rule). **M = 190 is the planning number.** Belady is
   unachievable and policy knobs buy ~6%, so M = 54-57 (from the easier forced trace) must not appear in any planning number.
   Run a Belady trace only as a zero-cost rider on a leg that runs anyway.
6. **Eviction / half-life workstream is CLOSED, including the orphaned `1ea22696f` knobs.** Gate T=2 (12.0 ms/token)
   beats Belady with mandatory install (17.1): perfect replacement loses to imperfect bypass. Do not reopen it.
7. **Provenance of c_up = 0.3003 ms/miss.** It is NOT a probe and NOT derived from a Gen1 number. It is the slope of the
   on-file cost model `ms/token = 25.7 + 0.3003 * misses` (`b950f4cc2`, r2 0.990, 196 steps, 1.881 MB/miss, card not
   stated), which the note reads as 6.27 GB/s: a healthy-link-era FITTED slope, i.e. an independent Gen4 anchor. The gap the
   architect asked me to name: 1.887 MB / 0.39 GB/s (the loaded Gen1 probe) predicts 4.84 ms/miss, but the measured Gen1
   slope is 3.05 ms/miss = 0.62 GB/s effective (62% of the 1.0 GB/s Gen1 x4 ceiling, vs 6.27/7.88 = 80% at Gen4). Two readings:
   (a) the probe is pessimistic, or (b) ~37% of each upload is hidden behind compute. The data cannot separate a constant
   hidden fraction from a pessimistic probe, but it does exclude a SATURATING overlap: the slope is the same in the low-miss and
   high-miss halves of every ledger (shallow 3.04-3.13 vs 3.02-3.06; 14K 3.10-3.22 vs 2.92-3.04), so there is no knee.
   Consequence for the Gen4 projection: c_up = 0.3003 is anchored to a Gen4 fit, not scaled from the Gen1 slope, so the
   1.6x unexplained factor does not propagate into it. Ratio check: 3.05 / 0.3003 = 10.2x between the two links, against
   a raw Gen4/Gen1 bandwidth ratio of 7.9x, consistent with Gen1 running at lower efficiency (62% vs 80%).
8. **Owner blocker downgraded.** The 3060 link retrain is no longer blocking the decision. It is confirmatory: it validates
   c_up for any design that still uploads, and the `cache42` / `cache42O` rerun.

## 19. F1/F2 at the design's real call spacing (sections 131-133) - READ THIS BLOCK FIRST

**Status: BETWEEN, escalated to the owner; the confirming run is BLOCKED on an owner constraint (item 11).**

- **The shippable design already fails; only an unachievable bound straddles the line.** The indicative hybrid (admit some, upload
  those, CPU-serve the rest) at the worst cell is **B = 13.21, below the 13.75 shelve line.** It carries a "sign, not a number"
  caveat (its 10% upload share comes from the easier forced-trace T=2 replay and it needs a `c_up * uploaded_fraction` term).
  The 14.18 PASS below belongs to pure bypass at M = 190, which is not a design but a bound.
- **Why pure-bypass-at-M=190 is only a bound (architect s133.3).** M = 190 is the steady state of a cache that INSTALLS on miss. A
  design that never uploads never installs, so its real M is 480 (every expert of every layer is a miss). `B = 1000/(I + M*c_cpu)`
  therefore takes the hit rate of an admitting cache and the traffic of a non-admitting one, i.e. it prices installs at zero.
  B is an OPTIMISTIC UPPER BOUND, so the s130 rule is a NECESSARY, not sufficient, condition: failing it shelves the lever,
  passing it only keeps the lever open. That is the concrete reason "do not shelve" is not build authorization.
- **What the pending run is worth.** It cannot authorise a build. It can SHELVE decisively: if the K-absorbing scaling is right the
  worst cell is 12.61, the optimistic bound itself fails, and the whole lever closes with no further work.
- Sections below are the measurement record; the pass/fail numbers in them are for the bound, not for the hybrid.

### 19 detail (F1/F2 at 0.85 ms spin gap, 4 idle / 3 busy / 2 busy+hog runs)

Bench `scripts/moe-controls/moe_cpu_bench.cpp` (real expert tensors of 12 layers spread over all four shards: IQ2_XXS/XS/S, IQ3_XXS/S
gate/up, IQ4_NL down; mmap, no repack; random expert ids, cold weights), `-t 6`, threadpool **poll = 50** (`ggml.c:8296`), identical
to the engine (`common/common.h:74`; the static arms passed no `--poll`). Call spacing **0.85 ms spin** (= I/48). Raw output
`scripts/moe-controls/f1f2-results-0.85ms.txt`, composition `f1f2-fit-0.85ms.txt` (`fit_f1.py`). Four idle runs, three busy
runs (cache42 decoding on the 3060, GPU 100%, link Gen1), two busy+hog runs.

1. **C-state artifact (leader's catch).** The first bench slept between calls (`sleep_for`), letting the CALLING thread's core drop
   into a C-state; the engine's decode thread never does. Spinning the gap removed it. Poll 50 keeps the workers spinning for
   ~n_rounds = 1024*128*50 = 6.6M iterations, so they do not sleep across 0.85 ms either.
2. **F1 as a multiplier is dropped (architect).** The CPU is called once per layer, 48x/token, serving j = M/48 experts, so j and M
   are one parameter and `CPU ms/token = 48 * T(M/48)`; with T = K + w*j this is `48K + M*w`, and
   **B = 1000 / (I + 48K + M*w)**. 48K is M-invariant; lower M amortises K over fewer experts, so cheaper-looking cells are
   worse than a fixed ratio says: measured c(j)/c(10) = **1.17 at M=190 (j=3.96), 1.28 at M=137 (j=2.85), 1.75 at j=1**, so the
   killed M = 54-57 cells (j~1.15) were ~1.7x, not 1.2x. That is the honest direction.
3. **Pooled per-call T(j), ms** (idle / busy / busy+hog): j=1 0.147/0.148/0.147, j=2 0.238/0.240/0.252, j=3 0.317/0.329/0.343,
   j=4 0.391/0.414/0.439, j=5 0.475/0.498/0.526, j=6 0.522/0.597/0.615, j=8 0.719/0.765/0.797, j=10 0.837/0.920/0.973.
4. **K and w (idle, pooled fit): K = 0.081 ms, w = 0.0769 ms.** F1_fit = 1.142, F1_raw (c(4)/c(10)) = **1.170**; the raw ratio is the
   more conservative and governs. Per-run K ranges 0.059-0.100 and w 0.072-0.082: the K/w split is poorly identified run to run
   (they trade off), while c(4) is stable at 0.092-0.103. That is why the rule uses T(j) interpolated at j = M/48, not K and w
   individually. Residuals of the pooled fit: j=1 -6.9%, j=2..5 +0.8..+2.0%, j=6 -3.8%, j=8 +3.3%, j=10 -1.6%. The +12% at j=6
   from the 1.5 ms-sleep run does NOT persist (it flips sign between runs: -14.6% .. +5%), so it is run-to-run noise, most likely
   thread placement on the 12700K's P/E cores; I cannot name a mechanism and did not pin threads. The j=1 dip (-7%) does recur
   (concave at small j); it does not touch the j ~ 3-4 operating point.
5. **Controls.** poll = 0 (workers sleep at once): K 0.068, c(4) 0.0995 vs idle K 0.081, c(4) 0.0979: **no penalty, so the pool
   wake-up is NOT what K is made of. The mitigation "keep the pool hot (`--poll`)" proposed in section 131.4 is FALSIFIED and
   STRUCK (architect s133.1).** K is per-call dispatch, activation quantisation and
   graph-node barriers. A 1.5 ms spin gap raises K to 0.120 and a sleeping caller to 0.095, so K does depend on call spacing
   by some mechanism other than pool sleep (unidentified; LOGGED AS UNEXPLAINED, not chased: it cannot move the rule cell, section 123). The lever that survives is fewer, larger calls (batch CPU-served
   experts across layers where the dependency graph allows): 48K = 3.9 ms of bench time, 5.4 ms scaled in situ, about 10% of a token.
6. **F2.** F2_pure = c(4, GPU decode)/c(4, idle) = **1.057** (per-j 1.01-1.14, no j below 1.0). F2_hybrid, with a 6 GB/s host-memory
   reader added, = **1.122**. The reader models Gen4 upload staging and is only valid for a hybrid design that still uploads;
   under pure bypass there is no such stream and the CPU's own weight reads are already inside c_cpu, so the RULE CONSUMES F2_pure
   (architect s132.2). **Sign correction (architect s133.2):** the busy workload was cache42, which UPLOADS ~190 experts/token over
   the Gen1 link, while pure bypass uploads nothing. So the measured F2_pure = 1.057 contains PCIe and host-memory contention the
   priced design does not generate: for pure bypass it is an OVERestimate (conservative), and the true pure-bypass B is HIGHER
   than every pure-bypass number in this section. An earlier draft of this note called it a lower bound at Gen4; that holds only
   for a hybrid design that still uploads (where F2_hybrid = 1.122 is the relevant figure).
7. **Scaling to in situ.** Bench c(10) = 0.0837 ms vs engine 0.099-0.116 (static arms), so s = 1.18-1.39. Two ways to apply it,
   because I cannot tell from here whether the engine-minus-bench gap is proportional or a fixed per-call cost:
   **uniform** (T_e = s*T, the method the architect specified) and **K-absorbing** (T_e = T + (10*c_meas - T(10)), all of the gap
   is a fixed per-call cost = upper bound). B = 1000/(I + 48*T_e(M/48)*F2_pure), the lower of {fit, raw} governs:

   | c_meas | scaling | M=190 (j=3.96) cpu ms | B at I=43.2 / 38.9 | M=137 (j=2.85) cpu ms | B at I=43.2 / 38.9 |
   |--------|---------|-----------------------|--------------------|-----------------------|--------------------|
   | 0.099 | uniform | 23.3 | 15.0 / 16.1 | 18.3 | 16.2 / 17.5 |
   | 0.099 | K-absorbing | 27.5 | 14.1 / 15.1 | 23.3 | 15.0 / 16.1 |
   | 0.116 | **uniform** | 27.3 | **14.18** / 15.1 | 21.5 | 15.5 / 16.6 |
   | 0.116 | **K-absorbing** | 36.1 | **12.61** / 13.3 | 31.9 | 13.3 / 14.1 |

   Shelve line 13.75.
8. **Verdict under the pre-registered stage-2 rule (worst cell I=43.2, M=190, c_meas at its upper bound 0.116, F2_pure, more
   conservative of fit/raw):** with the architect's uniform scaling **B = 14.18, PASS by 3.1%**; with the K-absorbing upper bound
   **B = 12.61, FAIL**. The range across the worst cell is 12.6-15.0. **The result is BETWEEN, so per section 130 it is escalated
   to the owner and NOT decided here.** Bench noise is the same size as the margin: idle c(4) ranged 0.092-0.103 across four
   runs (+-6%), which moves the rule cell by ~+-0.5 in B, so a 3% pass is not statistically resolved.
9. **Indicative only, not the rule:** a hybrid design (90% CPU-served, 10% uploaded at c_up 0.3003, F2_hybrid) at the worst cell
   gives B = 13.21 (FAIL). It needs the `c_up * uploaded_fraction` term the pure-bypass formula lacks; the 10% split is taken from
   the gate T=2 replay on the (easier) forced trace, so treat it as a sign, not a number.
10. **What would resolve it (proposal, NOT run, BLOCKED - item 11):** a SECOND in-situ point. Static arms `ncm2` and `allhost` at a
    reduced routed-expert count (`--override-kv qwen4exp.expert_used_count=int:4`, timing only, output meaningless), next to the
    measured j=10 arms (0.99-1.16 ms per layer). Taking the engine's CPU-vs-GPU delta between arms at the same k cancels I and
    gives K_engine and w_engine directly, which removes the bench from the critical path: it closes BOTH the 1.18-1.39 scaling
    ambiguity and the +-6% bench noise at once, because the static arms replicate at +-0.6%. Four short shallow arms, ~20 min of rig.
    It cannot authorise a build; it can SHELVE decisively (K-absorbing scaling gives 12.61 at the worst cell, so the optimistic
    bound itself fails and the lever closes). Do not pin threads: the engine is unpinned, so pinning would trade comparability for
    precision the two-point method makes moot.
11. **BLOCKED (architect s133.6).** `--override-kv qwen4exp.expert_used_count` collides with a standing owner constraint: k is not
    overridden, the model config value of 10 is used, no `--override-kv`. The timing-only rationale is a good argument that the
    constraint's purpose is not engaged, but it is the owner's rule; the architect routed the request to the owner. HOLD the run
    until the owner rules. The 3.1% pass being inside +-6% bench noise is carried to the owner verbatim.

## 20. In-situ T_engine(4) from k=4 static timing arms (architect s134, owner waiver) - PRE-REGISTRATION, written BEFORE any k=4 number

**Owner waiver (2026-09-22, relayed s134):** `--override-kv qwen4exp.expert_used_count=int:4` is allowed for these timing arms ONLY. k stays
10 everywhere else (memory: `owner-constraint-no-override-kv-expert-count`). Every k=4 record carries `TIMING_ONLY_K_OVERRIDE=true` and the
tag suffix `-k4`; no throughput figure from these arms may enter any table other than the K_engine/w_engine fit below.

**Why the k=4 arm is the operating point.** j = M/48 = 3.96 at M=190, so a static arm at top-4 serves j=4 experts per CPU layer call, exactly the
worst cell. No extrapolation, no K/w split needed for the rule.

**Protocol (fixed now).** Runner `scripts/moe-controls/arm_k4_timing.py`, driver `/tmp/opencode/controls/k4timing/run_k4.sh`. Shallow prompt (167 bytes,
open-ended story), ctx 81920, `-t 6`, MTP off, no decode-overlap, `--moe-expert-cache-size 0`, `per_layer_token_embd=CPU`, 3060 (CUDA1), binary
`a2c46384b` (libllama 7710842c as run3; libggml-cuda differs only by the ledger commit, irrelevant at cache size 0). Arms `ncm4`, `ncm2`, `allhost`
(= 44/46/48 layers' experts on CPU) each at k=10 (no override) and k=4, 4 reps per boot (rep 1 cold, DISCARDED; reps 2-4 = n=3 per boot). Two
passes over the six arms, pass 2 in reversed order, i.e. n=6 warm reps per arm x k. Depth-independence: the CPU-vs-GPU delta per layer cancels
attention, so shallow is valid; the k=10 shallow delta is also a depth check against section 14 (0.990 / 1.163 at 14K).

**Estimator (single, fixed).** D(k) = OLS slope of decode ms/token against layers-on-CPU (44, 46, 48), all warm reps of both passes pooled, in ms per
layer moved GPU -> CPU. **T_engine(4) := D(4).** Secondary, reported but not governing: pairwise slopes ncm4->ncm2 and ncm2->allhost, per-pass slopes.
Spread: per-boot slope range and the OLS standard error.

**Rule (architect s134, fixed before data).** `B_worst = 1000 / (I + 48 * D(4) * F2_pure)`, I = 43.2, F2_pure = 1.057. Threshold: B_worst <= 13.75 iff
D(4) >= 0.582 ms/layer.
- **B_worst <= 13.75 -> SHELVE, decisively.** Report and stop; no rescue measurements, no other cells.
- **B_worst > 13.75 -> the bound survives and nothing more.** Not build authorisation (the bound prices installs at zero). Next question is the hybrid's
  `c_up * uploaded_fraction` on a NATURAL trace, escalated to the owner with the hybrid B = 13.21 as the headline.
The rule is applied to the point estimate. If the +-1 SE interval straddles 0.582 that is disclosed, not resolved by choosing a friendlier estimator.

**Secondary:** K_e / w_e from D(j) = K_e + w_e * j using D(4) and D(10) of the same session (the in-situ analogue of the bench K=0.081 / w=0.0769;
gives 48 K_e for the batching lever).

**Known bias, stated now.** D = T_cpu - T_gpu (the layer's GPU expert cost is saved when it moves to the CPU), so D understates the CPU cost of a
design that ALSO keeps the GPU experts, which makes B_worst optimistic. This is the same convention as the k=10 c_cpu of section 14, so it does not
change the comparison, but a pass is weaker evidence than a fail.

**Serial-cost assumption, recorded before the branch is known (architect s135; record only, not acted on).** D(4) is a SERIAL cost: in the static arms
every expert of a layer is CPU-served, so the delta is the whole addition to the critical path. `B = 1000/(I + 48*D(4)*F2)` therefore charges the CPU work
as if none of it overlaps GPU work. A real split design runs CPU-4 alongside GPU-6 inside a layer, so its effective cost could be lower than D(4) by the
overlapped fraction, minus sync. This is a PESSIMISTIC term, and it partly offsets the OPTIMISTIC term above (installs priced at zero, D = T_cpu - T_gpu).
The two run in opposite directions; nothing here claims they cancel, and neither was measured. This is not an escape hatch: section 134 stands, and
B_worst <= 13.75 shelves and stops. It is written into the rationale so that a shelve is durable, i.e. so a later reader sees what the number assumed
(serial CPU cost, zero-install bound, F2_pure from a run that uploaded, I = 43.2 the max of three fits) instead of inheriting it as settled.

## 21. T_engine(4) in-situ result: bound survives, does not shelve (2026-09-22)

Campaign `scripts/moe-controls/run_k4.sh` -> `arm_k4_timing.py`, two order-counterbalanced passes, n=6 warm reps per arm x k (rep 1 of every
boot discarded). No void records, no host-contention outliers (cpu_busy_pct 15-18%, `foreign` PID idle throughout; the one elevated-sd cell,
`ncm2` k=10 at 2.25%, is a monotonic pass2 drift of 13.4 -> 12.7 tok/s, not a step change, so it is reported as-is, not excluded).
Results `scripts/moe-controls/results/k4-pass{1,2}.jsonl`. Fit `fit_k4.py`.

**Decode ms/token, n=6 each (sd, %sd):**

| arm | k=10 | k=4 |
|---|---|---|
| ncm4  | 72.641 (0.236, 0.32%) | 45.894 (0.229, 0.50%) |
| ncm2  | 75.623 (1.705, 2.25%) | 46.968 (0.193, 0.41%) |
| allhost | 77.506 (1.053, 1.36%) | 48.014 (0.222, 0.46%) |

**T_engine(4) := D(4)**, pooled OLS slope of ms/token against layers-on-CPU (44/46/48), all 18 k=4 points: **D(4) = 0.5299 +/- 0.0301 ms/layer
(1 SE)**. Per-pass: pass1 0.4807 +/- 0.0361, pass2 0.5790 +/- 0.0463 (passes bracket the pooled value, no order drift large enough to flip the
branch). Pairwise deltas agree: ncm4->ncm2 0.5371, ncm2->allhost 0.5226, ncm4->allhost 0.5299.

D(10) = 1.2163 +/- 0.1676 ms/layer (noisier, `ncm2` k=10 drift above is most of it).

**K_e/w_e** (secondary, from D(4) and D(10) of this session): K_e = 0.0723 ms/call, w_e = 0.1144 ms/expert, 48*K_e = 3.47 ms/token. Compare
bench (section 19): K = 0.081, w = 0.0769. K_e is close to the idle bench K (0.072 vs 0.081); w_e is 49% above the idle bench w (0.114 vs
0.0769) -- the in-situ per-expert cost, measured on the real serving path (sampling, metrics, whole-layer batch of 10 or 4 experts on `-t 6`
alongside everything else the server thread does), is higher than the standalone microbench predicted. This is evidence the bench under-priced
the real call, not that the fit is unstable: D(4) itself has the tightest relative spread of any number in this track (0.50% sd).

**B_worst = 1000/(I + 48*D(4)*F2_pure)**, I = 43.2, F2_pure = 1.057, threshold D(4) >= 0.582 ms/layer to shelve:

| estimator | D (ms/layer) | B_worst (tok/s) |
|---|---|---|
| point | 0.5299 | **14.27** |
| D - 1 SE (cheaper) | 0.4998 | 14.59 |
| D + 1 SE (pricier) | 0.5600 | 13.96 |

**The full +/-1 SE interval clears 13.75** (13.96-14.59); this is not a "between" case like section 19's bench-derived 12.61/14.18 split, it is
a direct in-situ measurement whose noise does not reach the line.

**BRANCH (pre-registered, section 20 / architect s134): B_worst > 13.75 at every estimator -> the bound survives, and that is all it does.**
Not build authorisation (the bound still prices installs at zero, section 19). Per s134, the next question is the hybrid's
`c_up * uploaded_fraction` term on a NATURAL trace (the 10% share behind the hybrid B = 13.21 came from the easier forced trace) -- that
escalates to the owner with the hybrid number as the headline, not a build proposal off this bound.

**What resolved and what didn't.** This retires the section 19 bench-vs-K-absorbing ambiguity (12.61 vs 14.18) with a direct measurement
(14.27, tight interval) instead of a scaled one -- but it answers only the pure-bypass bound, per its own pre-registration. The hybrid number
(13.21) and its natural-trace uploaded-fraction refinement remain the open, escalated question.

## 22. Corrections to section 19/21 and the hybrid f check (architect s137) - zero-rig, existing data only

**Correction 1 (architect, to their own s130/131 scaling hypotheses).** Neither bracketed the measured section 21 numbers: w_e = 0.1144 is
above even the uniform method's upper bound (0.091-0.107), K_e = 0.0723 is below bench K = 0.081. The per-expert term scales up harder than
either hypothesis, the per-call term scales slightly down. This is the strongest evidence in the track for measuring in situ over scaling a
bench: both branches would have mispredicted, in opposite directions.

**Correction 2 (batching lever, section 132).** 48*K_e = 3.47 ms of a ~70 ms token is **~5%, not ~10%** as earlier quoted. Fixed here.

**Correction 3 (statistical caveat on section 21, does not move the branch).** The pooled SE treats between-pass variance as zero; with only
two passes that variance is unidentifiable, not zero. Pass1 D=0.4807, pass2 D=0.5790, differ by 1.67 SE of the difference (no evidence of an
order effect, but not proof of none). On pass2 alone, B=13.78; pass2 +1 SE, B=13.35 (would fail). The branch stands (point estimate and every
pooled estimator clear 13.75), but "clean result" overstated the margin; recorded as thin under a conservative between-pass treatment.

**The headline (against static 12.5-12.6):**
- **Bound (installs priced at zero, unachievable): 14.27 = +13.7%**
- **Hybrid (the only shippable shape): 13.21 = +5.3%**
That is the entire return on split execution (companion tensors, expert->slot tables, a second `mul_mat_id` chain, CPU-side zeroing, summed
down-projections) on the slower card.

**f check, existing data, no rig.** No ids-bearing trace with real (non-grammar-forced) text exists; the only traces with routed-expert ids
are the grammar-forced repetitions (`rep14k` 14K, `smoke-rep` shallow), the same ones behind the section 17-19 numbers. Replaying `rep14k`
(14K, the depth matched to the M=190 worst-cell regime) through `sim_policy.py` gate T=2 hl=16 gives the exact split behind the earlier
"~10%" figure: hits 384.3, installs 9.78, bypass 85.87, **M' = 95.65, f = installs/M' = 0.1023**.

Solving `B_hybrid(f) = 1000/(I + 48*K_e*F2_pure + w_e*M*F2_pure*(1-f) + c_up*M*f)` at I=43.2, K_e=0.0723, w_e=0.1144, F2_pure=1.057,
c_up=0.3003, M=190 (the fixed real-steady-state total, independent of which trace supplies f): **exact break-even f <= 0.0846** (architect's
quoted 0.085, confirmed to 3 sig figs). At the measured f = 0.1023: **denominator 73.33 ms, B_hybrid = 13.64 tok/s < 13.75.**

**f exceeds the break-even on the easier (forced-repetition) trace itself.** Per the pre-registered reading (architect s137.4): a natural trace
routes more diversely, more experts cross the admission threshold, f should rise, not fall. This is decisive against, at zero rig cost.

**Conclusion:** both the bound (section 21) and the hybrid f check now point the same way. The bound survives (14.27 > 13.75) but is not
achievable (installs priced at zero). The one shippable design (hybrid) fails on measured ground: f = 0.1023 > 0.0846 threshold, B = 13.64 <
13.75. Recommendation to the owner: **shelve.** A genuinely natural-prompt trace with ids (a new, cheap, shallow rig leg) could only refine f
upward per the argument above; it is not expected to reverse this and is not proposed.

## 23. Correction to section 22: the f-direction claim was backwards; re-based shelve rationale (architect s139)

**Section 22's directional claim is WITHDRAWN: "a genuinely natural trace would push f up, not down" is wrong, and the conclusion it supported
("decisive against") is not established.** Struck here, not silently edited out, so the error is visible to a later reader.

**Why it was backwards.** Under gate T=2, per expert per window: used once -> 1 bypass, no install. Used n>=2 times -> 1 bypass + 1 install
(second sighting reaches T=2), then hits. So with A = experts used >=2 times and B = experts used exactly once in the window:
`installs = A`, `bypass = A + B`, `M = 2A + B`, **f = A/(2A+B)**. Diverse routing raises B/A (more experts seen once, fewer seen repeatedly
per window), which drives **f down**, not up. Measured f = 0.1023 on the forced (repetitive) trace implies B/A = 7.78. Natural routing
touches ~54% of experts/layer/turn (`moe-expert-coverage-by-subject` memory), so B/A should rise there, not fall.

Lower f raises B_hybrid, because `c_up = 0.3003` exceeds `w_e * F2_pure = 0.1209`: shifting an expert from upload to CPU-serve is cheaper per
expert, at this session's measured K_e/w_e. The break-even f <= 0.0846 needs B/A >= 9.82, a 26% increase over the forced trace's 7.78 -
plausible, not ruled out, for a natural trace. **So the sign favours PASS on a natural trace, the opposite of section 22's claim.**

**Why the natural-trace leg is still not run - for the right reason this time.** `B(f) = 1000/(69.843 + 34.082*f)` over the full range of f:

| f | B (tok/s) | vs static 12.55 |
|---|---|---|
| 0.1023 (measured, forced trace) | 13.64 | +8.7% |
| 0.0846 (break-even) | 13.75 | +9.6% |
| 0.05 | 13.98 | +11.4% |
| 0.0 (= pure bypass) | 14.32 | +14.1% |

**f cannot move the answer outside +8.7% to +14.1%**, a 0.7 tok/s span. Which side of 13.75 it lands on changes only the pre-registered branch
label, not the business answer ("about +10% for a large new subsystem" at every f). A measurement that cannot move a decision does not get rig
time (section 123 standard); this one cannot, regardless of which way f moves on a natural trace.

**Re-based shelve rationale.** The recommendation to shelve is UNCHANGED, but no longer rests on "hybrid fails a threshold" (section 22's
claim was unsound and is withdrawn). It rests on section 19/21's headline, which nothing here touches: **ceiling +13.7% (unachievable bound,
installs priced at zero); shippable hybrid +8.7% to +14.1% across every possible f** - against the cost of split execution (companion
tensors, expert->slot tables, a second `mul_mat_id` chain, CPU-side zeroing, summed down-projections) on the slower card. Shelve because the
return is thin for the subsystem size, not because a threshold is provably failed: a shelve resting on the wrong technical claim gets reopened
the moment someone recomputes f and finds it lower (which section 23 itself shows is the more likely direction), and would wrongly read that
as overturning the shelve when the real reason (thin return) still holds.

**M/M' splice, made explicit.** `M' = 95.65` (section 22) is the forced trace's OWN gated miss count, not half of the real M=190 - the gate is
about +5.6% on top of that same trace's ungated kernel miss count (90.6), not a reduction. Only `f` (the *fraction* split of misses into
install/bypass) is transferred from the forced trace onto the real M=190; the assumption is that the gate leaves the real trace's total miss
count roughly unchanged, which the forced trace's own +5.6% (not -50%) supports as a reasonable approximation, not a proof. Carrying that same
+5.6% forward (M_gate_natural = 190 * 1.056 = 200) at f = 0.1023 gives B = 13.38, slightly worse than the M=190 calculation above - so the
splice as run is mildly OPTIMISTIC, the right direction for a shelve recommendation to be conservative against.

## 24. 5060 Ti config A/B: shipped cache vs production `-ot` (owner ruling s141) - PRE-REGISTRATION

**Scope correction (owner s141): the section 19-23 shelve is a 3060 FINDING, not a verdict on the mechanism generally.** Every input to B was
measured on the 3060: I = 39-43 ms, M = 190 at N=42 (the 3060's VRAM-forced cap), c_up on a defective Gen1 x4 link. `PROJECT_STATUS.md` and
this note are re-scoped: the split-execution BUILD stays shelved (thin return, 3060), but that does not transfer to the 5060 Ti.

**A different question, authorised separately.** On the 3060 the shipped cache loses to static and only an unbuilt bypass design helps. On
the 5060 Ti the question is whether the ALREADY-SHIPPED `--moe-expert-cache-size` beats the current production `-ot` config - a config
comparison, zero engineering. Every term should move favourably: far more VRAM for cache slabs, ~3x faster GPU (I is 43.2 of a ~70 ms budget
on the 3060; lower on a faster card), no Gen1 link defect.

**Spec, fixed before any data.** Same binary (`build-demand/bin`, fork `a2c46384b`, this track's binary throughout - NOT the production
`build-g3` binary, since Arm B needs `--moe-expert-cache-size` and the comparison must hold the binary constant per arm), same model, CUDA0
(5060 Ti), ctx 81920, `--split-mode layer -ngl 99 -fit off --parallel 1 --flash-attn on --jinja -t 6 --load-mode none --spec-type none
--experimental-logs` (MTP off in both arms - draft-acceptance nondeterminism is a measured ~190x variance amplifier, memory
`mtp-decode-slowdown-nondeterminism`). Prompt: `/tmp/opencode/mtp14k/prompt-14k.txt` (the track's standard 14K natural-text prompt, ~13946
tokens), 4 reps per boot, rep 1 (cold) discarded, reps 2-4 = **n=3 warm** at matched depth (cache_n reused, same as every other campaign in
this track - this IS the matched-depth protocol, simpler than a multi-turn growth curve and already validated).

- **Arm A (production config):** `-ot 'blk\.(39|4[0-7])\.ffn_.*_exps.*=CUDA0,ffn_.*_exps.*=CPU' --moe-expert-cache-size 0` (the `0` is
  MANDATORY per the override-precedence trap below - production's own verbatim command from `docs/findings-moe-placement-campaign.md` does not
  set it, but on this binary any N > 0 elsewhere in the session or a nonzero default would silently override `-ot`).
- **Arm B (shipped cache):** `--n-cpu-moe 99 --moe-expert-cache-size <N_max>`.

**Pitfall 1 gate (override-precedence trap, memory `moe-expert-cache-size-is-N`): checked before the campaign is trusted, not after.** With
`--moe-expert-cache-size` N > 0 anywhere, the cache buft override is pushed ahead of user `-ot`/`--n-cpu-moe` and the loader is first-match-
wins, so Arm A's `-ot` would be silently ignored if N leaked into that arm. Gate: grep Arm A's server log for the `LLAMA_LOG_WARN` that fires
when a request is routed through the LRU cache path - **its ABSENCE in Arm A's log is the pass condition**, its presence would mean Arm A
silently became Arm B. Checked immediately after Arm A completes, before Arm B runs or numbers are compared.

**Pitfall 2, N_max: determined empirically, not assumed.** Estimate from this track's own per-expert size and the 3060's own cache42 VRAM
(10461 MiB at N=42, ctx 81920, `per_layer_token_embd=CPU` offloaded - Arm B here does NOT offload that tensor, so the 5060 Ti number will run
higher for the same N): cache VRAM approx N*48*1.887 MiB. At N=84: ~7608 MiB of cache alone; GPU0 has 15509 MiB free before boot (342 MiB
already held by the constant foreign CUDA context). Empirical gate: N=84 must (a) load without OOM and (b) complete all 4 reps of the full
14K-depth prompt (the actual OOM risk is mid-generation KV growth, not load) with headroom logged. If it does, N_max = 84 is used AS-IS; this
campaign does not grid-search upward for the absolute ceiling (marginal tok/s from a few more slabs is not expected to change which side of
the win rule below the result falls, and pushing closer to the card's ceiling raises OOM risk for a question this pre-registration does not
need answered precisely). If N=84 fails, back off (66, then 42) and report which N was used.

**Pitfall 3: Arm A is re-measured in this session, not compared to the historical 22.2541** (`docs/findings-moe-placement-campaign.md`,
unstated depth, different binary `build-g3`) - this track has drawn a wrong conclusion from exactly that mismatch twice already (sections 15,
19). Both arms run back to back in this campaign at the same matched depth.

**Pre-registered win rule, threshold filled in only after both arms' spreads are known (stated as a rule now, not a number):** Arm B wins only
if `mean(B) - mean(A) > range(A) + range(B)` (range = max-min of the 3 warm reps), i.e. the gap exceeds the two arms' combined n=3 noise. A
point estimate inside that combined spread is not a win. If Arm B wins: config change, report as such, the lever reopens on the 5060 Ti only,
no split-execution build is implied. If it does not win: the shelve goes global, the 3060 analysis stands as the reason, and PROJECT_STATUS
closes the track.

## 24 addendum: two conditionals pre-registered before the 14K result is seen (architect s142)

**1. Depth conditional.** The 14K comparison point is valid but production runs at ctx 81920, and the recorded 8-turn session reaches ~53K
where the 5060 Ti already shows a 22% depth tax (36.47 tok/s at 6.6K -> 28.26 at 53K, N=84). `-ot` holds a fixed 9 layers on CUDA0 regardless
of depth; the cache's hit rate interacts with the routing drift measured throughout this track. **The A/B ratio is itself depth-dependent** and
could shrink or invert between 14K and 53K. Fix: after the 14K legs land, run each arm ONCE through `multiturn-growth-test.sh` (8 turns,
~6.6K -> 53K, already exercised on both cards at ctx 81920) to get the ratio across the whole curve from one run per arm.

**Pre-registered:** if 14K and the deep (53K) point agree in sign, decide on 14K. **If they disagree, the deep point governs** (production runs
deep). A 14K win does not authorise the config change if it loses at 53K.

**2. N_MAX conditional.** N_MAX = 84 is asserted (known-good), not determined maximal - Arm B may be running below its own ceiling. Only
matters if Arm B loses. Calibration from the `-c 8192` sweep: N=84 -> 46.71, N=112 -> 49.18, N=126 -> 50.13, i.e. roughly **7%** of headroom
between N=84 and the ceiling.

**Pre-registered:** Arm B loses by more than ~7% -> N cannot rescue it, declare the global shelve. Arm B loses by less than ~7% -> probe
N=96 and N=104 at ctx 81920 (short probe, not a new campaign) before declaring the shelve, since the ceiling could cover the gap.

Nothing else about section 24 changes. Report the 14K legs first, per section 141's own ordering.

## 24 correction: Arm A OOM'd on the first campaign attempt, methodology bug, fixed before any data

First attempt crashed Arm A (`prodA`) on the very first decode step: `CUDA error: out of memory` allocating the cublas workspace
(`ggml-cuda.cu:117`, `cublas_handle`/`common.cuh:1583`), ~4 s after the first request began, not at load. Root cause: the runner script
(`arm_5060_ab.py`, adapted from the 3060 scripts) inherited `-fit off` in `COMMON`, which is NOT in the architect's literal production
command. `--fit` defaults to "on" and reserves headroom for lazily-allocated buffers (the cublas workspace is allocated at first matmul, not
at load, so `nvidia-smi` at boot showed no problem); `-fit off` removes that reservation. This was a copy-paste methodology error on my part,
caught immediately (no data was produced or reported), not a finding about the production config. Fixed: `-fit off` removed, `arm_5060_ab.py`
now matches the architect's spec exactly. Re-running from a clean rig-lock state.

## 25. 5060 Ti config A/B: 14K result - Arm B wins decisively (2026-09-22)

**Gate checked first, per section 24's own ordering.** Arm A's server log (`server-prodA.log`) has no `LLAMA_LOG_WARN` line for the
cache-routing path anywhere in it - grep for the LRU-cache warning returns zero hits. Arm B's log has exactly the expected line at boot:
`--moe-expert-cache-size is set; expert tensors route through the GPU LRU cache regardless of --cpu-moe / --n-cpu-moe.` **Pitfall 1 gate
PASSED**: Arm A genuinely ran on `-ot`, Arm B genuinely ran on the cache, no silent override.

**N_max=84 loaded and completed cleanly** (Pitfall 2): `vram_after_load_mib=13926`, `vram_peak_mib_this_request=14404` across all 4 reps,
against a 16311 MiB card - `~1907 MiB` headroom, no OOM, no backoff needed. N_max conditional (section 24 addendum #2) does not need to fire.

**14K result, n=3 warm reps (2-4), `decode_tps_server`, matched depth `cache_n=13942` both arms:**

| Arm | rep2 | rep3 | rep4 | mean | range | range % |
|-----|------|------|------|------|-------|---------|
| A (prodA, `-ot`) | 19.816 | 19.954 | 19.655 | 19.808 | 0.299 | 1.51% |
| B (cacheB, N=84) | 32.033 | 32.462 | 31.478 | 31.991 | 0.984 | 3.07% |

gap = mean(B) - mean(A) = **12.183 tok/s**; combined n=3 range = 0.299 + 0.984 = 1.283. Gap exceeds combined range by **9.5x**.

**Pre-registered win rule (section 24) applied: Arm B wins**, by a very wide margin (+61.5% relative to Arm A). Not a borderline call.

Secondary signal, unplanned but consistent: `gpu0_util_pct` during decode is ~86-92% for Arm B vs ~31-34% for Arm A, and `cpu_busy_pct` is
~7-9% for Arm B vs ~20-21% for Arm A - Arm A's `-ot` config is spending a large share of decode time off-GPU (9 fixed CPU-resident layers),
exactly the mechanism the cache is expected to shrink by keeping hot experts resident in the GPU LRU instead of a fixed layer split.

**Depth conditional (section 24 addendum #1) resolved - sign agrees, 14K result stands.** Ran `multiturn-growth-test.sh` once per arm
(1 session, 8 turns, `NEW_TOKENS=5821 N_PREDICT=750`), same servers/config as the 14K legs, back to back (Arm A rebooted to Arm B between
curves, same as the 14K protocol). Growth landed at prompt_tok ~39.2K-39.7K by turn 8 (short of the ~53K target - the script's word-count
estimate ran a little light, and Arm A's turn 3 hit an early stop at comp_tok=368/750, both noted, neither changes the sign at any point) -
still >2.8x deeper than the 14K comparison point, sufficient to exercise the depth conditional as intended.

Metric here is `comp_tok/wall` (conflates prefill+decode, unlike section 25's `decode_tps_server`) - absolute numbers are not comparable
across the two tables, but the metric is identical between arms within this table, so the A-vs-B comparison is valid:

| turn | prompt_tok (~) | A tok/s | B tok/s | B/A | delta % |
|------|-----------------|---------|---------|-----|---------|
| 1 | 4,885 | 13.28 | 17.91 | 1.35 | +34.9% |
| 2 | 9,710 | 13.51 | 16.45 | 1.22 | +21.8% |
| 3 | 14,530 | 9.85* | 17.16 | 1.74* | +74.2%* |
| 4 | 19,496 | 12.72 | 17.54 | 1.38 | +37.9% |
| 5 | 24,784 | 12.33 | 16.66 | 1.35 | +35.1% |
| 6 | 29,601 | 12.45 | 16.16 | 1.30 | +29.8% |
| 7 | 34,425 | 12.32 | 16.14 | 1.31 | +31.0% |
| 8 | 39,245 | 12.11 | 16.78 | 1.39 | +38.6% |

\* turn 3 Arm A generated only 368/750 tokens before an early stop, inflating the apparent gap at that one point - excluded from the
"consistent" claim below but does not change its sign either.

**Arm B leads Arm A at every single turn**, from turn 1 (4.9K, +34.9%) through turn 8 (~39.2K, +38.6%), with no sign flip and no
narrowing trend across the curve (delta% bounces in a 22-39% band, no monotonic decay toward zero). **14K and the deep point agree in
sign** (B wins both). Per the pre-registered rule (section 24 addendum #1): decide on the 14K result.

**Final verdict for section 24's win rule: Arm B (shipped `--moe-expert-cache-size`) wins on the 5060 Ti**, both at the 14K matched-depth
comparison (+61.5%, gate-verified) and across the full depth curve to ~39K (+22-39% on the conflated prefill+decode metric, sign-consistent
throughout). N_MAX conditional (section 24 addendum #2) does not fire - Arm B won outright, was never in the "loses by <7%" branch.
**Config change recommended for the 5060 Ti CUDA0 arm: replace the production `-ot` split with `--moe-expert-cache-size 84` (`--n-cpu-moe 99`
+ cache flag, N=84 confirmed safe with ~1.9GB headroom). This does not reopen or justify the split-execution BUILD (section 23's shelve for
that mechanism stands, 3060-scoped) - it is a config-only win on hardware that was never in scope for that shelve.**

## 26. Architect corrections (section 143) applied: scope, headline, and the prefill check

Architect accepted the sign but withheld closeable status pending three corrections and one zero-cost check. All addressed below.

**Arm A dose confirmation (costs nothing, pasted per request).** A fresh `-lv 5` load-only boot of Arm A's exact config (same binary,
flags, prompt unused) confirms the CPU/GPU split actually applied: `blk.0..38.ffn_*_exps.weight` tensors show `buffer type overridden to
CUDA_Host` (CPU), `blk.39..47.ffn_*_exps.weight` show `buffer type overridden to CUDA0` - exactly the 9-layer (39-47) dose the `-ot` regex
specifies, nothing silently different. Final summary line: `load_tensors: CUDA0 model buffer size = 11763.04 MiB`. Dose was correct.

**Headline correction (architect item 1): the number to plan around is the multi-turn band, not +61.5%.** +61.5% is n=3 on one repeated
14K prompt and is not reproduced at any single point on the depth curve (band there is +22% to +39%, section 25's table). The 14K point
correctly decided the *sign* per the pre-registered rule; it should not be quoted as the expected magnitude. The 61.5% vs ~30% gap between
the two measurement styles is unexplained (possibly repeated-identical-prompt vs varying-content sensitivity) and does not affect the sign.
**Planning number: +22% to +39%, not +61.5%.**

**Scope correction (architect item 2): this is not a Hydra-deployed config.** `scripts/set-profile.sh` deploys
Qwopus3.6-MoE-A3B-v1-APEX-I-Mini under COMBINED-OT (two-GPU expert split, different model entirely) - the `FOREIGN_PID` constant-VRAM
context these campaigns ran alongside on CUDA0 *is* that live deployment. "Arm A / production `-ot`" in sections 24-25 means only this
track's own best-known CUDA0-solo serve config for Qwen3.8-Flash-Next (`docs/findings-moe-placement-campaign.md` §1), not anything Hydra
currently runs. **Scoped recommendation: replace the track's CUDA0-solo Qwen3.8-Flash-Next config with `--moe-expert-cache-size 84`.
Taking this into a Hydra profile/launcher is a separate change, through CI/CD, gated by the owner at merge - not implied by this result.**
**Hard constraint, stated for the record: `--moe-expert-cache-size` must never be added to a COMBINED-OT launch.** The cache's buffer-type
override is pushed ahead of user `-ot` and the loader is first-match-wins (memory `moe-expert-cache-size-is-N`); on a COMBINED-OT launch
this would silently collapse the two-GPU expert split with no error, and `907a73da9` (production's own safety check) rejects only tensor
split, not this. Untested combination, exactly the silent-failure shape this track has already documented once.

**Prefill check (architect item 3, zero rig cost - both arms' growth-test server logs already had the per-turn `prompt eval time` /
`eval time` split).** Extracted both, matched turn-by-turn (`server-prodA.log`, `server-cacheB.log`, growth run):

| turn | A prefill tok/s | B prefill tok/s | A wall_s (pf+dec) | B wall_s (pf+dec) | B faster? |
|------|-----------------|-----------------|--------------------|--------------------|-----------|
| 1 | 260.3 | 248.7 | 56.45 | 41.85 | yes, -25.9% |
| 2 | 272.7 | 260.8 | 55.51 | 36.94 | yes, -33.4% |
| 3 | 266.5 | 252.9 | 37.32 | 43.68 | **no, +17.0%** |
| 4 | 258.1 | 244.1 | 58.95 | 42.73 | yes, -27.5% |
| 5 | 262.2 | 244.1 | 60.76 | 44.97 | yes, -26.0% |
| 6 | 250.1 | 237.7 | 60.17 | 46.34 | yes, -23.0% |
| 7 | 246.9 | 236.1 | 60.82 | 46.40 | yes, -23.7% |
| 8 | 243.4 | 231.6 | 61.84 | 44.61 | yes, -27.9% |

**Finding, not the feared one: prefill is not a catastrophe for B, it's a small, consistent tax.** Arm B's prefill throughput runs
~4-6% below Arm A's at every single turn (231.6-260.8 vs 243.4-272.7 tok/s) - real and systematic (plausibly LRU-cache bookkeeping /
host-bounce overhead touching the prefill path too, not just decode), but an order of magnitude smaller than decode's ~1.3-1.7x gap, so it
does not come close to erasing the win. The historical "272 vs 142 tok/s" prefill gap the architect cited (different binary, different run)
does **not** reproduce here - on this binary, at matched depth, prefill is close between arms with A slightly ahead throughout.

**Correction (architect s144): my first pass normalised wall time by total tokens processed (`(prefill_tok+eval_tok)/wall_s`) to remove the
turn-3 confound. That normalisation is invalid and is retracted - it lumps prefill tokens (~250 tok/s) with decode tokens (~20-28 tok/s,
~10x more expensive), so a total-token-count division can't absorb a 382-token decode-length difference; it produced "A leads turn 3 by
2.7%" as a pure artifact of the mixing, not a measurement.**

**Correct fix: rebuild each turn's wall time from the phase rates, at a common (P, D) for both arms, per the architect's formula
`wall(P, D) = P/p + D/d`.** A's decode rate over its truncated 368 tokens is still a valid *rate* - the early stop shortens the amount of
work, not the rate - so `p_A, p_B, d_A, d_B` are taken directly from each turn's measured `prompt eval time` / `eval time` lines (section
26's first table), then applied to a common reference `P_ref` (mean of the two arms' actual prompt token counts that turn) and `D_ref =
750` (the nominal target every turn was supposed to produce, removing both arms' early-stop artifacts, not just Arm A's):

| turn | p_A tok/s | p_B tok/s | d_A tok/s | d_B tok/s | P_ref | wall_A (rebuilt) | wall_B (rebuilt) | B faster by |
|------|-----------|-----------|-----------|-----------|-------|-------------------|-------------------|-------------|
| 1 | 260.3 | 248.7 | 19.90 | 33.77 | 4,885 | 56.45s | 41.85s | +25.9% |
| 2 | 272.7 | 260.8 | 19.84 | 32.99 | 4,829 | 55.51s | 41.25s | +25.7% |
| 3 | 266.5 | 252.9 | 19.15 | 32.22 | 4,992 | 57.90s | 43.02s | **+25.7%** |
| 4 | 258.1 | 244.1 | 18.90 | 33.78 | 4,990 | 59.02s | 42.65s | +27.7% |
| 5 | 262.2 | 244.1 | 18.48 | 29.78 | 5,061 | 59.88s | 45.92s | +23.3% |
| 6 | 250.1 | 237.7 | 18.34 | 30.01 | 4,948 | 60.68s | 45.81s | +24.5% |
| 7 | 246.9 | 236.1 | 18.17 | 28.90 | 4,828 | 60.82s | 46.40s | +23.7% |
| 8 | 243.4 | 231.6 | 17.85 | 33.13 | 4,956 | 62.38s | 44.04s | +29.4% |

**Turn 3 was the artifact, confirmed: rebuilt at the same (P, D), B wins turn 3 by +25.7%, right in line with every other turn (23-29%
band, tight).** With the confound correctly removed, **B wins all 8 of 8 turns** - the architect's prediction was right, and the "every
turn" rule does not fail; it was being asked the wrong question at turn 3 by an invalid measurement, not a real exception.

**The finding that actually matters: per-turn wall time is workload-shape-dependent, not just arm-dependent.** A prefills faster (~5%), B
decodes faster (~1.6-1.9x) - which arm wins a given turn depends on the ratio of output tokens D to new-prompt tokens P for that turn. Break-even
`D* = P_ref * (1/p_B - 1/p_A) / (1/d_A - 1/d_B)`, computed per turn from the measured rates above:

| turn | D* (tokens) | D*/P_ref |
|------|-------------|----------|
| 1 | 42.2 | 0.86% |
| 2 | 40.0 | 0.83% |
| 3 | 47.4 | 0.95% |
| 4 | 47.7 | 0.96% |
| 5 | 69.8 | 1.38% |
| 6 | 48.5 | 0.98% |
| 7 | 43.8 | 0.91% |
| 8 | 40.3 | 0.81% |

**D*/P band: 0.81% to 1.38%** (tighter than the architect's rough estimate of 1.3-2.1%, computed here from actual per-turn rates rather
than one aggregate figure). This harness ran D/P ≈ 15% (750 output / ~4.9K new prompt) - far above break-even, which is why decode
dominated and B won every turn at this ratio. **The honest framing is an asymmetry, not a single number:**
- **Prompt-heavy turns (D/P below ~0.8-1.4%): B loses, worst case ~4-6% (pure-prefill limit).**
- **Output-heavy turns (D/P above ~1.4%): B wins, up to +22-39% at this harness's D/P≈15%.**
- **Crossover band: D/P ≈ 0.81%-1.38%.**

Whether this matters for real usage depends entirely on where actual serving turns fall on that axis - checked next.

## 27. Production D/P, output-equivalence, and the 75K survival probe (architect s144 items 4-5, 2026-09-22)

**Production D/P: not available this session, not a rig-cost question.** Checked for a running Hydra pod / monitoring stack before assuming
the data exists: `podman ps` (and `podman ps -a`) shows no Hydra containers and no Prometheus/Loki/Grafana containers, running or stopped -
the monitoring stack described in `docs/monitoring-observability.md` (`bash scripts/start-env.sh`) is not currently up, so there is no live
or historical `prompt_n`/`predicted_n` series to query for this check. Per the architect's framing (s144 item 4): **this call goes to the
owner** - the D*/P crossover band (0.81%-1.38%, section 26) is computed and ready, but where real Hydra/Qwopus serving turns actually fall
on that axis is not something this session can measure without starting the pod, which is out of scope for a config-comparison check.

**Output-equivalence (architect s144 item 5a): teacher-forced KL-divergence, Arm A vs Arm B, PASS.** Built `llama-perplexity` from the
existing `build-demand` tree (target not previously built in this build dir, `cmake --build build-demand --target llama-perplexity`, ~40s,
links against the same compiled objects the server uses - same binary lineage). Base run: Arm A config
(`-ot 'blk\.(39|4[0-7])\.ffn_.*_exps.*=CUDA0,ffn_.*_exps.*=CPU' --moe-expert-cache-size 0`), `--save-all-logits`, `-c 4096` (3 non-overlapping
chunks over the 14K prompt file). Compare run: Arm B config (`--n-cpu-moe 99 --moe-expert-cache-size 84`), `--kl-divergence
--kl-divergence-base` against Arm A's saved logits, same prompt, same chunking.

| Metric | Value |
|---|---|
| Mean KLD | 0.000131 ± 0.000015 |
| Median KLD | 0.000013 |
| Max KLD (single token, 3 chunks) | 0.079593 |
| 99.9th pct KLD | 0.009994 |
| Mean PPL(Q)/PPL(base) | 0.999993 ± 0.000014 |
| Same top-1 token | **100.000 ± 0.000% (all 3 chunks)** |
| RMS Δp | 0.109 ± 0.010% |

Both arms pick the same top-1 token at every position across all 3 chunks; mean KLD is five orders of magnitude below anything that would
indicate a routing or numerics defect. **Confirms the architect's expectation: ordinary quantisation/kernel-path drift from moving 39 layers
of experts CPU->GPU, not a defect.** Arm A's turn-3 early stop (section 25/26) was therefore very likely ordinary sampling-boundary
sensitivity at `temperature=0` on a near-tied logit, not evidence of a correctness problem - consistent with this KL result.

**75K-token survival probe (architect s144 item 5b): PASS, clean headroom.** Built a 75,400-75,551-token prompt (6x-repeated 14K corpus,
trimmed to fit under ctx 81920 with margin) via `/tokenize` verification. Sent one `/v1/chat/completions` request (max_tokens=100,
temperature=0) to a fresh Arm B boot (`--moe-expert-cache-size 84`, ctx 81920, same binary/flags as sections 24-26), polling
`nvidia-smi` every 2s throughout.

| Metric | Value |
|---|---|
| Prompt tokens processed | 75,452 |
| Total resident (`n_tokens` at release) | 75,551 |
| Truncated | 0 (no) |
| Wall time | 259.1s |
| Peak VRAM (CUDA0) | 14,436 MiB / 16,311 MiB (~1,875 MiB / ~11.5% headroom) |
| Server errors | none (`grep -i error/oom/fail` clean; the one `common_fit_params: failed to fit params` line is the same benign warning present in every successful boot this campaign, not a new failure) |

No OOM, no truncation, comparable headroom to the 14K campaign's ~1.9GB margin (section 25) even at 5.4x the depth. The 3060's "loaded
fine, OOM'd on first long request" precedent that motivated this check does **not** reproduce on the 5060 Ti at N=84.

## 28. Where this leaves the recommendation (2026-09-22, superseded by section 29 - see correction below)

~~Both gating checks (item 5) pass cleanly - nothing here blocks a deploy on correctness or VRAM grounds.~~ **Correction (section 29): the
section 27 output-equivalence check only exercised the prefill/legacy path. It does not, by itself, validate the specific decode kernel
that produces the 22-39% win.** VRAM (75K survival) remains clear on its own terms. Read section 29 before treating the correctness gate as
closed. The workload-shape framing below stands, with the threshold language corrected per architect s145 item 2:

- **This harness's D/P (~15%, decode-heavy agent-style turns): B wins, +22% to +39% per turn** (section 25-26).
- **Prompt-heavy turns (large tool-result context, short completion), D/P below ~1%: B loses, worst case ~4-6%** (pure-prefill limit,
  section 26).
- **Concrete thresholds (architect s145): a 5K-token tool result favours B if the reply exceeds ~40-70 tokens. A 20K-token file read needs
  a reply longer than ~160-280 tokens. With reasoning/thinking output enabled, almost every turn clears this. Downside is capped at 4-6% of
  prefill time; upside is 22-39% of decode time.** Not "agent-style vs prompt-stuffing" - that framing overstated the safety margin now that
  D* is measured at ~1% of prompt tokens, not a large fraction.
- **The owner's call:** where real Hydra traffic falls against this D/P threshold. This track has no way to measure the real distribution
  without the monitoring stack running (section 27); the owner is better positioned to judge Hydra's actual traffic shape than a rig-side
  inference.
- **Scope, restated (section 26): this is the track's own CUDA0-solo Qwen3.8-Flash-Next config, not any current Hydra deploy profile.
  Never combine `--moe-expert-cache-size` with a COMBINED-OT launch (silent expert-split collapse, first-match-wins, untested).**
- **Owner escalation is held per architect s145, pending architect review of section 29.**

## 29. Decode-path KL-divergence: attempted, and why it could not reach the grouped kernel (architect s145 item 1, 2026-09-22)

**Goal (architect s145):** the section 27 KL check used `-c 4096` (multi-token batches), which the certificate logic always classifies as
`PREFILL_LEGACY` (see below) - it says nothing about the single-token grouped-decode kernel that produces the measured win. Fix requested:
re-run with `-ub 1` and confirm from the cache's own grouped/legacy transition logs that the grouped path actually engaged.

**What was done:** rebuilt `llama-perplexity`. Ran Arm B (`--moe-expert-cache-size 84`) with `-ub 1 -b 1 -lv 5` (verbosity raised - by
default `llama-perplexity`, unlike `llama-server`, filters the `GGML_LOG_INFO`/`GGML_LOG_DEBUG` lines the moe-cache diagnostics use) against
a 700-word (~1026-token) prompt, base run at Arm A for comparison. A temporary one-line `fprintf` canary (uncommitted, reverted after use)
confirmed the stats-dump function was reached even before `-lv 5` was added, isolating the earlier silence to log-level filtering, not a
missed call.

**Finding: `-ub 1` does not reach `GGML_CUDA_MOE_GRAPH_OUTCOME_DECODE_GROUPED` either.** Read the certificate logic directly
(`ggml/src/ggml-cuda/moe-cache.cu:10310-10357`, HEAD unchanged from section 1's `5226b502d`):

- Multi-token batches (section 27's `-c 4096` run): every call has `call_prefill=true` -> outcome is always `PREFILL_LEGACY`. Confirms the
  architect's read - that check validated the prefill path only.
- `-ub 1`: every call has `call_decode && !call_prefill`, which is eligible for `DECODE_GROUPED` - **but only if all 48 layer-groups
  independently earn `GROUP_REASON_ELIGIBLE`.** `decode_certificate` is AND-reduced across every group in the plan; a single non-eligible
  group anywhere (reason `MATERIALIZATION`, `EXECUTION`, `CONSUMER_EQUIVALENCE`, or `ROUTE`) forces the whole plan to `DECODE_LEGACY`. One
  source comment on the `ROUTE` case is explicit: "layer-split decode graphs often keep argsort on one device; fail closed to cached mmid" -
  both arms run layer-split, so this fail-closed path is an expected property of the config, not an artifact of this test.

**Measured:** the classifier only re-fires on plan recompile, not per token (consistent with `graphs reused = 2038` out of 1022 eval steps
in the run's own perf summary). It recompiled 11 times over the run. **All 11 times, 47 or 48 of 48 groups landed on a legacy reason -
never once all-`ELIGIBLE`.**

**This contradicts the section 27 75K survival probe's own server log**, which showed grouped decode fully engaged under the *same*
layer-split config, same model, real server traffic: `plan_calls=99 plan_compiles=2 plan_reuses=97 fallback=0`. So `DECODE_GROUPED`
demonstrably works reliably in actual serving. The divergence is specific to `llama-perplexity`'s call pattern - chunked teacher-forced
scoring has no genuine prefill-then-autoregressive-decode transition with slot continuity to seed the plan the way a real server request's
prompt does, so the plan appears to never stabilize to all-`ELIGIBLE` within this harness, in any ubatch configuration tried.

**Conclusion: `llama-perplexity` cannot exercise `DECODE_GROUPED` in this fork's design, in any flag combination found.** This is a
structural mismatch between how the tool issues calls and what the certificate requires, not a missing flag. Confirming grouped-path
numerics directly would need a teacher-forced harness driving real prefill-then-decode through server/slot infrastructure - real
engineering, not the "~2 minutes of rig" this was scoped as.

**What the evidence actually covers, as of this section:**

| Path | Check | Result |
|---|---|---|
| `PREFILL_LEGACY` (multi-token) | KL, section 27 | Mean KLD 0.000131±0.000015, 100% top-1 |
| `DECODE_LEGACY` (single-token, this section) | KL | Mean KLD 0.001456±0.000092, 100% top-1 - clean, ~11x higher than prefill but still five orders of magnitude below a defect signature |
| `DECODE_GROUPED` (single-token, all-groups-eligible) | not exercised | No direct numerics evidence. Structural argument only: grouped and legacy dispatch read from the same per-expert cache slabs and weight banks (shared candidate/record/bank-role machinery upstream of the certificate split, `moe-cache.cu:10280-10309`) - the `ELIGIBLE`-vs-legacy branch selects kernel-launch/dispatch strategy (batched graph vs individual cached `mul_mat_id`), not a different weight source. Not traced far enough downstream to confirm the actual GEMM call is byte-identical between the two dispatch strategies. |

**Reported to the architect (agent `2731baf3-2a8b-4b79-b237-111d0f1d4591`) for a ruling on: (a) accept prefill + decode-legacy as sufficient
bounding evidence with `DECODE_GROUPED` numerics as an open item, vs (b) hold for a real harness.** Diagnostic source edits (the `fprintf`
canary, the `perplexity.cpp` stats-dump call, the `#include "ggml-cuda.h"`) were reverted; the `src/llama-cpp` working tree is clean, nothing
uncommitted. **Owner escalation remains held per architect s145 pending this ruling.**

## 30. c1: the fork's own grouped-vs-reference unit suite FAILS on this model's live quant format (architect s146, 2026-09-22)

**Architect's counter to the (a)/(b) choice above (s146): neither. A structural "same slabs, different dispatch" argument cannot rule out a
slot-table/expert-lookup bug, which is exactly the class of defect this gate exists to catch, and the fork already ships a unit suite
(`tests/test-moe-cache.cpp`) that could show this directly with zero rig cost.** Requested: build it, run the default (non-`--grouped-multigpu-only`)
mode, identify any case that compares grouped-plan output against a reference `mul_mat_id` implementation.

**Result: FAIL, deterministic (reproduced twice, byte-identical failure), and on this model's own quant format.**

```
FAIL tests/test-moe-cache.cpp:8348  memcmp(current_first.data(), expected.data(), expected.size() * sizeof(float)) == 0
```

Inside `test_active_grouped_dispatch_types_case`, reached from `test_active_grouped_dispatch_generic()` (`tests/test-moe-cache.cpp:9657-9671`)
in the **default** test run - no special flag needed, this is not an opt-in diagnostic. `expected` is computed by a `reference_backend`
(direct/uncached dispatch); `current_first`/`current_second` come from two independently-constructed grouped/cached-context backends. Zero
tolerance - exact float32 `memcmp`. This is precisely the "grouped-plan output vs reference" comparison requested.

**Localized** (temporary case-print markers added to the test file, reverted after use - `git status --short` on `src/llama-cpp` confirmed
clean before and after): first failure is `type=IQ3_S (ggml enum 21), layout=SEPARATE (enum 1), n_slots=12` - the very first IQ3_S case in
the loop. All 12 preceding combinations (Q3_K and IQ3_XXS, both layouts, both slot counts) pass cleanly. `test_active_grouped_dispatch_case`
uses the same type for gate/up/down uniformly, so this is IQ3_S in all three weight roles at once, not an isolated role.

**Why this is not a generic/irrelevant unit-test fail: it is this model's live format.** The deployed GGUF's own model-load trace
(`armB_run_lv5.log`) shows:

| Tensor | Type |
|---|---|
| `blk.0.ffn_gate_exps.weight` | `iq3_xxs` |
| `blk.0.ffn_up_exps.weight` | `iq3_s` |
| `blk.0.ffn_down_exps.weight` | `iq4_nl` |

No `ffn_gate_up_exps` tensor loads (the loader probes for it, finds nothing) - gate and up are genuinely separate tensors, confirming
**SEPARATE layout**, matching the failing case exactly. **`IQ3_S` is the live quant type for `ffn_up_exps` in Qwen3.8-Flash-Next-APEX-I-Mini**,
in the layout the model actually uses. `n_slots=12` in the test is not production's `N=84`; slot-count-dependence is unconfirmed, but the
mismatch appears on the first dispatch call (pass 0 of 4), not after cache pressure/eviction, so a slot-count gate looks unlikely though not
ruled out.

**Open question, not yet answered:** whether `ffn_up_exps` (the IQ3_S tensor) ever actually gets routed through `DECODE_GROUPED` in real
serving. The section 27 75K survival probe's server log showed grouped decode engaged with `fallback=0`, but that doesn't establish which
specific layer-groups were `GROUP_REASON_ELIGIBLE` - gate/down groups could be eligible while the up-tensor group silently falls back
(consistent with the `ROUTE`/layer-split fail-closed comment found in section 29), which would mean the defect exists in the dispatch code
but never fires in practice. This needs checking before treating the defect as proven-live rather than proven-in-suite.

**Status:** reported to the architect. c2 (server logprob comparison) not yet run - holding per architect's direction pending their read of
this result, since it may be more useful to first determine whether c1's defect is reachable in real serving at all. **Owner escalation
remains held.** This is now a candidate fork defect independent of the Arm A/B config question - not yet filed as a GitHub issue pending
architect direction, to avoid duplicating or cutting across their review.

## 31. c1 magnitude/pattern, upstream attribution, and n_slots=84: confirmed P1, not numerics (architect s147, 2026-09-22)

**Architect's read of section 30 (s147): a zero-tolerance `memcmp` fires for a real routing/slot bug and for ordinary kernel-selection
drift alike, and proposed a specific innocent mechanism (IQ3_S has its own `get_mmvq_mmid_max_batch()` threshold, different from Q3_K/IQ3_XXS,
so the grouped plan and the reference could cross it differently and land on different kernels) - a hypothesis, not a finding, with a
concrete measurement to confirm or kill it. Requested: magnitude/pattern of the mismatch, which kernel each side ran, a free upstream check,
and a rerun at `n_slots=84`.

**Step 1 - magnitude and pattern.** Temporary env-gated report at the `memcmp` site (`tests/test-moe-cache.cpp`, reverted after,
`git status --short` clean throughout). IQ3_S/SEPARATE/n_slots=12, pass 0:

| Metric | Value |
|---|---|
| Elements mismatched | **512 / 512** |
| Max abs diff | 262.765289 |
| Max rel diff | 7.7402451 (774%) |
| Example | idx=1: expected -135.669, actual +4.192 - **sign flip** |
| `current_first == current_second` | byte-identical (deterministic across two independent grouped contexts) |

Whole-output, large-magnitude, deterministic - the "materially different" signature the architect said means a real defect, not the
"scattered low-bit" signature of accumulation-order drift. Contrast with the actual decode-legacy KL check (section 29): 0.0015 mean KLD,
100% top-1 agreement - that is what benign numeric drift looks like on this same rig. This mismatch looks nothing like that.

**Step 2 - kernel selection, both sides: identical.** `reference.selection=1` vs `first(gate).selection=1` - same kernel on both paths.
This refutes the mmvq/mmid-threshold hypothesis by direct measurement, and by source: `get_mmvq_mmid_max_batch()`
(`ggml-cuda/mmvq.cu:285`) - "Volta, Ada Lovelace, and Blackwell always use MMVQ for MUL_MAT_ID" unconditionally, type-independent. CUDA0 in
this track is the 5060 Ti (Blackwell, sm_120); the per-type threshold table the hypothesis relied on is never consulted on this hardware.
The mechanism would have been a real risk on the 3060 (Ampere, hits the turing-plus table) - just not on the card this reproduces on.

**Step 3 - upstream attribution.**
- Same assertion, same IQ3_S case, byte-identical, still exists at `gs/moe-cache` tip - relocated to `tests/test-moe-cache-dispatch.cpp`
  after an upstream test-suite split (`test-moe-cache.cpp` shrank from 13,420 to 192 lines). Not fixed or relaxed by removing the test.
- `ggml-cuda/mmvq.cu`: zero diff between merge-base (`77b733d5c`) and `gs/moe-cache` tip.
- Our own commits ahead of merge-base touching `moe-cache.cu`: exactly 3 (`a2c46384b`, `ab8424b1b`, `042f0a5f5` - ledger/telemetry, frequency
  half-life tuning). Grepped their diffs directly for `decode_certificate`/`mixed_certificate`/`GROUP_REASON_*`/`get_mmvq_mmid_max_batch`:
  zero hits. **This fork's own changes did not introduce the defect.**
- `moe-cache.cu` has a large upstream rewrite since merge-base (one hunk alone +1274/-136 lines) that could plausibly contain a fix, but
  confirming that needs an actual `gs` tip build and test run - a full 176-commit port evaluation, not a free check. Left open.

**Step 4 - production slot count.** Isolated `IQ3_S/SEPARATE/n_slots=84` as the first test run (bypassing the 12/48 cases that abort the
process first on failure). **Fails identically**: 512/512 mismatched, same `max_abs_diff=262.765289`, same index, same expected/actual
values, byte-for-byte the same as `n_slots=12`. Not slot-count-gated. Reproduces at the exact configuration Arm B ships.

**CI #120, checked and ruled out as related.** The failing `cuda`/"Build with CMake" job (`gh api .../jobs/103587504496/logs`) is a
`-Werror` build failure: `moe-cache.cu:1724` enum/non-enum conditional, `:7229` unused parameter, `:8906` missing field initializer - the
same three warnings this session's own build prints non-fatally under a less strict flag set. Real, ~9-day-old, untriaged, and unrelated -
three one-line fixes, not a numerics defect. Separate maintenance item.

**Free sanity check (read B's live multi-turn text): not possible from existing artifacts.** The section 25-26 campaign log
(`results-5060-ab.jsonl`) only recorded an output SHA hash per turn, not the text. Would need a fresh boot to check directly; held off as
not actually free.

**Verdict: meets the architect's own pre-registered P1 bar.** Whole-output, large-magnitude, deterministic, reproduces at production
`n_slots=84`, identical kernel selection on both sides (rules out the benign explanation), not introduced by this fork's own commits.
**The config recommendation is treated as dead pending a fix, independent of the speed result - this is now a correctness blocker, not a
D/P workload-shape question.** c2 (server logprob comparison) not run - reported to the architect for direction on whether it's still
useful (e.g. to check whether IQ3_S groups ever reach `GROUP_REASON_ELIGIBLE` in real serving, which would make this latent-but-unreachable
rather than live). **Owner escalation remains held**, now blocked on correctness grounds regardless of D/P. All diagnostic edits reverted;
`src/llama-cpp` tree clean.

## 32. c2: live server liveness test - VERDICT LIVE (architect s148, 2026-09-22)

**Reachability, established two ways before running any live request.**

1. **Existing production evidence, already in hand.** `server-cacheB.log` from the section 24-25 campaign itself (the exact 14K-depth
   run behind the "Arm B wins" measurement) already shows `moe-grouped-decode: registered=48 covered=48 plan_calls=199 plan_compiles=2
   plan_reuses=197 calls=9552 ... fallback=0 rollback=0`. All 48 layer groups reached the grouped/cached path with zero fallback to legacy,
   across the entire campaign. This alone answers "was the grouped path reached in the literal run under review" - yes, unconditionally.
2. **Direct dispatch-variant instrumentation, both offline and live.** Traced the real dispatch chain: `ggml_cuda_mul_mat_id`
   (`ggml-cuda.cu:3728`, the `GGML_CUDA_MOE_GRAPH_GROUP_GROUPED_ACTIVE` branch) and the GLU-fusion path in `ggml_cuda_try_fuse`
   (`ggml-cuda.cu:~5916`) both bottom out in the same low-level kernel launcher, `ggml_cuda_mul_mat_vec_q` (`mmvq.cu:1408`) - this is the
   one place real GPU work happens for a `MUL_MAT_ID` node, used identically by llama-server's decode step and by
   `run_active_grouped_dispatch()` in the unit test (both go through the standard `ggml_backend_graph_compute` API, not a test-only
   shortcut). Added a temporary diagnostic (`DIAG-C2-MMVQ`, gated on `src0->type == GGML_TYPE_IQ3_S`) at the top of
   `ggml_cuda_mul_mat_vec_q`, rebuilt `build-demand` (`llama-server` + `test-moe-cache`, same binary for both).
   - Re-ran `test-moe-cache`: 7 hits right before the known FAIL at `test-moe-cache.cpp:8348`. Confirms `test_active_grouped_dispatch_case`
     exercises this exact function for the failing case, cache-backed pointers, `GLU-fused` dispatch (`fusion=1`) for the up bank.
   - Booted **Arm B live** (`--n-cpu-moe 99 --moe-expert-cache-size 84`, same flags as the section 24 campaign, `-lv 5` for visibility) and
     sent a real completion request. **15 live hits**, e.g. `DIAG-C2-MMVQ: tensor=blk.0.ffn_up_exps.weight data=0x71acf2000000 ne2=84
     dst=ffn_moe_up-0 fusion=0`. This is the real model's actual `ffn_up_exps` tensor (confirmed `iq3_s` per the section 30 load trace),
     dispatched through the cache-backed grouped path (`ne2=84` = `N_MAX`, non-model-mmap pointer), for real generated tokens. The
     production call used the **unfused** variant (`fusion=0`, `ggml_cuda_mul_mat_id`'s single-bank branch) rather than the test's fused
     one - a partial, not exact, dispatch-variant match. Both variants construct the `bank_view` via the identical
     `ggml_cuda_moe_group_views()` call reading the same cache-pool memory with the same IQ3_S layout before handing off to the same
     `ggml_cuda_mul_mat_vec_q`; fusion state only changes whether the gate multiply happens inside the same kernel call, not how the up
     bank's bytes are read or dequantized. Treated as sufficient for "reachable by construction" - noted as a residual, smaller gap rather
     than a clean miss.

**c2 live comparison.** Booted Arm A (`prodA`, production `-ot`, no cache) and Arm B (`cacheB`, `N=84`) fresh, same `COMMON` flags as the
section 24 campaign, `/completion` with `temperature=0`, `n_probs=20`, `post_sampling_probs=false` (pre-sampling), `cache_prompt=false`.
Two prompts: a short mechanical one (`prompt-short.txt`) and the 14K-deep prompt from `/tmp/opencode/mtp14k/prompt-14k.txt` - the same
depth as the section 24-25 campaign, per the architect's requirement 3.

- **Short prompt:** byte-identical output across 200 tokens on both arms. Mean truncated top-20 KL over the full shared sequence:
  -7.1e-5 (noise floor). No divergence.
- **14K-deep prompt:** first top-1 divergence at **token index 2** (of 200), a genuine near-tie on both arms (`"\n\n"` 0.514 vs `"\n"`
  0.486 on A; 0.595 vs 0.405 on B - margins 0.028 and 0.190, neither a confident single-token lock). Taken alone this token would read as
  ordinary drift. **But the two shared-prefix tokens before it (indices 0-1) do not.** Token 0: both arms agree on top-1 (`"\n\n"`) but
  with very different confidence - A 0.62 (with real alternative mass on `" Verify"`/`" Exactly"`/`" Confirm"`, 0.03-0.08 each), B 0.94
  (sharply peaked, next alternative <0.011). **KL(A||B) at token 0 alone: 1.905 nats.** Token 1 (`<think>` vs `<think>`, both ~0.96-0.99):
  KL 0.059. **Mean truncated top-20 KL over the shared prefix: 0.982 nats** - roughly 650x the architect's ~1.5e-3 LIVE threshold, driven
  almost entirely by token 0's confidence collapse rather than the token-2 coin flip. Compare to the section 29/31 KL-divergence run via
  `llama-perplexity` (structurally confirmed unable to reach `DECODE_GROUPED`): mean KLD 1.3e-4 to 1.5e-3 depending on batch config, 3-4
  orders of magnitude smaller. The gap between "can't reach the defect" (perplexity, tiny KL) and "does reach the defect, confirmed live"
  (server, huge KL from token 0 onward) is the cleanest possible signal.
  - **Read both arms' full generated text** (not just hashed). Neither is garbled or incoherent - both are fluent, on-topic, and both
    correctly compute the answer (3000 items, `w0`..`w2999`). A goes straight to the arithmetic (`Count = 2999 - 0 + 1 = 3000`). B opens
    with a longer, more hedging chain-of-thought ("Let me count them carefully... Actually, looking at this more carefully...") before
    presumably converging (truncated at 200 tokens, still mid-reasoning). **Not visibly degraded** - this is the kind of corruption that
    a quality skim would miss, which is exactly why the KL check (not just eyeballing text) was the point of c2.

**Verdict: LIVE**, per the architect's own pre-registered bar (`shared-prefix KL far above ~1.5e-3` - met by a wide margin; text-degradation
was not observed, but the KL criterion alone is sufficient per the "or" framing in s148). Reachability is established both from existing
production logs (the section 24 campaign's own `fallback=0`/`covered=48`) and from fresh live instrumentation (real `blk.N.ffn_up_exps`
IQ3_S tensor, cache-backed, dispatched through the grouped path, during an actual generation request on Arm B). The defect is not confined
to the unit test's synthetic construction.

**Consequence per s148 verdict rules:** the section 24-27 5060 Ti A/B measurement (Arm B "wins decisively"), the N sweeps, and the 3060
ledger traces/M=190 numbers that feed the shelve arithmetic are **VOID** - any of them that exercised the grouped/cached path with IQ3_S
banks (which, per the section 24 campaign's own `covered=48`/`fallback=0`, means all of them) may be comparing a corrupted computation
against a clean one, not "cache vs `-ot`" as advertised. The **3060 shelve decision itself still stands** - shelving was already the
conservative/safe direction independent of this magnitude question, so voiding the measurements that fed it does not flip that call, it
just removes the numeric justification underneath it.

All diagnostic edits (two in `ggml-cuda.cu`, one in `mmvq.cu`) reverted after use; `build-demand` rebuilt clean; `src/llama-cpp` tree
verified clean via `git status --short`. Two internal tracking issues opened on `ddvnguyen/hydra_vortex` (`review-finding` label, `--repo`
explicit, both from outside `src/llama-cpp`): **#791** (this defect, full section 30-32 evidence trail), **#792** (CI #120, unrelated
`-Werror` breaks, ruled out as a sibling of this defect). No upstream filing (not authorized). Report sent to the architect.

## 33. Section 32's LIVE verdict withdrawn: c1' isolates this model's own triple as clean, c2' clears the token-0 anomaly as noise (architect s150-s154, 2026-09-22/23)

**Architect rebuttal (s150), three points, all source-verified before any rig work:**
1. `ggml-cuda.cu:1790` requires `ffn_up->src[0]->type == ffn_gate->src[0]->type` for GLU gate/up fusion to fire. This model's real
   weights are gate=IQ3_XXS, up=IQ3_S - different types - so the failing unit case's fused path (all three banks same-type IQ3_S) is
   **structurally unreachable** for this model. My section 31 "shouldn't matter" reasoning (both paths call the same
   `ggml_cuda_moe_group_views()`) was an inference, not evidence.
2. The short prompt's byte-identical output through the grouped path directly contradicts LIVE - if every up-projection element were
   wrong on every layer, byte-identical decode would be impossible.
3. **Token 0 of a completion is sampled from the prompt's last-position logits (prefill/legacy pass), not `DECODE_GROUPED`.** The
   1.905-nats headline number in section 32 was measuring the wrong code path. The only genuine shared-prefix decode-path evidence was
   token 1 (KL=0.059, small) and a token-2 near-tie - not a statistic. Also: KL(A||B) measures difference, not error; A is not ground
   truth.

**c1' part 1 - this model's own triple, isolated, stock build: PASSES.** Built a `--c1prime-only` early-exit into `test-moe-cache`'s
`main()` that calls `test_active_grouped_dispatch_types_case({IQ3_XXS, IQ3_S, IQ4_NL}, SEPARATE, 84u)` directly, skipping every unrelated
test in the default chain. Ran on a completely stock build (no env vars, no source patches) with `mmvq.cu` launcher instrumentation
active: every `ggml_cuda_mul_mat_vec_q` call for this case traced `fusion=0`, no `FAIL`. Confirms the architect's point 1 by direct
measurement, not just source reading.

**c1' part 2 - is the same-type failure caused by fusion specifically? Attempted, inconclusive, abandoned deliberately.** Two independent
attempts to force-disable only the GLU fusion decision both corrupted the passing model-triple control case:
- Global `GGML_CUDA_DISABLE_FUSION=1` also silently disables an unrelated fusion (MoE weighted-reduction `add_alloc_dep` registration at
  `ggml-cuda.cu:7199`, a different pattern from GLU gate/up) - confounded.
- A scoped bypass of just the two `ggml_cuda_can_fuse(..., GGML_OP_GLU)` call sites (`ggml-cuda.cu:5947`, `:6063`) still broke the
  model-triple control, even though `ggml_cuda_can_fuse` already naturally evaluates false for this model's mismatched types under
  default settings. This suggests `ggml_cuda_can_fuse`'s GLU branch may perform dispatch/cache-group-view setup as a side effect of the
  pattern match, not purely return a yes/no decision - **a predicate with side effects is a plausible mechanism for the original defect**,
  filed as an observation on #791 rather than tested further by patching. Two independent toggles corrupting a stock-passing case is
  itself the finding: the toggles are invalid instruments, not evidence that fusion is exonerated. Per architect s152: **part 2 does not
  gate the verdict** - part 1 alone answers "does this model's shipping config compute correctly on the grouped path", and it does.
  Whether the same-type failure is fusion-caused changes the scope of #791, not this track's numbers.

**c2' - the token-0 anomaly, resolved as a noise-floor artifact.** No-cache (`--moe-expert-cache-size 0` on every arm), same 14K prompt,
`cache_prompt=false` (full reprocess, `prompt_n=13894` on every arm - same as section 25's Arm B), `n_predict=1` (only token 0 matters),
`temperature=0`, `n_probs=20`. Three arms: **A** (prodA's `-ot`, 9 expert layers on CUDA0), **A_repeat** (identical config, second boot),
**A0** (0 expert layers on CUDA0, all `ffn_*_exps` on CPU). Token 0 top-1 (`"\n\n"`, id 271) agreed across all three. Truncated top-20 KL:

- **KL(A||A_repeat) - determinism floor between two boots of the identical config: 0.826 nats.**
- KL(A||A0) - depth-sensitivity signal: 1.896 nats.

Two boots of the *same* config disagree by 0.83 nats at this token - nearly as large as the A-vs-A0 signal, and comparable to section 32's
original 1.905-nats B-vs-A number. Top candidates in Arm A: `"\n\n"` 0.274 vs `" I"` 0.131 vs `"Confirm"` 0.089 - a near-tied decision at
this exact position, not a confident single-token lock. Per the architect's pre-registered branch, KL(A||A0) clears the ~0.5-nats bar for
"inherent depth sensitivity, not defect evidence" once the 0.83-nat floor is accounted for.

**Instrument-mismatch correction (architect s154, owed from s146):** the `~1.5e-3` LIVE threshold quoted in sections 29-32 came from
`llama-perplexity`'s **full-vocab** KL. Section 32's c2 numbers are **truncated top-20** KL from the server - tail mass falls outside the
window and inflates the value. The two are not comparable; the "650x over threshold" framing in section 32 was an artifact of that
mismatch, not evidence on its own. The valid comparison is within-instrument: c2' vs c2, both truncated top-20 KL from the same server
setup, which is what the verdict above actually rests on.

**Verdict: LATENT for this model.** Suspension lifted. The section 24-27 5060 Ti A/B measurement, the N sweeps, and the 3060 shelve
arithmetic all **stand** - none of them exercised the fused, defective sub-path, because this model's gate/up types never take it. #791
stays open as a genuine P1 code defect (unit-level, confirmed, unfixed at `gs/moe-cache` tip) with **unknown trigger** and **unknown
affected configurations** - it constrains future model/quant choices, not this measurement.

**Determinism floor, generalized (owner-visible, per architect s154 - recorded in `PROJECT_STATUS.md` Verified Facts, not just here):**
at near-tied token positions at ~14K depth, two boots of an identical config can differ by ~0.83 nats truncated top-20 KL even though
top-1 is unchanged. This is **position-specific, not general** - `arm_correctness.sh`'s `la0` vs `la0b` pairing produced byte-identical
200-token output, so run-to-run determinism holds wherever the top-1 decision is not near-tied. Practical consequence: exact-match and
tight-KL correctness gates are invalid at depth unless a same-config noise floor is measured first with the same instrument. This
insight - establishing the floor before judging the signal - is what overturned the section 32 LIVE verdict; it is the second time on
this track a noise floor overturned a scary-looking number (see section 22-23 for the first).

**Follow-up filed, not implemented (architect s154 item 5):** #791 is latent because of this specific GGUF's tensor types, not because of
anything under this track's control. A future quant of this model, or a different model, with gate and up both IQ3_S would make the
defect live, silently - our own text-quality skim did not catch section 32's corrupted output, only the unit test and the KL check did.
Proposed a startup tripwire (log a loud warning, or refuse to start, when `--moe-expert-cache-size > 0` and a layer's gate/up expert
tensors share a type #791 covers) as issue #793, linked to #791, not implemented.

Records updated: `PROJECT_STATUS.md` finding 2 restored to RECOMMENDATION STANDS; determinism floor added to Verified Facts; 3060
SUSPENDED-PRECAUTIONARY block lifted (the model's gate/up types are a GGUF property, not GPU-architecture-dependent, so the 3060 numbers
were never actually at risk). #791 comment posted with the part-1/part-2 findings and the side-effect hypothesis. All diagnostic edits
reverted a second time (`--c1prime-only` flag, `mmvq.cu` instrumentation, the two `ggml-cuda.cu` scoped fusion-disable edits);
`src/llama-cpp` tree verified clean by both this session and the architect independently.

**Recommendation returns to the owner as the workload question it always was:** break-even D/P ≈ 0.81-1.38%; B (cache) wins above that
line, A (`-ot`) wins by up to 4-6% below it; the change is a single launcher flag (reversible). Architect's recommendation: take it -
typical turns clear the break-even line comfortably, and no production D/P data exists to refine it further without a monitoring stack
that is not currently running. Scope: this is the track's CUDA0-solo config for Qwen3.8-Flash-Next only - must never go on a COMBINED-OT
launch (first-match-wins override precedence would silently collapse the two-GPU expert split); moving it into an actual Hydra profile
is a separate CI/CD change, owner-gated at merge.

## 34. Owner direction (s155, 2026-09-23): two solo servers, 3060 optimisation phase, cache decision adopted

**Target architecture, stated by the owner:** the final Hydra form is two separate llama-servers, one per GPU, each maximising its own
throughput; a P/D mixed-quant split is deferred, not abandoned. 5060 Ti is the main device, but the owner wants the 3060 optimised first
("when 3060 good then 5060 Ti will be super").

**Adopted, effective immediately:** `--n-cpu-moe 99 --moe-expert-cache-size 84` is now this track's 5060 Ti solo serving config,
replacing the `-ot` config used as the reference arm in sections 24-33. This closes finding 2's outstanding D/P workload-shape question
- the owner's two-solo-servers direction settles it. Still not a Hydra profile (separate CI/CD, owner-gated at merge); the COMBINED-OT
prohibition is unchanged.

**Physical fact (owner-supplied):** the 3060 sits behind an NVMe/M.2-to-PCIe x16 adapter, so x4 link *width* is by design, not a defect.
The Gen1 link *speed* is unexplained by that - root port `00:06.0` is Gen4 x4 capable, and adapters commonly force a Gen1 downtrain
independent of width. Owner is setting the M.2 slot to Gen3 in BIOS; this is a physical action outside agent reach.

**New work, in order (owner spec, condensed):**
- **0.** (this section) - done.
- **P1 - 3060 matched-depth A/B, run twice** (before the BIOS fix = now, after = once relayed): reuse `arm_5060_ab.py` pattern with
  `CUDA_VISIBLE_DEVICES=1`. Arm A = best static `-ot` dose at ctx 81920, determined empirically (largest layer count that both loads and
  survives a full 200-token request - 7 layers was the 16K-context cap, expected lower at 81920). Arm B = `--moe-expert-cache-size 42`
  (N=56 OOMs on first long request, per prior 3060 work). n=3, MTP off, `--moe-expert-cache-size 0` explicit on Arm A (override-precedence
  gate), matched depth, pre-registered before running. Include multi-turn growth curve + per-turn prefill as on the 5060 Ti.
- **P2** - concurrent aggregate throughput: both servers decoding simultaneously (5060 Ti at N=84, 3060 on P1's winner). Report each
  server's tok/s concurrent-vs-solo and the sum. Risk: this rig has been CPU-bound before (97% busy); favourable prior: the cache cut
  5060-Ti-side `cpu_busy` from ~20% to ~8%. Test disjoint `taskset` core split (12700K = 8P+4E) vs unpinned, pre-registered. If the
  concurrent sum falls well below the solo sum, name the contended resource (cores vs memory bandwidth) before proposing a fix. Can run
  on Gen1 as a first read but must be labelled Gen1 and must not drive a thread-layout recommendation alone.
  - **P3** - 3060 prefill collapse (4.7 tok/s vs 122 on 5060 Ti): re-measure after the BIOS fix, before investigating further. Gen1 is the
  prime suspect; do not spend effort on root cause until the link is fixed. **Depth-control note (architect, mid-P1):** N=3 dose-probe
  prefill readings fell steadily with depth (9.6 -> 9.1 -> 8.7 tok/s over ~2048/4096/6144 tokens) - likely ordinary attention-cost growth
  with context depth, not a rig artifact, but it means Gen1-vs-Gen3 must be compared **at matched prompt depth**, not just as a single
  average tok/s number, or the link-speed gain will be confounded with the depth slowdown. Record per-`print_timing`-line depth alongside
  tok/s in both P3-before and P3-after.

Sequencing: 0 and P1-before now; P2 Gen1-labelled first read allowed now; P1-after and P3 wait on the owner's BIOS change.

**Dose-search result + P_max decision rule (architect, mid-P1, 2026-09-23):** measured VRAM-at-load on the 3060 at ctx 81920 scales
linearly with tail-expert-layer count: N=3 -> 9,903 MiB, N=5 -> 11,805 MiB, i.e. **~951 MiB/layer** (not the ~2-layer capacity the
architect had estimated earlier from the 16K-context figure - correcting that estimate here). Linear extrapolation puts N=6 at
~12,756 MiB, over the card's 12,288 MiB total; N=6 was **not run** (recorded as infeasible by extrapolation, saving the probe time).
With the measured 0.2065 tok/s/layer rate, the static arm's edge over the cache arm is now expected around **+1.0 tok/s**, not the
+0.4 tok/s the architect's earlier under-estimate implied - the A/B is closer than first thought.

P_max (Arm A's static dose) is decided by **peak VRAM during the full run, not the at-load figure**: sample `nvidia-smi` at ~1 Hz for
the whole request (prefill + decode) and take the max. A dose is "serving-safe" only if peak headroom (12,288 - peak) is >= 256 MiB.
If N=5's peak headroom comes in under 256 MiB, Arm A drops to N=4 (~10,854 MiB by the same linear rate) and N=5 is logged as
"fits but not serving-safe" rather than discarded outright.

**P_max result: N=5.** Full run measured via 1 Hz `nvidia-smi` sampling across the whole 14K-token request (prefill + 200-token decode):
peak VRAM 11,807 MiB / 12,288 MiB, peak headroom **481 MiB >= 256 MiB -> SERVING_SAFE**. N=6 was not run (infeasible by extrapolation,
above). N=5's request also produced the first valid decode reading for this campaign (prior N=3 result's decode fields are the known
raw-`/completion` bug): prefill 8.84 tok/s (13,946 real prompt tokens, matches the ~9.6->8.1 depth-falloff curve), decode **12.44 tok/s**
(200/200 predicted). **Arm A for the P1 A/B is therefore N=5** (`--override-tensor 'blk\.(43|44|45|46|47)\.ffn_.*_exps.*=CUDA0,ffn_.*_exps.*=CPU' --moe-expert-cache-size 0`).

**P1 A/B pre-registration (before running, per spec requirement):**
- Script: `scripts/moe-controls/arm_3060_ab.py`, structurally mirrors `arm_5060_ab.py` (same Monitor/void/reps logic), generalised
  from GPU index 0 to index 1, no foreign-co-resident-process filter (none known on the 3060, unlike the 5060 Ti's Qwopus ctx).
- Arm A = `static3060`: `-ot` tail-N=5 (`blk.(43|44|45|46|47)`), `--moe-expert-cache-size 0` explicit (override-precedence gate).
- Arm B = `cache3060`: `--n-cpu-moe 99 --moe-expert-cache-size 42` (N=56 known to OOM on first long request per prior 3060 work).
- Both: ctx 81920, `--spec-type none` (MTP off), same fixed 14K-token prompt (`PFILE`), `cache_prompt: true`, n=3 reps/boot (rep 1 = cold
  boot, reps 2-3 = warm/prefix-reused - the multi-turn growth-curve proxy used on the 5060 Ti campaign).
- Primary metric: `decode_tps_server` (server-reported `predicted_per_second`) per rep, plus `prefill_tps` at matched depth (same prompt,
  so depth is identical between arms by construction - no confound here, unlike the P3 Gen1-vs-Gen3 comparison).
- Gates checked post-hoc, not cherry-picked: (1) override-precedence - `lru_cache_warn_lines` must be empty for `static3060`; (2)
  `vram_peak_mib` peak headroom >= 256 MiB for both arms (Arm A already known-safe from the dose search; Arm B TBD); (3) `log_errors`
  empty (no OOM/CUDA error) for both.
- Decision rule: higher mean `decode_tps_server` across the 3 reps wins, reported alongside prefill numbers - not a single-rep pick.

**Amendment (architect, 2026-09-23T~13:35Z, before Arm B starts):** the same fixed 14K prompt is sent every rep with `temperature: 0`
(greedy, confirmed in `arm_3060_ab.py`, no seed since sampling is deterministic). For `cache3060`, rep 1's decode leaves the expert
cache pre-loaded with exactly the experts reps 2-3 will route to again (identical prompt -> identical greedy output -> identical expert
sequence) - an unrealistic best-case hit rate no real multi-turn traffic gets, the same class of artifact as #743. `static3060` has no
cache, so it is unaffected. **Decision rule replaced:** the primary metric is now **rep 1 (cold) `decode_tps_server` per arm**, not the
3-rep mean. Reps 2-3 are kept (cheap, already collected/collectable) but reported as a **"replay upper bound"**, not averaged into the
A/B decision - option (b) from the architect's amendment, chosen because it needs no change to `static3060`'s already-running boot and
no re-run of Arm A. Headroom and LRU-warning gates are unchanged.

**Tie-break threshold (architect, pre-registered before Arm B's rep-1 result lands):** Arm A's own 3-rep spread (12.18-12.71 tok/s,
~+-4% around the mean) is the only noise estimate available for an n=1 primary metric, so a +-5% band around Arm A's cold value
(12.18 tok/s) is the tie zone:
- Arm B rep-1 decode >= 12.79 tok/s (+5%) -> **cache wins**.
- Arm B rep-1 decode <= 11.57 tok/s (-5%) -> **static wins**.
- Between 11.57 and 12.79 tok/s -> **tie, static wins by default** (simpler, keeps explicit `-ot` control since the cache silently
  overrides `-ot`/`--n-cpu-moe` first-match-wins, and avoids the cache's known 4-6% prefill penalty measured on the 5060 Ti campaign).

Arm B's peak VRAM will be checked against the same 256 MiB headroom gate used for the dose search.

**P1 A/B result: static wins decisively, outside the tie zone.** Arm A (static3060, N=5) rep-1 cold: decode **12.18 tok/s**, prefill
8.98 tok/s, peak VRAM 11,807 MiB (headroom 481 MiB, gate passed). Arm B (cache3060, N=42) rep-1 cold: decode **1.51 tok/s** (~8x
slower, far below the 11.57 tok/s lower tie-break bound), prefill 8.56 tok/s (close to Arm A - prefill is not where the collapse is),
peak VRAM 10,769 MiB (headroom 1,519 MiB, gate passed - Arm B is VRAM-safe, the collapse is a throughput defect not a capacity one).
Arm B's own server log shows heavy prefill-phase cache churn (`moe-cache-phase: phase=prefill ... l1_hit_rate=49.62% h2d_mib=76287.68`
- 76 GB host-to-device during prefill) but the decode-phase block itself shows only `h2d_mib=51.27` with `l1_hit_rate=68.89%`, so the
mechanism for the decode-side collapse is not yet clear from this log alone (small decode-phase H2D volume, yet 8x slower decode) -
flagged to the architect as a new finding, not diagnosed further here. **Verdict: static3060 (N=5, `-ot` tail-5) is the 3060's P1
serving config**, matching the tie-break default's stated preference (simpler, explicit `-ot` control, avoids the cache's known
prefill penalty) but by a much larger margin than a default-tiebreak would suggest.

**Full campaign closed:** `cache3060` reps 2-3 (warm, replay upper bound) confirm the collapse persists: 1.61 and 1.55 tok/s, both
consistent with rep 1's 1.51 - this is not a cold-start artifact, the cache arm is slow throughout. `ARM_DONE` clean for both arms
(no voids, no log errors).

**Decode-collapse diagnostic (architect, time-boxed, before P2):** the same cache runs on the 5060 Ti (P2, dual-GPU final form), so
the mechanism matters beyond P1's scope even though the P1 verdict itself doesn't change. Three hypotheses: H1 link-bound (the cache
path moves more data than its own `h2d_mib` counter records, and Gen1 makes that ~60x more expensive than the 5060 Ti's link), H2
per-layer GPU<->CPU sync stalls (not bandwidth) when a layer's experts split between cache-hit-GPU and CPU, H3 a slow kernel path
specific to sm_86 (same area as #791). Diagnostic: `scripts/moe-controls/pcie_decode_diag.py`, short prompt (~512 tok, `prompt-512.txt`,
truncated from the 14K prompt) + 200 decode tokens, one cold boot per arm, time-boxed to <=3 boots / ~30 min: (a) `cache3060` (N=42,
3060) (b) `cache5060_n42` (N=42 on the 5060 Ti - matched N, only GPU/link differs from (a), NOT the adopted N=84) (c) `static3060`
(N=5, baseline). Measures decode-window PCIe RX MB/s (`nvidia-smi dmon -s t`, aligned to the server's own `prompt_ms`/`predicted_ms`
timings) and link-gen samples, computes bytes/token against the cache's own decode-phase `h2d_mib` counter. Read: both GPUs move
similar bytes/token but 5060 Ti stays fast -> H1 (predicts P1-after should recover roughly in proportion to link speed); 3060's
bytes/token stay small but it's still slow -> H2 or H3 (one more boot of (a) with `GGML_CUDA_DISABLE_FUSION=1` separates them, #791
area); neither fits -> record "unlocalized" and stop, no fix work belongs in P1.

**Diagnostic result: H1 confirmed, cleanly, all three boots.**

| Arm | GPU / link | decode tok/s | decode RX avg | decode total RX | RX MiB/token | MiB/(layer x k=10) |
|---|---|---|---|---|---|---|
| `cache3060` (N=42) | 3060 / Gen1 x4 (constant, 108 samples) | 1.69 | 1,009.9 MB/s | 119,105 MiB | 595.52 | 1.241 |
| `cache5060_n42` (N=42) | 5060 Ti / Gen5 (constant, 6 samples) | 29.87 | 12,155.3 MB/s | 80,993 MiB | 404.96 | 0.844 |
| `static3060` (N=5, baseline) | 3060 / Gen1 x4 | 13.52 | 73.2 MB/s | 1,077 MiB | 5.39 | 0.011 |

`static3060`'s own decode-phase PCIe traffic (5.39 MiB/token) is ~110x smaller than either cache arm's - confirming the huge RX volume
is specifically the cache mechanism, not general decode/KV traffic, and not something Gen1 alone would expose (`static3060` is also
Gen1 and is unaffected). The two cache arms move a similar order of magnitude of data per token (0.84-1.24 MiB per layer x expert-slot,
~1.5x apart - plausibly cache-size/eviction-dynamics noise, not the dominant effect) but at vastly different link speeds (Gen5 vs
Gen1, ~12x raw MB/s ratio), which alone predicts most of the ~17.7x decode-speed gap (29.87 vs 1.69 tok/s) between the two cache arms.
The cache's own instrumentation (`ids_mib=0.00` at the phase level, tens-of-MiB at the per-tensor level) undercounts this by 3-4
orders of magnitude on both GPUs - a real instrumentation gap, not 3060-specific, so **H3 (sm_86-specific kernel path) and #791 are
not indicated**; no `GGML_CUDA_DISABLE_FUSION=1` follow-up boot is needed. **Verdict: H1, link-bound.** Prediction for P1-after (BIOS
Gen3 fix): the cache arm's decode should recover roughly in proportion to link speed (Gen1->Gen3 is a further ~4x jump on the same
x4 width), though `static3060` remains the safer P1 pick regardless since it isn't link-sensitive.

**P2 implication - two-config plan, corrected (architect):** the two GPUs sit on separate CPU root ports (`00:01.0`, `00:06.0`) and
`static3060` itself uses only 5.4 MiB/token of PCIe, so **the shared resource is not the PCIe root complex** - it's **host RAM
bandwidth**. The 5060 Ti cache arm pulls ~405 MiB/token x ~30 tok/s = **~12 GB/s out of host RAM**; the 3060 server's 43 CPU-offloaded
layers are themselves RAM-bandwidth-limited, and dual-channel DDR4 on this host tops out around ~50 GB/s total. P2 runs **two configs**,
3060 fixed at `static3060` (P1's winner) in both: **Config 1** = 5060 Ti `cache` (N=84, adopted) + 3060 `static` (N=5) - the real target
architecture. **Config 2** = 5060 Ti `static` (matched `-ot` tail dose, cache off) + 3060 `static` (N=5) - the RAM-bandwidth-contention
control (removes the cache's ~12 GB/s RAM draw). For each config, also run **each server alone** (same prompt, same thread pinning) so
**interference = concurrent tok/s / solo tok/s** per server; decision metric is **combined tok/s**. Pin threads so the two servers get
disjoint P-core sets (12700K = 8P+4E) - CPU-thread contention must not be conflated with RAM-bandwidth contention in the result.

**What the diagnostic table says about the cache mechanism itself:** 0.84-1.24 MiB per layer-slot is roughly the size of one routed
expert tensor - the cache is moving **close to a full routed expert per slot on every token**, i.e. it is effectively streaming
weights, not caching them (consistent with the low L1 hit rates seen throughout this track). The 5060 Ti's "win" in the diagnostic is
Gen5 raw streaming bandwidth, not cache-hit reuse.

**Follow-up filed: issue #794** (not implemented, per track workflow) - the cache's `h2d_mib`/phase-level counters undercount real
PCIe traffic by 3-4 orders of magnitude on both GPUs - an instrumentation defect, not 3060-specific. Counter fix is dev-agent work,
deferred.

**Pre-registered prediction for P1-after (post-BIOS-fix):** `cache3060` decode tok/s should scale as roughly link-bandwidth / 595
MiB/token. Gen3 x4 (~3.5 GB/s) predicts **<= ~6 tok/s for the cache arm - `static3060` (~12.2 tok/s cold) should still win P1-after**.
P1-after is therefore scoped down to a single short-prompt boot per arm (confirm-the-prediction, not a full re-run).

**Reboot readiness note:** the BIOS Gen3 reboot takes down the whole host (P100 VM, podman, Paseo daemon, this leader session, the
architect's session). P2 had not started at the time of this note, so no measurement is at risk. Design doc state is current as of
this entry. First post-reboot check: confirm `pcie.link.gen.current` reads 3 under load, before resuming P1-after/P3/P2 in that order.

**P2 pre-registration (before running):** owner did not respond to a timing check on whether to hold for the reboot; proceeding now
under standing auto-drive authorization for routine/reversible decisions ([[703-auto-drive-authorization]]) - if the host reboots
mid-run, the detached process just dies and is rerun after, no real cost.
- Script: `scripts/moe-controls/p2_concurrent.py`. 5060 Ti = dev0/port18441, 3060 = dev1/port18442 (no port clash, true concurrency).
- Prompt: same ~512-tok short prompt as the PCIe diagnostic (`prompt-512.txt`), 200 decode tokens - chosen for speed (P2 is a
  first-read on concurrent *decode* throughput/contention, not a full prefill re-test; P1 already characterised prefill separately).
- CPU pinning: 12700K P-cores split disjoint via `taskset` - 5060 Ti server on logical cpus 0-7 (P-cores 0-3), 3060 server on logical
  cpus 8-15 (P-cores 4-7), confirmed via `/sys/.../topology/core_id` (cpu0-15 = 8 P-cores x2 HT, cpu16-19 = 4 E-cores, left idle for
  host OS overhead). Both servers keep `-t 6` (unchanged from all prior arms).
- Per config: SOLO 5060 (own boot+request, 3060 not running), SOLO 3060 (own boot+request, 5060 not running), then CONCURRENT (both
  booted together, both requests fired via parallel threads at the same wall-clock start). One rep per condition - time-boxed first
  read, not a multi-rep campaign (matches the original P2 spec's "first read" framing, sec 34 above).
- Decision metric: **combined concurrent tok/s** (`r5060.decode_tps + r3060.decode_tps` in the concurrent condition), plus
  **interference per server** = concurrent tok/s / solo tok/s. Config 1 (cache_n84 + static_n5) vs Config 2 (static_tail9 +
  static_n5) comparison isolates whether the cache's ~12 GB/s RAM draw (sec 34 above) causes measurably worse interference than the
  RAM-bandwidth-neutral static-vs-static control.

**Config 1 concurrent phase crashed - real finding, different from the RAM-*bandwidth* hypothesis P2 set out to test.** Solo legs
succeeded (5060 Ti `cache_n84` 34.5 tok/s decode / 181.0 tok/s prefill; 3060 `static_n5` 12.27 tok/s decode, both no-void). The
concurrent phase (both booted, 5060 Ti already fully loaded and idle, 3060 mid-load) was killed by **`systemd-oomd`** (userspace OOM
daemon, not the kernel OOM killer directly) - `journalctl -k`: `oom-kill:constraint=CONSTRAINT_NONE,...,global_oom,task=llama-server,
pid=1849478` `total-vm:190552340kB` `shmem-rss:45185384kB`. Root cause: this host's swap was **already at ~83% (19/23 GiB) at rest**
(`systemctl status systemd-oomd`: default `SwapUsedLimit=90%`), from unrelated long-idle desktop-session processes (`baobab`, `gdu`,
`plasmashell`, `kscreenlocker_g` - checked via per-PID `/proc/*/status VmSwap`, confirmed not GPU/track-related). Booting the 5060
Ti's `--n-cpu-moe 99 --moe-expert-cache-size 84` config alone already carries a very large host RSS/mmap footprint (same pattern as
the `cache3060` diagnostic's ~46 GB RSS, sec 34 above); loading the second (3060) server on top of that pushed swap over the 90%
oomd trigger before the concurrent-decode phase this experiment was designed to measure ever ran. **This is a real capacity finding
for the two-solo-servers target architecture** (owner s155): running the 5060 Ti's adopted cache config concurrently with a second
full model load is not reliably stable on this host's current memory profile, independent of the RAM-*bandwidth* contention P2 was
designed to test - a swap-*capacity* risk, not a bandwidth one. No sudo on this box (`swapoff`/raising the oomd limit are root-only),
so this can't be fixed by the leader; flagged to the architect as a genuine blocker, with the safe next step (Config 2, both arms
static, far smaller host RSS) run first to get a clean P2 data point while a retry strategy for Config 1 is decided.

**Architect correction: this is a harness setting, not an architecture limit.** `--load-mode none` gives each server its own
**private** copy of the 47 GB GGUF (5060 Ti cache_n84 solo measured `Shmem`=43.1 GB, `RssFile`=0.17 GB - i.e. almost entirely
private anonymous memory, not shared page cache). Two servers under `none` therefore need ~86 GB private + ~13 GB RAM-backed
`/tmp` (other agents' build trees) + desktop - the swap gate was always going to reject Config 1 under `none`, by arithmetic, not
bad luck. `--load-mode mmap` should let both processes share **one** ~47 GB page-cache-backed mapping instead. Declined the
architect's option (a) (treat the swap-abort itself as Config 1's finding) since that would record a harness flag as an
architecture limit, and declined (b) (touch other sessions'/desktop's swap) since that state isn't ours to touch. Killed the
`none`-mode retry (predicted by the architect's own math to hit the same swap gate; verifying it would only have cost ~10 more
minutes for a foregone conclusion) and moved directly to plan (c):
1. **Solo-verify `--load-mode mmap`** on each arm alone (no concurrent load), one discarded warm-up request (pays the SATA-NTFS
   page-in cost) then one measured request. Pass = decode tok/s within +/-5% of the `none`-mode baselines (5060 Ti cache_n84 34.5
   tok/s decode / 181.0 tok/s prefill; 3060 static_n5 12.27 tok/s decode), plus `RssFile` high / `Shmem` low confirming a real
   shared file-backed mapping. Script: `scripts/moe-controls/p2_mmap_solo_check.py <cache5060|static3060> <out_dir>`, running
   detached now (`/tmp/opencode/controls/p2/mmap-solo/`).
2. **If both pass:** rerun Config 1 and Config 2 concurrently with `mmap` on both servers, keeping the swap gate (sec 34 above) as
   a safety backstop, not the primary defense.
3. **If cache_n84 is slower under `mmap`** (e.g. the cache needs its own private pinned pool and can't tolerate a shared mapping):
   that becomes the real P2 finding - the cache config costs a second full private weight copy in the dual-server form, so the
   5060 Ti should run `-ot` (static, matching Config 2) in production, not the cache config, when serving concurrently with the
   3060.
- Deferred, not blocking this run: if (2) holds, Hydra node configs for both engines need `load-mode: mmap` in the dual-server
  production form (architect will spec once P2 settles); the 13 GB RAM-backed `/tmp/opencode` build-tree cleanup is an owner call,
  not touched.

**mmap solo-verify result: 5060 Ti `cache_n84`.** `RssFile`=48.4 GB / `RssShmem`=151 MB confirms the mapping mechanically IS a
real shared, file-backed page-cache mapping (not a second private copy) - the OOM-avoidance half of the fix works. Decode
26.92 tok/s vs the 34.5 tok/s `none`-mode baseline (-22.0%), outside the +/-5% gate.

**Architect correction (self-flagged scoping error on my part): this does NOT disqualify Config 1.** The +/-5% gate tested
whether `mmap` preserves `none`-mode speed; it doesn't, but that's not the P2 decision variable. What decides P2 is the cache arm
against its *static alternative*, both under the same load mode: cache_n84/`mmap` (26.92 tok/s) vs 5060 Ti `static_tail9` solo
(~22.25 tok/s, previously measured under `none`) - the cache arm is still ~21% faster even after the mmap penalty. Also flagged:
the mmap penalty may be general (private allocations may get transparent huge pages that `mmap`'s 4 KB page-cache pages don't), in
which case `static_tail9` slows proportionally too and the ranking is unaffected either way - needs its own `mmap` solo number to
know. **Revised P2 matrix (all arms under `mmap`, swap gate kept as backstop):**
1. Four solo baselines under `mmap`: cache_n84 (have: 26.92), static_tail9 (running now, was skipped earlier since it wasn't
   needed for the swap-capacity fix - now needed for the apples-to-apples cache-vs-static comparison), static_n5/3060 (running).
2. Concurrent: **both** Config 1 (cache_n84 + static_n5) and Config 2 (static_tail9 + static_n5) - Config 1 is back in scope.
3. Report per server: concurrent/solo (interference), plus combined tok/s.
4. **Decision rule, pre-registered before running:** higher combined tok/s wins; within +/-5% is a tie and **Config 2 wins ties**
   (avoids the cache's under-counted PCIe traffic [issue #794] and the RAM-streaming behavior, sec 34 above).
- Held for later, needs owner+sudo: if the mmap penalty turns out to hit both configs, mounting the GGUF on a hugepage-backed
  tmpfs (root-only) could recover it for both - architect will only spec this if the `static_tail9`/mmap data calls for it.

**All 4 solo `mmap` baselines complete:**

| arm | GPU | decode tok/s (mmap) | vs `none` baseline | gate |
|---|---|---|---|---|
| cache_n84 | 5060 Ti | 26.92 | 34.5 (-22.0%) | outside +/-5% |
| static_tail9 | 5060 Ti | 18.86 | ~22.25 (-15.2%) | outside +/-5% |
| static_n5 | 3060 | 12.75 | 12.27 (+3.9%) | **within +/-5%** |

Confirms the architect's general-mmap-penalty hypothesis, but asymmetrically: both 5060 Ti CPU-offload configs lose real
throughput under `mmap` (cache -22.0%, static_tail9 -15.2%), while the 3060's own CPU-offload config (`static_n5`, also offloads
`ffn_*_exps` to CPU) is essentially unaffected (+3.9%, within gate) - the penalty doesn't track "does this arm offload to CPU" in
general, so its mechanism is still open (possibly scale-dependent: the 5060 Ti's much higher decode rate multiplies the same
per-fetch page-fault overhead into a bigger relative hit). Solo cache-vs-static ranking on the 5060 Ti holds under `mmap`
(26.92 > 18.86, cache still wins), matching the architect's prediction. Launched Config 1 and Config 2 concurrent runs under
`mmap` (sequenced, `/tmp/opencode/controls/p2/mmap-concurrent/`), swap gate kept as backstop (expected not to trigger, since
`mmap` shares one ~40-48 GB page-cache copy per GPU instead of two ~43-49 GB private copies).

**Config 1 (cache_n84 + static_n5) concurrent result, mmap - swap gate did NOT trigger** (`pre3060_swap_pct`=84.3%, under the
85% abort line - confirms the mmap fix works mechanically, no oomd risk).

| leg | 5060 Ti decode tok/s | 3060 decode tok/s | combined |
|---|---|---|---|
| solo | 28.53 | 12.74 | 41.27 |
| concurrent | 27.91 | 12.74 | **40.65** |
| interference (concurrent/solo) | 0.978 (-2.2%) | 1.000 (~0%) | - |

Interference is small on both servers - the RAM-bandwidth hypothesis (sec 34 above) predicts more drag than this shows; either
the cache's RAM draw is smaller than estimated, or the two P-core-pinned processes + disjoint GPU DMA paths keep it mostly out of
each other's way. Config 2 (static_tail9 + static_n5) now running (solo 5060: 19.23 tok/s decode / 176.1 prefill - close to the
`static5060_tail9`/mmap solo-check number 18.86 above, small run-to-run noise; solo 3060 + concurrent in progress).

**Config 2 (static_tail9 + static_n5) concurrent result, mmap - swap gate did NOT trigger** (`pre3060_swap_pct`=84.5%).

| leg | 5060 Ti decode tok/s | 3060 decode tok/s | combined |
|---|---|---|---|
| solo | 19.23 | 12.74 | 31.97 |
| concurrent | 18.78 | 12.74 | **31.52** |
| interference | 0.977 (-2.3%) | 1.000 (~0%) | - |

**P2 VERDICT: Config 1 wins decisively.** Combined concurrent tok/s: Config 1 = **40.65**, Config 2 = **31.52** - a **+29.0%**
gap, far outside the pre-registered +/-5% tie band (which would have defaulted to Config 2). Both configs show the same small,
near-identical interference pattern (5060 Ti server ~-2.2 to -2.3% under concurrent load, 3060 ~0%) - concurrent load barely
perturbs either server once `mmap` removes the swap-capacity risk, so the RAM-bandwidth-contention hypothesis that motivated P2
(sec 34 above) does not show up as a material effect at this prompt length/depth (~590 tok prompt, 200 decode tok, single rep) -
the combined-throughput ranking is set almost entirely by each config's own solo speed, not by how much they interfere with each
other. **Production recommendation: two solo servers, 5060 Ti on `cache_n84` (`--n-cpu-moe 99 --moe-expert-cache-size 84`), 3060
on `static_n5` (`-ot` blk 43-47), both under `--load-mode mmap`** (required for OOM-safety per the systemd-oomd finding above -
`--load-mode none` is not viable in the dual-server concurrent form on this host regardless of which 5060 Ti config is chosen).
P2 closed. Reported to architect; next: P1-after / P3 pending the owner's physical Gen3 BIOS reboot (still outstanding), plus the
two previously-queued tasks (`task-207401ba40` Colibri merge/build/correctness gates, `task-6becca4365` atlas expert-usage
sweeps) per sec 35, now unblocked by P1+P2 closing.

**Architect confirmed P2 closure, no re-run at depth now.** Ranking is a 29-point gap; flipping it via depth-driven interference
growth would need interference to grow ~13x (from ~2% to ~29%) which has no obvious mechanism (KV cache lives on GPU; the
host-RAM/CPU-side work that actually competes is roughly depth-independent per token). Folded the depth check into the
post-Gen3-reboot P1-after/P3 window instead: **one concurrent Config 1 rep at the 14K P1 prompt**, checking interference at depth
(incl. both servers prefilling simultaneously - affects TTFT, the short-prompt run couldn't show this), swap margin with a full
KV cache, and the new link speed, all on final (post-reboot) hardware in one boot.

**Noted for later (not gating now):** `mmap` cost the 5060 Ti ~14-17% on both its configs (cache 34.5->28.53, static 22.25->19.23)
but the 3060 got slightly *faster* (12.27->12.74) - consistent with a 4KB-page-fault mechanism the 5060 Ti is fast enough to be
sensitive to and the 3060 isn't. Potential fix (owner-gated, needs sudo): GGUF on a hugepage-backed tmpfs, both servers mapping
it - potential +4-6 tok/s combined on the 5060 Ti side. Architect will write this up as an owner decision after Gen3 results.

**Gate updates for `task-207401ba40` (Colibri merge/build/correctness), since production is now `mmap`:**
- Gate 1 (tok/s comparison): compare against the **`mmap` solo baselines (28.53 / 12.74)**, not the `none`-mode numbers. The
  byte-identical output check against `correct-la0.txt` is unaffected (load mode doesn't change the math).
- Gate 2 (telemetry smoke test): run on **both servers concurrently** - the new production shape - not solo.
`task-6becca4365` (atlas sweeps) unchanged: 3060 first, pass threshold set before running.

Production spec (pending Gen3 results): Hydra node configs - 5060 Ti `cache_n84`, 3060 `static_n5`, both `--load-mode mmap`.
Architect will write the full spec for a Haiku dev hand-off once Gen3 measurements land; merging stays with the owner.

## 35. Colibri track folded in (s156, 2026-09-23) - queued behind P1/P2, no build while measurement in flight

Owner decision `d-556c6cc692`: track `t-d94162d3f0` (Colibri) archived, folded into this track. Consult-side prep (fork merge +
telemetry hooks, no build) already done: `hydra-fork/feat/flash-next-colibri` @ `ee13fdaa6` fast-forwards this track's
`feat/moe-demand-admission` @ `a2c46384b` with 16 env-gated commits (`HYDRA_EXPERT_STATS`/`HYDRA_EAN_STATS`/`HYDRA_EXPERT_CAPTURE`/
`HYDRA_EXPERT_META`, HTTP `GET /experts`, no new RPC opcode - this lineage has no Hydra RPC listener so the earlier 0x33 plan was
dropped). Parent `origin/feat/flash-next-colibri` @ `4a6614cc8` merges `origin/epic/787-per-turn-telemetry` (atlas-web/tools/atlas),
`reproduce.py` passes, submodule pinned to `ee13fdaa6`.

**Queued tasks (orchestration `task_add`):**
- `task-207401ba40` (after this section's P1/P2 close): `git merge --ff-only feat/flash-next-colibri`, merge parent into
  `baseline-flash-next`, build sm_120+sm_86. **Gate 1 (OFF-parity):** atlas envs unset, 5060 Ti N=84 config, greedy 200-tok
  byte-identical to `correct-la0.txt`, tok/s within this section's noise band. **Gate 2 (ON smoke):** `META+STATS=1`, `/experts` returns
  200 with `telemetry:true` and nonzero cells post-decode on the cache-ON config; record ON-mode decode cost. Gate 1 failure stops the
  task - no patching around it.
- `task-6becca4365`: atlas sweeps on APEX-I-Mini, 3060 first - does REAP saliency or Edge0 prerouter predictability beat global heat
  h@38~0.39 (see `moe-expert-coverage-by-subject`) by enough to move the 3060's small P_max under a per-expert GPU/CPU backend-selection
  budget of 36.3 us/invocation (see `moe-ranking-mechanism-budget`)? Threshold pre-registers before the run, per #790.

**Slotting decision:** task-207401ba40 runs strictly after P1 and P2 close - the rig is single-GPU-task-at-a-time by design, and a build
while a measurement is mid-flight would invalidate both binary provenance and the measurement's binary hash. P1 (3060 dose probe + A/B)
is in progress as of this section; P2 (concurrent) follows. task-6becca4365 (atlas sweeps) queues behind the merge+build since it needs
the Colibri telemetry hooks to exist in the binary it measures against.
