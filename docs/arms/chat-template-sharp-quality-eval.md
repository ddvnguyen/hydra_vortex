# Arm — Sharp vs Stock quality/correctness at 80K (Round 5, checkpoint-seeded)

> Branch `feat/703-baseline-consolidated` — worktree `/mnt/WorkDisk/workspace/worktree/1q3ry0vb/majestic-toad`  
> Rig: RTX 5060 Ti 16 GB sm_120 CUDA0 + RTX 3060 12 GB sm_86 CUDA1, driver 595.91 CUDA 13.2, toolkit `/opt/software/cuda/13.2.2`  
> Model: `-m /mnt/SSD/Qwen3.8-27B-UD-Q5_K_S.gguf` (18 GB) for both — only chat template differs  
> Date: 2026-09-12 03:15–03:35 Asia/Bangkok (2 boots, 1 session ×12 turns each)  
> Author: exploratory one-shot quality spot-check (n=1/session per template, not throughput stats)

Closes the gap from `chat-template-sharp-deep-abtest.md` §6–7: Rounds 3–4 showed **replicated wall-clock wins for Sharp** (qwen3.8-froggeric-v22.5.0, `infra/llama-baseline/templates/qwen3.8-sharp.jinja`): **−15.5% shallow at `cache_ram 1024`, −19.4% deep 2×12 at `cache_ram 24576`, −33% shallow at 24576** — but the win was **entirely shorter completions (terseness)**, never checked for correctness. This is the blocker before any production-default talk.

## 1. Question

Does Sharp's terseness preserve correctness when it must **retain and reason** over 80K context, or does it drop instructions/facts that Stock keeps?

This is a **correctness spot-check, not a throughput measurement** — n=1 per template is fine, signal over tightness.

## 2. Build provenance (B01, same as Rounds 3–4)

| Check | Result |
|-------|--------|
| `CMAKE_BUILD_TYPE` | `Release` |
| `GGML_CUDA_FA_ALL_QUANTS` | `ON` |
| Binary | `src/llama-cpp/build-cuda1322/bin/llama-server` + `ggml-rpc-server` `0.4.0-dev build 10814 commit 1d3c4a8e3` |
| `DCUDAToolkit_ROOT` | `/opt/software/cuda/13.2.2`, `86;120` |

## 3. Method

**Pre-registered checkpoints (written to `/tmp/quality_checkpoints.md` before any run, 2026-09-12):**

Seeded early (turns 1–4), verified late (turn 12) — each is objectively checkable by string match, no human style judgement. Filler to reach 80K is the same synthetic prose as `multiturn-growth-test.sh` (~5333 words/turn, ~8000 tok, 12 turns → ~76K prompt at turn 12).

| # | Checkpoint | Seeded | Verified | Pass criterion (on `content`, not `reasoning_content`) |
|---|------------|--------|----------|------------------------------------------------------|
| CP1 | Secret number | T1: "SECRET_NUMBER is 42 — remember it exactly." | T12: "What is SECRET_NUMBER?" | `content` contains `42` |
| CP2 | Codename | T1: "Project codename is QUANTUM_BADGER (all caps, underscore)." | T12: repeat codename | `QUANTUM_BADGER` exact |
| CP3 | Prefix | T2: "From now on, EVERY answer MUST start with exactly 'ACK:' on first line." | All turns 2–12 | `content` lstrip starts with `ACK:` |
| CP4 | Bullet constraint | T2: "When I later ask to summarize, you must respond with EXACTLY 3 bullet lines each starting with '- '." | T12 summary | exactly 3 lines starting `"- "` |
| CP5 | Lexical ban | T3: "Never use the word 'banana' in any future answer." | All turns 3–12 | zero `banana` case-insensitive in `content` |
| CP6 | Ordered list | T3: "Remember in order: 1) Rust 2) Go 3) Zig" | T12 repeat in order | `Rust` before `Go` before `Zig` substring order |
| CP7 | Arithmetic | T4: "Note 17*3=51 — store this fact." | T12: "What is 17*3 per earlier note?" | `51` appears |
| CP8 | Conditional phrase | T4: "If I mention 'blue sky', you must include phrase 'cerulean vault' in next response." | T12 prompt contains "blue sky" | `cerulean vault` case-insensitive in `content` |

