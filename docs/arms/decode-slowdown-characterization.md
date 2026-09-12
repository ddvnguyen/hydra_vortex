# Decode Slowdown Characterization — intermittent single-slot outlier (not UM-paging, not #744 concurrency)

## Summary

User reports intermittent Qwen3.8-27B decode "sometimes slows down" — confirmed **not** the known deep-context UM-paging collapse (which is deterministic at 80-103K, ~7-9 tok/s, VRAM oversub 262K). This arm characterizes the actual intermittent pattern under **realistic single-slot shallow** conditions per M01 canonical before hypothesizing a cause.

**Finding:** Single-slot shallow decode (M01: `head -c 2000 /tmp/bigprompt.txt` ~808 tok prompt, `max_tokens 150`, `stream false`, B01 build `10814 1d3c4a8e3` 86;120 / CUDA 13.2.2 Release, production shape `ctx 140000 p1 16384 q8_0/q4_1 MTP`, `GGML_CUDA_ENABLE_UNIFIED_MEMORY=1`, `15847/11911` VRAM, `49-57°C`) shows **occasional outlier-slow requests amid otherwise consistent ones, without concurrency or depth**:

- 3×15 sequential requests (45 total, drain-verify `1 MiB → health fail → 1 MiB` not needed between — single-slot serial, but production pod kept running, `curl localhost:18081/health` OK throughout):
  - **Run1 (15):** mean 43.76 median 43.22 stdev 4.94 min **30.42** max 50.73 range 20.3 CV 0.113 → **1/15 outlier (>2σ low)** req14: `30.42 tok/s, wall 5.08s, draft 87/185 (acc 0.47), cached 804, prompt_ms 181` vs typical 42-50 tok/s, acc 0.82-1.0, prompt_ms 180-197.
  - **Run2 (15):** mean 44.64 median 44.02 stdev 3.80 min 37.83 max 50.29 range 12.47 CV 0.085 → **0/15 >2σ**, but tail still 37.8 (≈15% slow).
  - **Run3 (15):** mean 44.71 median 44.99 stdev 3.89 min 38.91 max 50.42 range 11.51 CV 0.087 → **0/15 >2σ**, tail 38.9.

