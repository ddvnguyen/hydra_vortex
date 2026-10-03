# Fork `--profile-decode` run - RTX 3060 decode-gap attribution (T6 / T12)

Date: 2026-10-02
Branch: `docs/811-profile-decode` (off `docs/811-impl-gap`)
Task: owner-authorized split of the ~34 ms/token decode gap (fork vs Strata @4K)
using the in-source `--profile-decode` profiler, two sequential thread-count
variants (`-t 6`, then `-t 12`).

## TL;DR / verdict

- Both arms ran clean end-to-end (`status=ok`), H2D gate 6.12 GB/s before and
  after each run, `link_bad_samples=0`, rig left clean (no llama-server, GPU1 = 1 MiB).
- Profiled steady-state decode: **T6 = 73.1 ms/tok, T12 = 71.3 ms/tok**
  (PROF window medians, win>=2). Strata baseline @4K = 30.9 ms/tok.
- **Thread-count item (IMPL-GAP #3): worth only ~1.8 ms/tok** (73.06 -> 71.27
  going `-t 6` -> `-t 12`), vs the 5-15 ms arithmetic estimate. NOT the
  primary cause of the 34 ms gap.
- **Measured output/sampling sync region (`t_sync_ms`): ~0.005 ms/tok - zero factor.**
- **PCIe during decode: rx = tx = 0 MiB/s in every window** (NVML counters) -
  no per-step PCIe contribution in this single-host config.
- The remaining **~32-33 ms of the 34 ms gap is UNATTRIBUTED as one bucket**
  by PROF fields alone: CPU-MoE compute, ~96-97 split-boundary
  `cudaStreamSynchronize` + copies, and GPU-idle interleaved between splits are
  all inside `t_wall_ms` and are not separable by any PROF field. Structural
  cross-check (splits count) remains `[hypothesis]` per `IMPL-GAP-FORK.md`;
  splitting this bucket further needs per-kernel timing (nsight systems), not
  `--profile-decode`.
- Caveat: the profiler itself costs **+7.7 ms/tok vs the un-profiled t0006 F0
  baseline** (T6 steady 73.1 vs 65.4 ms/tok; instrumentation-vs-variance split
  UNATTRIBUTED). Gap accounting below uses the un-profiled baseline; the
  T6-vs-T12 comparison is unaffected (both profiled).

## 1. Method

### Rig / binary / guards

- RTX 3060 only: `CUDA_VISIBLE_DEVICES=1 CUDA_DEVICE_ORDER=PCI_BUS_ID`, port
  8093 (8091 never touched), one GPU one task, runs strictly sequential.
- Binary: `.local/q2g/src/build/bin/llama-server` (Sep 28, `--help` confirms
  `--profile-decode`), `LD_LIBRARY_PATH=build/bin:/opt/software/cuda/13.2.1/lib64`.
- Harness: `.local/strata-research/arm-profile.sh` (reuses t0006
  `sample_link.sh`, `bench3060.py`, `parse_arm_logs.py`; stderr captured in
  full to the per-arm engine log; exact-pid kills only).
- Pre-gates: rig idle before start; MemAvailable >= 67 GiB;
  H2D pinned probe `.local/pcie-probe/h2d` before AND after each arm
  (expected ~6.1 GB/s).

### Commands (F0-exact + `--profile-decode`)

T6 (t0006 F0 cmdline + profiler):

```
-m /mnt/SSD/strata-models/GSQ-RCO/IQ3_S/Qwen3.8-Flash-Next-GSQ-RCO-IQ3_S-00001-of-00002.gguf
--split-mode layer -fit off -ngl 99 --n-cpu-moe 99
--override-tensor per_layer_token_embd=CPU --moe-expert-cache-size 0
-c 16384 --parallel 1 --flash-attn on --jinja -t 6
--experimental-logs --load-mode none --ple-prefetch -b 2048 -ub 2048
--host 127.0.0.1 --port 8093 --alias gsq-rco-iq3_s --spec-type none
--profile-decode
```

T12: identical with `-t 12`. No `--decode-overlap` in either arm.

### Bench protocol (t0006-identical)

1. warmup: 1 request (`prompts/warmup.txt`, sha `ad920c19...`)
2. p4k prompt (`prompts/p4k.txt`, 4068 tok, sha
   `fa13b451d5825828ddb2a2c2f1020c4b7b3e2eec8a02d5a69062115f86e310e7` -
   byte-identical to t0006)
