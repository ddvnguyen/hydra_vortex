# Proposal — Qwopus3.8-27B-Flash as CANDIDATE MAIN MODEL (pin 131, UNTESTED)

> Status: **proposal only — NOT eval-verified as a main model. Do NOT point
> `LLAMACPP_PARAMS_FILE` in `docker-compose.baseline.yml` at pin 131 without
> a real quality/behavior eval first. Live pin stays 130.**
> Branch: `candidate/qwopus3.8-27b-flash-mainmodel` → PR to `baseline`, no merge.
> No hardware was touched for this proposal (config/docs only).

## 1. What the model is

- File: `/mnt/WorkDisk/LLM-Models/Qwopus3.8-27B-Flash-MTP-Q5_K_S.gguf`
  (18,971,682,016 bytes, 866 tensors).
- Source: https://huggingface.co/Jackrong/Qwopus3.8-27B-Flash-GGUF
- Despite the `-MTP-` in the filename, this is a **FULL model, not a
  draft-head stub** (confirmed earlier today).
- Pin: `infra/llama-baseline/params/131-baseline-qwopus38-flash-main-candidate.yml`
  — standalone main model, no `spec_draft_model` key, all other launch flags
  byte-identical to pin 130 (production shape).

## 2. Why it is a candidate (structural compat — verified)

Verified earlier today against the current production main model
(Qwen3.8-27B-UD-Q5_K_S):

| Check | Result |
|-------|--------|
| Architecture | Same `qwen35` arch, same dims: 65 blocks, 5120/17408, 24/4 heads, 262K ctx, same RoPE |
| Tokenizer | Byte-identical, 248320 tokens |
| Swap compatibility | Drop-in-compatible swap candidate: no harness, template, or ctx/KV-cache config changes needed to try it (pin 131 keeps the full pin-130 shape) |

This is **compatibility, not quality**. It means the model *can* be booted in
the production shape — not that it *should* be.

## 3. What is already verified vs. what is NOT

### Verified

- Structural/arch compat and tokenizer match (see §2).
- As a **draft head paired with the production main model** (separate
  branch/worktree, pin 132): 73–86% acceptance. That work is untouched by
  this proposal.

### NOT verified — do not overclaim

- **Generation quality/behavior as a standalone main model is COMPLETELY
  UNTESTED.** Instruction-following, reasoning quality, terseness/verbosity,
  long-context retention, and failure modes are all unknown for this weight
  set as a main.
- The 73–86% draft-acceptance figure **does not transfer**: it measures how
  often the production main model agrees with this file's draft tokens — it
  says nothing about what this file generates on its own. A good draft is
  not necessarily a good main, and this proposal must not be read as claiming
  otherwise.
- No throughput, VRAM-ceiling, or deep-concurrency data exists for this file
  as a main either (same shape suggests a similar envelope, but that is an
  inference, not a measurement).

## 4. Recommended next step (before any production consideration)

A quality/behavior eval at the **same rigor bar** as
`docs/arms/chat-template-sharp-quality-eval.md`:

1. Boot pin 131 on the rig when GPUs are free (drain-verify protocol as in
   prior arms docs; never alongside production or another agent's rig test).
2. Re-run the 8-checkpoint retention harness (CP1–CP8: secret number,
   codename, `ACK:` prefix, 3-bullet constraint, lexical ban, ordered list,
   arithmetic, conditional phrase) at 76K+ depth with `max_tokens 1500`,
   grading `content` by string match — same pre-registered-checkpoint method.
3. Add a main-model-specific correctness layer the template eval did not
   need: side-by-side answer-quality comparison vs. pin 130 on a fixed prompt
   set (reasoning coherence, instruction adherence, factual accuracy), since
   here the weights — not just the template — differ.
4. Record VRAM ceiling, boot time, and wall-clock alongside quality, and
   write it up as `docs/arms/qwopus38-flash-main-quality-eval.md` before any
   discussion of pointing the live env var at 131.

Until that eval lands and is reviewed, pin 131 is a parked candidate:
config present, docs present, rig untouched, production unchanged.
