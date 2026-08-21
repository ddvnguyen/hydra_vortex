# agent-management-log — leader/delegate operations record

This is the **management-side** counterpart to `latest-status.md` (development-side
handoff) and `docs/decisions/` (durable technical decisions). It tracks *how the work
got done* — delegation choices, model performance/cost, and standing operational
mechanisms — not *what the work is*. Same ephemeral/session-layer rule as
`latest-status.md`: update at session boundaries, promote anything durable (e.g. "we
standardized on model X") into `docs/decisions/` once it's actually settled.

## Operating mode

Since 2026-08-21: Claude acts as orchestrating leader for this workstream — plans,
delegates execution to Paseo subagents, reviews and verifies their output, rather than
implementing directly. Delegate model default is time-boxed (see below); Claude still
does direct verification/spot-checks itself rather than blindly trusting delegate
output (see `docs/decisions/001-freeze-470-rewrite-internals.md` correction history —
two delegate agents disagreed on a citation and direct verification found the actual
answer differed from both).

**Heartbeat:** `470-stabilization leader check-in`, id `d660ee11`, every 30 min,
expires 2026-08-21T12:45Z (8h from setup). Checks delegate-agent health, task-list
state, pending permissions; updates this file and `latest-status.md` if material
changes occurred. Re-create or extend if the workstream outlives the expiry.

## Delegate model roster

| Model | Rate (in/out/cache per 1M) | Context | Status |
|---|---|---|---|
| `opencode-go/mimo-v2.5` | $0.14 / $0.28 / $0.0028 | 1M | Default until 2026-08-21, now fallback |
| `opencode-go/muse-spark-1.2-contributor` | $0.10 / $0.20 / $0.002 | 1.05M | Evaluated 2026-08-21, needs per-workspace data-policy opt-in |
| `opencode-go/hy3` | $0.14 / $0.58 / $0.035 | 256K | **Default as of 2026-08-21** — user granted 8x usage multiplier through 2026-08-30; no quality data yet, first real delegations pending |

## Model comparison notes (2026-08-21, small sample — not statistically confident)

One identical head-to-head task (review cookbook docs + independently verify #641's
root-cause location) — **normalize by tokens actually used, not just total task
cost**, since total cost conflates model rate with response verbosity:

| | Total cost | Output tokens | $/1K output tokens |
|---|---|---|---|
| mimo-v2.5 | $0.01365 | 1,553 | **$0.009** |
| muse-spark | $0.01216 | 620 | **$0.020** |

Per output token, mimo-v2.5 was **more than 2x cheaper** — muse-spark's lower total
cost came from writing a much shorter response, not a cheaper rate for the work done.
(Caveat: this normalization is only valid for single-turn tasks like this eval —
`get_agent_status`'s `lastUsage` gives cumulative session cost but only the most
recent turn's token counts, so multi-turn/tool-heavy sessions can't be normalized
this way with what's exposed; total dollar cost is the only reliable figure for those,
see the in-flight tasks below.)

- **mimo-v2.5**: cited the wrong file for #641 (`server-context.cpp` swa_full gate —
  real code, wrong mechanism).
- **muse-spark-1.2-contributor**: despite the terser/more expensive-per-token answer,
  cited the correct file (`server-checkpoint-policy.h`, commented "Hydra #641") — this
  citation directly led to discovering #641's fix was already merged and just needed
  verification/closing, not a new fix. Also caught a doc-internal-consistency error
  mimo-v2.5 missed. Terseness didn't cost it accuracy here, but did mean mimo-v2.5
  independently caught a separate error (stale line count) that muse-spark's shorter
  response didn't surface — worth weighing coverage against cost-per-token, not just
  picking whichever number is smaller.
- **Caveat on muse-spark**: opened its response with an unprompted `[#Engineer]` /
  `[#Engineer.Skills]` tag pattern citing skills that don't exist in this repo's
  toolset (stray/injected-looking content, not something Claude asked for) — treat its
  claims with a bit more independent verification than mimo-v2.5's, even when correct.
- **mimo-v2.5 separately**: got stuck in a ~50-step unproductive reasoning loop on an
  ambiguous CI-diagnosis task before a manual redirect (2 concrete follow-up checks)
  got it to converge on the correct root cause. Steerability weakness on ambiguous
  investigative work, not just this workstream.
- **hy3**: no data yet — route the next real task to it to get an actual sample
  before treating "best value" as settled. Nominal per-token rate is the priciest of
  the three, but the 8x multiplier makes real cost near-negligible through 2026-08-30.

**Working conclusion, not final:** Hy3 is the practical default through 2026-08-30
purely on the usage-multiplier, not proven quality or rate efficiency yet. Between
mimo-v2.5 and muse-spark on rate alone, mimo-v2.5 is actually the more token-efficient
of the two per the normalized numbers above — muse-spark's edge was accuracy on one
citation, not cost. Don't default to "whichever quoted the lower total" without
checking token counts first. Revisit after 2026-08-30 when the Hy3 multiplier ends.

## Cost tracking

Partial data only — the `get_agent_status` API doesn't expose `lastUsage` for every
delegate session (some snapshots lack the field entirely, cause unclear). Known costs
so far, 2026-08-21:
- mimo-v2.5 eval task: $0.01365
- muse-spark eval task: $0.01216
- mimo-v2.5 in-flight tasks (PR #695 merge, epic/610 rebase): $0.0149 and $0.0766 as
  of last check, both still running and climbing.

No running cumulative total exists yet — add one here as more data comes in if this
becomes worth tracking precisely.
