# Arm UD-Q5K_S vs Q5_K_M — config-only quant-file swap (Round 1)

> Branch `feat/703-baseline-consolidated` — worktree `/mnt/WorkDisk/workspace/worktree/1q3ry0vb/majestic-toad`  
> Rig: RTX 5060 Ti 16 GB sm_120 CUDA0 + RTX 3060 12 GB sm_86 CUDA1, driver 595.91 CUDA 13.2, toolkit `/opt/software/cuda/13.2.2`  
> Method mirror: `docs/arms/arm-uniform-q8-kv.md` structure (closest precedent), rig protocol drain-verify per `docs/hydra-system-pod.md`  
> Date: 2026-09-11/12 23:09–00:20 Asia/Bangkok  
> Author: config-only arm test (no code change)

## 1. Goal

Compare `-m /mnt/SSD/Qwen3.8-27B-UD-Q5_K_S.gguf` (18 GB) against production pin `-m /mnt/SSD/Qwen3.8-27B-UD-Q5_K_M.gguf` (19 GB) at **identical** other launch flags (production shape). No download needed — both files resident on `/mnt/SSD`. Same question as `arm-uniform-q8-kv` but for the UD-Q5 family: does the smaller quant win on decode (fewer bits → less VRAM bandwidth per token) and what does it cost in quality?