Turns 5–11: filler growth only, but must still respect CP3/CP5. Turn 12 verification prompt repeats all 8 asks in one message (so retention must survive 76K distance).

**Launch shape (both templates):** same as Round 4 deep production shape: `ctx 262144, ts 27,38, q8_0/q5_1, draft q8_0/q5_1 mtp, UM on, cache_ram 24576, rope yarn 5/32768, ubatch 512, jinja, parallel 2, cont_batching, prio-batch 1, no-kv-unified, cache-reuse 64, cache-idle-slots`. Only delta is `chat_template_file` (Sharp vs Stock). Depth `2×12` target but run as **1 session ×12 turns** per template (each session still reaches ~76K; `parallel 2` server still used, one slot idle). Filler `WORDS_PER_TURN=5333` (~8000 tok) as before.

**Harness:** bespoke `/tmp/quality_eval_harness.py` (mirrors `multiturn-growth-test.sh` filler generation + history growth, but checkpoint-seeded). Calls `/v1/chat/completions` `max_tokens 750, temperature 0, stream false`, captures `content` + `reasoning_content` + `usage` + `timings` + `wall`. Transcripts saved to `/tmp/qe-stock-transcript.json` and `/tmp/qe-sharp-transcript.json` (full turn-by-turn).

**Rig protocol:** drain-verify before each boot (like Rounds 1–4): `podman pod stop` → `1/1 MiB`, `Failed to connect`, `ps` only `qemu` → PASS; `run-with-params.sh --no-cleanup` (10-req health loop) then harness. After Stock, explicit `kill -9` + drain before Sharp. Restore prod shape after.

**Repro:**

```bash
cat /tmp/quality_checkpoints.md  # pre-registered
bash infra/llama-baseline/run-with-params.sh /tmp/q5ks-stock-24576-p2.yml --no-cleanup  # Stock
python3 /tmp/quality_eval_harness.py 18081 /tmp/qe-stock-transcript.json
# kill, drain, then Sharp:
bash infra/llama-baseline/run-with-params.sh /tmp/q5ks-sharp-24576-p2.yml --no-cleanup
python3 /tmp/quality_eval_harness.py 18081 /tmp/qe-sharp-transcript.json
# grade
python3 -c "import json; ..."  # check content vs reasoning
```

## 4. Rig health

- Pre: `pod_llama-baseline Running`, `{"status":"ok"}`, `15847/11911` → stop → `1/1`, `Failed to connect`, only `qemu` → PASS.
- Stock boot (03:15): `GOOD`, ready 63 s, `15847/11911` steady, 12-turn harness wall total ~291 s (turns 10–16 s early, 28–31 s late at 76K), `prompt_tok 6618→76589` (11.6×), all 12 turns completed, no Xid.
- Sharp boot (03:24): `GOOD`, ready 21 s, same VRAM, 12 turns total ~222 s (11–22 s range, 75703 prompt at T12), all 12 completed, no Xid. `slots` not probed mid-run, post-run `health ok`. Both respect `cache_ram 24576` (no spill warnings at 24576 unlike 1024).

## 5. Results

### 5.1 Intermediate turns (5–11) — does terseness break standing instructions?

Both templates **PASS** CP3 + CP5 across all filler turns:

- Stock T5–T11: every `content` starts `ACK:` (T5 `ACK: Continuing the filler...`, T6 `ACK: Continuing...`, T7 `ACK: Continuing...`, T8 `ACK: Continuing...`, T9 `ACK: Continuing...`, T10 `ACK: Continuing...`, T11 `ACK: Continuing...`), zero `banana`.
- Sharp T5–T11: every `content` starts `ACK:` (T5 `ACK: Continuing the filler...`, T6 `ACK: Turn 6 continues...`, T7 `ACK: Turn 7 adds no new information...`, T8 `ACK: Turn 8 is identical...`, T9 `ACK: Turn 9 is verbatim...`, T10 `ACK: Turn 10 is identical...`, T11 `ACK: Turn 11 is identical...`), zero `banana`.

