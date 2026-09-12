# UM Paging Collapse Mitigation — ctx right-size lever (262144 → 148000)

## Lever Choice & Hypothesis

**Lever:** `ctx` right-size — control `262144` (747.x oversub family, 2×131072 hard-fenced, UM required to boot) vs experiment `140000` (arm088/090 family right-size, parallel 1, kv_unified on, 16384 cache_ram, q8_0/q4_1 MTP — the only lever with prior validated evidence of fixing the collapse; 148000 without UM OOMs on this build due to MTP cliff, so 140000 with UM used as closest validated right-size — see Method).

**Why this lever:** All prior non-ctx levers have replicated nulls at this oversub+UM shape:
- `cache_ram` 1024 vs 24576 (arm-ud Round1) and 24576 vs 8192 (cache_ram arm, 2× per condition) both collapsed to ~7-9 tok/s at 80K — no threshold shift.
- `tensor_split` 25,40 / 30,35 in arms 106/107 at 102 shape — no diff.
- `kv_unified` on/off in 747.4 / 121-123 — no diff.

The collapse is VRAM oversubscription: `docs/investigations/740-results-report.md §3430` and arm120 math place the no-paging ceiling at ~61-88K combined filled tokens for the 27,38/q8_0/q5_1 shape (438528 total cells → CUDA0 21.76 GiB vs 16.31 physical, RPC0 15.22 vs 12.29). At 262144 total (2×131072 per-slot) with 80-81K resident, the active KV + model + draft + RS exceeds physical VRAM, UM pages via PCIe thrash (10-13 GB/s observed in 740), decode collapses to 7-9 tok/s (vs 30-50 shallow). Right-sizing `ctx` to the no-paging envelope (088 140000 → 407 MiB free, 090 148000 → 167-191 MiB free, both p1/q8_0/q4_1/16384/MTP validated 2026-08-29 via 3-agent/6-turn stress test) makes the **full-fill VRAM fit blind-test pass** (arm120: 98304 total p3 → 528 MiB free CUDA0, 938 MiB RPC0 at full 32K/slot). At 140-148K single-slot, 80K resident is well within the envelope, so UM paging should not trigger and decode should hold at the shallow compute-bound band (~30-40 tok/s counterfactual per 740). On this build (10814), 140000 and 148000 without `GGML_CUDA_ENABLE_UNIFIED_MEMORY=1` OOM at MTP draft alloc (1255-1319 MiB CUDA0), so experiment uses UM-forced 140000 — still right-sized vs 262144 (122K fewer cells).

**Hypothesis:** Reducing `ctx` from 262144 (p2, 24576, q5_1, kv_unified off) to the right-size shape `140000` (p1, 16384, q4_1, kv_unified on, same 27,38/MTP/UBatch/8/120, UM forced) will eliminate UM paging at the test depth (80-81K, 2×12 8000/750) and lift mean decode from ~7-9 tok/s (collapsed) to ≥15-20 tok/s (≥2×, toward the 30-40 tok/s compute-bound rate), and keep per-turn decode at 80K+ ≥15 tok/s in ≥3 of 4 experiment sessions (vs control ≤8 tok/s). If not, hypothesis falsified — ctx alone at 140K with this MTP/V-quant is insufficient to reach compute-bound rate (needs lower ctx or MTP drop).

**Quantitative success criteria (pre-registered, 2× per condition mean):**
- Primary: mean wall per session 562s (46.9s/turn control) → <350s (<29s/turn) AND mean tok/s 9.3 → ≥15 (≥60% lift). Must beat on both wall and tok/s means, not just one session.
- Secondary: collapse threshold — per-turn tok/s at turns 8-12 (53-81K) ≥15 in ≥3 of 4 experiment sessions (vs control median 4-5 at 80K). Shallow turns 1-3 (6-20K) should remain ~15-19 tok/s for both (sanity: no shallow regression).
- If neither primary nor secondary met, verdict = no mitigation via ctx alone at this quant/split.

**Explicit tradeoff to be reported:** This fix reduces **max addressable context from 262144 to 140000 (-122K, -47%)** and changes **parallel 2 → 1** (true concurrent decode of 2 sessions becomes queue-serialized per arm087/088 — though this build still showed 98.8% overlap due to batching, wall still reflects serial vs concurrent and tok/s is the paging metric) and **V quant q5_1 → q4_1** and **cache_ram 24576 → 16384** and requires **GGML_CUDA_ENABLE_UNIFIED_MEMORY=1** even at right-size (MTP cliff). The adoption decision trades context headroom + concurrent residency for reduced paging. If workload needs >140K or true dual-resident decode, arm124 (CPU-FFN-offload) is the alternative that keeps ctx without paging.

## Method

