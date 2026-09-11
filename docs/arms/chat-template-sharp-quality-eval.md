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

---

*Do not merge to main — this is an exploratory one-shot. Next step if leader wants to unblock Sharp: re-run turn 12 alone with `max_tokens 1500` on the same 76K histories (no new boots) and confirm both emit the full `ACK:` + 3 bullets.*