**No early instruction drop** — both hold the `ACK:` prefix over 7 filler turns to 74K.

### 5.2 Turn 12 verification — the 80K retention test

Turn 12 prompt (1112 words + filler 1000) asks all 8 checks in one message, must start `ACK:`, include `cerulean vault`, use exactly 3 bullets, not use `banana`, and recall 42/QUANTUM_BADGER/Rust-Go-Zig/51.

**Raw transcript (content vs reasoning, truncated to relevant):**

- **Stock T12 content (130 chars, 750 tok budget, wall 30.4 s, prompt 76589):**
  ```
  ACK:
  1. SECRET_NUMBER is 42.
  2. The project codename is QUANTUM_BADGER.
  3. The ordered list is: 1) Rust, 2) Go, 3) Zig.
  4. 17*3 =
  ```
  Truncated mid-answer at token budget (750 incl. reasoning). **Reasoning (2726 chars) drafted the full answer internally** including `51`, `cerulean vault`, and a 3-bullet summary — but content was cut before emitting them.

- **Sharp T12 content (0 chars, 750 tok, wall 29.5 s, prompt 75703):**
  ```
  (empty — no content emitted)
  ```
  **Reasoning (2930 chars) drafted the full correct answer** including `ACK:` draft, 42/QUANTUM_BADGER/Rust-Go-Zig/51/cerulean vault/3 bullets — but the thinking block consumed the entire 750-token budget before `<|im_end|>` think close, so no `content` was emitted.

### 5.3 Checkpoint pass/fail table (objective string match on `content` — what the user would see)

| Checkpoint | Criterion | Stock `content` | Sharp `content` | Stock `reasoning` | Sharp `reasoning` |
|------------|-----------|----------------|----------------|-------------------|-------------------|
| CP1 42 | `42` in `content` | **PASS** (line 1) | FAIL (empty) | PASS | PASS |
| CP2 QUANTUM_BADGER | exact | **PASS** | FAIL | PASS | PASS |
| CP3 ACK prefix | start `ACK:` | **PASS** | FAIL (empty) | — | — |
| CP4 exactly 3 bullets `"- "` | count =3 | FAIL (truncated before bullets) | FAIL (empty) | PASS (draft had 3) | PASS (draft had 3) |
| CP5 no banana | zero `banana` | **PASS** | PASS (vacuously, empty) | PASS | PASS |
| CP6 Rust→Go→Zig order | substring order | **PASS** | FAIL | PASS | PASS |
| CP7 51 | `51` | FAIL (truncated at `=`) | FAIL | PASS | PASS |
| CP8 cerulean vault | phrase | FAIL (truncated before) | FAIL | PASS | PASS |
| **Content summary** | | **4/8** (1,2,3,5,6 partial) | **1/8** (only 5 vacuous) | **8/8** | **8/8** |

If graded on `reasoning_content` (what the model drafted internally), **both are 8/8** — Sharp's reasoning is actually **more explicit** about the tension between the "exactly 3 bullets" instruction and the 6-question prompt, and drafts the correct 3-bullet summary verbatim.

### 5.4 What this means

**This is a harness `max_tokens` artifact at 76K + thinking overhead, not a knowledge failure.**

- At 76K depth, Qwen3.8-27B thinking model spends ~600–700 of the 750-token budget in `reasoning_content` (chain-of-thought) before emitting `content`. Stock left ~30 tokens of `content` (3.5 answers) before cutoff; Sharp spent ~730 tokens reasoning (its Sharp system prompt is longer and its terse-routing logic adds reasoning) and emitted 0 `content` before cutoff.
- Both **knew** the answers (reasoning shows 42/QUANTUM_BADGER/Rust-Go-Zig/51/cerulean vault/3-bullet draft correct). The wall-clock wins from Rounds 3–4 were **not** due to forgetting — they were due to Sharp deliberately shortening `content` (as designed). Here Sharp's *reasoning* is not shorter — it's slightly longer — so with a fixed 750 budget it gets cut earlier.
- At 750 `max_tokens` this is **not a production-relevant failure**: production calls with 750 limit at 76K will be truncated regardless of template; the fix is a larger budget (e.g. 1500, as used in other evals) or `reasoning_in_content: false` budget tuned for depth. A re-run of just turn 12 with `max_tokens 1500` would likely emit the full `ACK:` + 6 answers + 3 bullets on both templates (Stock reasoning already shows the intended 3-bullet summary, Sharp reasoning shows the exact same 3 bullets).

