# t0007 ctx63k — Strata @ ~63K prompt, int8 KV, RTX 3060 (GPU1)

Owner directive (leader, 2026-10-02 ~08:55): run Strata at ~63K prompt context with
int8 KV on the idle rig. Evidence committed on `docs/811-ctx63k-layerinfo`; no rig leftovers.
Evidence for two arms, run sequentially on GPU1, port 8092.

## Arms

| | K1 | K2 |
|---|---|---|
| base | t0006 S1 (`--spec 4 --spec-min-p 0.5 --mtp … --expert-cache auto --prefill auto`) | K1 + `--kv-resident 32768` |
| added args | `--max-context 65536 --kv int8` | `--max-context 65536 --kv int8 --kv-resident 32768` |
| config | `arms/strata-k1.json` | `arms/strata-k2.json` |

Prompt: `prompts/p63k.txt` — **62,990 pack-tokenizer tokens** (server sees 63,041
with chat template), 16 verbatim hydra `docs/*.md` sources joined
`\n\n---\n\n` with a unique `# ctx63k …` header, sentence-boundary trimmed
(`prompts/SOURCES.md`, `sha256sums.txt`). Distinct source pool from t0006
p12k (strata README/bench-results/DETAILS) — no shared bytes.
Protocol per arm: short independent warmup (64 tok), then cell p63k × 3 reps
(rep0 cold, rep1–2 prompt-cached), max_tokens 256, `STRATA_DECODE_TIMING=1` +
`STRATA_PREFILL_TIMING=1`, 1 Hz link sampler, H2D probe pre/post, gates
(8091 down, MemAvailable ≥ 67 GiB).

## Results

| metric | K1 (int8, all 65536 cells in VRAM) | K2 (int8 + kv-resident 32768) |
|---|---|---|
| engine KV report | no streaming line ⇒ full resident; KV type = config `--kv int8` | `KV streaming: 32768 of 65536 cells per QSA layer in VRAM, the K/V in 0.77 GiB of pinned RAM` (K2-engine.log:6) |
| expert cache after load | 2358 slots, 4.53 GiB VRAM | 2581 slots, 4.96 GiB VRAM |
| VRAM free after load | 456 MiB | 458 MiB |
| cold prefill (engine) | **684.4 tok/s** (63041 tok / 92.1 s) | 609.9 tok/s (63041 tok / 103.4 s) |
| TTFT (harness, rep0) | 92.31 s | 103.57 s |
| decode tok/s rep0/1/2 | 25.18 / 36.82 / 38.13 → **median 36.82** (cached-only median 37.48) | 34.93 / 36.50 / 35.14 → **median 35.14** (cached-only 35.82) |
| decode windows (final) | 1.80 tok/window, avg T 2.13, 47.34 ms/window | 1.79 tok/window, avg T 2.22, 51.01 ms/window |
| … breakdown (final) | verify 42.52 = GPU-wait 22.29 + host 17.80 (CPU 17.06) + stage 0.28; commit 0.90; draft 3.92 | verify 46.01 = GPU-wait 21.97 + host 21.53 (CPU 19.91) + stage 0.30; commit 0.94; draft 4.04 |
| prompt reuse rep1/2 | 63036/63041, TTFT 0.30 / 0.28 s | 63036/63041, TTFT 0.31 / 0.32 s |
| expert-cache hit (final, cumulative) | 68.8 % (98041/142605) | 64.8 % (96810/149283) |
| KV streaming stats | n/a | 98.93 % of block reads hit VRAM, 253.5 MiB from RAM (cumulative) |
| prompt chunk auto | 6144 | 6144 |
| H2D pre / post | 6.12 / 6.11 GB/s | 6.10 / 6.13 GB/s |
| link invalid samples (gen≠4 at util≥10) | 0 | 0 |
| gates / verdict | ok, `align match=True`, `link_bad_samples=0` | ok, `align match=True`, `link_bad_samples=0` |
| MemAvailable after | 77.3 GiB | 77.4 GiB |

## Comparison

- t0006 S1 @ 12K (same flags, ctx 16384): decode **42.5**, cold prefill **606 tok/s**.
  → 63K int8: decode 36.8 (**−13 %**), cold prefill 684 (**+13 %**, longer prompt
  amortises fixed chunk costs; GPU-timeline phase shares show `wait copy` 28 %,
  no KV phase — K1's KV is fully resident).
- Strata published 5070 IQ3_S @ 32K: 48.3 tok/s decode. Our 3060 @ 63K: 36.8 —
  different GPU, 2× context, not directly comparable.
- K2 vs K1: `--kv-resident 32768` cost −11 % cold prefill (K2 adds a `kv stage`
  11.8 % phase on the GPU timeline and 102.5 s wall vs 91.3 s) and −4.6 % decode
  median, in exchange for +223 expert-cache slots (5.86 vs 5.43 GiB free) and
  upper-half KV spill to 0.77 GiB pinned RAM. At 63K on 12 GB, **K1's plain int8
  already fits (456 MiB headroom) — kv-resident is not needed for capacity**.

## Protocol notes / deviations

- Prompt built with `/mnt/WorkDisk/strata/src/.venv/bin/python` (pack tokenizer
  needs `regex`, absent from system python — same constraint as t0006 rebuild_cells).
- The engine prints **no explicit KV-type line**; K1's int8 residency is
  evidenced by config arg + the absence of any `KV streaming` line (present
  only in K2). `--kv int8` and `--kv-resident 32768` both accepted, no OOM.
- run labels `K1-1` / `K2-1` (first run per arm); `K*-engine.log` is the config
  log copied after each arm (config log truncated per launch).

## Rig state after both arms

No `strata` process, port 8092 free, GPU1 = 1 MiB, MemAvailable 77 GiB, H2D
probe 6.1 GB/s. 8091 was never started (gate-checked pre-arm).

## File inventory

- `arms/strata-k{1,2}.json` — engine configs; `arms/K{1,2}-1.{bench,logparse}.json`
- `logs/K{1,2}-arm.out`, `logs/K{1,2}-engine.log`, `logs/K{1,2}-1-server.out`,
  `logs/K{1,2}-1.link.tsv`, `logs/K{1,2}-1.h2d.txt`, `logs/K{1,2}-1.summary.txt`,
  `logs/K{1,2}-1.engine.pid` (removed at stop)
- `prompts/p63k.txt`, `warmup.txt`, `manifest.json`, `sha256sums.txt`, `SOURCES.md`
- builder: `.local/strata-research/build_p63k.py`; runner: `arm-strata63k.sh`
  (arm-strata.sh fork for K arms), harness reused from t0006
  (`bench3060.py`, `sample_link.sh`, `parse_arm_logs.py`)