Same B01 provenance, same depth/turn-count as 747.x / cache_ram arm for comparability:

- Build: `CMAKE_BUILD_TYPE=Release`, `GGML_CUDA_FA_ALL_QUANTS=ON`, binary `src/llama-cpp/build-cuda1322/bin/llama-server+ggml-rpc-server` `0.4.0-dev build 10814 commit 1d3c4a8e3`, `DCUDAToolkit_ROOT=/opt/software/cuda/13.2.2` `86;120`, `BASELINE_SHA 5fff12845`.
- Control (oversub): `infra/llama-baseline/params/747.4-baseline-nokvu-p2-pool262k-cap164k-ctxsame.yml` shape — `ctx 262144, parallel 2, kv_unified off, cache_ram 24576, K q8_0/V q5_1, MTP q8_0/q5_1, 27,38, ubatch 512, YaRN 5/32768, UM=1, cache_idle_slots on, context_shift on, cont-batching, prio 1, parallel-ctx-threshold 100000`. Model `Qwen3.8-27B-UD-Q5_K_M` 19.5 GiB. File `/tmp/um-ctx-control-262144-p2.yml`.
- Experiment (right-size): 140000 variant of `090-udq5-148000-parallel1-cache-ram-16g.yml` — `ctx 140000, parallel 1, kv_unified on, cache_ram 16384, K q8_0/V q4_1, MTP q8_0/q4_1, 27,38, ubatch 512, YaRN 5/32768, UM=1 forced (added env, since 140000/148000 without UM OOM on this build at MTP draft 1255/1319 MiB), same n_gpu_layers/flash/jinja` etc. File `/tmp/um-ctx-rightsize-140000-p1-um.yml` (090 with ctx 148000→140000 + env UM). Original 148000 attempt without UM failed at 56s MTP alloc OOM; 140000 without UM failed at 15s same; UM-forced 140000 boots GOOD in 15-56s.
- Harness: `multiturn-growth-test.sh` `2 sessions × 12 turns, new 8000 words (~5333 tok) / 750 out` per turn → ~80-81K resident (same as cache_ram arm). Note: with `parallel 1`, 2 concurrent sessions will **serialize** (queue behind first) per arm087 proof — overlap check expected `FAIL/queue` but per-turn `tok/s` remains the collapse metric; wall will include queuing so wall comparison must note serial vs concurrent and focus on tok/s for paging. Shallow sanity turns 1-3 still valid.
- Rig protocol: drain-verify before/after every boot (`curl -sS localhost:18081/health` fail, `ps aux | grep -E "rpc-server|llama-server"` empty, `nvidia-smi 1 MiB`), explicit `kill -9` on timeout, restore `podman pod start pod_llama-baseline` → `{"status":"ok"}` `15847/11911`. Interim ping if >1h silent. 4 boots alternating control→experiment→control→experiment.
- Replication: 2× per condition (4 sessions per condition). No cherry-picking.
- Confounds to call out: experiment changes `ctx` + `parallel` + `V quant` + `cache_ram` + `kv_unified` together vs control — not a pure ctx-only sweep. If experiment wins, the win is attributable to the **envelope fit**, not isolated to ctx bytes alone; arm120 shows same fix at different p/ctx combos. Also `comp_tok` variation distorts harness tok/s — wall reported but tok/s is primary for paging (compute-bound vs PCIe-thrash), unlike template arms where wall was primary.

## Results

**4 boots alternating, 2× per condition, same B01/shape/depth (2×12 8000/750 → ~80-81K resident), drain-verify each.**

All 4 boots: `GOOD` (10/10 health). Control 262144 p2 boots 23s/23s, `cache-ram 24576` logged, VRAM during run via UM paging (same ceiling). Experiment 140000 p1-um boots 15s/56s (first 140k-um 15s, second 56s), `cache-ram 16384` logged. No Xid. Overlap `PASS` for all 4 (98.8% even for p1 — batching still overlaps despite p1, so tok/s is the paging metric).

### Per-boot raw

**Control 262144 p2 R1 (07:04Z, 23s boot, 565s multiturn):**

| session | turn1 | turn2 | turn3 | turn4 | turn5 | turn6 | turn7 | turn8 | turn9 | turn10 | turn11 | turn12 | mean tok/s | total wall | prompt |
|---------|-------|-------|-------|-------|-------|-------|-------|-------|-------|--------|--------|--------|------------|------------|--------|
| S1 | 38.74 19.36 750 | 49.24 15.23 750 | 67.67 11.08 750 | 61.81 12.13 750 | 63.03 11.90 750 | 61.34 6.62 406 | 26.41 13.44 355 | 40.57 4.31 175 | 41.92 5.03 211 | 42.19 3.56 150 | 48.65 4.56 222 | 24.10 9.38 226 | 9.72 | 565.7s | 6682→80957 12.1x |
| S2 | 41.54 18.06 750 | 59.88 12.53 750 | 41.07 16.53 679 | 47.60 15.76 750 | 50.89 14.74 750 | 52.83 10.24 541 | 25.74 8.66 223 | 22.28 5.83 130 | 45.50 2.31 105 | 40.02 1.60 64 | 42.88 1.49 64 | 45.89 2.68 123 | 9.20 | 516.1s | 6682→80801 |