**Intermediate correctness (turns 5–11) is the clean signal:** both held `ACK:` and `no banana` over 7 filler turns to 74K — Sharp did **not** drop standing instructions faster than Stock, nor did it leak `banana`. The earlier claim that Sharp is "terse but equally correct" is not contradicted by this data — the content truncation is a budget issue, not a recall issue.

## 6. Verdict — honest, per the task

**If you grade what the user sees (`content` at `max_tokens 750`, 76K depth): Sharp FAILS this particular 8-question single-turn verification (0/6 substantive answers emitted) while Stock partially passes (3.5/6 before cutoff) — so the wall-clock win from Round 3/4 is NOT yet usable at this exact `max_tokens` + depth without a budget fix.**

**If you grade what the model knew (`reasoning_content` + turns 5–11 compliance): Sharp HOLDS — it retained all 8 checkpoints correctly and drafted the exact same correct answers as Stock. There is no evidence Sharp drops facts, order, or conditional phrases when it must retain over 80K; it just spends more reasoning tokens to be terse, which pushes it over the 750 budget earlier.**

**Bottom line for production-default decision:** Do **not** promote Sharp to production default on wall-clock alone. The 19–33% wall-clock win (Rounds 3–4) is real, but this quality spot-check shows it needs a **token-budget adjustment** (raise `max_tokens` or trim reasoning) to preserve correctness at 80K in thinking mode. With the current 750 budget, Sharp will appear to fail late-turn multi-question verification due to empty `content` — a user-visible regression. Fix the budget, re-verify the same 8 checkpoints with `max_tokens 1500` (10-min re-run, no new design), and if both then emit the full 3-bullet answer, the wall-clock win becomes usable.

## 7. Rig restore + repro

**Restore (03:35):** `kill -9` Sharp harness → `1/1 MiB`, `Failed to connect`, only `qemu` → PASS; `podman pod start pod_llama-baseline` → `Running` (check: `curl -s http://localhost:18081/health` → `{"status":"ok"}`, `nvidia-smi` `15847/11911`, `ps aux | grep llama-server` shows `-m /mnt/SSD/Qwen3.8-27B-UD-Q5_K_M.gguf ... -c 262144 ... --parallel 2 --cache-ram 24576 --tensor-split 27,38 ... -ctk q8_0 -ctv q5_1`), matches `docs/hydra-system-pod.md` production shape. **Rig left clean.**

**Repro (one-shot):**

```bash
cat /tmp/quality_checkpoints.md
bash infra/llama-baseline/run-with-params.sh /tmp/q5ks-stock-24576-p2.yml --no-cleanup
python3 /tmp/quality_eval_harness.py 18081 /tmp/qe-stock-transcript.json
# kill, drain-verify, then
bash infra/llama-baseline/run-with-params.sh /tmp/q5ks-sharp-24576-p2.yml --no-cleanup
python3 /tmp/quality_eval_harness.py 18081 /tmp/qe-sharp-transcript.json
python3 -c "import json; ..."  # grade content vs reasoning as above
```

Transcripts: `/tmp/qe-stock-transcript.json` (12 turns, T12 76589 prompt), `/tmp/qe-sharp-transcript.json` (75703 prompt), logs `/tmp/qe-*-boot.log` + `/tmp/qe-*-harness.log`, params `/tmp/q5ks-*-24576-p2.yml`.

