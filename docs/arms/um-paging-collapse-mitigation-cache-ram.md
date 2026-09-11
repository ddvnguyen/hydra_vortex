# UM Paging Collapse Mitigation — cache_ram_mib Lever (24576 → 8192)

## Lever Choice & Hypothesis

**Lever:** `cache_ram_mib` — control `24576` (production family 747.x) vs experiment `8192` (3× reduction, still ≥2× per-session KV at ~80K).

**Why this lever:** All three candidate levers have prior null evidence at this oversub+UM shape:
- `cache_ram` 1024 vs 24576 both collapsed to ~7.2 tok/s in `arm-ud-q5ks-vs-q5km` Round1 deep (Q5_K_S, 262K/p2).
- `tensor_split` 25,40 / 30,35 showed no diff in arms 106/107 at 102 shape (438K/p3).
- `kv_unified` on/off showed no diff in arm 747.4 / 121-123 at 262K/p2 (both collapsed 0.9-2.7 tok/s).

`cache_ram` is the only lever that directly touches UM managed-memory pressure without changing VRAM sharding or slot fencing. `cache_ram_mib` reserves pinned host RAM for idle KV slots (`--cache-ram` → `mmap`/`mlock` backing for `--cache-idle-slots`). At `24576` (24 GiB), the host reserves ~24 GiB for KV swap, which under `GGML_CUDA_ENABLE_UNIFIED_MEMORY=1` competes with `cudaMallocManaged` migration (managed memory pages migrate between host RAM and GPU VRAM on demand). Large pinned reservation may increase fragmentation / migration overhead when both slots hold ~80-103K KV (~0.8 GiB per slot active + ~4-5 GiB per idle-swapped entry at this quant). Lowering to `8192` (8 GiB) still fits ~2× 80K entries (sizing rule: ~4-4.8 GiB per 110-120K entry, so 8 GiB fits 1.5-2 entries) but frees ~16 GiB host RAM, potentially reducing UM thrashing.

**Hypothesis:** Reducing `cache_ram_mib` from 24576 → 8192 will reduce UM paging overhead at deep context (80-103K, 2×12 8000/750) and lift mean decode from ~7.3 tok/s toward ≥9.5 tok/s (≥30% improvement) or shift collapse threshold from ~61-88K (§3430) outward to ≥100K. If no improvement, hypothesis is falsified — `cache_ram` is not the causal variable for this collapse (consistent with prior 1024 vs 24576 null).

**Quantitative success criteria (pre-registered, must beat on 2× per-condition mean):**
- Primary: mean decode at 80-103K `tok/s` 7.3 → ≥9.5 (≥30% lift) AND wall-clock per-turn 522s → <400s.
- Secondary: collapse not earlier — per-turn decode at 80K+ stays ≥9 tok/s in ≥3 of 4 experiment turns (vs control 3-5 tok/s at 80K+).
- If neither met, verdict = no mitigation (honest-caveat: cache_ram ruled out, validated fixes remain arm124 CPU-FFN-offload or arm120 right-size ctx per §3430).

## Method

Same B01 provenance check, same depth/turn-count as 747.x / Q5_K_S deep for comparability:

- Model: `Qwen3.8-27B-UD-Q5_K_M` `Q5_K_M` (19.5 GiB), `tensor_split 27,38`, `K q8_0 / V q5_1` + `draft-mtp q8_0/q5_1`, `n_gpu_layers 99`, `flash_attn on`, `ubatch 512`, `YaRN yarn 5/32768`, `UM=1`.
- Shape: `ctx 262144 = 2×131072 hard-fenced (kv_unified off, pool 262K)`, `parallel 2`, `cache_ram` varies, `cache_reuse 64`, `cache_idle_slots on`, `context_shift on`, `parallel-ctx-threshold 100000`, `cont-batching on`.
- Build: `CMAKE_BUILD_TYPE=Release`, `GGML_CUDA_FA_ALL_QUANTS=ON`, binary `src/llama-cpp/build-cuda1322/bin/llama-server+ggml-rpc-server` `0.4.0-dev build 10814 commit 1d3c4a8e3`, `DCUDAToolkit_ROOT=/opt/software/cuda/13.2.2` `86;120`, `BASELINE_SHA 5fff12845`.
- Harness: `multiturn-growth-test.sh` (`4227f78c3`) `2 sessions × 12 turns, new 8000 words (~5333 tok) / 750 out` per turn → ~80-103K depth, same overlap/cached_tokens accounting as Round1/747.x. Also `head -c 2000 /tmp/bigprompt.txt` sanity pre-check not required for this arm but will log VRAM ceiling.
- Rig protocol: drain-verify before/after every boot (`curl -sS localhost:18081/health` fail, `ps aux | grep -E "rpc-server|llama-server"` empty, `nvidia-smi --query-gpu=memory.used --format=csv` `1 MiB`), explicit kill on timeout, restore `podman pod start pod_llama-baseline` check `{"status":"ok"}` + `15847/11911` ceiling. Interim ping if >1h silent.
- Replication: 2× per condition, alternating boots (control, experiment, control, experiment) to control for host drift. Boot log must show `cache-ram` value and `1 MiB` pre-boot VRAM. No cherry-picking.
- Confounds to call out: `cache_ram` does not change GPU VRAM pool size (only host RAM pinning); at 2-session rotation both 8192 and 24576 fit ≥1 idle entry, so cache-hit difference expected minimal; any wall-clock diff must be attributed to UM migration, not cache-hit. Also note 1024 vs 24576 prior null suggests effect size likely <30%.