3. 3 measured decodes, `--reps 3 --max-tokens 256 --warmup`

### PROF field semantics (read from source before interpreting)

| Field | Exact meaning | Source |
|---|---|---|
| `t_wall_ms` | `CLOCK_MONOTONIC` delta of ONE `llama_decode()` call, summed over the 64-step window | `llama-context.cpp:3559,4014`, window at `:807-852`, `LLAMA_PROF_WIN_STEPS=64` `:739` |
| `t_cpu_ms` | `CLOCK_PROCESS_CPUTIME_ID` delta (ALL threads aggregate) over the same interval. **Includes thread-pool spin - not pure useful CPU compute** (proven by T12: +58% aggregate cpu at unchanged wall) | `llama-context.cpp:3560,4015` |
| `t_dev_ms` | `cudaEventElapsedTime(start,end)` on the CUDA stream: start event before `sched_graph_compute_async`, end event after enqueue. = device-timeline SPAN, **includes GPU-idle gaps while CPU backend work runs between splits. NOT GPU-busy time.** Waited via `cudaEventSynchronize(end)` per decode | `llama-context.cpp:4527-4529,4606-4608`; `ggml-cuda.cu:7089-7160` |
| `t_sync_ms` | wall of the output-extraction region only (logits get + async sampling copies), between graph finish and decode end | `llama-context.cpp:3844-3950` |
| `PROF prefill` | first batch with `n_tokens > 256` (its own line, excluded from windows) | `llama-context.cpp:4016-4023` |
| `PROF summary` | median over the per-window lines (not a total) | `llama-context.cpp:877-885` |
| `rx_mbs/tx_mbs` | NVML PCIe throughput counters delta over the window (counts, not ms) | `llama-context.cpp:754-761,841-844` |

Not covered by any field: the per-split `cudaStreamSynchronize` + blocking
copies inside ggml-backend-sched execution. Anything the table cannot assign =
**UNATTRIBUTED**, no guessing.

## 2. Raw results

### Bench (`arms/T6-1.bench.json`, `arms/T12-1.bench.json`)

| arm | warmup tok/s | rep0 / rep1 / rep2 decode tok/s | decode median | prefill rep0/1/2 tok/s (cold, warm, warm) |
|---|---|---|---|---|
| T6-1 | 14.06 | 13.58 / 13.84 / 12.33 | **13.58** | 201.81 / 16476.0 / 15965.12 |
| T12-1 | 14.77 | 14.16 / 13.75 / 13.83 | **13.83** | 202.68 / 16410.36 / 15781.61 |

All `err=None`, every rep `n=256`, align match in both logparse.json.

### Gates / health (logs/*.summary.txt, *.h2d.txt, *.link.tsv)

| check | T6-1 | T12-1 |
|---|---|---|
| H2D pre (GB/s) | 6.12 / 6.12 | 6.12 / 6.12 |
| H2D post (GB/s) | 6.12 / 6.12 | 6.11 / 6.16 |
| link_bad_samples | 0 | 0 |
| VRAM after load (MiB) | 6271 | 6271 |
| mem avail after (GiB) | 74.3 | 75.6 |
| PROF lines | 15 (13 win + 1 prefill + 1 summary) | 15 |
| arm verdict | `status=ok bench_rc=0 parse_rc=0` | `status=ok bench_rc=0 parse_rc=0` |

### Per-window PROF lines (transcribed; raw files: `arms/T6-1.prof.txt`, `arms/T12-1.prof.txt`)

T6-1 (`-t 6`), one line per 64 decode steps:

| win | tps | t_wall_ms | t_cpu_ms | t_dev_ms | t_sync_ms | note |
|---|---|---|---|---|---|---|
| 0 | 6.67 | 9290.3 | 20769.7 | 9268.2 | 0.30 | startup (graph capture) |
| 1 | 4.33 | 14549.4 | 25724.4 | 14526.8 | 0.20 | startup (first decode after 4K prompt) |
| 2 | 13.55 | 4723.2 | 16265.9 | 4715.5 | 0.30 | steady |
| 3 | 13.60 | 4705.4 | 16118.3 | 4697.7 | 0.30 | steady |
| 4 | 13.72 | 4665.1 | 16030.2 | 4654.2 | 0.30 | steady |
| 5 | 13.69 | 4676.0 | 16317.6 | 4660.2 | 0.30 | steady |
| 6 | 14.15 | 4522.7 | 15678.0 | 4515.2 | 0.30 | steady |
| 7 | 14.18 | 4514.2 | 15798.2 | 4506.5 | 0.30 | steady |
| 8 | 14.16 | 4518.6 | 15735.6 | 4507.8 | 0.30 | steady |
| 9 | 11.98 | 5340.4 | 17841.5 | 5324.3 | 0.30 | dip (median-robust) |
| 10 | 12.09 | 5294.0 | 18301.2 | 5285.7 | 0.30 | dip |
| 11 | 12.24 | 5230.7 | 17703.7 | 5222.4 | 0.30 | dip |
| 12 | 13.82 | 4632.6 | 15860.8 | 4621.4 | 0.30 | steady |
| summary | 13.60 | 4705.4 | 16265.9 | 4697.7 | 0.30 | = median over windows |

`PROF prefill`: n_tokens=2029, tps=205.82, wall 9857.9, cpu 9638.9, dev 9846.1.

T12-1 (`-t 12`):

| win | tps | t_wall_ms | t_cpu_ms | t_dev_ms | t_sync_ms | note |
|---|---|---|---|---|---|---|
| 0 | 6.86 | 9035.2 | 30337.8 | 9013.1 | 0.30 | startup |
| 1 | 4.39 | 14335.8 | 35211.0 | 14312.3 | 0.30 | startup |
| 2 | 14.46 | 4426.2 | 24805.0 | 4418.4 | 0.30 | steady |
| 3 | 13.86 | 4618.7 | 25678.5 | 4610.4 | 0.30 | steady |
| 4 | 14.41 | 4439.8 | 24621.2 | 4428.6 | 0.30 | steady |
| 5 | 13.53 | 4728.9 | 26419.9 | 4711.8 | 0.30 | steady |
| 6 | 13.82 | 4632.5 | 25463.7 | 4624.0 | 0.30 | steady |
| 7 | 14.58 | 4388.4 | 24952.3 | 4380.2 | 0.30 | steady |
| 8 | 14.03 | 4561.3 | 25680.2 | 4549.6 | 0.30 | steady |
| 9 | 13.84 | 4623.4 | 25865.6 | 4606.8 | 0.30 | steady |
| 10 | 14.56 | 4395.2 | 24848.8 | 4387.1 | 0.30 | steady |
| 11 | 14.10 | 4539.7 | 25352.6 | 4531.5 | 0.30 | steady |
| 12 | 13.59 | 4708.7 | 26233.5 | 4696.7 | 0.30 | steady |
| summary | 13.86 | 4618.7 | 25678.5 | 4606.8 | 0.30 | = median over windows |

`PROF prefill`: n_tokens=2029, tps=207.64, wall 9771.8, cpu 9628.2, dev 9760.6.

Both arms: `rx_mbs=0 tx_mbs=0` in every window and `bytes_per_step_mib=0.0`
(no per-step PCIe traffic during decode).

## 3. Medians and ms/token breakdown

Steady-state = windows 2-12 (n=11), startup windows 0-1 excluded
(graph capture / first-decode-after-4K-prompt: 9-14.5 s per 64-step window).

| metric | T6-1 steady median | T12-1 steady median | T6 vs T12 |
|---|---|---|---|
| window tps | 13.69 | 14.03 | +2.5% |
| wall ms/step | **73.06** | **71.27** | **-1.79 ms (-2.4%)** |
| cpu ms/step (aggregate) | 251.85 | 397.87 | +58% |
| cpu/wall (busy core-equivalents) | 3.45 (of 6) | 5.58 (of 12) | - |
| dev span ms/step | 72.82 | 71.09 | -1.73 |
| dev/wall | 0.997 | 0.997 | - |
| sync region ms/step | 0.0047 | 0.0047 | 0 |
| bench decode median (tok/s) | 13.58 | 13.83 | +1.8% |

Reading of `cpu/wall`: aggregate process CPU is 3.45 core-equivalents at `-t 6`
and 5.58 at `-t 12`. Doubling threads raised aggregate CPU +58% while wall
barely moved -> the extra aggregate is thread-pool spin, and the wall is NOT
thread-starved. `t_cpu` must not be read as "CPU compute ms".

