# t0006 — Strata v0.1.29 vs llama.cpp fork A/B on RTX 3060 (like-for-like IQ3_S)

**Status:** complete 2026-10-02 02:35 (+07). Window 00:15–02:35 ≈ **2.4 h** (timebox 3–4 h). All gates, H2D probes and link samples clean on every scored arm. Deliverable of track `t-d1cf43b723`; worker report to architect `01afbe44`.

## Question & pre-registered decision rules (frozen before any run)

Does Strata's CPU-serves-misses design beat our llama.cpp fork on the 3060 (decode / prefill / prefix reuse), same GPU1, same IQ3_S model, identical byte prompts?

| # | Rule (pre-registered) | Outcome |
|---|-----------------------|---------|
| R1 | **Primary:** spec-off 4K decode, Strata/fork ≥ 1.30× → Strata wins; ±15% → parity; fork >15% → audit Strata | **Strata/fork = 32.44/15.30 = 2.12× → Strata wins** (raw; draft-adjusted 34.06/15.30 = 2.23×, same verdict) |
| R1b | 1K / 12K decode (same bands) | 1K 2.16×, 12K 2.21× → Strata wins both |
| R2 | Cold prefill 12K, same bands | **605.9/201.0 = 3.01× → Strata wins** (4K 2.23×; 1K 167.2/137.4 = 1.22×, outside the ±15% parity band → Strata, marginal) |
| R3 | Fork TTFT-turn-3 ≤ 0.5× Strata → fork prefix advantage | 11.25 vs 5.86 s → ratio 1.92, **rule not met; Strata better** |
| V | Invalid arm (Gen1 **under load** / crash) → rerun once, else report; stop at 2 invalid arms | 0 link-invalid arms (see Validity). 3 invalid **attempts** each recovered on ≤1 retry (S0-1, F0-1, F1-OOM) — no arm exhausted its retry |

**Primary scores (pooled mean of per-run medians, 3 timed reps/cell):**

| Cell | S0 decode | F0 decode | ratio S0/F0 | S0 cold prefill | F0 cold prefill |
|------|-----------|-----------|-------------|-----------------|-----------------|
| 1K | 33.14 | 15.34 | 2.16× | 167.2 | 137.4 |
| **4K (primary)** | **32.44** [32.37–32.50] | **15.30** [15.12–15.48] | **2.12×** | 450.8 | 202.6 |
| 12K | 33.73 | 15.23 | 2.21× | 605.9 | 201.0 |

| Prefix turn (S0 vs F0) | Strata TTFT | fork TTFT | cached tokens (both, ≈) |
|---|---|---|---|
| turn1 (9,163 tok cold) | 19.12 s | 48.50 s | 0 / 42 (template only) |
| turn2 | 5.74 s | 9.97 s | 9,159 |
| turn3 | 5.86 s | 11.25 s | 9,886 |

## Setup (like-for-like contract)

- GPU1 RTX 3060 12 GB (PCIe gen4 x4; link never gen1 while loaded — see Validity), **ctx 16384 both engines**, `--parallel 1`, greedy `temperature 0`, **256 new tokens**, 3 timed reps/cell, median reported, min–max in `arms/*.bench.json`. Cells: 1K/4K/12K prefill+decode + 3-turn prefix (9,163 → +~730/turn). First 64-char output snippet per rep in `arms/*.bench.json`.
- Same model: GSQ-RCO **IQ3_S**. Strata: pack `/mnt/SSD/strata-models/packs/iq3_s` + native/ple gguf shards (exe `build-sm86/strata`, sha `5f01300d49a904db…`). Fork: `llama-server` sha `7be4f4fbb8ec3527…` (`.local/q2g`), `--alias gsq-rco-iq3_s`.
- Identical byte prompts: `prompts/{p1k,p4k,p12k,prefix_turn1..3}.txt` + independent `warmup.txt` (see deviations D2), sha256 in `prompts/sha256sums.txt`.
- Harness: `harness/{bench3060.py, arm-strata.sh, arm-fork.sh, sample_link.sh, parse_arm_logs.py, selftest.py, rebuild_cells.py}` (copied from the git-ignored `.local/strata-research/`; scripts reference that path). Strata launch uses `/mnt/WorkDisk/strata/src/.venv/bin/python -m serve.server` (system python3.14 lacks `regex` required by the pack tokenizer).
- ABBA order: **S0, F0, F0, S0 · S1, F1, F1, S1 · F2** (per leader ruling). One short report line per completed arm was sent as arms finished.

