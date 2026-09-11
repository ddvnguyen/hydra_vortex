# Arm — Sharp chat template (opt-in, unconfirmed)

Config-only arm: no code changes. Adds
[`Qwen-Sharp-Chat-Templates`](https://huggingface.co/peculiar-ragdoll/Qwen-Sharp-Chat-Templates)
(`qwen3.8-froggeric-v22.5.0`) as an opt-in `--chat-template-file`, alongside
the production stock template. Plain Jinja, no weights/executable code
(verified by hand, 30.4 KB) — a terse-routing system prompt (default ON) +
reasoning-effort/tool-XML logic. The template's own claim is faster
*task-completion* via terser output at equal correctness (SWE-bench-Live),
not raw decode tok/s — flagged as a methodological risk before testing,
confirmed relevant below.

## Status: single probe only, NOT a confirmed result

Dispatched 2026-09-11 to agent `e8954f11` on the production rig (RTX 5060 Ti
+ RTX 3060, production shape, 1 session × 4 turns per template).

| metric | stock template | sharp template |
|---|---|---|
| completion tokens (t1-4) | 750 / 750 / 417 / 447 | 595 / 750 / 750 / 750 |
| wall time per turn (s) | 25.75 / 29.91 / 19.74 / 22.40 | 22.94 / 23.38 / 23.85 / 30.90 |
| harness tok/s (mean) | 23.82 | 28.43 |

Correctness: spot-checked responses on both templates for a KV-quant
question — both correct and technically sound; sharp's answer was only
mildly shorter.

## Why this is not yet actionable

1. **n=1 session, 4 turns.** A single, unreplicated sample — this session's
   own standard (applied to every other arm, e.g. arm102's deep-depth
   retest) requires at least one repeat before trusting a result this size.
2. **The metric that "won" is the one already proven unreliable this
   session.** Harness tok/s = `completion_tokens / wall_seconds`. The
   "last-turn tail" investigation (same day) proved this ratio gets
   distorted by early-EOS/short completions — exactly what happens here
   (sharp: 595/750/750/750 vs stock: 750/750/417/447 — different
   completion-length profiles per turn, not a controlled comparison).
3. **Wall-clock-per-turn — the more honest metric — does not show a clean
   win.** Sharp is faster on turns 1-2 but *slower* on turns 3-4 (23.85s /
   30.90s vs stock's 19.74s / 22.40s).

## Conclusion

There is a plausible signal, not a confirmed one. Do not adopt as the
production default from this data. This PR only adds the template file +
an opt-in params YAML
(`infra/llama-baseline/params/arm-chat-template-sharp-opt-in.yml`) so a
proper multi-turn, multi-session repeat (arm102-style methodology: >=2 full
boots, matched completion-length profile or explicit wall-time-only
comparison) can be run without re-deriving the launch shape. No production
behavior changes as part of this PR.

## Rig protocol (this pass)

Live pod (`pod_llama-baseline`, production shape) was not stopped for this
probe — sharp template was booted standalone on the shared ports, then torn
down and the production pod's normal boot restored (health 200,
15847/11911 MiB, confirmed after).
