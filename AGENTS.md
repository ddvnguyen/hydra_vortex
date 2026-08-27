# AGENTS.md — bootstrap for any agent working in this repo

This is the entry point for any agent (Claude, Paseo-hosted delegates, or otherwise)
touching `hydra_vortex`. Read `CLAUDE.md` first for project facts (architecture,
hardware, language decisions, task lifecycle) — this file covers *process*: who does
what, how work gets delegated and verified, and where the durable record lives.

**Supersedes** the v2.1.1 "hermes leader" fleet contract that lived here before
2026-08-21 (deepseek-v4-flash-only workers, 5-min tick, `hermes-lead-template`
references). That framework's referenced files (`orchestration/hermes-lead-template/`,
`Lead.goals.md`, `Lessons.md`) never existed in this checkout and the model constraint
had already been overridden in practice. See `docs/decisions/002-supersede-hermes-fleet-contract.md`.

## Operating model

Claude acts as **leader**: plans, delegates implementation/research to Paseo subagents,
independently verifies their output, decides what lands. This is a **standing default**,
not scoped to one workstream — see `docs/decisions/001-freeze-470-rewrite-internals.md`
for where it was adopted.

**Delegate models** (Paseo create string `opencode/<model-id>`; registry 2026-08-27v2):
- **Leads:** `command_code/z-ai/glm-5.3-flash` — owner directive 2026-08-27v2.
  Known failure mode: long multi-step turns truncate mid-tool-call or degenerate-repeat;
  mitigate with micro-scoped single-action prompts + empty-tick rule on heartbeats.
- **All subagents/workers:** `command_code/minimax/minimax-m3-free` — owner directive
  2026-08-27v2. Re-run minimax claims against evidence before trusting them.
- `opencode-go/hy3` — proven-leader fallback if command_code unavailable.
- RETIRED 2026-08-27: `opencode-go/glm-5.3-flash` (workspace billing exhausted —
  CreditsError 401 killed a lead mid-task), `opencode-go/ox-alpha-free` (model not
  found), `mimo-v2.5`/`muse-spark-1.2-contributor`/`deepseek-v4-flash` (legacy).

No hard model restriction beyond "pick the one that fits the task and note why" —
routing rationale and cost/quality comparisons live in
`orchestration/state/agent-management-log.md`, not fixed here.

## Zero-trust verification (binding)

**Never relay a delegate's "done" report as fact.** Independently re-run whatever the
delegate claims — build, test, diff review — before committing, pushing, or telling the
user something is resolved. This has caught real problems: a delegate's "3 pre-existing
failures, golden traces unaffected" claim turned out to be 5 failures including a full
differential-parity-harness collapse; a separate delegate silently entered a genuine
degenerate loop while reporting no problem. Depth scales with stakes — a doc typo needs
a glance, a merge into a shared branch needs an actual build+test run.

## Heartbeat

A Paseo heartbeat (`create_heartbeat`) runs periodically (30 min as configured) against
the leading session: checks delegate-agent health (stuck/looping/idle), current task
list, pending permissions, and updates `orchestration/state/latest-status.md` +
`agent-management-log.md` if anything material changed. Heartbeats are time-boxed (not
permanent) and agent-scoped (not transferable across sessions) — check whether one is
already active before creating a new one; recreate if expired and the workstream is
still live.

## Durable-record structure

| Location | What it holds | Update cadence |
|---|---|---|
| `docs/decisions/NNN-slug.md` | Why a non-trivial architectural/process choice was made. ADR format: Problem / Decision / Alternatives considered / Consequences / `Ref: #NNN`. | Appended when a decision is made, same PR as the change it documents. |
| `docs/cookbook/*.md` | Step-by-step "how do I actually run this" recipes for recurring ops tasks. Links back to the `docs/workflow/` doc that governs it. | Written when a task is next performed for real (not speculatively). |
| `orchestration/state/latest-status.md` | Ephemeral dev-side handoff: branches in flight, open questions, next steps. | Session boundaries. |
| `orchestration/state/agent-management-log.md` | Management-side: delegation choices, per-task cost/retry/verification tracking, model comparisons. | Same cadence as above + every heartbeat tick. |
| GitHub issues/PRs | Source of truth for *what shipped*. | Normal GitHub workflow — see `docs/workflow/*.md`. |

Same ephemeral/durable split throughout: `latest-status.md` and the heartbeat notes in
`agent-management-log.md` are session-layer and expected to go stale; anything worth
keeping past a session gets promoted into `docs/decisions/` explicitly. Don't duplicate
a fact across files — link to its one home instead.

## Merge/push discipline

- Pushing to a feature/epic branch (not `main`) to hand off delegate work: fine to do
  directly once independently verified.
- **History-rewriting pushes** (force-push after a rebase) to a branch that already has
  a remote history: don't work around a permission denial — ask. Prefer a new branch
  name over rewriting shared history when the choice is available.
- **Merging into `main`, or any epic→main close-out**: requires explicit user
  confirmation, no exceptions — see `docs/workflow/09-epic-branch.md`. A CI-green
  check is necessary but not sufficient (branch protection has been bypassed before;
  don't treat "checks passed" alone as a merge license).