## Arms

| Arm | Engine start (diff vs other arm in **bold**) | Runs scored |
|---|---|---|
| **S0** (primary strata) | `--spec 2 --spec-min-p 1.0 --suffix-draft 0`, `STRATA_DECODE_TIMING=1`, `STRATA_PREFILL_TIMING=1` | S0-2, S0-3 |
| **F0** (primary fork) | q2cell production flags + `--moe-expert-cache-size 0`, **`--spec-type none`**, **no `--decode-overlap`** (D3) | F0-2, F0-3 |
| S1 (informational) | `--spec 4 --spec-min-p 0.5` (suffix-draft default 3) | S1-1, S1-2 |
| F1 (informational) | F0 flags + `--moe-expert-cache-size 22` | F1-1, F1-2 |
| F2 (informational) | F1 + `--spec-type draft-mtp --spec-draft-model …mtp-Q4_K_M.gguf --spec-draft-n-max 3 --spec-draft-p-min 0.75 --spec-draft-ngl 0 --spec-draft-moe-expert-cache-size 22` — **MTP head loaded** | F2-1 |

> **S1/F1/F2 are informational only** (leader ruling 2026-10-02): primary decode rule = S0/F0 (spec-off both).

## Informational arms

| Arm | decode 1K/4K/12K | cold prefill 4K/12K | notes |
|---|---|---|---|
| S1 (strata spec-4) | 39.32 / **38.81** / 41.86 | 452 / 608 | +19.6% over S0 @4K. Residual draft cost 3.11 ms of 53.49 ms/window (avg **T 2.54**) → draft-adjusted 4K ≈ 41.2 tok/s. Fill 5.06 GiB, hit rate 57–66%. |
| F1 (fork cache N=22) | 9.64 / **9.28** / 9.08 | 206 / 204 | **Slower than F0 (15.30)** — see "Why F1 < F0". |
| F2 (fork MTP) | 7.14 / **7.15** / 7.03 | 191 / 190 | MTP head loads (VRAM 8891 vs 8045). Draft acceptance 0.86–0.98, mean len 2.4–3.3 — but draft runs on CPU (`--spec-draft-ngl 0`) atop the same cache-thrash path → worst decode. |

### Why F1 < F0 (and F2 < F1)

Measured, not hypothesised:

1. **`--moe-expert-cache-size` forces ALL expert traffic through the GPU LRU** regardless of `--n-cpu-moe` (fork's own startup warning: *"expert tensors route through the GPU LRU cache regardless of --cpu-moe / --n-cpu-moe"*). F0 (cache 0) keeps the pure host-RAM CPU-GEMM stream; F1 adds a gather/scatter layer per token.
2. **N=22 (1,774 MiB resident) thrashes the working set**: `moe-cache-mm h2d_mib=6520` moved per run, decode-phase L1 hit ≈73%, thousands of H2D copies (`moe-cache-experts: tensors=144 experts=73728`, unique experts up to 120/512 per hot tensor).
3. **Fill-matching Strata (5.06 GiB) is impossible on 12 GB**: measured unit = 80.64 MiB/N → N=64 (+5,161 MiB) → **CUDA OOM during load** (`moe-cache: cudaMalloc(58982616 bytes) failed`, ggml-cuda.cu:117). So the fork never gets a cache-matched run on this card — an inherent asymmetry of the design (Strata's expert cache shares its host-pinned weight residency; the fork's cache adds on top of CUDA-resident everything else).

**Asymmetry disclosure (important for reading R1):** Strata always runs with its auto expert cache (2,641 slots = **5.06 GiB**, stable across runs). The pre-registered primary fork arm F0 runs cache-0 (q2cell production flags). R1 therefore compares *strata-with-GPU-expert-cache* vs *fork-without* — exactly as pre-registered, but the cache-matched view (S1 vs F1) is informational only, and F1 is under-matched (1.77 GiB) and slower anyway.

## Deviations & voids (full ledger: `deviations.md`)

| id | what | why |
|---|---|---|
| **D1** | S0 = `--spec 2 --spec-min-p 1.0 --suffix-draft 0`, not pre-registered `--spec 0` | **`--spec 0` is not expressible in `serve` — the engine requires `--spec >= 2` and `--mtp`.** Leader ruling set Option A (residual draft cost reported as secondary figure; no Strata patch). Raw **and** draft-adjusted numbers both reported below. |
| **D2** | Prompt cells rebuilt with the real tokenizer (p4k 6,250→4,068 tok; p12k 19,341→12,283 tok; independent warmup) | char/4 estimates overflowed ctx 16,384 (HTTP 400). Originals archived `prompts/archive-2026-10-02-char-est/`. Neutral: same bytes both engines. |
| **D3** | Fork arms run **without `--decode-overlap`**; `--spec-type none` passed explicitly for F0/F1 | `--decode-overlap` crashes with this model at the first decode graph rebuild (pos≈96): `rebuilt graph requires another backend for ffn_moe_gate-0 (MUL_MAT_ID)` (llama-context.cpp:3092 async sampled-input) → `post_decode() failed` HTTP 500. Reproduced twice; clean without it (probe logs `probe-nospectype.log`, `probe-nooverlap2.log`). **Conservative vs fork** (overlap slightly helps fork decode). Candidate review-finding. |
| **V1** | S0-1 VOID — bench aborted at p12k HTTP 400 (prompt > ctx). Sanity only: decode 31–33 tok/s, cold 1K prefill 161 tok/s. | D2 root cause |
| **V2** | F0-1 VOID — decode-overlap crash at warmup. VRAM baseline captured (6,385 MiB). | D3 |
| **V4** | F1 fill-matched attempt VOID — CUDA OOM at N=64. N pinned back to 22 (= F1-1 config) per leader 02:06 ruling. **Label note:** the OOM attempt died before writing a bench.json, so the successful N=22 rerun reused the `F1-2` label and overwrote its `F1-2-engine.log`; OOM evidence survives in `deviations.md` + bg-job log `ad285fe7fafd`. | 12 GB VRAM ceiling |

## Validity checks (every arm)

- **Gates:** 8091 down + MemAvailable ≥ 67 GiB before each arm (79–92 GiB observed); MemAvailable after ≥ 90 GiB.
- **H2D probe** (`.local/pcie-probe/h2d 1`, pinned): pre **6.10–6.12 GB/s**, post **6.11–6.13 GB/s** — no drift on any arm.
- **PCIe link, 1 Hz:** width x4 always; gen1 samples exist **only while idle** (3060 known idle-downshift) — **gen1∧util≥20 = 0 samples in all 12 runs** → no link-invalid arms. Under load gen4 x4.
- **Log/bench join:** `align match=True` on all 9 scored arms (13 requests ↔ 13 timing blocks each).
- **Telemetry peaks** (`logs/*.link.tsv`): util 100% during decode; system RAM 11.5 GiB (S arms) / 6.3–9.5 GiB (F arms); engine RSS 49 GiB (S) / 52–55 GiB (F); load1 max 9.1 (S, host-expert streaming) vs 4.0 (F).

## Evidence layout

```
docs/evidence/strata-3060-ab/
├── README.md              ← this file
├── deviations.md          ← D1–D3, V1–V4 ledger
├── prompts/               ← cells + manifest(actual_tokens) + sha256sums + warmup.txt + archive/
├── arms/                  ← strata-{s0,s1}.json, <label>.bench.json, <label>.logparse.json
└── logs/                  ← <label>-{engine.log,server.out,link.tsv,h2d.txt,summary.txt,vram_after_load.txt}
```

Void attempts kept: `S0-1.*`, `F0-1.*`, OOM evidence in `deviations.md` V4 (+ bg logs).

## Verdict summary

**Against every pre-registered rule, Strata wins on the 3060 with this model:** 2.12× decode (spec-off primary), 3.01× cold 12K prefill, better prefix TTFT at every turn. The fork's only defence configurations (expert cache, MTP) both lose ground on this card: cache N=22 thrashes (9.3 tok/s), fill-matched N=64 OOMs, and CPU-side MTP drafting adds overhead it cannot amortise. Caveat to carry into the owner review: the primary comparison is *strata-with-cache vs fork-production-flags-without-cache* (as pre-registered); a fair cache-matched fork arm is physically infeasible at 5 GiB on 12 GB VRAM — that is itself a finding, not an omission.