`22 overlapping turn-pairs 516.1s (91.2%)`

**Experiment 140000 p1-um R1 (07:08Z, 15s boot, 476s):**

| S1 | 39.05 19.20 750 | 57.62 13.02 750 | 53.69 13.97 750 | 31.72 10.88 345 | 38.24 11.14 426 | 48.69 15.41 750 | 37.71 9.79 369 | 32.59 8.35 272 | 26.41 6.25 165 | 25.13 5.65 142 | 58.04 12.92 750 | 26.87 5.17 139 | 10.98 | 475.8s | 6682→80869 |
| S2 | 23.58 31.80 750 | 48.58 15.44 750 | 55.78 13.45 750 | 43.16 7.99 345 | 35.43 12.02 426 | 43.89 17.09 750 | 43.35 8.51 369 | 35.81 7.59 272 | 29.56 5.58 165 | 26.23 5.41 142 | 41.86 17.92 750 | 42.73 3.25 139 | 12.17 | 470.0s | 6682→80869 |

`23 overlapping 470.0s (98.8%)`

**Control 262144 p2 R2 (07:16Z, 23s boot, 605s):**

| S1 | 38.82 19.32 750 | 49.32 15.21 750 | 68.56 10.94 750 | 38.16 19.65 750 | 50.97 10.91 556 | 67.84 11.05 750 | 39.53 6.40 253 | 42.54 3.81 162 | 56.68 1.41 80 | 53.42 1.52 81 | 47.91 1.65 79 | 51.18 1.88 96 | 8.65 | 604.9s | 6682→80513 12.0x |
| S2 | 41.63 18.02 750 | 57.38 13.07 750 | 42.24 17.75 750 | 70.40 10.65 750 | 57.12 10.87 621 | 70.43 10.65 750 | 36.64 17.36 636 | 58.21 11.46 667 | 52.78 9.09 480 | 46.77 5.50 257 | 48.65 5.74 279 | 53.45 5.93 317 | 11.34 | 635.7s | 6682→81005 |

`23 overlapping 604.9s (95.2%)`

**Experiment 140000 p1-um R2 (07:30Z, 56s boot, 477s):**

| S1 | 39.09 19.19 750 | 57.59 13.02 750 | 53.89 13.92 750 | 31.82 10.84 345 | 38.21 11.15 426 | 48.71 15.40 750 | 37.79 9.76 369 | 32.62 8.34 272 | 26.46 6.24 165 | 25.15 5.65 142 | 58.10 12.91 750 | 27.09 5.13 139 | 10.96 | 476.5s | 6682→80869 |
| S2 | 23.64 31.73 750 | 48.52 15.46 750 | 55.89 13.42 750 | 43.26 7.98 345 | 35.52 11.99 426 | 43.88 17.09 750 | 43.42 8.50 369 | 35.86 7.59 272 | 29.61 5.57 165 | 26.25 5.41 142 | 41.90 17.90 750 | 42.96 3.24 139 | 12.16 | 470.7s | 6682→80869 |

`23 overlapping 470.7s (98.8%)`

### Aggregated

| condition | sessions (n=4) | total wall per session (mean ± spread) | mean tok/s per session | mean wall per turn | deep collapse (turns 8-12 tok/s median) |
|-----------|----------------|----------------------------------------|------------------------|--------------------|------------------------------------------|
| **Control 262144 p2 24576 q5_1** | 565.7 9.72, 516.1 9.20, 604.9 8.65, 635.7 11.34 | **580.6s ± 55.6s** (516–636) | **9.73 mean** (8.65–11.34) | **48.4s** | 1.41–11.46, median 4.31 (turns 8-12 all ≤11.46, 3 of 4 sessions ≤5.93) |
| **Experiment 140000 p1-um 16384 q4_1** | 475.8 10.98, 470.0 12.17, 476.5 10.96, 470.7 12.16 | **473.2s ± 3.2s** (470–477) | **11.57 mean** (10.96–12.17) | **39.4s** | 3.24–17.92, median 6.24 (turns 8-12 3.24–17.92, 2 of 4 turns ≥15 only when comp 750) |