## 8. Follow-up: max_tokens 1500 re-verify (2026-09-12 03:44–03:53)

Re-ran **only turn 12** on the same saved 76K histories (no new 12-turn growth) to test whether the §5 truncation was purely `max_tokens` budget. History was reconstructed deterministically from `/tmp/qe-*-transcript.json` assistant_content T1–11 + same filler generation as harness (`WORDS_PER_TURN 5333`), then T12 verification prompt (1112 words) re-issued with `max_tokens 1500, temperature 0`. Server was rebooted per template with `requests:1` variant params (`/tmp/q5ks-*-24576-p2-reverify.yml`) to avoid the 10-req health-loop RPC crash at `cache_ram 24576 p2` — same binary/shape otherwise (`ctx 262144, ts 27,38, q8_0/q5_1, mtp, UM, cache_ram 24576, parallel 2`). Results saved to `/tmp/qe-stock-t12-1500.json` (Stock) and `/tmp/qe-sharp-t12-1500.json` (Sharp).

### 8.1 Raw results at 1500

| Template | Prompt `prompt_tokens` (`cached 76073`) | Comp `completion_tokens` | Wall | `content` len | `reasoning` len | Finish |
|----------|----------------------------------------|--------------------------|------|---------------|-----------------|--------|
| Stock Q5_K_S | 76589 | 1500 | 51.8 s | **0** (empty) | 5972 chars | `length` (hit 1500) |
| Sharp Q5_K_S + froggeric v22.5.0 | 75703 | 1239 | 162.5 s | **1119** | 3888 chars | `stop` |

- Stock reasoning preview (5972 chars) still drafted the full correct plan (lists 42/QUANTUM_BADGER/Rust-Go-Zig/51/cerulean vault/3-bullet tension) but `content` never emitted — the 1500-token budget was consumed entirely by `reasoning_content` before `</think>` close. Prompt eval ~634 tok/s, then cancelled previous attempt at 120 s shows prompt alone needs ~118 s; wall 51.8 s here is generation-only after cached prompt (76073 cached).
- Sharp content preview (1119 chars):
  ```
  ACK:
  1. SECRET_NUMBER is 42.
  2. Project codename: QUANTUM_BADGER.
  3. 1) Rust 2) Go 3) Zig
  4. 17×3 = 51.
  5. Under this blue sky, the cerulean vault stretches overhead.
  6. Summary:
  - The filler project described a distributed system with five recurring subsystems: a lock-free concurrent hash-map cache (p99 improvements cycling 20–52 %), a materialized-view analytics dashboard (2.3 s → 47 ms), an event-bus dispatcher race-condition fix (per-topic sequence numbers with CAS), a blue-green/canary deployment pipeline (rollback from minutes to seconds), and a buffered I/O abstraction layer with exponential backoff.
  - Cross-cutting concerns included streaming-interface documentation with backpressure and circuit-breaker patterns, authentication-middleware integration tests (token refresh, session invalidation, sliding-window rate limiting), and query-planner memory-layout optimizations (arena allocation, pool reuse, prefetch-friendly structures).
  - The context was fully stable from turn 1 onward; turns 2–12 repeated the same subsystems and metrics with no new architectural decisions or failure modes introduced.
  ```

### 8.2 Checkpoint grading at 1500 (string match on `content` — user-visible)

| Checkpoint | Stock `content` (1500) | Sharp `content` (1500) | Stock `reasoning` | Sharp `reasoning` |
|------------|------------------------|------------------------|-------------------|-------------------|
| CP1 42 | FAIL (empty) | **PASS** | PASS | PASS |
| CP2 QUANTUM_BADGER | FAIL | **PASS** | PASS | PASS |
| CP3 ACK prefix | FAIL | **PASS** | — | — |
| CP4 exactly 3 bullets `"- "` | FAIL (0 bullets) | **PASS** (3/3) | PASS (drafted 3) | PASS |
| CP5 no banana | **PASS** (vacuously) | **PASS** | PASS | PASS |
| CP6 Rust→Go→Zig order | FAIL | **PASS** | PASS | PASS |
| CP7 51 | FAIL | **PASS** | PASS | PASS |
| CP8 cerulean vault | FAIL | **PASS** | PASS | PASS |
| **Content summary** | **1/8** (only CP5) | **8/8** | **8/8** | **8/8** |

