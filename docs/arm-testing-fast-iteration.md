# Fast arm-test iteration: smoke-gate + checkpoint-seeded replay

> Tooling: `infra/llama-baseline/test-suite.sh`, `multiturn-growth-test.sh`,
> `checkpoint-replay.sh`. Motivated by the deep-test cost documented in
> `docs/arms/chat-template-sharp-quality-eval.md` §8-9.

## The problem

An arm test that grows context to 76–80K via
`multiturn-growth-test.sh` (8000 tok/turn × 12 turns) costs **~700–810 s of
wall per run**. Whenever a new variable is tested at depth (thread count,
`max_tokens`, chat template, quant, …) that growth cost is paid again — even
though most of the time is context growth, not the measurement. And several
expensive deep runs have been wasted on configs that a cheap smoke boot would
have caught first (OOM, skipped model load, wrong path).

Round 5 (`chat-template-sharp-quality-eval.md` §8-9) proved the workaround by
hand: save the 76K transcript once, reconstruct history deterministically from
the saved turns + filler logic, then re-issue **only the final turn** while
sweeping `max_tokens` — 1 growth + 20 cheap calls instead of 20 regrowths.
This document describes the first-class tooling for that pattern.

## New workflow

### 1. Smoke-gate first (seconds, always)

Before any deep run, boot the server and run:

```bash
bash infra/llama-baseline/test-suite.sh --smoke 18081
```

This does the `/health` + `/props` check (model alias, build info, not
sleeping) plus a minimal **2-turn × 512-token** shallow probe with a 16-token
output budget. It exits `0` fast, or `1` with a clear `SMOKE GATE FAILED`
banner. Chain it:

```bash
bash infra/llama-baseline/test-suite.sh --smoke 18081 \
  && bash infra/llama-baseline/test-suite.sh 18081 12
```

Smoke catches boot-level breakage: OOM/short load, fit-solver skips, wrong
model path, template render failure. It does **not** catch depth-only
problems (paging collapse at 80K, context-shift bugs) — those still need a
full or seeded deep run.

### 2. Then pick one: fresh full run or seeded replay

**Fresh full run** — when the variable changes the generated conversation
itself (different filler, different model) or you have no checkpoint:

```bash
bash infra/llama-baseline/test-suite.sh 18081 12
```

**Checkpoint-seeded replay** — when you want to sweep a *decode-time* variable
(`max_tokens`, sampling, template rendering, thread count) against an existing
deep context. Grow once, save the transcript, then replay:

```bash
# Grow once and save the deep transcript (survives the run):
bash infra/llama-baseline/multiturn-growth-test.sh 18081 1 12 8000 750 \
     --checkpoint-dir /tmp/hs-ckpt

# Sweep max_tokens on the saved 76K prefix — one prefill, N cheap calls:
bash infra/llama-baseline/checkpoint-replay.sh 18081 \
     /tmp/hs-ckpt/session1.transcript.json \
     --sweep-max-tokens 1100,1200,1300,1385,1500 --save-results /tmp/sweep.json

# Re-issue a brand-new final prompt (e.g. a checkpoint-verification question):
echo "What is SECRET_NUMBER? What is the codename?" > /tmp/verify.txt
bash infra/llama-baseline/checkpoint-replay.sh 18081 \
     /tmp/hs-ckpt/session1.transcript.json \
     --turn-prompt-file /tmp/verify.txt --max-tokens 1500

# Replay the last 3 saved turns sequentially (default budget):
bash infra/llama-baseline/checkpoint-replay.sh 18081 \
     /tmp/hs-ckpt/session1.transcript.json --replay-last 3
```

`checkpoint-replay.sh --help` lists all flags. Key ones:

| Flag | Meaning |
|------|---------|
| `--prefix-turns N` | use first N saved turns as the cached prefix (default: all but last) |
| `--replay-last K` | prefix = all−K, re-issue the last K saved turns |
| `--turn-prompt` / `--turn-prompt-file` | new final user turn instead of the saved next turn |
| `--max-tokens N` | single output budget (default: checkpoint's output budget) |
| `--sweep-max-tokens LIST` | one call per budget, same prefix, prefix cache reused |
| `--regen-filler` | regenerate prefix user turns via `gen_content()` (Round-5 parity) |

### 3. Reproducibility / determinism guarantees

- Filler generation, message construction and the request shape are shared via
  `infra/llama-baseline/lib/multiturn_common.py` — the replay path cannot drift
  from the growth path.
- Transcripts store each turn's exact `user_content` and `assistant_content`,
  plus generation config. Replay is `temperature 0` and reproduces the original
  request byte-for-byte; on the same params the model response is reproduced
  exactly (verified: turn-6 `reasoning_content` identical between growth and
  seeded replay).
- `--regen-filler` instead rebuilds the user turns from `gen_content(turn,
  words_per_turn)`, matching the Round-5 manual reconstruction that only kept
  assistant content.
- The transcript format is versioned (`hydra-multiturn-transcript/v1`); an
  incompatible file fails loudly instead of replaying silently wrong.

### 4. Prefix cache: warm vs cold

The sweep reuses the server's prompt/prefix KV cache. **Same boot as the
growth** → the prefix is hot, every call is cheap. **After a reboot** (needed
when the variable changes template/threads/quant) → the first replay call pays
one prefill of the whole deep prefix, the rest are cheap. Both are far cheaper
than regrowing; the cold case is the honest number for cross-boot sweeps.

## Measured savings (real, not theoretical)

Measured on an RTX-rig CPU-only stand-in (`Qwen3.5-2B-Q8_0`, `-ngl 0`, 8
threads, 8192 ctx) so the live GPUs could stay reserved for other work. Depth
6 turns × 1500 new tok (~7.5K prompt). 8 `max_tokens` values swept. Full logs
in `/tmp/hs-evidence/`.

| Path | Total wall | vs naive | Notes |
|------|-----------|----------|-------|
| Naive: 8 full regrowths | **453.60 s** | 1.0× | 76.4 s cold, then ~53.9 s each (cross-run prompt cache already helping the naive side) |
| Checkpoint, same boot | **81.35 s** | **5.58×** (82.1% less) | 65.70 s growth + 15.65 s warm sweep |
| Checkpoint, rebooted | **128.96 s** | **3.52×** (71.6% less) | 65.70 s growth + 63.26 s cold sweep (49.1 s first-call prefill, then 0.8–4.1 s) |
| Seed sweep only (checkpoint already exists) | 15.65 s warm / 63.26 s cold | **29.0× / 7.2×** | recurring marginal cost per sweep |

The 8-budget sweep reused `7515/7519` prompt tokens from cache on every call
after the first (`cached_tokens` in `/tmp/hs-evidence/hs-replay-*.json`).

At the docs' 76K/12-turn scale the naive side is ~700–810 s per run, so the
same 8-variant test that costs ~1.5–1.7 h naive costs **one growth + one
sweep** with the checkpoint.

## Files

- `infra/llama-baseline/lib/multiturn_common.py` — shared filler/messages/request + transcript format
- `infra/llama-baseline/lib/replay_transcript.py` — replay engine behind `checkpoint-replay.sh`
- `infra/llama-baseline/checkpoint-replay.sh` — CLI wrapper
- `infra/llama-baseline/tests/test_harness_tools.py` — hermetic mock-server tests
- `infra/llama-baseline/tests/mock_llama_server.py` — mock `/health`, `/props`, `/v1/chat/completions`

Run the hermetic tests any time (no GPU, ~1.5 s):

```bash
python3 infra/llama-baseline/tests/test_harness_tools.py
```
