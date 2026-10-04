# Stage 0d — NSYS profile of one cold prefill ubatch: **`f` NOT MEASURED**; two rig findings

**Epic:** #811 · **Branch:** `docs/811-stage0d` (off `origin/epic/811-strata-gap-analysis`)
**Date:** 2026-10-04 · **Rig:** RTX 3060 = GPU1 only; GPU0 never touched (1 MiB throughout)
Follows `STAGE1B-RESULTS.md` (PR #830). Raw artefacts in `stage0d-nsys/`.

## Verdict

**The primary goal — a direct measurement of `f` — was NOT achieved.** Two rig findings *were*, and
both matter more: the flag-gate fix is **verified** for flag-off, and it **regressed flag-on to
inert**.

| item | status |
|---|---|
| `f` = expert-GEMM share of non-transfer prefill | **NOT MEASURED** (§3) |
| Flag-gate fix (`34068ff9`), flag OFF byte-identical | **VERIFIED — 6271 MiB** (§1) |
| Flag-gate fix, flag ON still engages | **REGRESSED — inert** (§2) |
| Link duty, both flag states (mechanism gate) | **NOT MEASURED** (§3) |

---

## 1. Flag-gate fix VERIFIED (flag off)

`STAGE1B-RESULTS.md` §8 named this "the single most important unverified claim here". It holds.

| artifact | sha256 | `build-info.txt` `source_commit` |
|---|---|---|
| `.local/stage1b-ci/34068ff9/bin/llama-server` | `0076d1ef3b14a00b29e3240ee1550e4520fdd87fca921ff8ab32cf19e55910a6` | `34068ff959505962669288463f4debbbc21c05d7` |

CI run `37203862664`, green. Flags reproduce the Stage 0 F0 configuration verbatim plus
`--moe-prefill-stream` for the ON arm; the OFF arm's launched argv was verified to contain **zero**
occurrences of the flag.

| | flag OFF | expected |
|---|---:|---|
| VRAM after load | **6271 MiB** | 6271 ✔ |
| VRAM after one p4k prefill | 6535 MiB | — |
| `moe-stage:` evidence lines | **none** | none ✔ (mechanism must be silent) |

Exact predicted value. **Flag off is byte-identical again** on fork tip `34068ff9`.

## 2. REGRESSION: the same fix made flag-ON inert

| | flag OFF | flag ON | #830 (`524a7659`) flag ON |
|---|---:|---:|---:|
| VRAM after load | 6271 MiB | **6271 MiB** | 7171 MiB |
| VRAM after prefill | 6535 MiB | **6535 MiB** | 7435 MiB |
| `moe-stage:` lines | none | **none** | (line did not exist yet) |
| p4k prefill, nsys run | 167.32 tok/s | **167.23 tok/s** | 178.6–179.7 tok/s |
| p4k prefill, clean run | 152.5 tok/s | **153.14 tok/s** | — |

**Flag-on is indistinguishable from flag-off** (+0.05%, +0.4%). A 2 × 450 MiB bank cannot be free,
so the bank is not allocated and the mechanism is off.

**Cause — from source, NOT rig-confirmed.** `34068ff9` gated the bank on a new optional iface slot:

```cpp
/* ggml-cuda.cu */
static bool ggml_backend_cuda_moe_stage_enabled(ggml_backend_t backend) {
    (void) backend;
    return ggml_backend_cuda_moe_get_prefill_stream();
}
```

`ggml_backend_cuda_moe_get_prefill_stream()` is Stage 1a's **plan** flag — set only once Stage 1a's
own viability whitelist (`moe_prefill_stream_plan_viable`, `moe-cache.cu:9710-9737`) accepts the
graph. If that whitelist declines, the raw CLI flag being on is not enough, and my scheduler gate
inherits the decline. In `524a7659` the bank was allocated unconditionally (hooks-present), which is
why #830 saw 7171 MiB.

**Consequence for #830.** Its +7% was measured where the bank was allocated by a gate that
*ignored* the flag — and the same bug handicapped the flag-off control. Both #830 ratios are
therefore suspect in **magnitude**. The sign and the `f ≈ 0.2` inference are unaffected: an inert
plus a handicapping control cannot manufacture a 7% gain that a non-existent overlap would not
produce.

The one-run diagnostic that would settle it: `--moe-prefill-stream` with Stage 1a's plan log line
at raised verbosity, to see whether the whitelist accepts or declines. **Not run.**

**No fork code touched, no fork PR opened**, per instruction. This is a diagnosis to hand back.

## 3. `f` NOT MEASURED — and why, precisely

Two capture attempts, both failed on **window placement**, not tooling:

1. **Too early.** `nsys profile --delay=40 --duration=70`. `nsys --delay` is measured from process
   **launch**, and model load under instrumentation takes ~100 s. The window (40–110 s) covered
   model loading. A report *was* produced (492 KB) — which is exactly why this failed silently.
2. **Too late.** `--delay=165 --duration=75`, overcorrecting past load + warmup.

Both then analysed with `nsys stats --report cuda_gpu_kern_sum --report cuda_gpu_mem_time_sum`,
both returning:

```
SKIPPED: S1OFF-prefill.sqlite does not contain CUDA kernel data.
```

i.e. **zero CUDA kernels inside the captured window** — CPU-side work only. One further trap:
`nsys stats` reused a **stale `.sqlite`** exported from the aborted attempt 1 and reported success
on it; `--force-export=true` was required to regenerate, after which the empty result was real.

`f` therefore remains the **inferred** `0.20–0.23` from `STAGE1B-RESULTS.md` §6, **not a direct
measurement**. This task was meant to replace that inference and has not.

**What a third attempt needs:**

- Do **not** use `--delay`. Launch under `nsys profile` with a long `--duration` (e.g. 600 s),
  issue the measured request after `/health` + warmup, then **stop the server** (`SIGINT`) so nsys
  finalises on exit. Window arithmetic disappears.
- Or profile the whole short session and slice `cuda_gpu_trace` against the request's `ttft`.
- Delete stale `*.sqlite` alongside `*.nsys-rep`; `nsys stats` silently reuses them.
- Confirm non-empty **first**: `nsys stats --report cuda_gpu_kern_sum --format csv --output ksum
  <rep>` must yield a non-zero-byte CSV before any analysis.

## 4. Link duty (mechanism gate) NOT MEASURED

A 1 Hz `nvidia-smi` PCIe-util sampler ran alongside each capture (`stage0d-nsys/*-link.tsv`), but
because both windows missed the prefill the samples do not bracket a measured ubatch. **No duty
figure is quoted.** The mechanism gate stays **NOT MEASURED**, as in #830.

## 5. What the data supports as the next lever

**None of them.** Stated rather than papered over:

- The `f ≈ 0.2` figure motivating (a)–(c) is **inferred, not measured**, and the one flag-on datum
  that could have supported (a) came from a build whose flag-off control was simultaneously
  handicapped. **§2 must be resolved first** — until flag-on demonstrably engages, the baseline
  itself is untrustworthy and no lever can be evaluated.
- Option (d) "nothing worth doing" is **not** supported either; nothing here rules it out.
- What *is* established, independent of `f`: prefill expert GEMMs already run on CUDA0 (143/145
  splits, `STAGE1B-REDESIGN.md` §1.3); the link probe is stable at 6.10–6.12 GB/s; decode is
  untouched by construction.

## 6. Could not measure / known-open

1. **`f` — not measured.** The primary deliverable (§3).
2. **Link duty, both flag states — not measured** (§4).
3. **Kernel breakdown** (expert gate/up/down separately, attention, norms, host-sync gaps,
   launch/idle gaps) — **not obtained**; both captures contain zero CUDA kernels.
4. **`34068ff9` flag-on behaviour — diagnosed from source, not confirmed** by a plan-verbosity
   log (§2).
5. **No CI cycle used** (fork untouched, no fork PR). The 6/6 budget from #830 was spent and is
   not extended.
6. **G-K2 not run**, per instruction.

## 7. Rig state

GPU1 only. Both GPUs returned to **1 MiB / 0%**; ports 8091/8086/8093 free; no `llama-server`, no
`nsys`. Pre-arm `MemAvailable` ≥ 67 GiB at both checks (79, 84 GiB). All output under `.local/`,
`TMPDIR` in `.local/tmp`, **nothing under `/tmp`**. No sudo, no local binaries.

## Change summary

**Changed:** this document plus `stage0d-nsys/` raw artefacts, on branch `docs/811-stage0d`. No
product, harness, export or config file touched. **Fork untouched** (`ddvnguyen/llama.cpp`
`feat/moe-prefill-stream` still at `34068ff9`, DRAFT PR #161 open, not merged), per instruction.

**Assumptions:** `34068ff9` is the correct artifact for both arms; the Stage 0 F0 flag set is
reproduced verbatim; p4k = 4119 prompt tokens at `-ub 2048`.

**Risks:** §2 is a live regression on the fork branch — flag-on is inert there, so #830's +7% is
not reproducible on the current tip and should not be quoted as a property of the branch.