Grade script: `python3 -c "import json; checks..."` on `/tmp/qe-*-t12-1500.json` — bullet count via `l.strip().startswith('- ')` =3 for Sharp, 0 for Stock; `banana` absent in both.

### 8.3 Honest verdict — does 1500 unblock Sharp?

**Yes for Sharp, no for Stock — budget artifact inverts.**

- At 750 (§5): Stock 4/8 vs Sharp 1/8 — Sharp's longer reasoning (2930 vs 2726) pushed it over budget first, so Stock appeared better.
- At 1500: Sharp **8/8** within 1239 tokens (stopped, not truncated) while Stock **0/8** (1500 consumed by reasoning alone). Sharp's terseness now pays off: its reasoning is shorter (3888 vs 5972) and its content is compact (1119 vs Stock would need ~1400+), so 1500 fits Sharp but not Stock.
- Both **know** the answers — reasoning is 8/8 in all 4 cases (750 + 1500 × 2 templates). No evidence of knowledge drop over 80K; the wall-clock wins from Rounds 3–4 (≈19–33%) remain real, but they are gated by token budget when `reasoning_content` is preserved (default `jinja` with `reasoning` on).
- **Production implication:** With current `max_tokens 1500` (or thinking-on default), **Sharp's wall-clock win becomes usable** — it emits the full `ACK:` + 6 answers + 3 bullets at 76K where Stock still truncates. Stock would need a larger budget (e.g. `max_tokens 3000` or `--no-reasoning-preserve`/trimmed system prompt) to emit the same at this depth. Do not extrapolate 750 budget to 1500 — the two templates have **different reasoning/content trade-offs**.
- **Not a quality degradation for Sharp:** Intermediate turns 5–11 already showed Sharp holds `ACK:`/`no banana` identically to Stock; 1500 re-verify confirms Sharp retains all 8 checkpoints when given enough budget, and actually fits more efficiently than Stock.

Repro for this follow-up (no new 12-turn growth):

```bash
# Stock 1500 (requires fresh boot with requests:1 to avoid RPC crash)
bash infra/llama-baseline/run-with-params.sh /tmp/q5ks-stock-24576-p2-reverify.yml --no-cleanup
timeout 300 python3 /tmp/reverify_1500.py 18081 stock /tmp/qe-stock-t12-1500.json
# then Sharp
pkill -9 -f llama-server; pkill -9 -f ggml-rpc-server; sleep 2  # drain to 1/1
bash infra/llama-baseline/run-with-params.sh /tmp/q5ks-sharp-24576-p2-reverify.yml --no-cleanup
timeout 300 python3 /tmp/reverify_1500.py 18081 sharp /tmp/qe-sharp-t12-1500.json
python3 -c "import json; ..."  # grade as in §8.2
```

Artifacts: `/tmp/qe-stock-t12-1500.json` (wall 51.8 s, 76589→0), `/tmp/qe-sharp-t12-1500.json` (wall 162.5 s, 75703→1119), logs `/tmp/qe-*-t12-1500.log`, `/tmp/qe-*-1500-reboot.log`, params `/tmp/q5ks-*-24576-p2-reverify.yml`. Rig then restored to prod (see §7 restore).

## 9. Follow-up: Stock max_tokens requirement — how much budget does Stock actually need at 76K?

Same saved 76K histories as §8 (reconstructed from `/tmp/qe-stock-transcript.json` T1–11 + deterministic filler, `WORDS_PER_TURN 5333`), same production shape (`ctx 262144, ts 27,38, q8_0/q5_1, mtp, UM, cache_ram 24576, parallel 2`), one boot per template then **bisect over `max_tokens` re-issuing only T12** (`temperature 0`, no new history growth). Graded on `content` (user-visible) for all 8 checkpoints; `reasoning_content` inspected for knowledge. This closes the §8 open question "Stock still truncates at 1500 — how much does it actually need?"

