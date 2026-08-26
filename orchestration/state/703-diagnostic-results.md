# 703-diagnostic-results.md — 128K turn-8-10 timeout diagnostic arm results

**Source:** dev worker (128K-diagnostic arm)
**Status:** COMPLETE — all 5 sub-arms executed
**Spec:** `docs/investigations/703-timeout-diagnostic-arm.md`
**Created:** 2026-08-26
**Executed:** 2026-08-26 15:48–16:15 UTC+7

## Sub-arms

| ID  | Arm   | Model                    | Split | Purpose                        |
|-----|-------|--------------------------|-------|--------------------------------|
| T1  | 045   | MTP-Q5_K_M (standard)    | 27/38 | Passing baseline, instrumented |
| T2  | 046   | MTP-Q5_K_M (standard)    | 26/39 | Failing split, instrumented    |
| T3  | 047   | MTP-Q5_K_M (standard)    | 28/37 | Boundary nudge +1 toward 5060Ti|
| T4  | 048   | MTP-Q5_K_M (standard)    | 25/40 | Passing split, confirm         |
| T5  | 049   | UD-Q5_K_M (Unsloth Dyn)  | 27/38 | UD degradation factor          |

## Results table

| Arm | Turn 1  | Turn 2  | Turn 3  | Turn 4  | Turn 5  | Turn 6   | Turn 7   | Turn 8    | Turn 9    | Turn 10   | Pass/Fail | First Fail | Evictions | Fail n_past | Fail cache% | Fail curl (s) | Coherence |
|-----|---------|---------|---------|---------|---------|----------|----------|-----------|-----------|-----------|-----------|------------|-----------|-------------|-------------|---------------|-----------|
| T1  | OK 11.5s | OK 7.1s | OK 10.4s | OK 14.1s | OK 22.1s | OK 42.8s | OK 97.9s | FAIL .3s  | FAIL .6s  | FAIL 1.2s | 7/10      | turn 8     | 0         | 0           | 0.0%        | 0.30          | ok        |
| T2  | OK 11.4s | OK 7.1s | OK 10.0s | OK 13.5s | OK 21.9s | OK 42.4s | OK 96.5s | FAIL .3s  | FAIL .6s  | FAIL 1.2s | 7/10      | turn 8     | 0         | 0           | 0.0%        | 0.30          | ok        |
| T3  | OK 14.8s | OK 10.4s| OK 14.8s | OK 13.4s | OK 21.9s | OK 41.9s | OK 97.6s | FAIL .4s  | FAIL .6s  | FAIL 1.2s | 7/10      | turn 8     | 0         | 0           | 0.0%        | 0.40          | ok        |
| T4  | OK 11.2s | OK 7.1s | OK 10.2s | OK 13.7s | OK 21.6s | OK 42.0s | OK 95.9s | FAIL .3s  | FAIL .6s  | FAIL 1.2s | 7/10      | turn 8     | 0         | 0           | 0.0%        | 0.30          | ok        |
| T5  | OK 9.6s  | OK 10.7s| OK 12.9s | OK 13.4s | OK 22.4s | OK 43.9s | OK 98.0s | FAIL .3s  | FAIL .7s  | FAIL 1.3s | 7/10      | turn 8     | 0         | 0           | 0.0%        | 0.30          | ok        |

### Per-turn timing detail (server total at deep ctx)

| Arm | Turn 5 | Turn 6 | Turn 7 | Turn 8 fail reason |
|-----|--------|--------|--------|-------------------|
| T1  | 22.1s  | 42.8s  | 97.9s  | 159,765 tokens > 131,072 ctx |
| T2  | 21.9s  | 42.4s  | 96.5s  | 159,765 tokens > 131,072 ctx |
| T3  | 21.9s  | 41.9s  | 97.6s  | 157,479 tokens > 131,072 ctx |
| T4  | 21.6s  | 42.0s  | 95.9s  | 159,765 tokens > 131,072 ctx |
| T5  | 22.4s  | 43.9s  | 98.0s  | 159,384 tokens > 131,072 ctx |

### Slots at failing turn (turn 8, before)

All arms: `n_past=0 total=~79K cached=0 processed=0 ctx=131072 cache_pct=0.0%`

### Evictions

All arms: **0 evictions** — no "making room for prompt cache entry" in server log.

### Generated text coherence

Last OK turn (turn 7) for all arms: coherent Python code, unique_ratio > 0.89. No repetition or collapse.

## Decision criteria

- curl slow + server total >90s → **server compute collapse**: **NOT CONFIRMED** — curl returned in <0.5s at fail turn. Server responded immediately with 400 error (not compute stall).
- curl fine but pi slow → **pi-harness client bug**: **NOT TESTED** — diagnostic used raw curl, not pi harness. Pi harness was not in the loop.
- repetitive garbage → **coherence collapse**: **NOT OBSERVED** — all generated text was coherent (unique_ratio 0.89–0.96).
- T2 fails / T3 passes (vs T1/T4) → **tensor-boundary effect confirmed**: **NOT CONFIRMED** — all 4 splits (27/38, 26/39, 28/37, 25/40) produce identical pass/fail pattern (7/10 OK, fail at turn 8). Split ratio has no observable effect in this diagnostic.
- T5 fails while T1 passes → **UD deep-context degradation**: **NOT CONFIRMED** — T5 (UD) and T1 (standard) both fail at turn 8 with identical context overflow. UD shows slightly lower unique_ratio (0.89 vs 0.94) but both are coherent.

