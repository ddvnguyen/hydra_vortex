# Arm — Sharp vs Stock at deep depth + production cache-ram (Round 4)

> Branch `feat/703-baseline-consolidated` — worktree `/mnt/WorkDisk/workspace/worktree/1q3ry0vb/majestic-toad`  
> Rig: RTX 5060 Ti 16 GB sm_120 CUDA0 + RTX 3060 12 GB sm_86 CUDA1, driver 595.91 CUDA 13.2, toolkit `/opt/software/cuda/13.2.2`  
> Model: `-m /mnt/SSD/Qwen3.8-27B-UD-Q5_K_S.gguf` (18 GB) for both arms — only chat template differs  
> Date: 2026-09-12 01:25–03:07 Asia/Bangkok (4 deep + 2 shallow boots)  
> Author: config-only arm test (no code change) — 6 alternating boots, drain-verify each

Closes the two caveats from `docs/arms/chat-template-sharp-q5ks-abtest.md` §6 (Round 3): the 15.5% wall-clock win was **shallow (~27K, 1×4) and at `cache_ram 1024`**, never checked at **production `cache_ram 24576`** or at **real depth where UM paging collapses** (`~7 tok/s` at 60–80K in Round 1 deep 2×12). This Round 4 is the production-relevant test.

**Question:** does the shallow win hold, shrink, vanish, or reverse once the workload is deep (2×12, ~80–82K resident) and `cache_ram 24576` is used? Also: does `cache_ram 24576` change the picture even shallow?

## 1. Goal (from task)

Same Q5_K_S, same launch shape as Round 3 **but** `cache_ram_mib: 24576` (production value) and the full **2-session/12-turn deep** shape from Round 1 (`multiturn-growth-test.sh 18081 2 12 8000 750`, ~80–103K target, ~5333 words new/turn, 750 out). With each deep run ≈ 700–810 s wall, 2 replicates per template is enough — prioritize signal over rig-time, note if cut to 1. Also check `cache_ram 24576` at shallow if budget remains. Report **both harness tok/s and wall-clock-per-turn** — wall answers the question, tok/s will be distorted by `comp_tok` variation as in Round 3.

## 2. Build provenance (B01, shared)

| Check | Result |
|-------|--------|
| `CMAKE_BUILD_TYPE` | `Release` (`src/llama-cpp/build-cuda1322/CMakeCache.txt`) |
| `GGML_CUDA_DEBUG` | `unset` |
| `GGML_CUDA_FA_ALL_QUANTS` | `ON` |
| Binary | `src/llama-cpp/build-cuda1322/bin/llama-server` + `ggml-rpc-server` `0.4.0-dev build 10814 commit 1d3c4a8e3` (`feat/server --parallel-ctx-threshold` + UM) |
| `DCUDAToolkit_ROOT` | `/opt/software/cuda/13.2.2`, `CMAKE_CUDA_ARCHITECTURES=86;120` |

All 6 boots re-measured on same binary/rig state.

## 3. Method

**Launch shape — deep (4 boots):** `ctx 262144, ts 27,38, q8_0/q5_1, draft q8_0/q5_1 mtp, UM on, `**`cache_ram 24576`**, `rope yarn 5/32768, ubatch 512, cont-batching, prio-batch 1, no-kv-unified, cache-reuse 64, jinja, parallel 2`.

*Why `parallel 2` not Round 3's `parallel 1`?* Round 3 shallow used `parallel 1` for 1 session. Deep needs **2 concurrent sessions** with genuine overlap (same as Round 1 deep, which used `-np 2`). With `parallel 1` the second session would queue behind the first — the overlap check would never be genuine and wall would include queuing, not true concurrent decode. Production pod itself runs `parallel 2` (`ps aux` on restore: `--parallel 2 --cache-ram 24576`), so `parallel 2` is also the production-relevant choice. Only delta between Stock and Sharp is `chat_template_file`:

- **Stock deep:** `/tmp/q5ks-stock-24576-p2.yml` — no `chat_template_file`
- **Sharp deep:** `/tmp/q5ks-sharp-24576-p2.yml` — `chat_template_file: infra/llama-baseline/templates/qwen3.8-sharp.jinja` (30.4 KB, `qwen3.8-froggeric-v22.5.0`)

**Shallow sanity (2 boots, optional):** same but `parallel 1, 1×4` — `q5ks-stock-24576-p1.yml` / `q5ks-sharp-24576-p1.yml` — to isolate `cache_ram` effect at shallow depth.

