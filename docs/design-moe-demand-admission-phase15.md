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

## 19. F1/F2 at the design's real call spacing (sections 131-132) - result: rule cell PASSES by 3%, upper bound FAILS

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
   wake-up is NOT what K is made of and "keep the pool hot" is worth ~0.** K is per-call dispatch, activation quantisation and
   graph-node barriers. A 1.5 ms spin gap raises K to 0.120 and a sleeping caller to 0.095, so K does depend on call spacing
   by some mechanism other than pool sleep (unidentified). The lever that survives is fewer, larger calls (batch CPU-served
   experts across layers where the dependency graph allows): 48K = 3.9 ms of bench time, 5.4 ms scaled in situ, about 10% of a token.
6. **F2.** F2_pure = c(4, GPU decode)/c(4, idle) = **1.057** (per-j 1.01-1.14, no j below 1.0). F2_hybrid, with a 6 GB/s host-memory
   reader added, = **1.122**. The reader models Gen4 upload staging and is only valid for a hybrid design that still uploads;
   under pure bypass there is no such stream and the CPU's own weight reads are already inside c_cpu, so the RULE CONSUMES F2_pure
   (architect s132.2). The busy workload was real (GPU 100%, llama-server 100% of a core, link Gen1 ~0.6 GB/s), so F2_pure is
   also a lower bound on contention at Gen4 uploads.
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
10. **What would resolve it (proposal, not run):** whether the engine-minus-bench gap is fixed or proportional needs a SECOND
    in-situ point. Running the static arms `ncm2` and `allhost` at a reduced routed-expert count
    (`--override-kv qwen4exp.expert_used_count=int:4`, timing only, output meaningless) gives the engine's per-layer CPU-vs-GPU
    delta at j=4 next to the measured j=10 (0.99-1.16 ms), i.e. K_engine and w_engine directly, without building split execution.
    Four short shallow arms (~20 min of rig). It can move the rule cell to either side of 13.75, so it passes the section 123 test.