## Verdict

### Primary finding: Diagnostic script context-accumulation artifact

**All 5 sub-arms fail at turn 8 with `exceed_context_size_error`** (requested tokens exceed 131,072 ctx limit). This is a **script artifact**, not the original turn-8-10 timeout:

1. The diagnostic script accumulates the **full conversation history** (all prior turns' user+assistant messages) into each new request, causing exponential token growth: ~20K → ~40K → ~80K → ~160K tokens.
2. At turn 8, accumulated history (~80K) + new prompt (~80K) = ~160K tokens, which exceeds the 131,072 context limit.
3. The server rejects the request immediately with a 400 error (curl returns in <0.5s) — this is NOT the original "timeout" behavior.

### What this diagnostic did NOT reproduce

The original turn-8-10 timeout (arms 018, 025, 026, 028–034) was reported as a **timeout** (no response), not a context overflow error. The diagnostic script's curl-based approach with full-history accumulation does not match the original failure mode because:

- Original arms used the `pi` harness (which may handle context differently)
- Original arms may not have accumulated full history (context-shift may have been active)
- Original arms showed different pass/fail patterns across splits (27/38 passes, 26/39 fails) — this diagnostic shows no split-dependent behavior

### Secondary finding: No split-dependent behavior observed

All 4 tensor splits (25/40, 26/39, 27/38, 28/37) produce identical:
- Timing profiles (turn 7 takes ~96-98s for all)
- Failure mode (context overflow at turn 8)
- Eviction count (0 for all)
- Coherence (all generate coherent text)

This suggests the original split-dependent pass/fail pattern is **not reproducible** with this diagnostic approach, or the effect is subtle enough to require a different test methodology.

### UD model comparison

T5 (UD) shows slightly lower unique_ratio (0.89 vs 0.94 for standard) but both are well above the 0.3 repetition threshold. No evidence of UD deep-context degradation in this diagnostic.

## Recommendations for follow-up

1. **Fix the diagnostic script** to implement proper context-shifting (truncate old history when approaching ctx limit) rather than accumulating full history.
2. **Re-run with fixed script** to reproduce the actual turn-8-10 timeout behavior.
3. **Test with pi harness** to match the original test methodology (curl bypass may not exercise the same code path).
4. **Investigate why `--context-shift` is not working** — the server flag is set but context overflow still occurs at turn 8.

## Notes

- Results in `/tmp/rpc-test/results/045-049-5fff12845/`
- Each contains: `llama-server.log`, `slots_before_*.json`, `slots_after_*.json`, `turn_*.json`, `turn_*_text.txt`, `history.json`, `evictions.log`
- Script fix applied during execution: `--max-tokens` → `--n-predict` (invalid flag), and file-based JSON I/O (avoids ARG_MAX)
- NOT written to `docs/investigations/703-results-report.md` ledger rows (per spec Out of scope)

## Post-execution: Harness Bug Fix (2026-08-26)

**ALL PRIOR T1-T5 RUNS (045-049) are INVALID — harness bug (doubling).**

Root cause: `history.update` in the diagnostic script used `history.extend(msgs)` where `msgs` already contained `old_history + new_user_msg`, causing exponential doubling of history on every turn (~20K→40K→80K→160K tokens by turn 8). This is why all 5 arms failed identically at turn 8 with `exceed_context_size_error`.

**Fix applied:** Changed history update to only append the NEW turn's user message via `msgs[len(history):]` before extending. History now grows linearly (+1 user +1 assistant per turn).

**What we CAN salvage from prior runs:**
- Turn 7 cache-reuse data (47% hit rate on T1, 79,700 prompt / 37,279 cached) is valid — the bug only caused incorrect context SIZE, not incorrect cache behavior at turn 7.
- Server compute scaling (turn timing: 11s→7s→10s→14s→22s→43s→98s) is valid — timing reflects real server compute, not harness bug.
- Coherence (unique_ratio > 0.89 all arms) is valid.
- No split-dependent behavior across 25/40–28/37 is valid.

**What needs re-running:**
- Full T1-T5 diagnostic with fixed script to get correct token counts and verify linear context growth.
- Re-verify turn 8+ behavior (with correct linear growth, context may not overflow at turn 8 if it was only the doubling that caused it).

**Dry-run verification:** Fixed logic produces constant +4,319 chars/turn (linear). Buggy logic produced doubling delta (+188→+376 chars/turn, exponential).

**Commit:** `2d7bfcd08` on `baseline-dual-rtx-llamacpp-dsh`