**Rig protocol:** Standard drain-verify before each boot (`curl localhost:18081/health` fail, `ps` no llm, `nvidia-smi 1/1 MiB`) and after each kill (explicit `kill -9 <pid>` + 4 s settle). 6 boots **alternating Stock→Sharp→Stock→Sharp→Stock-shallow→Sharp-shallow** to avoid block confound. Each boot: `run-with-params.sh --no-cleanup` (10-req health loop) then `multiturn-growth-test.sh` (deep 2×12 or shallow 1×4). Both wall and tok/s captured — wall is the decision metric.

**Repro:**

```bash
# deep 2×12
cat /tmp/q5ks-stock-24576-p2.yml  # vs sharp
bash infra/llama-baseline/run-with-params.sh /tmp/q5ks-stock-24576-p2.yml --no-cleanup
bash infra/llama-baseline/multiturn-growth-test.sh 18081 2 12 8000 750
# kill, drain-verify, alternate …
# shallow
bash infra/llama-baseline/run-with-params.sh /tmp/q5ks-stock-24576-p1.yml --no-cleanup
bash infra/llama-baseline/multiturn-growth-test.sh 18081 1 4 8000 750
# restore
podman pod start pod_llama-baseline; curl -s http://localhost:18081/health | jq; nvidia-smi --query-gpu=memory.used --format=csv
```

Artifacts: `/tmp/r4-stock1-deep.log` (01:38), `/tmp/r4-sharp1-deep.log` (01:52), `/tmp/r4-stock2-deep.log` (02:06), `/tmp/r4-sharp2-deep.log` (02:18), shallow `/tmp/r4-stock-shallow-deep.log` (02:23, Stock 1×4) + `/tmp/r4-sharp-shallow-deep.log` (03:04, Sharp 1×4), boot logs `/tmp/r4-*-boot.log`, results dirs `/tmp/rpc-test/results/q5ks-*-24576-*-1d3c4a8e3/`.

## 4. Rig health

- Pre-deep: `pod_llama-baseline Running`, health `{"status":"ok"}`, `15847/11911` → `podman pod stop` → `1/1 MiB`, `Failed to connect`, `ps` only `qemu` → PASS.
- Each deep boot: `GOOD` (10/10 health loop), `llama-server ready 58–63 s`, VRAM steady during runs `15847/11911`, `slots` `is_processing` true while active, `is_processing false` after completion (idle task 3256 after Stock-shallow — completed, not stalled). No Xid — all `test.log` `Xid check` GOOD, no `Xid`/`NVRM` in `llama-server.log` tails; `dmesg` not readable without sudo but journal shows no GPU error. Shallow Stock-p1 was **not stalled** — its log shows `4/4 complete, 24.09/25.74/33.31/24.46 walls, mean 25.52` and `slots` correctly idled after.
- Final restore: `podman pod start pod_llama-baseline` → 56 s loading `11241/7269` → after 60 s `{"status":"ok"}` `15847/11911` — matches pre-arm ceiling.

## 5. Results — deep 2×12 (production cache_ram, ~81K, 2 replicates per template = 4 sessions per template)

All 4 deep runs: **PASS** — 12/12 turns each session, **20–22 overlapping turn-pairs, 84–95% overlap**, `concurrency check: PASS`. Residency reached `80731–81841` Stock, `80691–80847` Sharp (~11.9–12.2× growth, 6682→~81K Stock, 6795→~80K Sharp; Sharp +113 tok prompt overhead from its system prompt, same as Round 3).

### 5.1 Per-run, per-turn walls + tok/s (the raw truth)

**Stock 24576 p2 — R1 (01:25, 63s boot):**

| session | turn1 | turn2 | turn3 | turn4 | turn5 | turn6 | turn7 | turn8 | turn9 | turn10 | turn11 | turn12 | mean tok/s | total wall |
|---------|-------|-------|-------|-------|-------|-------|-------|-------|-------|--------|--------|--------|------------|------------|
| S1 | 37.66s 19.91 750 | 57.46 13.05 750 | 38.85 19.31 750 | 50.31 12.40 624 | 61.39 12.22 750 | 55.96 13.40 750 | 39.52 5.59 221 | 38.34 5.19 199 | 41.12 5.11 210 | 25.68 5.92 152 | 40.93 2.47 101 | 23.16 2.94 68 | 9.79 | 510.4 s |
| S2 | 49.55 15.14 750 | 32.82 22.85 750 | 68.06 11.02 750 | 50.91 12.08 615 | 32.51 12.49 406 | 41.25 9.33 385 | 40.58 6.85 278 | 46.26 8.24 381 | 42.40 6.60 280 | 41.96 5.96 250 | 64.65 4.19 271 | 26.74 9.72 260 | 10.37 | 537.7 s |