Overall **1/45 ≈2% strong outlier (30 tok/s), ~3/45 ≈7% mild tail (37-39 tok/s)** at shallow single-slot. Draft acceptance correlates tightly: outlier 0.47 vs warm typical 0.77-1.0 (`111/111` pristine when continuation matches draft state, per #744). `cached_tokens 804` and `prompt_ms 180-197` stable — **not** a KV-cache miss or prefill artifact. Thermal stable `49-57°C`, `0%` util between requests, no throttling.

**Replicate before trusting:** 3×15 meets the "replicate before trusting" bar for the strong outlier (1/15 in run1, 0/15 in run2/3 shows intermittency, not a stable split). Frequency estimate ~2-7% needs larger N to tighten — see Recommendation.

**Does it match #744? No.** #744 is **deterministic per-slot concurrent asymmetry**: with the #743 priming fix (`concurrent-decode-test.sh` now primes both slots with full prompt N=8), arm111 n=2 shows **stable per-slot split** `~23 vs ~30-32 tok/s` (43 vs 31 ms/tok, aggregate 52-53) — **byte-identical acceptance curves across repeats** slot1 `[3,3,1,2,0,0,0,2…]` acc 85/188 (0.45, 63 steps) vs slot2 `[3,3,1,2,0,0,2,1,3…]` acc 106/127 (0.83, 43 steps), diverging at step ~6 where continuations diverge due to kernel reduction-order logit flips under greedy. **This is not a bug, no draft-KV restore corruption** (probes: `tgt_pos_max==dft_pos_max` 2357/2357, prompt-cache load never fired, catch-up batches contiguous). Our single-slot shallow test **excludes concurrency**: one slot at a time, no divergent pairing, still shows a **sporadic** low-acceptance outlier with the **same prompt** — different mechanism.

**Does it match #743? Partially the small sub-anomaly left after that fix.** #743 (corrected 2026-09-07) was a **test-harness cache-miss artifact**: `run-with-params.sh [5/6]` primed only `head -c 2000` prefix on one slot, and fork prompt-cache reuse only credits exact repeats (`808→2362` reuses 42 tok, eval 2320 @3016ms), so `concurrent-decode-test.sh`'s first n=2 run paid 1-2 full prefills and read as fake "19.7 vs 32-34 tok/s" slow decode. Fix `3355a032e` (prime-by-default with N_SLOTS concurrent full-prompt N=8) resolved the **systematic** 1.7× first-run bias. The issue explicitly notes a **separate small sub-anomaly left unresolved** after the fix — the deterministic per-slot split tracked as #744. Our single-slot intermittent 30 tok/s outlier is **not that sub-anomaly** (which is deterministic and concurrent-only); it is a **new, shallower, sporadic** effect — likely what the user reports as "sometimes slows down" at realistic shallow/medium context.

## What #743/#744 resolved vs left open

- **#743:** Warm-loop never primed bigprompt KV → fake slow first run (prefill amortization). **Resolved** by concurrent full-prompt priming; do not re-investigate.
- **#744:** After priming, n=2 concurrent still shows deterministic 23 vs 32 tok/s per-slot split → **root cause localized** to divergent greedy continuations → different MTP draft acceptance (0.45 vs 0.83), **not** draft-KV restore (disproven by targeted probes). Measurement hazard for per-slot tok/s, not a code defect. **Tracked separately; not the user’s intermittent.**
- **#743 sub-anomaly:** After priming, a small remaining anomaly noted but not characterized beyond #744 — now known as #744. **Our task adds a third pattern:** single-slot intermittent outlier (2% strong, ~7% tail) at shallow, not tied to n=2.

## Historical telemetry check

Per `docs/monitoring-observability.md`, stack is Prometheus `:9091`, Grafana `:3000`, Loki `:3100` via Quadlet `infra/quadlets/` + Promtail `k8s-file` → `ctr.log`. On this worktree/rig at characterization time:

- `curl :9091/api/v1/query?query=up`, `:3000/api/health`, `:3100/ready` → **no response** (stack not running on this worktree; `scripts/start-env.sh` not deployed for arm-test rig). No Loki-promtail history to query for decode-speed drops vs concurrency/MTP/cache state. Node/GPU exporter `:9100`/`:9835` also no metrics endpoint on bare-metal baseline pod.
- `nvidia-smi` history not persisted; live polls during runs show `49-57°C`, `0%` util, `15847/11911` VRAM stable — no thermal/power throttling correlate. `dmesg` not readable without sudo, but `Xid check` GOOD each boot.
- Past arm-test runs (`docs/investigations/740-results-report.md`, `docs/arms/chat-template-sharp-*.md`, `um-paging-*.md`) all report **collapsed vs shallow** deltas, not intermittent shallow outliers — no timeline of sporadic drops recorded.

**Honest note:** No useful historical timeline exists for this characterization; reproduction was required.

## Reproduction method (minimal, M01 canonical)

- **Build provenance (B01):** `CMAKE_BUILD_TYPE=Release`, `GGML_CUDA_FA_ALL_QUANTS=ON`, `GGML_CUDA_FORCE_CUBLAS=OFF`, `arch 86;120`, `DCUDAToolkit_ROOT=/opt/software/cuda/13.2.2`, binary `src/llama-cpp/build-cuda1322/bin/llama-server+ggml-rpc-server 0.4.0-dev build 10814 commit 1d3c4a8e3` (`feat/server --parallel-ctx-threshold` + UM), `BASELINE_SHA 5fff12845`, `model Qwen3.8-27B-UD-Q5_K_M.gguf 19.5 GiB`, `tensor_split 27,38`, `K q8_0/V q4_1` (090 shape) `140000 p1 16384 MTP q8_0/q4_1`.
- **Params:** experiment reused production pod (no custom boot needed for single-slot shallow — drain-verify `1 MiB` before/after each boot in prior arms, here `health OK` `15847/11911` held). `payload: POST /v1/chat/completions {model:local, messages:[{role:user, content: head -c 2000 /tmp/bigprompt.txt}], max_tokens:150, stream:false}` via `python requests` (same as `run-with-params.sh` M01: 10 sequential, reported cold vs warm — we extend to 15 sequential, 0.5s gap, single-slot serial, no concurrency).
- **Metric:** `timings.predicted_per_second` + `predicted_ms` + `draft_n`/`draft_n_accepted` + `usage.prompt_tokens_details.cached_tokens` + `prompt_ms` per request. Wall measured client-side as sanity. Replication: **3×15 sequential** (45 total) on same pod without restart, to isolate "does it happen even without concurrency/depth".
- **Prompt:** `head -c 2000 /tmp/bigprompt.txt` (~808 tok, `cached_tokens 804` warm, `prompt_ms 180-197` stable) — same shape as M01, not the 4-token stub that caused #743 confusion. One prompt, greey, same across all 45.

Logs: `/tmp/m01_seq2.py`, `/tmp/m01_seq2.log` (run1), `/tmp/m01_seq2_run2.log` (run2), `/tmp/m01_seq2_run3.log` (run3).

## Pattern found (what matches vs what doesn't)

- **Pattern:** Single-slot shallow decode is **usually 42-50 tok/s** (prompt 808 → 150 out, `cached 804`, `prompt_ms ~180`, draft acc 0.82-1.0, `draft 111/111` pristine when continuation matches draft state as in #744 sequential baseline). **Occasionally (~2% strong, ~7% tail)** it dips to **30-39 tok/s** with **draft acc 0.47-0.70** (`87/185`, `98/147`, `100/145`) and **wall +1.5-2s** for same 150 tokens. Not tied to turn index (outlier at req14 in run1, not early/late systematic), not to cached miss (cached 804 always), not to prompt_ms (181 vs 180-197), not to thermal (temps flat).
- **Matches #744? No** — #744 is deterministic concurrent 23 vs 32 per-slot split from divergent continuations (same prompt, two slots, acceptance curves stable across repeats, diverge at step 6). Our single-slot outlier is **sporadic, same slot, same prompt, acceptance varies run-to-run** — suggests per-request nondeterminism even without co-batching.
- **Matches #743 sub-anomaly? No** — #743 sub-anomaly is the same #744 split; this is a different intermittent.
- **New hypothesis:** **MTP draft nondeterminism even single-slot** — kernel reduction order / speculative scheduling still varies per request due to GPU async or prior KV residue, causing logit argmax flips at low-margin positions and thus draft acceptance swings 0.47→1.0. The #744 sequential baseline that hit `3/3` every step (111/111) may have been a lucky continuation; our prompt's continuation is more marginal. Alternative: **GPU boost clock jitter** without thermal correlate, or **UM paging micro-thrashing** even at shallow (unlikely at 808+150, but 140K ctx with UM forced could still micro-page). **Not** the deep UM collapse (which is 7-9 tok/s at 80K, deterministic, not 30 vs 44).

## Recommendation (characterization-first, not a fix yet)

1. **Instrument harness:** Log `draft_n`, `draft_n_accepted`, `acc = accepted/draft`, `predicted_per_second`, `prompt_ms`, `cached_tokens`, and `nvidia-smi` temp/power per request in one CSV — already done in `/tmp/m01_seq2.py`, promote to `infra/llama-baseline/m01-sequential-characterization.sh` for larger N. Report **mean/median/stdev/CV and outlier rate**, not just mean.
2. **Larger N to nail frequency:** Run **100 sequential M01** (same prompt, same pod, no restart) + **100 sequential with different prompt** (to test prompt-marginality hypothesis) — this distinguishes per-prompt marginality (some prompts always draft poorly) vs true intermittency (same prompt sometimes 0.47, sometimes 1.0). Already 45 suggests intermittency.
3. **Ablate MTP:** Boot **same shape with `spec_type` removed** (no draft-mtp, per 090 fallback 3b) and repeat 30 sequential — if outlier disappears (tok/s tight at ~28-32 without draft), confirms MTP draft variance is the driver (mirrors #744's acceptance split, but single-slot). If outlier persists without MTP, investigate GPU clocks/UM micro-paging.
4. **Do not conflate with #744 concurrent hazard:** For arm comparisons, continue to report **aggregate tok/s + mean acceptance** per #744 guidance, and treat per-slot deltas <1.5× same-prompt greedy as suspect.

## Provenance & replication

- **B01:** `Release`, `FA_ALL_QUANTS=ON`, `FORCE_CUBLAS=OFF`, `arch 86;120`, `CUDA 13.2.2`, `build 10814 1d3c4a8e3`, `BASELINE_SHA 5fff12845`, `llama-server --parallel 1 --ctx 140000 --cache-ram 16384 --kv-unified --spec-type draft-mtp` (090 shape, UM=1, 27,38, q8_0/q4_1, ubatch 512, YaRN 5/32768).
- **Replication:** 3×15 sequential (45 requests) on same pod, same prompt, no restart — one strong outlier (30.4) + mild tail (38-40) reproduced as tail in all runs but strong outlier only in 1/3 runs → intermittent, not deterministic.
- **Rig:** RTX 5060 Ti 16G sm_120 + RTX 3060 12G sm_86, driver 595.91, `pod_llama-baseline` `18081/50052`, drain-verify `1 MiB` before/after prior boots, production restored `{"status":"ok"}` `15847/11911` — left clean.

## Next step

Characterization complete: **single-slot shallow intermittent 30-39 tok/s outlier (≈2-7%) with low draft acceptance (0.47-0.70) vs typical 42-50 tok/s (0.82-1.0), same cached/prompt_ms/thermal, not depth/concurrency, distinct from #744 deterministic concurrent 23 vs 32 and #743 cache-miss.** Recommend **N=100 M01 + MTP-disabled ablation** before proposing a mitigation (e.g., larger draft-acceptance logging, or draft-MTP tuning).