Params files:
- Control: `/tmp/um-cache-ram-24576-p2.yml` (copy of `747.4-baseline-nokvu-p2-pool262k-cap164k-ctxsame.yml` with `cache_ram_mib: 24576`)
- Experiment: `/tmp/um-cache-ram-8192-p2.yml` (same but `cache_ram_mib: 8192`)

## Results

**4 boots alternating, 2× per condition, same B01/shape/depth (2×12 8000/750 → ~80-81K resident), drain-verify each.**

All 4 boots: `GOOD` (10/10 health), `llama-server ready 64-65s`, `--cache-ram` logged per params (24576 vs 8192), VRAM during run `15847/11911` via UM paging (same ceiling), `concurrency PASS` — no Xid.

### Per-boot raw

**Control 24576 R1 (06:02Z, 65s boot, 567s multiturn):**

| session | turn1 | turn2 | turn3 | turn4 | turn5 | turn6 | turn7 | turn8 | turn9 | turn10 | turn11 | turn12 | mean tok/s | total wall | prompt |
|---------|-------|-------|-------|-------|-------|-------|-------|-------|-------|--------|--------|--------|------------|------------|--------|
| S1 | 41.49 18.08 750 | 57.33 13.08 750 | 42.84 17.51 750 | 40.58 10.35 420 | 44.63 9.68 432 | 35.56 11.67 415 | 40.97 7.00 287 | 21.56 3.90 84 | 29.49 3.36 99 | 51.55 2.25 116 | 47.29 2.09 99 | 46.21 2.16 100 | 8.43 | 499.5s | 6682→80975 12.1x |
| S2 | 38.68 19.39 750 | 49.30 15.21 750 | 69.01 10.87 750 | 49.56 15.13 750 | 77.82 9.64 750 | 51.39 14.60 750 | 49.75 10.09 502 | 46.69 6.92 323 | 45.15 4.39 198 | 46.85 3.65 171 | 22.33 7.39 165 | 19.62 4.95 97 | 10.18 | 566.1s | 6682→81126 |

`21 overlapping turn-pairs 499.5s (88.2%), mean 8.43/10.18`

**Experiment 8192 R1 (06:15Z, 64s boot, 547s):**

| S1 | 38.74 19.36 750 | 49.21 15.24 750 | 68.55 10.94 750 | 49.19 15.25 750 | 53.24 10.76 573 | 82.93 9.04 750 | 41.01 5.22 214 | 42.11 4.94 208 | 39.20 2.55 100 | 42.72 2.81 120 | 19.38 5.26 102 | 20.80 6.20 129 | 8.96 | 547.1s | 6682→80932 |
| S2 | 41.54 18.05 750 | 57.27 13.10 750 | 42.23 17.76 750 | 39.84 10.39 414 | 51.80 14.48 750 | 39.81 7.54 300 | 34.87 10.47 365 | 27.69 7.58 210 | 25.76 3.84 99 | 42.02 2.33 98 | 42.08 1.47 62 | 39.54 1.59 63 | 9.05 | 484.4s | 6682→80680 |

`21 overlapping 484.5s (88.5%)`

**Control 24576 R2 (06:26Z, 64s boot, 604s):**

| S1 | 38.74 19.36 750 | 49.30 15.21 750 | 68.13 11.01 750 | 28.00 12.96 363 | 74.01 10.13 750 | 55.82 9.53 532 | 30.09 12.06 363 | 46.11 3.82 176 | 55.79 4.95 276 | 54.45 4.79 261 | 50.00 2.54 127 | 54.21 2.84 154 | 9.10 | 604.6s | 6682→80687 |
| S2 | 41.54 18.06 750 | 58.38 12.85 750 | 39.82 15.74 627 | 44.52 10.78 480 | 63.04 11.90 750 | 45.07 8.23 371 | 43.00 7.09 305 | 33.64 8.00 269 | 51.72 7.21 373 | 53.21 5.68 302 | 54.22 5.07 275 | 52.10 5.53 288 | 9.68 | 580.3s | 6682→81023 |

`23 overlapping 580.3s (96.0%)`

**Experiment 8192 R2 (06:38Z, 64s boot, 586s):**

| S1 | 38.78 19.34 750 | 52.74 14.22 750 | 67.49 11.11 750 | 45.17 11.36 513 | 42.70 15.60 666 | 66.96 11.20 750 | 27.64 14.22 393 | 43.98 5.55 244 | 51.28 7.41 380 | 51.03 4.62 236 | 46.56 4.23 197 | 51.67 2.94 152 | 10.15 | 586.0s | 6682→81435 |
| S2 | 41.59 18.03 750 | 62.58 11.98 750 | 40.39 15.80 638 | 47.65 15.74 750 | 70.95 9.15 649 | 33.20 11.02 366 | 33.59 4.23 142 | 32.14 5.57 179 | 46.25 4.78 221 | 53.07 4.09 217 | 47.63 2.12 101 | 52.98 3.85 204 | 8.86 | 562.0s | 6682→80993 |