`prompt_tok 6682 → 81841/81408`, overlap 22 pairs 510.4 s 94.9%.

**Stock 24576 p2 — R2 (01:53, 61s boot):**

| S1 | 37.71 19.89 750 | 57.48 13.05 750 | 41.31 18.16 750 | 68.34 10.98 750 | 38.75 15.95 618 | 60.89 8.62 525 | 61.02 6.34 387 | 71.22 6.73 479 | 52.51 6.91 363 | 21.86 6.72 147 | 21.97 8.51 187 | 23.15 7.99 185 | 10.82 | 556.2 s |
| S2 | 37.96 19.76 750 | 45.62 16.44 750 | 65.90 11.38 750 | 36.09 14.60 527 | 75.74 9.90 750 | 27.76 10.77 299 | 35.56 4.50 160 | 24.80 8.87 220 | 37.79 4.92 186 | 27.29 7.29 199 | 43.18 4.26 184 | 27.01 4.92 133 | 9.80 | 484.7 s |

`→ 81265/80752`, overlap 20 pairs 484.7 s 87.1%.

**Sharp 24576 p2 — R1 (01:41, 62s boot):**

| S1 | 35.07 14.51 509 | 31.54 11.13 351 | 42.77 17.54 750 | 21.11 10.89 230 | 27.23 4.63 126 | 17.47 6.36 111 | 30.98 4.23 131 | 21.89 6.94 152 | 33.12 4.05 134 | 36.94 4.39 162 | 38.77 3.17 123 | 41.03 3.29 135 | 7.59 | 377.9 s |
| S2 | 46.24 16.22 750 | 41.86 17.92 750 | 48.97 10.31 505 | 54.57 6.32 345 | 51.17 4.47 229 | 36.02 5.05 182 | 38.80 4.23 164 | 39.61 3.61 143 | 20.67 3.97 82 | 27.61 10.47 289 | 19.62 5.15 101 | 23.27 7.56 176 | 7.94 | 448.4 s |

`6795→80731/80847`, overlap 21 pairs 377.9 s 84.3%.

**Sharp 24576 p2 — R2 (02:07, 58s boot):**

| S1 | 35.12 14.49 509 | 31.56 11.12 351 | 40.89 16.90 691 | 20.46 10.80 221 | 57.83 12.97 750 | 35.51 7.18 255 | 33.87 4.25 144 | 31.31 3.10 97 | 49.19 2.99 147 | 60.91 3.15 192 | 21.21 2.50 53 | 22.06 4.49 99 | 7.83 | 439.9 s |
| S2 | 46.30 16.20 750 | 41.88 17.91 750 | 53.36 9.52 508 | 24.72 11.49 284 | 37.19 7.37 274 | 32.09 5.42 174 | 33.77 3.88 131 | 29.53 2.98 88 | 20.14 4.12 83 | 34.62 2.31 80 | 22.04 3.22 71 | 41.08 1.92 79 | 7.20 | 416.7 s |

`→ 80691/80751`, overlap 22 pairs 416.7 s 94.7%.

### 5.2 Aggregated — wall-clock is the answer, tok/s is the distortion

**Mean wall per turn (4 sessions per template, i.e. 2 boots ×2 sessions):**

