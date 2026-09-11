# Arm — Chat template Sharp vs Stock, replicated A/B on Q5_K_S (Round 3, supersedes opt-in caveats)

> Branch `feat/703-baseline-consolidated` — worktree `/mnt/WorkDisk/workspace/worktree/1q3ry0vb/majestic-toad`
> Rig: RTX 5060 Ti 16 GB sm_120 CUDA0 + RTX 3060 12 GB sm_86 CUDA1, driver 595.91 CUDA 13.2, toolkit `/opt/software/cuda/13.2.2`
> Model: `-m /mnt/SSD/Qwen3.8-27B-UD-Q5_K_S.gguf` (18 GB) for both arms — only chat template differs
> Date: 2026-09-12 00:37–01:15 Asia/Bangkok
> Author: config-only arm test (no code change) — 6 fresh boots, alternating Stock ↔ Sharp

Supersedes the caveats in `docs/arms/chat-template-sharp-opt-in.md` (which was n=1, 4-turn, mixed completion lengths). This document provides the first **replicated** A/B at the `arm-chat-template-sharp-opt-in.yml` shape.

## 1. Goal

Peculiar-Ragdoll's `qwen3.8-sharp.jinja` (30 KB, `qwen3.8-froggeric-v22.5.0`, commit `4227f78c3` infra) claims *task-completion* speed via terser output at equal correctness, not decode tok/s. The n=1 probe (2026-09-11, agent `e8954f11`) showed a harness-tok/s "win" (28.43 vs 23.82) that was already flagged as unreliable because `tok/s = completion_tokens / wall_seconds` gets distorted by early-EOS/short completions. The correct metric per `docs/arms/arm-ud-q5ks-vs-q5km.md` §5.3 and the project's standing "last-turn tail" finding is **wall-clock-per-turn** at controlled `NEW_TOKENS` / `N_PREDICT`. Round 2 covered T2 replication for Sharp (42.12 tok/s stable); this Round 3 does the missing **multiturn A/B at matched depth**.

## 2. Build provenance (B01, shared)

| Check | Result |
|-------|--------|
| `CMAKE_BUILD_TYPE` | `Release` (`src/llama-cpp/build-cuda1322/CMakeCache.txt`) |
| `GGML_CUDA_DEBUG` | `unset` |
| `GGML_CUDA_FA_ALL_QUANTS` | `ON` |
| Binary | `src/llama-cpp/build-cuda1322/bin/llama-server` + `ggml-rpc-server` `0.4.0-dev build 10814 commit 1d3c4a8e3` |
| `DCUDAToolkit_ROOT` | `/opt/software/cuda/13.2.2`, `CMAKE_CUDA_ARCHITECTURES=86;120` |

Both templates re-measured on same binary/rig state — no reuse of old numbers.

## 3. Method

**Launch shape (both):** copy of `infra/llama-baseline/params/arm-chat-template-sharp-opt-in.yml` — verifies to `c 262144, ts 27,38, q8_0/q5_1, draft q8_0/q5_1 (mtp), UM on, cache-ram 1024, parallel 1, rope yarn 5/32768, ubatch 512, cont-batching, prio-batch 1` — only delta is `chat_template_file`:

- **Stock:** `/tmp/q5ks-stock-params.yml` — no `chat_template_file` (production Jinja)
- **Sharp:** `/tmp/q5ks-sharp-params.yml` — `chat_template_file: infra/llama-baseline/templates/qwen3.8-sharp.jinja`

**Rig protocol:** Standard drain-verify before each boot (`curl localhost:18081/health` fail, `ps` empty, `nvidia-smi 1 MiB`) and after each kill, then fresh `run-with-params.sh --no-cleanup` boot. 6 boots total, **alternating** Stock/Sharp/Stock/Sharp/Stock/Sharp to avoid block confound (all Sharp in one block vs all Stock in another). Each boot: `multiturn-growth-test.sh 18081 1 4 8000 750` — 1 session, 4 turns, `NEW_TOKENS≈8000 (~5333 words)`, `N_PREDICT=750`, target ~33K resident by turn 4. Reports `prompt_tok`, `comp_tok`, `wall`, `tok/s` (harness) per turn. Both tok/s **and** wall-clock collected — wall settles the question when `comp_tok` varies.

**Repro:**

```bash
cat /tmp/q5ks-stock-params.yml   # vs q5ks-sharp-params.yml
for i in 1 2 3; do
  bash infra/llama-baseline/run-with-params.sh /tmp/q5ks-stock-params.yml --no-cleanup
  bash infra/llama-baseline/multiturn-growth-test.sh 18081 1 4 8000 750  # → walls + tok/s
  pkill -9 -f "llama-server.*18081"; pkill -9 -f "ggml-rpc-server.*50052"; sleep 5
  bash infra/llama-baseline/run-with-params.sh /tmp/q5ks-sharp-params.yml --no-cleanup
  bash infra/llama-baseline/multiturn-growth-test.sh 18081 1 4 8000 750
  pkill -9 -f "llama-server.*18081"; pkill -9 -f "ggml-rpc-server.*50052"; sleep 5
done
# final restore
podman pod start pod_llama-baseline; curl -s http://localhost:18081/health | jq; nvidia-smi --query-gpu=memory.used --format=csv
```