`23 overlapping 562.0s (95.9%)`

### Aggregated — cache_ram shows no mitigation

| condition | sessions (n=4) | total wall per session (mean ± spread) | mean tok/s per session | mean wall per turn | deep collapse (turns 8-12 tok/s) |
|-----------|----------------|----------------------------------------|------------------------|--------------------|-----------------------------------|
| **Control 24576** | S1 499.5 8.43, S2 566.1 10.18, S1 604.6 9.10, S2 580.3 9.68 | **562.6s ± 52.6s** (499–605) | **9.35 mean** (8.43–10.18) | **46.9s** | 5/5 turns collapsed ≤8.0, median 4.79 at 80K+ (3.90→2.16, 6.92→4.95, etc.) |
| **Experiment 8192** | S1 547.1 8.96, S2 484.4 9.05, S1 586.0 10.15, S2 562.0 8.86 | **544.9s ± 50.8s** (484–586) | **9.26 mean** (8.86–10.15) | **45.4s** | 5/5 turns collapsed ≤7.58, median 4.23 (4.94→1.59, 5.22→6.20, etc.) |

Δ wall: **-17.8s (-3.2%)**, Δ tok/s: **-0.09 (-1%)** — well within per-boot ±~50s replicate noise (499–605 spread). No threshold shift: collapse still at ~61-88K (§3430) — turns 7 (47K) already 5-14 tok/s, turns 8-12 (53-81K) 1.5-8.0 tok/s at both cache_ram values. Overlap 88-96% PASS both, so not a concurrency artifact.

**Repro dirs:** `/tmp/um-r1-control-24576-boot.log` + `multiturn.log`, `/tmp/um-r1-experiment-8192-*`, `/tmp/um-r2-*`, results dir `/tmp/rpc-test/results/747.4-baseline-nokvu-p2-pool262k-cap164k-ctxsame-1d3c4a8e3`, params `/tmp/um-cache-ram-*.yml`.

## Verdict

**Did it help? No. Hypothesis falsified at 2× per condition replication.**

`cache_ram_mib` 24576 → 8192 does **not** mitigate UM paging decode collapse at 80-103K (2×12) for this oversub+UM shape (262144 ctx, p2, hard-fenced 131072/slot, 27,38/q8_0/q5_1 MTP). Mean wall -3.2% and tok/s -1% are null — same collapse magnitude (2-7 tok/s at depth, vs 18-19 tok/s shallow) and same 499-605s total span. This reproduces the prior `arm-ud` 1024 vs 24576 null (both ~7.2 tok/s) at controlled replication with identical model/quant/split, and aligns with the `§3430` ceiling (61-88K no-paging fit) — `cache_ram` only pins host RAM for idle slots, not GPU VRAM pool, so it cannot move the VRAM oversubscription cliff.

**Safe for prod?** Not a mitigation; also not a degradation — 8192 still fits ~2× 80K entries (sizing rule 4-4.8 GiB per 110K entry → 8 GiB holds 1.5-2, 24 GiB holds 5-6) so for 2-session rotation behavior is indistinguishable (no extra evictions observed, overlap 88-96% both). But there is **no benefit** to justify switching the 747-family prod from 24576 to 8192, and lowering further would risk evictions at 3-session rotation (arm090 needed 16 GiB for 3× 110K). Keep production `cache_ram` at its validated pin (090: 16384, 747-family: 24576) — this lever is ruled out.

**Honest-caveat:** n=4 sessions per condition is replicated but small-n for a high-variance collapse regime (per-turn tok/s 1.5-19, per-session wall 484-605s). Effect size <~10% would need more boots to detect, but ≥30% success criterion was not approached; a subtle <10% effect cannot be excluded but is not a *mitigation* for the 14× collapse. Also `comp_tok` variation (62-750) distorts harness tok/s — wall is the decision metric here; tok/s is reported only for comparability with prior arms.

**Validated fixes remain:** arm124 (CPU-FFN-offload, 8-20× at depth by removing UM paging entirely) or arm120 (right-size ctx to fit VRAM, e.g. 140-148K pin per arm088/090, ceiling 61-88K no-paging) or revert to arm090 production pin — per `docs/investigations/740-results-report.md` §3542-3563 and this arm's replication, `cache_ram` / `kv_unified` / `tensor_split ±5` do not move the cliff at this oversub.

**Next:** restore `pod_llama-baseline` production shape and go idle; do not merge this doc as a mitigation — it is a negative-result record for the track.

## Provenance

- Build: `10814` `1d3c4a8e3` `86;120` `/opt/software/cuda/13.2.2`
- Host: RTX 5060 Ti 16G sm_120 CUDA0 + RTX 3060 12G sm_86 CUDA1, driver 595.91, CUDA 13.2
- Pod: `pod_llama-baseline` `18081/50052`