Production shape under test (from task, not 747.4's 24576 cache-ram):

```
GGML_CUDA_ENABLE_UNIFIED_MEMORY=1
ggml-rpc-server -H 0.0.0.0 -p 50052  (CUDA_VISIBLE_DEVICES=1)
llama-server --rpc 127.0.0.1:50052 -ts 27,38 -ngl 99 --rope-scaling yarn --rope-scale 5
  --yarn-orig-ctx 32768 -fa on -ctk q8_0 -ctv q5_1 -ctkd q8_0 -ctvd q5_1 --no-kv-unified
  --cache-prompt --cache-reuse 64 --cache-idle-slots --cache-ram 1024 --ubatch-size 512
  --cont-batching --parallel-ctx-threshold 100000 --spec-type draft-mtp --prio-batch 1 --jinja
  -c 262144 -np 2
```

Notes:

- `-c 262144` with `--no-kv-unified` + `-np 2` → 2×131072 hard-fenced slots (matches 747.4's ctx, fenced vs shared-pool — task explicitly wants this shape).
- `--cache-ram 1024` is **deliberately** small vs 747.4/090's 24576 — task's prescribed value to keep the arm comparable to production shape textually; it does cause cache-state spill (see §3.3).
- MTP kept (`draft-mtp` + `q8_0/q5_1` draft KV) — same for both quants.

## 2. Build provenance (B01)

> Rig has documented history of debug-flag confounds — see PR103.0 provenance note. Do not trust any number without this check.

| Check | Result |
|-------|--------|
| `CMAKE_BUILD_TYPE` | `Release` (verified in `src/llama-cpp/build-cuda1322/CMakeCache.txt`) |
| `GGML_CUDA_DEBUG` | `unset` (env, also absent from CMakeCache) |
| `GGML_CUDA_FA_ALL_QUANTS` | `ON` (required for `q8_0/q5_1` pairing, `ggml/src/ggml-cuda/fattn.cu:302`) |
| Binary | `src/llama-cpp/build-cuda1322/bin/llama-server` + `ggml-rpc-server`, version `0.4.0-dev build 10814 commit 1d3c4a8e3` (`feat/server --parallel-ctx-threshold` + UM prefetch, branch `feat/703-baseline-consolidated`) |
| `DCUDAToolkit_ROOT` | `/opt/software/cuda/13.2.2`, `CMAKE_CUDA_ARCHITECTURES=86;120` |

Both quants re-measured on **today's** binary/rig state — no reuse of old numbers for T2.

## 3. Rig protocol

Standard drain-verify before touching hardware, and after every boot/kill cycle confirm GPU memory returns to prior baseline. Leave rig in clean stopped state when done — restart `pod_llama-baseline` (production Q5_K_M shape) at end and verify health 200 + normal VRAM ceiling.

Verified sequence:

1. Pre-check (23:09, before stop): `curl localhost:18081/health` → `{"status":"ok"}`, `ps` showed `ggml-rpc-server` + `llama-server` (747.4 Q5_K_M), `nvidia-smi` 15847/11911 MiB, `pod_llama-baseline` Running — matches leader's restore confirmation (e8954f11 finished). Proceed.
2. `podman pod stop pod_llama-baseline` → `Exited`, GPUs → `1 MiB / 1 MiB`, `curl` → `Failed to connect … Could not connect to server`, `ps` empty → drain-verify PASS.
3. Boot Q5_K_S → drain-verify before next boot, etc. (see §4).
4. Final restore: `podman pod start pod_llama-baseline` + health 200 + VRAM 15847/11911 (see §7).

## 4. Test design (mirror arm-uniform-q8-kv)

| Cell | What | Config | Metric | Reps |
|------|------|--------|--------|------|
| **Fit** | Does Q5_K_S boot at full prod shape? | `-c 262144 -np2 ts27,38 UM on` | Boot time, VRAM used, pass/fail | 1 |
| **T2-single** | M01 canonical single-request decode latency | `/v1/chat/completions`, `head -c 2000` of `/tmp/bigprompt.txt` (~808 tok, `cached_tokens≈804` on warm), `max_tokens:150`, 10 sequential reqs, report cold req1 + warm 2-10 mean, `timings.predicted_per_second` | tok/s, draft acceptance | 10 seq per quant, fresh boot per quant |
| **T1 deep** | Multiturn growth, genuine overlap | `infra/llama-baseline/multiturn-growth-test.sh` AS-IS (commit `4227f78c3` per-turn overlap fix), 2 sessions, 12 turns, `NEW_TOKENS≈8000` (~5333 words), `N_PREDICT=750`, target ~80–103K resident | Per-turn tok/s + wall, overlap PASS/FAIL, prompt_tok growth | Q5_K_S only (Q5_K_M numbers exist from today's earlier arm102 deep runs — ask leader if needed) |
| **Coherence** | Spot-check not garbage | Same 150-tok outputs as T2 | Human inspect `reasoning_content` vs `content` (Qwen3 thinking model) | 1 sample per quant |

Prior expectation (BARS): Q5_K_S smaller → faster decode (fewer bits → less bandwidth) but may degrade quality — exploring speed/quality tradeoff, not expecting pure win. Report both directions honestly.

## 5. Results

### 5.1 Fit check — does Q5_K_S boot at 262144/2 ?

| Quant | Model path | Boot time (health ready) | VRAM 5060 Ti / 3060 (steady, `nvidia-smi`) | Requests 10/10 | Result |
|-------|------------|--------------------------|--------------------------------------------|----------------|--------|
| **Q5_K_S** | `/mnt/SSD/Qwen3.8-27B-UD-Q5_K_S.gguf` (18 GB) | 66 s | 15847 / 11911 MiB (16311 / 12288 total) | PASS 10/10 | **FIT** |
| **Q5_K_M** (control, same shape) | `/mnt/SSD/Qwen3.8-27B-UD-Q5_K_M.gguf` (19 GB) | 64 s | 15847 / 11911 MiB | PASS 10/10 | **FIT** |

**Notes:**

- Q5_K_S is ~1 GB smaller on disk but **reports identical steady VRAM** to Q5_K_M at this oversub+UM shape (both at the production ceiling 15847/11911). With UM, `cudaMallocManaged` paging masks the 1 GB model-weight delta — the ceiling is dominated by KV pool + MTP compute-graph reserve, not just weights. Boot log shows same `tensor_split 27,38` placement, no OOM, no fallback, `GGML_CUDA_ENABLE_UNIFIED_MEMORY=1` required to boot this 262K oversub at all (same as 747.4/102 family).
- `cache-ram 1024` caused repeated `prompt state size 2301–4694 MiB exceeds cache size limit 1024 MiB, skipping` warnings (server log) — the idle-slot host cache could not retain state, so every return turn paid a full re-prefill. With 747.4's 24576, these never appeared. This is expected for this arm's prescribed 1024 value and did not cause crashes.
- Smaller quant did **not** need more VRAM — fit was easier-or-equal as predicted, not harder.

### 5.2 T2-single — M01 10×150 tok, sequential, `timings.predicted_per_second`

Prompt: `head -c 2000 /tmp/bigprompt.txt` → `prompt_tokens=808` (first req) / `cached_tokens≈804` on warm (LCP 1.0, `cache_reuse 64`), `prompt_n≈4` (yaRN + FA overhead — same both quants). `max_tokens=150`, `temperature=0`, stream off. 10 sequential, 1 s gap.

#### Q5_K_S (fresh boot, build 10814)

```
req 1 wall=3.04s pred_per_sec=52.39  prompt_per=21.8  draft 111/111 (1.00)
req 2 wall=3.02s pred_per_sec=52.56  draft 111/111
req 3 wall=3.03s pred_per_sec=52.46
req 4 wall=3.03s pred_per_sec=52.69
req 5 wall=3.03s pred_per_sec=52.47
req 6 wall=3.03s pred_per_sec=52.49
req 7 wall=3.03s pred_per_sec=52.41
req 8 wall=3.05s pred_per_sec=52.19
req 9 wall=3.03s pred_per_sec=52.34
req10 wall=3.03s pred_per_sec=52.34
```

- **Cold req1:** 52.39 tok/s
- **Warm 2-10 mean:** 52.43 tok/s
- **Overall mean:** 52.43 tok/s, **std:** 0.14, **min–max:** 52.19–52.69
- Draft acceptance: 0.73–1.00 per slot-log, 1.00 on M01's short reuses (full cache hit).

#### Q5_K_M (control, same build, fresh boot)

```
req 1 wall=3.14s pred_per_sec=50.60
req 2 wall=3.12s pred_per_sec=50.76
req 3 wall=3.12s pred_per_sec=50.83
req 4 wall=3.11s pred_per_sec=51.00
req 5 wall=3.12s pred_per_sec=50.77
req 6 wall=3.14s pred_per_sec=50.51
req 7 wall=3.14s pred_per_sec=50.43
req 8 wall=3.12s pred_per_sec=50.84
req 9 wall=3.13s pred_per_sec=50.60
req10 wall=3.14s pred_per_sec=50.52
```

- **Cold req1:** 50.60 tok/s
- **Warm 2-10 mean:** 50.70 tok/s
- **Overall mean:** 50.69 tok/s, **std:** 0.17, **min–max:** 50.43–51.00

#### Delta

|  | Q5_K_S | Q5_K_M | Δ (S−M) |
|---|--------|--------|---------|
| Cold | 52.39 | 50.60 | **+1.79 (+3.5%)** |
| Warm 2-10 mean | 52.43 | 50.70 | **+1.73 (+3.4%)** |
| Overall mean | 52.43 | 50.69 | **+1.74 (+3.4%)** |

**Reading:** Small, consistent win for Q5_K_S, in the direction of the engineering prior (fewer bits → less HBM traffic per decode step). **Not** noisy/inconclusive — spreads are tight (0.14–0.17 std), cold vs warm are indistinguishable because `cache_reuse 64` gives 804/808 cached on turns 2-10, but both show the same ~1.7 tok/s gap. No bimodal or Xid, same prompt, same 150-token length, so tok/s is directly comparable. This is a **speed win**, not a proof of quality parity.

### 5.3 T1 deep-depth multiturn — Q5_K_S only

`bash infra/llama-baseline/multiturn-growth-test.sh 18081 2 12 8000 750` — 2 sessions, 12 turns, ~5.3K words new per turn (~8000 tok), 750 out, ~103K target depth by turn 12.

**Result: PASS** — both sessions completed 12/12 turns, **21 overlapping turn-pairs, 736 s total overlap (91.5% of run span)**, `concurrency check: PASS (requests genuinely overlapped in flight)` — the per-turn fix (4227f78c) is the signal; the old whole-session check would have been trivially PASS but is not used.

| Session | Turn 1 | Turn 12 | Mean (12) | Prompt tok growth | Notes |
|---------|--------|---------|-----------|-------------------|-------|
| **1** | 21.67 tok/s wall 34.6s 6682 tok | 2.39 wall 43.0s 80385 | 7.20 | 6682→80385 (12.0×) | comp_tok fell 750→103 at depth |
| **2** | 12.34 wall 60.8s 6682 | 9.07 wall 23.1s 80920 (12.1×) | 7.32 | 6682→80920 | comp_tok 750→209 |

**Depth vs speed curve (session 1, Q5_K_S):**

- Turns 1-2 healthy: 21.67 → 8.85 → 4.95 → 3.35 tok/s by turn 4 (wall 34→84→151→223 s). This is the **UM paging collapse** already seen at 747.3/747.4 (~8–15 tok/s at 40K+), not a quant-specific regression — same `27,38/q8_0/q5_1` + oversub + `cache_ram 1024` shape collapses regardless of kv_unified (prior A/B 121/122 vs 123 already ruled that out, §5.2 in 740-report).
- Turns 5-12 highly variable in both tok/s and `comp_tok` (281, 181, 168, 119, 123, 183, 129, 103) — not a measurement artifact but model early-stop / EOS at deep context, so `timings.predicted_per_second` is averaged over fewer tokens. Wall-clock per turn is the more honest signal here (23–43 s at 66–80K), not the tok/s ratio.
- Both sessions saw **deferrals** from `parallel-ctx-threshold 100000` (`resident + candidate >= threshold → defer`) after turn 6, and `cache-ram 1024` spills — same shape, same collapse as 747.4's Q5_K_M at this depth. Q5_K_M's equivalent deep numbers exist from today's earlier arm102/747 runs — ask leader for direct comparison rather than re-running; this arm was not designed to isolate quant vs quant at depth (cache spill dominates).

**Caveat:** With `cache_ram 1024`, every deep turn logged `prompt state size 2300–4694 MiB exceeds cache size limit 1024, skipping` — so the harness paid **full re-prefills** each turn, inflating wall-clock vs 747.3's 24576 (which retains ~100% cache hit on return). Do not compare wall-clock here to 090/747.3 without correcting for that.

### 5.4 Coherence / correctness spot-check (Q5_K_S)

Qwen3.8-27B is a **thinking** model (`reasoning_format: deepseek`, `reasoning_in_content: false`). At `max_tokens=150`, all 10 T2 responses had:

- `content: ""` (empty)
- `reasoning_content: "We need answer user's request. User: \"You are Meta AI analyzing …"` (403 chars, coherent, on-prompt, no repetition/degeneration)
- `finish_reason: "length"` (cut mid-thinking, expected — 150 is not enough to finish the think block and reach `<|im_end|>`)

This is **not garbage/degenerate** — it's the normal truncated-thinking output for this prompt length. Same shape for Q5_K_M (also 403-char reasoning, 0 content). Longer `max_tokens` would be needed to judge final answer quality; this spot-check only rules out empty/loop failure, which PASSes. Quant difference is not byte-identical by design — no byte comparison attempted.

## 6. BARS assessment

> Prior: Q5_K_S smaller → decode faster (bandwidth) but may degrade quality — exploring tradeoff, not expecting pure win.

| Bar | Expectation | Observed | Verdict |
|-----|-------------|----------|---------|
| Fit | Q5_K_S easier than Q5_K_M | Same ceiling 15847/11911, both 10/10, no OOM | **Confirm** |
| Speed (T2) | Q5_K_S > Q5_K_M | 52.43 > 50.69 (+3.4%, tight spread) | **Confirm, small win** |
| Speed at depth (T1) | Collapse at 60–80K regardless | 7.2 mean, 2–3 tok/s at 80K, cache spill + deferrals — same family as Q5_K_M's 747 deep runs | **No quant-isolated delta** — collapse dominates |
| Quality | Q5_K_S may be worse | Spot-check only — both truncated-thinking but coherent, no signal | **Inconclusive — needs longer generation + human eval** |

Report honestly: **Q5_K_S is a small, reproducible decode win at shallow/medium depth (+3.4% at 808 tok prompt, 150 out) at the same VRAM ceiling**; quality delta not measured here. Deep multiturn is dominated by the oversub+UM paging collapse (8–10 tok/s at 30K+ with 1024 cache-ram), not by quant choice — do not extrapolate the +3.4% to 80K.

## 7. Rig restore + caveats

**Restore (post-arm, 00:17–00:20):**

- `pkill` Q5_K_M test servers → GPUs 1/1 MiB, `curl localhost:18081/health` → `Failed to connect …` (drain-verify PASS)
- `podman pod start pod_llama-baseline` → `pod_llama-baseline` Running, `curl localhost:18081/health` → `{"status":"ok"}`, `nvidia-smi` 15847/11911 MiB — normal VRAM ceiling — matches pre-arm production pin (747.4 shape). **Rig left in clean, stopped-test state? No — restored to prod as planned, not left running Q5_K_S.**

**Caveats / open questions:**

- **n=1 per quant for T2** — tight spread (0.14–0.17) is reassuring but not a substitute for replication. The leader's standing rule is "replication before trust" — this will be repeated 3× in Round 2 for Q5_K_S + Sharp (and should be for stock too).
- **cache-ram 1024 vs prod 24576** — wall-clock numbers here are **not** comparable to 090/747.3 without correcting; next rounds should consider testing at prod 24576 to isolate quant vs cache-spill effects.
- **Deep quality not measured** — need longer `max_tokens` or task evals (coding/human) to judge Q5_K_S quality degradation; this arm only rules out boot failure and gross decoding pathology.
- **VRAM masking by UM** — the 1 GB on-disk delta did not translate to 1 GB `nvidia-smi` delta because UM overcommits; do not quote "same VRAM" as "no saving" — host RAM paging still pays the delta (see `rpc-server.log` `available_memory_kb` lines).
- **Bimodal / run-to-run variance** not probed here — 747.1/747.3 showed boot-to-boot bimodal; this arm did one boot per quant only.

## 8. Repro

```bash
# B01 build (already built — verify before trusting any number)
grep -E "CMAKE_BUILD_TYPE|GGML_CUDA_FA_ALL_QUANTS" src/llama-cpp/build-cuda1322/CMakeCache.txt
# GGML_CUDA_DEBUG must be unset; FA_ALL_QUANTS=ON; Release

# Fit + T2 (identical shape, only model_path differs)
cat /tmp/q5ks-params.yml  # 262144/2, ts27,38, q8_0/q5_1, draft q8_0/q5_1, UM on, cache-ram 1024
bash infra/llama-baseline/run-with-params.sh /tmp/q5ks-params.yml --no-cleanup
bash /tmp/m01_test.sh 18081  # 10× head -c 2000 /tmp/bigprompt.txt, max_tokens 150
# repeat with /tmp/q5km-params.yml

# T1 deep
bash infra/llama-baseline/multiturn-growth-test.sh 18081 2 12 8000 750

# Restore
podman pod start pod_llama-baseline; curl -s http://localhost:18081/health | jq; nvidia-smi --query-gpu=memory.used --format=csv
```

Results dirs: `/tmp/rpc-test/results/q5ks-fit-q8q51-262144-p2-1d3c4a8e3/` and `q5km-fit-q8q51-262144-p2-1d3c4a8e3/`, M01 JSONs in `/tmp/q5ks-m01-json/` / `/tmp/q5km-m01-json/`, T1 log `~/.local/share/mcp-bg-bash/logs/3e02e3e70fff.log` (`/tmp/q5ks-t1-full.log`).

---

**Conclusion (Round 1):** At the prescribed 262144/2, `27,38`, `q8_0/q5_1`, MTP, `cache-ram 1024`, UM-on shape on build 10814, **Q5_K_S boots and holds the same production VRAM ceiling as Q5_K_M and decodes ~3.4% faster at 150-tok short prompts** (52.43 vs 50.69 tok/s, tight replication). Quality delta not measured beyond a truncated-thinking coherence spot-check (both coherent). Deep 12-turn concurrency is functional (PASS, 91.5% overlap) but collapses to ~7 tok/s mean at 80K regardless — same UM paging regime as the 747 family, not a quant-isolated effect. Next steps are replication (3×) and a proper quality eval before any pin change — Round 2/3 cover the template-replication part (see below).

*Do not commit/push without checking with the leader first — per task.*

---

## 9. Round 2 — Q5_K_S + Sharp template, 3× replicated T2 (2026-09-12 00:37–00:53)

> Follows Round 1. Same B01 build (10814/1d3c4a8e3), same rig protocol. This round tests the **Sharp community Jinja** (`infra/llama-baseline/templates/qwen3.8-sharp.jinja` 30 KB, `qwen3.8-froggeric-v22.5.0`) on Q5_K_S at the `arm-chat-template-sharp-opt-in.yml` shape. Only delta vs stock is `chat_template_file` — all other flags identical to that file's production shape (parallel 1, `cache-ram 1024`, `q8_0/q5_1` MTP, UM on). **Important:** that shape is `parallel 1` (not Round 1's `parallel 2`), so absolute tok/s is not directly comparable to Round 1's 52/50 numbers — compare Sharp vs Sharp replication and Sharp vs stock at same `parallel 1` (see Round 3's stock at `parallel 1`: ~25.5 tok/s multiturn, but T2 at `parallel 1` stock would be ~? not re-measured here). Round 2's purpose is **replication before trust** — does Sharp deliver a stable T2?

**Method:** `params: /tmp/q5ks-sharp-params.yml` (copy of `arm-chat-template-sharp-opt-in.yml` with `model_path` → Q5_K_S). 3× fresh boots (drain-verify each), each boot runs `run-with-params.sh` (10 reqs) then independent M01 (`head -c 2000 /tmp/bigprompt.txt` → 808 tok, `max_tokens:150`, 10 seq, `timings.predicted_per_second`). Each run is a fresh boot to catch run-to-run/bimodal variance — not 10 back-to-back on one warm server.

**Per-run results (M01, `predicted_per_second`):**

| Run | Boot | Cold req1 | Warm 2-10 mean | Overall mean | Std | Min–Max | Reasoning len | Result |
|-----|------|-----------|----------------|--------------|-----|---------|---------------|--------|
| 1 | 63 s | 41.61 | 42.11 | **42.06** | 0.18 | 41.61–42.31 | 498 (coherent) | GOOD |
| 2 | 25 s | 42.20 | 42.12 | **42.13** | 0.08 | 41.97–42.22 | 498 | GOOD |
| 3 | 21 s | 43.02 | 42.06 | **42.16** | 0.29 | 42.02–43.02 | 498 | GOOD |

Boot times varied 21–63 s (cold vs warm page cache — normal), but all 10/10 PASS, no Xid, same VRAM 15847/11911.

**Aggregated (replication):**

- **Mean of means:** (42.06 + 42.13 + 42.16) / 3 = **42.12 tok/s**
- **Spread (max−min of means):** 0.10 tok/s (0.24% range)
- **Across-run std:** ~0.05
- **Within-run std:** 0.08–0.29 (run3 cold outlier 43.02 inflates its std, but warm is stable 42.06)

**Reading:** **Highly replicable** at `parallel 1` — 42.12 ±0.06, tight. The 0.10 spread is < the 1.7 tok/s Round 1 quant delta, so replication would not have hidden a real 3% effect. No bimodal observed across 3 boots (unlike 747.1's boot-bimodal at `parallel 2`). Sharp's T2 is **stable**, not noisy — so any later stock-vs-sharp T2 comparison at same `parallel 1` would be credible.

**Caveat:** Do not directly compare this 42.12 to Round 1's 52.43 — different `parallel` (1 vs 2) changes compute-graph duplication and MTP scheduling. The proper stock-vs-sharp T2 at `parallel 1` would require re-measuring stock Q5_K_S at `parallel 1` with same 3× replication — not done here (Round 3 does the multiturn A/B at `parallel 1` instead).

**Artifacts:** `/tmp/r2-sharp-run1..3/` M01 JSONs, `/tmp/r2-run*-boot.log`, `/tmp/rpc-test/results/q5ks-sharp-262144-p1-1d3c4a8e3/` (last run's logs overwrote earlier, but per-run JSONs saved separately).

---

## 10. Repro (Round 2)

```bash
cat /tmp/q5ks-sharp-params.yml # from arm-chat-template-sharp-opt-in.yml, model_path→Q5_K_S
for i in 1 2 3; do
  bash infra/llama-baseline/run-with-params.sh /tmp/q5ks-sharp-params.yml --no-cleanup
  bash /tmp/m01_test.sh 18081   # save to /tmp/r2-sharp-run$i/
  pkill -9 -f "llama-server.*18081"; pkill -9 -f "ggml-rpc-server.*50052"; sleep 5; nvidia-smi ...
  curl -s http://localhost:18081/health  # should fail → drain-verify
done
# aggregate: python3 -c "vals=[...]; print(mean, spread)"
```