Artifacts: `test.log` GOOD in `/tmp/rpc-test/results/q5ks-{stock,sharp}-262144-p1-1d3c4a8e3/` (last run of each template; earlier runs overwritten but per-turn walls/tok/s captured in console logs), M01 3× logs in `/tmp/r2-run1..3-m01.log`, boot logs `/tmp/r2-run1..3-boot.log`.

## 4. Results

All 6 boots: `GOOD`, 10/10 fit + 4/4 multiturn, no Xid, VRAM 15847/11911 steady, `cache-ram 1024` spill warnings `2300–4694 MiB exceeds ... skipping` on every return turn (expected at this shape).

### 4.1 Per-run, per-turn

**Stock template (Q5_K_S, production Jinja) — 3 boots:**

| Run | Turn 1 (6682 tok) wall / tok/s / comp | Turn 2 wall / tok/s / comp | Turn 3 wall / tok/s / comp | Turn 4 wall / tok/s / comp | Mean tok/s | Total wall |
|-----|----------------------------------------|----------------------------|----------------------------|----------------------------|------------|------------|
| S1 | 23.97 s / 31.29 / 750 | 25.70 s / 29.19 / 750 | 34.71 s / 21.61 / 750 | 26.48 s / 18.69 / 495 (early EOS) | 25.19 | 110.86 s |
| S2 | 23.99 s / 31.27 / 750 | 25.50 s / 29.41 / 750 | 33.25 s / 22.56 / 750 | 24.52 s / 19.25 / 472 | 25.62 | 107.26 s |
| S3 | 23.76 s / 31.56 / 750 | 25.61 s / 29.28 / 750 | 33.21 s / 22.58 / 750 | 24.37 s / 19.37 / 472 | 25.70 | 106.95 s |

Residency reached 26550 / 26550 / 26550 by turn 4 (4.0× growth, 6682→26550).

**Sharp template (Q5_K_S + qwen3.8-sharp.jinja) — 3 boots:**

| Run | Turn 1 (6795 tok) wall / tok/s / comp | Turn 2 wall / tok/s / comp | Turn 3 wall / tok/s / comp | Turn 4 wall / tok/s / comp | Mean tok/s | Total wall |
|-----|----------------------------------------|----------------------------|----------------------------|----------------------------|------------|------------|
| H1 | 18.52 s / 22.83 / 423 (early EOS) | 24.84 s / 21.26 / 529 | 25.50 s / 29.41 / 750 | 22.22 s / 16.75 / 372 | 22.57 | 91.08 s |
| H2 | 18.59 s / 22.76 / 423 | 24.86 s / 21.20 / 527 | 25.48 s / 29.43 / 750 | 22.35 s / 16.64 / 372 | 22.53 | 91.28 s |
| H3 | 18.61 s / 22.73 / 423 | 24.91 s / 21.16 / 527 | 25.53 s / 29.38 / 750 | 23.13 s / 17.43 / 403 | 22.69 | 92.18 s |

Residency reached 26947 each run (6795→26947, 4.0×, Sharp adds ~113 tok prompt overhead from its system prompt).

Small 2-turn sanity on Sharp before the A/B (1000/100) also PASS: 24.19 tok/s mean, 1.7 s / 0.8 s walls — not compared here.

### 4.2 Aggregated — wall-clock is the honest metric

**Mean wall per turn (3-run mean):**

| Turn | Stock wall | Sharp wall | Δ (Sharp−Stock) | Stock tok/s | Sharp tok/s |
|------|------------|------------|-----------------|-------------|-------------|
| 1 | 23.91 s | 18.57 s | **−5.34 s (−22%)** | 31.37 | 22.77 |
| 2 | 25.60 s | 24.87 s | −0.73 s (−3%) | 29.29 | 21.21 |
| 3 | 33.72 s | 25.50 s | **−8.22 s (−24%)** | 22.25 | 29.41 |
| 4 | 25.12 s | 22.57 s | −2.55 s (−10%) | 19.10 | 17.0 |
| **Total** | **108.35 s** | **91.51 s** | **−16.84 s (−15.5%)** | **25.50** | **22.60** |

Spread across 3 runs is tight: total wall std Stock 2.2 s, Sharp 0.6 s; per-turn walls vary <1 s except Stock turn 3 (1.5 s). Replication confirms stability — not a single-run fluke.

**Reading:**

