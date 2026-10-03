# Stage 0 pre-registration (frozen before any run) — 2026-10-03

Source: `OPTION-C-DESIGN.md` §3 Stage 0 (PR #821), gates G-0a/G-0b/G-0c + G-L/G-V/G-G.
Owner rules: GPU1 only, k=10, no `--override-kv`, no builds, no `/tmp`, pre-arm
MemAvailable ≥ 67 GiB, both GPUs ~1 MiB before arming, spare rule ≥ 2 logical CPUs
(`leader-handoff-state.md:756-757`: SPARE = available logical CPUs − compute threads).

## Arms (order frozen here; flags = t0006 F0 EXACTLY + stated instrumentation only)

| # | arm | delta vs F0 | bench | purpose |
|---|---|---|---|---|
| 1 | `F0C` | none (`-t 6`) | warmup + p4k×3 + p12k×3, 256 tok | un-profiled F0 control (today's 65.4-class number) |
| 2 | `F0P` | `--profile-decode` | same | item 1: `t_wall/t_cpu/t_dev/t_sync` per 64-step window |
| 3 | `T12` | `-t 12` + `--profile-decode` | same | item 3 config arm, spare = 8 |
| 4 | `T16` | `-t 16` + `--profile-decode` | same | item 3 config arm, spare = 4 |
| 5 | `SPLIT` | `GGML_SCHED_DEBUG=1` + `-lv 5`, `-t 6` | warmup + p4k×1 + p12k×1, 128 tok | item 2: `## SPLIT` per token |

All arms unpinned (no `taskset`), sequential, port 8093, `CUDA_VISIBLE_DEVICES=1`,
GPU0 never touched. Prompts byte-identical to t0006 (p4k `fa13b451…`, p12k
`2e5eabdf…`, warmup `ad920c19…`).

Comparisons: profiled-vs-profiled for F0P/T12/T16 (profiler costs wall — see
`PROFILE-DECODE.md` §5.1); F0C vs F0P gives the in-session instrumentation delta
(positions 1 vs 2, adjacent). G-0c uses the profiled pair F0P vs T16.

## Gates as read from OPTION-C-DESIGN.md §3/§5

- **G-0a** attribution: profile windows + split count + config arms must explain
  **65.4 ± 10% ms/token** (= 58.9–71.9). Fail ⇒ stop the whole option.
- **G-0b** instrument: G-K2 build-vs-itself KL floor must exist as a number.
  *Owner bar this session: `llama-perplexity` is not built and local builds are
  forbidden ⇒ expected outcome `NOT RUN` (documented, not silently dropped).*
- **G-0c** cheap win: `-t 16` alone ≥ **+6%** decode with G-R/G-V/G-L passing ⇒
  ship as config change.
- **G-L** PCIe: h2d probe pre/post **6.00–6.30 GB/s**; `gen1 ∧ util≥20 = 0 samples`.
- **G-V** VRAM after load ≤ **11 000 MiB** (expect 6271).
- **G-G**: `graphs reused` ≥ control, zero `post_decode() failed` / HTTP 500.
- **G-R**: no non-target axis regresses > 3% vs the arm's control.
- **Spare**: T12 spare 8, T16 spare 4, F0 arms spare 14 — all ≥ 2 (pass by rule;
  recorded explicitly, plus `load1` context from the 1 Hz sampler).

Abort conditions (owner): any gate precondition fail, crash, invalid PCIe link ⇒
STOP and report; do not work around.
