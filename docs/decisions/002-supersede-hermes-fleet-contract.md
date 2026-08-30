# 002 — Supersede the hermes fleet contract with the leader/Paseo-delegate model

## Problem
`AGENTS.md` (committed 2026-08-15, PR from the #470 lineage) documented a "v2.1.1
Leader Contract" for a "hermes" fleet-orchestration framework: workers constrained to
`opencode-go/deepseek-v4-flash` only, a 5-minute leader tick / 30-minute consult
cadence, and detail references under `orchestration/hermes-lead-template/skills/...`.

Auditing it on 2026-08-21 (in response to a user question — "do we have this fully
documented?") found it was already dead in practice and partly fictional on disk:
- `orchestration/hermes-lead-template/` does not exist in this checkout.
- `Lead.goals.md` and `Lessons.md` / `lessons/`, both referenced as required reading,
  do not exist.
- The model constraint had already been silently overridden: this session's actual
  delegation (established 2026-08-21, see decision 001) uses `opencode-go/mimo-v2.5`,
  `opencode-go/muse-spark-1.2-contributor`, and `opencode-go/hy3` — none of which is
  `deepseek-v4-flash`.
- The tick cadence in practice is a 30-minute Paseo heartbeat, not 5/30.

Two unreconciled process descriptions existed simultaneously, one of them pointing at
nonexistent files — exactly the "docs drift from reality" failure mode this decision's
sibling ADRs (`docs/decisions/001-...`) were written to prevent going forward.

## Decision
Replace `AGENTS.md`'s content with a short, accurate description of the model actually
in use: Claude as leader, delegating to Paseo subagents (model chosen per task, not
hard-restricted), zero-trust independent verification of delegate output before
anything is committed/pushed, a time-boxed Paseo heartbeat, and pointers to the durable
docs structure (`docs/decisions/`, `docs/cookbook/`, `orchestration/state/`) that
decision 001 stood up. The old hermes contract's *content* isn't preserved as a live
file — it's fully recoverable from git history (`git log -- AGENTS.md`) if the
`hermes-lead-template` framework is ever actually stood up again; keeping a stale copy
next to the accurate one recreates the exact problem this decision fixes.

## Alternatives considered
- **Keep both, clearly scoped** (hermes contract for opencode-runtime agents, a
  separate doc for the Claude-leader process): rejected — there's no actual
  opencode-runtime consumer of the hermes contract active in this repo right now (its
  own referenced files don't exist), so scoping it "for opencode agents" would keep
  dead, misleading content live rather than fixing the underlying drift.
- **Leave it alone, note the conflict in `latest-status.md` only**: rejected — a
  reader landing on `AGENTS.md` cold has no reason to also check `latest-status.md`
  first; the bootstrap file itself needs to be trustworthy.
- **Fix only the dangling file references, keep the deepseek-v4-flash constraint**:
  rejected — the constraint has already been overridden in practice for a week's
  worth of real delegated work; re-asserting it in the doc without reverting the
  practice would just create a third source of truth.

## Consequences
- `AGENTS.md` is now the accurate, minimal bootstrap doc; anyone (agent or human)
  reading it cold gets the real operating model.
- If the hermes fleet framework is intentionally revived later, it should come back
  as a fresh decision (not a silent re-edit) since it represents a materially
  different orchestration model (fleet of workers with a hard model constraint vs.
  single-model-per-task delegation with independent verification).
- No code or running process depended on the hermes contract's specifics (it was
  documentation only, and its own reference files were already missing) — this is a
  docs-only change.

Ref: #697