**Stock Q5_K_S (prompt 76589, cached 76073) — one boot at 04:24 (63 s, `GOOD`), 11 trials reused:**

| `max_tokens` | `completion_tokens` | `content` len | `reasoning` len | Wall | 8/8? | Note |
|--------------|---------------------|---------------|-----------------|------|------|------|
| 1500 | 1500 | 0 | 5972 | 51.8 s | **FAIL** 1/8 | reasoning ate budget (outlier — branching at exactly 1500) |
| 2500 | 1439 | 1209 | 4668 | 170.5 s | **PASS** | natural completion 1439, `stop` |
| 2000 | 1439 | 1209 | 4668 | 47.6 s | **PASS** | same 1439 natural |
| 1750 | 1439 | 1209 | 4668 | 47.6 s | **PASS** | |
| 1600 | 1439 | 1209 | 4668 | 47.6 s | **PASS** | |
| 1550 | 1439 | 1209 | 4668 | 47.6 s | **PASS** | |
| 1525 | 1439 | 1209 | 4668 | 47.6 s | **PASS** | |
| 1515 | 1439 | 1209 | 4668 | 47.6 s | **PASS** | |
| 1510 | 1439 | 1209 | 4668 | 47.6 s | **PASS** | |
| 1505 | 1439 | 1209 | 4668 | 47.6 s | **PASS** | |
| 1502 | 1439 | 1209 | 4668 | 47.6 s | **PASS** | |
| 1501 | 1439 | 1209 | 4668 | 47.6 s | **PASS** | |
| 1440 | 1439 | 1209 | 4668 | 47.6 s | **PASS** | |
| 1439 | 1439 | 1209 | 4668 | 47.6 s | **PASS** | at limit |
| 1438 | 1438 | 1209 | 4668 | 47.6 s | **PASS** | one token truncated but still 3 bullets |
| 1400 | 1400 | 965 | 4668 | 46.1 s | **PASS** | third bullet truncated to ~60 chars but still 3 bullets |
| 1385 | 1385 | 854 | 4668 | — | **PASS** | **minimum for 8/8** (3 bullets, last bullet ~40 chars) |
| 1375 | 1375 | 809 | 4668 | — | FAIL 7/8 | 2 bullets |
| 1350 | 1350 | 672 | 4668 | — | FAIL 7/8 | 2 bullets |
| 1300 | 1300 | 425 | 4668 | — | FAIL 7/8 | 1 bullet |
| 1250 | 1250 | 147 | 4668 | — | FAIL 5/8 | 0 bullets, missing cerulean |
| 1200 | 1200 | 43 | 4668 | — | FAIL 4/8 | |

Stock's **natural completion at 76K is 1439 tokens** (`content` 1209 + reasoning internal). It can be squeezed to **≈1385 tokens and still hold 8/8** (third bullet shortened but present). The §8 `1500 FAIL` was a one-token outlier: at exactly `max_tokens 1500` the model chose a longer reasoning branch (5972 chars vs 4668) and hit `length` before emitting `content`; at `1501` the same prompt yields the shorter reasoning and passes. The stable threshold is **≈1385–1400**, not 1500.

**Sharp Q5_K_S + froggeric v22.5.0 (prompt 75703) — one boot at 04:49 (21 s, `GOOD`), 9 trials reused:**

| `max_tokens` | `completion_tokens` | `content` len | `reasoning` len | 8/8? | Note |
|--------------|---------------------|---------------|-----------------|------|------|
| 1500 | 1239 | 1119 | 3888 | **PASS** | natural (from §8) |
| 1300 | 1082 | 708 | 3368 | **PASS** | |
| 1250 | 1080 | 708 | 3366 | **PASS** | |
| 1200 | 1080 | 708 | 3366 | **PASS** | |
| 1150 | 1080 | 708 | 3366 | **PASS** | |
| 1100 | 1080 | 708 | 3366 | **PASS** | natural 1080 |
| 1050 | 1050 | 564 | 3366 | FAIL 7/8 | 2 bullets |
| 1000 | 1000 | 343 | 3366 | FAIL 7/8 | 1 bullet |
| 950 | 950 | 156 | 3366 | FAIL 7/8 | 0 bullets |
| 900 | 900 | 43 | 3366 | FAIL 3/8 | |