## 4. Gap attribution verdict (vs the 34 ms)

Baseline arithmetic (un-profiled, both @4K, n=1): fork t0006 F0 = 65.4 ms/tok
(15.3 tok/s), Strata = 30.9 ms/tok (32.4 tok/s) -> gap 34.5 ms.

| # | Bucket (IMPL-GAP item) | Predicted ms/tok | Measured ms/tok | Verdict |
|---|---|---|---|---|
| 1 | Thread count `-t 6` -> `-t 12` (item 3) | 5-15 `[arithmetic]` | **1.8** (PROF steady), 1.3 (bench) | **REJECTED as primary - worth ~2 ms**, 12 threads not starved (spin evidence) |
| 2 | Output/logits sync region (`t_sync_ms`) | part of "sync" | **0.0047** | Zero factor |
| 3 | Per-step PCIe during decode | (IMPL-GAP: counters are counts) | **0** (rx=tx=0 all windows) | Zero factor in this config |
| 4 | Split-boundary `cudaStreamSynchronize` + copies (~96-97/step, item 2) | 10-25 `[hypothesis]` | not timed by any PROF field | **UNATTRIBUTED** (inside `t_wall_ms`) |
| 5 | CPU-MoE compute on critical path (item 1 decode row) | decode CPU ~25-40 `[arithmetic]` | only aggregate upper bound (incl. spin) | **UNATTRIBUTED** as exact ms; CPU side visibly engaged (cpu/wall 3.45-5.58) but not separable |
| 6 | GPU-busy vs GPU-idle inside dev span | - | dev/wall = 0.997 (span covers wall; idle hidden inside) | **UNATTRIBUTED** - needs per-kernel timing (nsight systems), not PROF |
| 7 | Residual (items 4+5+6 combined) | 35-65 (items 1+2) | **~32.7 of 34.5** after removing thread effect | **DOMINANT bucket**; PROF cannot split it further |

Conclusion for IMPL-GAP items:
- **#3 (threads): explained, worth only ~2 ms - close the item as minor.**
- **#1 + #2 (CPU compute vs split/sync): still open as a combined ~33 ms
  UNATTRIBUTED bucket.** `--profile-decode` at window granularity cannot split
  it; the ~96-97-splits structural explanation in `IMPL-GAP-FORK.md` remains
  `[hypothesis]`. Decisive next instrument = per-kernel/zone timing on one
  decode step (nsight systems / CUDA profiler ranges), not more PROF windows.

## 5. Caveats

1. **Profiler overhead:** profiled T6 steady 73.1 vs un-profiled t0006 F0
   65.4 ms/tok -> +7.7 ms/tok (per-decode `cudaEventSynchronize` + event
   records + clocks). Split between instrumentation and run-to-run variance =
   UNATTRIBUTED. All cross-arm deltas use profiled-vs-profiled numbers.
2. Startup windows 0-1 excluded from medians (startup effect 2-3x wall).
3. T6 win9-11 dip (~12 tok/s, +14% wall) unexplained; median-based results
   unaffected. Not a rig anomaly class event (no OOM/crash, gates green).
4. `t_cpu_ms` includes thread-pool spin (see section 3) - upper bound only.
5. Strata 30.9 ms/tok figure is the t0006/ctx63k same-prompt baseline, not
   re-run in this task (one GPU one task; fork only).

## 6. Evidence files

- `arms/T6-1.prof.txt`, `arms/T12-1.prof.txt` - raw PROF lines (15 each)
- `arms/T6-1.bench.json`, `arms/T12-1.bench.json` - bench raw
- `arms/T6-1.logparse.json`, `arms/T12-1.logparse.json` - parsed arm stats
- `logs/T6-1-engine.log`, `logs/T12-1-engine.log` - full stderr (PROF source)
- `logs/T6-arm.out`, `logs/T12-arm.out` - harness transcripts
- `logs/*-1.h2d.txt`, `*-1.link.tsv`, `*-1.summary.txt`,
  `*-1.vram_after_load.txt` - gates and health
- `prompts/` - t0006-identical p4k + warmup prompts (sha256 recorded)
- Repro harness: `.local/strata-research/arm-profile.sh` (not committed)

Rig state at close-out: no llama-server running, GPU0/GPU1 = 1 MiB, ports
8086/8091/8093 free, MemAvailable 75+ GiB.