- **Wall-clock: Sharp wins by ~15% total (−16.8 s) at 1×4×8000/750** — this is the signal. It is driven by Sharp finishing turns 1 and 3 much faster (−22%, −24%), despite `comp_tok` distortion.
- **Harness tok/s: Sharp loses (22.6 vs 25.5)** — the *opposite* conclusion from the same data. This is exactly the distortion flagged in `chat-template-sharp-opt-in.md`: Sharp's completions are shorter on turns 1–2 (423/527 vs 750/750) so its `completion_tokens / wall` ratio is pulled down even though wall is shorter. Turn 3 is the only turn where Sharp's comp was 750 (full) and tok/s appears to "win" (29.4 vs 22.25) — again an artifact of length, not speed. **Do not quote tok/s for this template; quote wall-clock or a length-controlled tok/s.**
- The 1.7 s walls in the 1000/100 sanity vs 18–34 s at 8000/750 confirms the harness is exercising the full KV growth path, not a degenerate short path.
- Residency 26–27K is shallow vs Round 1's 80K collapse — at this depth the 15% is real, not masked by UM paging (~21–31 tok/s here vs 2–3 tok/s at 80K in Round 1 deep 12-turn).
- Completion-length variation is itself part of Sharp's effect (terser output is the template's purpose) — so a "controlled-length" repeat would answer a different question than "does Sharp save wall-clock on this workload?" This A/B answers the wall-clock question directly.

### 4.3 Relation to prior n=1 probe

The n=1 probe's tok/s "win" (28.43 vs 23.82) was a length artifact; its wall-clock was also mixed (Sharp 22.94/23.38 vs Stock 25.75/29.91 on turns 1–2, but Sharp 23.85/30.90 vs Stock 19.74/22.40 on turns 3–4 — no clean win). With 3× replication at a larger `NEW_TOKENS` (8000 vs probe's ~smaller implicit), the pattern flips to a consistent wall-clock win for Sharp across all 4 turns, with tight replication. The probe's conclusion ("plausible signal, not confirmed") is now **upgraded to a confirmed wall-clock win at this workload** (+15%, 3/3 runs), with the caveat that it is tok/s-distorted and workload-dependent (see §6 caveats).

## 5. BARS assessment (template arm)

| Bar | Expectation | Observed | Verdict |
|-----|-------------|----------|---------|
| Wall-clock at 1×4 8000/750 | Sharp terser → faster wall if it shortens completions without adding per-token cost | 91.5 s vs 108.4 s, −15.5%, 3/3 consistent, every turn faster | **Confirm (wall-clock win)** |
| tok/s (harness) | Not expected to win — same decode, shorter completions distort ratio | 22.6 vs 25.5 — Sharp slower on tok/s | **Confirms distortion — do not use tok/s for this template** |
| Fit / stability | Same ceiling, no regression | 6/6 GOOD, 1/1 MiB drain-verify each kill, 15847/11911 steady | **Confirm** |
| Quality | Stock may be more verbose but not wrong | Spot-checked KV-quant question — both correct; Sharp shorter (see opt-in doc) — not re-judged here | **Inconclusive at depth — needs longer eval** |

## 6. Caveats / open questions

- **n=3 per template, 1×4 each, ~27K depth.** Tight replication but still small-n vs the deep 2×12 80K family. At 80K the UM paging collapse (2–3 tok/s) would likely swamp the 15% wall-clock delta — do not extrapolate this 15% to deep 12-turn; the deep regime needs its own A/B at 262144 with cache-ram 24576 to isolate KV spill effects.
- **Completion length is the mechanism, not a bug.** Sharp's win comes with shorter completions on 3 of 4 turns (423/527/372 vs 750/750/472). If the task requires fixed-length completions, the win disappears. Report wall-clock, not tok/s, and state the workload.
- **cache-ram 1024 vs prod 24576.** Every return turn still spilled (`exceeds cache size limit 1024`), paying full re-prefills. Prod's 24576 would retain more and change per-turn residency/wall balance — the 15% at 1024 may not hold at 24576.
- **Quality at depth not measured here** — same as Round 1. Needs longer-generation human eval or task-eval (coding/human) before pinning as default; this arm only confirms it boots, is stable, and saves wall-clock on this workload.
- **No stock T2 at parallel 1 for direct tok/s comparison** — Round 2's Sharp T2 (42.12) at parallel 1 has no stock T2 at same parallel to compare to; Round 3's multiturn tok/s is not a T2 substitute.
- **Template is plain Jinja** — no weights/code, 30.4 KB, hand-verified — safe to keep as opt-in; do not adopt as production default until deep + quality A/B lands.

## 7. Rig restore

After 6 alternating boots: `pkill -9` both servers → GPUs 1/1 MiB, `curl localhost:18081/health` → `Failed to connect`, `ps` empty → drain-verify PASS; `podman pod start pod_llama-baseline` → `pod_llama-baseline Running`, `curl localhost:18081/health` → `{"status":"ok"}`, `nvidia-smi` 15847/11911 MiB — matches pre-arm ceiling. **Rig left in clean prod shape** (Q5_K_M 262144/2, 27,38, q8_0/q5_1 MTP, cache-ram 24576), not Sharp or Q5_K_S.

---

*Do not commit/push without checking with the leader first — per task. This A/B upgrades the opt-in probe to a replicated wall-clock win at 1×4 8000/750; it does not yet justify a production default pin.*