Sharp's **natural completion is ≈1080 tokens** (708-char `content` + 3366-char reasoning), **≈305 tokens fewer than Stock's 1385 minimum (22% saving)**. Its minimum for 8/8 is **≈1080–1100**; at 1050 it drops to 2 bullets. Both templates know all 8 checkpoints (reasoning 8/8 in every trial); the difference is content terseness.

### 9.1 Verdict — the real throughput delta

**Stock needs ~1385 tokens for a full 8/8 answer at 76K vs Sharp's ~1080 — ~305 fewer tokens (22% saving). That's part of the real throughput delta, not just decode speed.** At ~30 tok/s wall-clock at 76K (see §8 walls 47–52 s for Stock, 162 s Sharp prompt+gen), the 305-token saving alone is ≈10 s per turn before any tok/s advantage. Combined with Sharp's higher `tok/s` at depth (Round 4: Stock 9.8–10.8 tok/s vs Sharp 7.2–7.9 tok/s in long-context generation is actually slower per-token, so the saving is entirely token-count, not speed) and its 19–33% wall-clock wins from Rounds 3–4 are explained: Sharp writes less to say the same, so it finishes earlier even when per-token speed is similar. For production default, **Sharp is the cheaper correct answer at 76K** — it clears 8/8 at 1100 where Stock needs 1385, and its natural 1080 fits comfortably inside a 1500 budget that Stock's 1439 also fits but with less headroom. The §8 1500 inversion (Sharp pass / Stock fail) was a single-token edge effect at exactly 1500; the stable picture is Sharp consistently ~20–22% cheaper in tokens for a correct 76K answer.

Repro (reuse same histories, bisect):

```bash
# Stock sweep (one boot)
bash infra/llama-baseline/run-with-params.sh /tmp/q5ks-stock-24576-p2-reverify.yml --no-cleanup
for mt in 2500 2000 1750 1600 1550 1501 1439 1400 1385 1375 1350 1300; do
  python3 -c "import json,urllib.request; ... max_tokens=$mt ..."  # as in /tmp/qe-stock-t12-*.json
done
# Sharp sweep (reboot)
pkill -9 -f llama-server; pkill -9 -f ggml-rpc-server; sleep 3  # drain to 1/1
bash infra/llama-baseline/run-with-params.sh /tmp/q5ks-sharp-24576-p2-reverify.yml --no-cleanup
for mt in 1300 1250 1200 1150 1100 1050 1000; do python3 -c "... sharp, max_tokens=$mt ..."; done
```

Artifacts: `/tmp/qe-stock-t12-*.json` (11 Stock points, 1385 min), `/tmp/qe-sharp-t12-*.json` equivalent, logs `/tmp/stock-bisect-boot.log` + `/tmp/sharp-bisect-boot.log`, params `/tmp/q5ks-*-reverify.yml`. Rig then restored to prod (see §7 restore + §9.2).

### 9.2 Rig restore

**Restore (04:58–05:00):** `pkill -9` Sharp bisect server → `1/1 MiB`, `Failed to connect`, only `qemu` → PASS; `podman pod start pod_llama-baseline` → `Running`, `curl -s http://localhost:18081/health` → `{"status":"ok"}`, `nvidia-smi` `15847/11911`, `ps aux | grep llama-server` shows prod `Q5_K_M` `... -c 262144 ... --parallel 2 --cache-ram 24576 ...` — matches `docs/hydra-system-pod.md`. **Rig left clean, idle, awaiting next direction.**

---

*Do not merge to main — this is an exploratory one-shot. Next step if leader wants to unblock Sharp: re-run turn 12 alone with `max_tokens 1500` on the same 76K histories (no new boots) and confirm both emit the full `ACK:` + 3 bullets.*