| Turn | Stock avg wall | Sharp avg wall | Δ (Sharp−Stock) | Stock avg tok/s | Sharp avg tok/s |
|------|----------------|----------------|-----------------|-----------------|-----------------|
| 1 | 40.72 s | 40.68 s | −0.04 s (~0%) | 18.68 | 15.36 |
| 2 | 48.35 s | 36.71 s | **−11.64 s (−24%)** | 16.35 | 14.52 |
| 3 | 53.53 s | 46.50 s | −7.03 s (−13%) | 14.97 | 13.57 |
| 4 | 51.41 s | 30.22 s | **−21.20 s (−41%)** | 12.52 | 9.88 |
| 5 | 52.10 s | 43.36 s | −8.74 s (−17%) | 12.64 | 7.36 |
| 6 | 46.47 s | 30.27 s | **−16.19 s (−35%)** | 10.53 | 6.00 |
| 7 | 44.17 s | 34.36 s | −9.82 s (−22%) | 5.82 | 4.15 |
| 8 | 45.16 s | 30.59 s | −14.57 s (−32%) | 7.26 | 4.16 |
| 9 | 43.46 s | 30.78 s | −12.68 s (−29%) | 5.89 | 3.78 |
| 10 | 29.20 s | 40.02 s | +10.82 s (+37%) | 6.47 | 5.08 |
| 11 | 42.68 s | 25.41 s | **−17.27 s (−40%)** | 4.86 | 3.51 |
| 12 | 25.02 s | 31.86 s | +6.85 s (+27%) | 6.39 | 4.32 |
| **Total (12 turns)** | **522.2 s** | **420.7 s** | **−101.5 s (−19.4%)** | **10.20** | **7.64** |
| **Mean per turn** | 43.52 s | 35.06 s | −8.46 s | — | — |
| **Mean per session total** | 522.2 s | 420.7 s | — | — | — |

Replication tight: Stock session totals 510.4/537.7 vs 556.2/484.7 (range 485–556 s, ±7% around mean); Sharp 377.9/448.4 vs 439.9/416.7 (378–448 s). No single outlier drives the win — Sharp wins 10 of 12 turns on average, loses only turns 10 and 12 (both deep, high-variance early-EOS turns where `comp_tok` fell to 53–192 and wall was dominated by tail latency).

**Reading — the question from the Round 4 brief:**

> Does the 15.5% shallow win hold, shrink, vanish, or reverse once UM paging collapse dominates?

**Answer: it grows to ~19.4% at deep depth with production `cache_ram 24576`.** At 1024 the deep collapse was `~7 tok/s mean at 80K` (Round 1 §5.3, same 8000/750 shape but cache spill). At 24576 the collapse is **less severe but still present**: Stock 10.2 mean, Sharp 7.6 mean at ~81K — still half the shallow 25 tok/s, confirming the UM paging regime still dominates. Yet Sharp's wall-clock win **not only holds but widens** (−19.4% vs −15.5% shallow-1024). The reason is the same as shallow: Sharp shortens completions (`comp_tok` 53–509 vs Stock 68–750, many turns at 100–200) and its Jinja/system-prompt overhead (+113 prompt tok) is amortized over 80K, so per-turn wall is saved even though `completion_tokens / wall` (harness tok/s) looks *worse* (7.64 vs 10.20 — the opposite conclusion). **Do not quote tok/s for this template; quote wall-clock or a length-controlled tok/s.** The task-completion speed claim (−19% wall) is confirmed at depth, not a tok/s effect.

### 5.3 Shallow at 24576 — does `cache_ram` change the picture?

Optional bonus, but we had budget and did it (1×4, `parallel 1`, same 8000/750, now at 24576):

|  | Turn1 wall/tok/s/comp | Turn2 | Turn3 | Turn4 | Total wall | Mean tok/s | prompt 6682→ |
|---|----------------------|-------|-------|-------|------------|------------|--------------|
| **Stock 24576 p1** (02:18, 52s boot) GOOD | 24.09s 31.13 750 | 25.74 29.14 750 | 33.31 22.51 750 | 24.46 19.30 472 | **107.6 s** | 25.52 | 26550 |
| **Sharp 24576 p1** (03:00, 21s boot) GOOD | 19.04 22.22 423 | 23.05 21.39 493 | 16.20 14.39 233 | 13.56 9.15 124 | **71.9 s** | 16.78 | 27009 (+113) |

Δ total **−35.8 s (−33.3%)** — Sharp wins 4/4 turns shallow at 24576, and the win is **more than double** the 15.5% seen at `1024` (Round 3 shallow avg 108.35 vs 91.51). At `1024` Stock shallow totals were 110.86/107.26/106.95 (avg 108.35) and Sharp 91.08/91.28/92.18 (avg 91.51) — i.e. Stock is unchanged by `cache_ram` (107.6 vs 108.35), but Sharp **improves 71.9 vs 91.5** with the larger host cache. Production never runs at 1024, so the **production-relevant shallow result is the 33% win, not the 15.5%**. The deep 19% at 24576 is the true depth result; the 15.5% at 1024 understated Sharp at both depths.

Note shallow `comp_tok` again explains tok/s inversion: Sharp 423/493/233/124 vs Stock 750/750/750/472 — shorter by design, so `tok/s = comp/wall` is ~16.78 vs 25.52 (Sharp slower on tok/s, faster on wall).