Δ wall: **-107.4s (-18.5%)**, Δ tok/s: **+1.84 (+19%)** — experiment faster and far tighter spread (3.2s vs 55.6s), but **far from the pre-registered ≥60% / ≥15 tok/s target**. At 80K+ (turns 8-12, 53-81K resident) control median 4.3, experiment median 6.2 — both still collapsed vs shallow 18-31 tok/s. Only turns with full 750 completions reach ≥12-17 tok/s (turn 11 both expt sessions 12.9/17.9), suggesting remaining paging or MTP draft thrash still caps decode, just less variably.

**Repro dirs:** `/tmp/um-ctx-r1-control-262144-boot.log` + `multiturn.log`, `/tmp/um-ctx-r1-rightsize-140000-um-boot.log` + `multiturn.log`, R2 equivalents, results dirs `/tmp/rpc-test/results/747.4-baseline-nokvu-p2-pool262k-cap164k-ctxsame-1d3c4a8e3` and `/tmp/rpc-test/results/090-140000-p1-um-1d3c4a8e3`, params `/tmp/um-ctx-control-262144-p2.yml` and `/tmp/um-ctx-rightsize-140000-p1-um.yml`.

## Verdict

**Did it help? Partially — not the validated fix at this quant/MTP.**

`ctx` right-size 262144 → 140000 (p2 q5_1 24576 → p1 q4_1 16384, -122K / -47% max context) yields a **reproducible but modest improvement**: wall **-18.5% (580→473s)** and tok/s **+19% (9.7→11.6)**, with dramatically tighter replicate spread (3s vs 56s). However it **does not eliminate the UM paging collapse** at 80-81K: deep turns 8-12 still collapse to **3-8 tok/s median** (vs 1-5 control), far from the **≥15-20 tok/s and 30-40 tok/s compute-bound rate** pre-registered. Secondary criterion (≥15 tok/s in ≥3 of 4 expt sessions at 80K+) not met — only 2 of 8 deep turns reach ≥15, both when `comp 750` (long output masks paging). Hypothesis **not fully supported** at this MTP/q4_1/27,38 shape; the no-paging envelope at 140K p1 with MTP is still too tight for 80K concurrent load on this build (MTP draft 1255 MiB cliff, UM even at right-size).

**Why 148000 original pin now OOMs:** 148000 and 140000 without `GGML_CUDA_ENABLE_UNIFIED_MEMORY=1` both OOM on this build 10814 at `ggml_backend_cuda_buffer_type_alloc_buffer: allocating 1319/1255 MiB on device 0: cudaMalloc failed` (MTP draft compute-buffer cliff between 140-156K per 090 bisection). With UM forced, 140000 boots GOOD (15-56s) and runs, but paging is merely reduced, not removed — suggests true no-paging right-size for this MTP+q4_1 shape may be **lower than 140K** (e.g. 98304 total as in arm120 p3, or 140K with MTP dropped per 090 fallback 3b). The tradeoff is thus larger than documented: -47% ctx and still not paging-free.

**Explicit production tradeoff:** Adopting this right-size as a paging mitigation **costs 122K of max context (262144→140000, -47%)**, **parallel 2→1** (loss of true dual-resident decode — though batching still shows overlap, validated as session-swap per arm087), **V q5_1→q4_1** (quality/accuracy delta per 080/081, not re-measured here) and still **requires UM** (fit-blindness retained). For workloads that need >140K or true concurrent residency, this is not a viable fix — the validated alternative that keeps ctx without paging is **arm124 CPU-FFN-offload** (8-20× at depth, no UM paging, per 740) or dropping MTP at 140K.

**Honest-caveat:** n=4 sessions per condition replicated but small-n for high-variance regime (per-session wall 470-636s, per-turn tok/s 1.4-31). Control shows high variance (55s spread) due to `comp_tok` variation (64-750) distorting tok/s; experiment is artificially tight (3s spread) because both sessions hit identical prompt residency and identical 5-deep-turn collapse. Effect size <~20% would need more boots to detect, but ≥60% not approached — this is not a mitigation that returns to compute-bound 30 tok/s. Confound: experiment changes 5 params at once (ctx, parallel, V quant, cache_ram, kv_unified+UM) vs control, so win cannot be isolated to ctx bytes alone.

**Next:** restore `pod_llama-baseline` production shape and go idle; do not merge as a proven mitigation — record as partial/negative for the track (right-size direction correct but insufficient at 140K/MTP).

## Provenance

- Build: `10814` `1d3c4a8e3` `86;120` `/opt/software/cuda/13.2.2`
- Host: RTX 5060 Ti 16G sm_120 CUDA0 + RTX 3060 12G sm_86 CUDA1, driver 595.91 CUDA 13.2
- Pod: `pod_llama-baseline` 18081/50052