## 6. BARS assessment (Round 4)

| Bar | Expectation | Observed | Verdict |
|-----|-------------|----------|---------|
| Deep wall-clock at 24576 2×12 | UM collapse (~7 tok/s) may swamp template delta; 15.5% may vanish | 420.7 s vs 522.2 s, **−19.4%**, 4 sessions, 2 replicates per template, 10/12 turns faster | **Win holds and grows at depth** |
| Shallow wall at 24576 | Production cache_ram may change picture vs 1024 | 71.9 vs 107.6 s, **−33.3%** at 24576 vs −15.5% at 1024 | **Win grows with production cache_ram** |
| tok/s (harness) | Distorted by early-EOS, not expected to win | 7.64 vs 10.20 deep, 16.78 vs 25.52 shallow — Sharp slower on tok/s | **Confirms distortion — report wall** |
| Fit / stability | Same ceiling, genuine overlap, no regression at 24576 | 6/6 GOOD, 20–22 overlap 84–95% PASS, 1/1 drain each kill, 15847/11911 steady, no Xid | **Confirm** |
| Quality | May be terser but not wrong | Sharp consistently shorter `comp_tok` (e.g. deep turns 5–12 often 50–250 vs Stock 100–750); spot-check not re-done here — see Round 3 opt-in for correctness | **Wall win is via terser output — quality eval still needed before default** |

## 7. Caveats / open questions

- **n=2 per template deep (4 sessions)** — tight replication (485–556 s Stock vs 378–448 s Sharp) but still small-n vs the deep collapse regime. Not proof of quality parity — Sharp's win is **via shorter completions**, so if the task requires fixed-length 750-token completions the win disappears. Report wall-clock **with workload**, not tok/s, and state the workload.
- **Production shape detail:** deep here used `parallel 2` (needed for 2-session genuine overlap; production pod runs `parallel 2`). If the task truly wanted `parallel 1` at deep, the second session would serialize — wall would include queuing, not concurrent decode. The choice is documented; a `parallel 1` deep repeat would answer a different question.
- **Quality at depth not re-measured** — same as Rounds 1–3. Needs longer-generation human eval or task eval (coding/human) before pinning Sharp as production default; this arm only confirms it boots, is stable at 24576, and saves wall-clock both shallow and deep.
- **VRAM masking by UM** still applies — `nvidia-smi` 15847/11911 during all 6 boots, same ceiling, host RAM paging pays the delta (see `rpc-server.log`).
- **Shallow 24576 n=1 per template** — not replicated (budget left only 1 per side); deep is the replicated result (n=2). Shallow 24576 33% should be replicated before quoting as a stable shallow number.

## 8. Rig restore + repro

**Restore (03:06):** `kill -9` Sharp-shallow → `1/1 MiB`, `Failed to connect`, `ps` only `qemu` → PASS; `podman pod start pod_llama-baseline` → `Running`, 6 s `11241/7269` loading → 60 s `{"status":"ok"}`, `15847/11911` — matches pre-arm ceiling. **Rig left in clean prod shape** (`Q5_K_M 262144/2, 27,38, q8_0/q5_1 mtp, cache_ram 24576, parallel 2`), not Q5_K_S/Sharp.

**Repro:**

```bash
# deep 24576 p2
cat /tmp/q5ks-stock-24576-p2.yml  # vs q5ks-sharp-24576-p2.yml
bash infra/llama-baseline/run-with-params.sh /tmp/q5ks-stock-24576-p2.yml --no-cleanup
bash infra/llama-baseline/multiturn-growth-test.sh 18081 2 12 8000 750
# alternate, kill, drain-verify, repeat
# shallow 24576 p1 bonus
bash infra/llama-baseline/run-with-params.sh /tmp/q5ks-stock-24576-p1.yml --no-cleanup
bash infra/llama-baseline/multiturn-growth-test.sh 18081 1 4 8000 750
```

Results dirs: `/tmp/rpc-test/results/q5ks-stock-24576-p2-deep-1d3c4a8e3/` (and sharp, and shallow), per-turn logs `/tmp/r4-stock1-deep.log` etc., shallow `/tmp/r4-stock-shallow-deep.log` etc.

---

*Do not commit/push without checking with the leader first — per task. This deep A/B upgrades Round 3's shallow-1024 15.5% to a production-relevant result: −19.4% deep and −33.3% shallow at `cache_ram 24576`, but only via shorter completions — not a tok/s win.*

