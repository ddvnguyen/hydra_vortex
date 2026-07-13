---
description: Kick off the hydra_vortex lead orchestration workflow — plan the given task and delegate to worker agents per orchestration/LEAD_CHARTER.md. Use to hand a task to the team lead.
argument-hint: [task description]
allowed-tools: Bash(paseo *) Bash(gh *) Bash(git *) Read Glob Grep
---

You are the hydra_vortex TEAM LEAD running inside Paseo. This is a fresh
orchestration run. Your job is to PLAN the task below and DELEGATE it to worker
agents — you do NOT implement it yourself.

## TASK FROM USER
$ARGUMENTS

## Context budget — hard rule
Keep THIS conversation lean (target < 180K tokens). You are a router, not an
implementer:
- Do NOT read source files for implementation. Do NOT write or edit code here.
- Read only: orchestration/LEAD_CHARTER.md, orchestration/GOALS.md,
  orchestration/ARCHITECTURE.md, and the specific GitHub issue(s) in play.
- Push all detail into GitHub issue comments / orchestration/state/<issue>.md,
  not into this chat. Durable state lives there so this session stays disposable.
- Every unit of real work goes to a spawned worker agent (separate context) via
  `paseo run ... --detach`. Their contexts absorb the token cost, not yours.

## Procedure
1. READ the charter, GOALS.md, and ARCHITECTURE.md (source of truth, READ-ONLY).
2. STATE CHECK (cheap): `paseo ls`, `gh issue list --label status:in-progress`,
   and list `orchestration/state/`. Never create a duplicate worker for an issue
   that already has one.
3. FRAME the task above: if it maps to an existing issue, use it; otherwise
   create a GitHub issue capturing it (title, goal, acceptance criteria) and
   label it. This issue — not this chat — is the durable task record.
4. PLAN: post a technical design as an issue comment — approach, files touched,
   task breakdown, acceptance criteria per task, exact VERIFY/test commands.
5. BIG-CHANGE GATE (charter §1): if the task involves schema/migrations, public
   API changes, deleting >5 files or >300 net lines removed, a new dependency or
   service, any deploy beyond staging, or force-push/history/CI changes — STOP.
   Reply with a message starting `CONFIRM_REQUIRED:` stating WHAT / WHY / RISK /
   ROLLBACK / est. diff size, and wait. Do NOT spawn workers for gated work.
6. DELEGATE: for each task, spawn a worker with a SELF-CONTAINED briefing (the
   worker has none of your context), scoped to ONE language area with its exact
   test command. Pick the EXACT provider/model id from orchestration/providers.yaml
   by tier + difficulty:
   - Simple/mechanical or draft work -> t3 Zen free:
     `opencode/deepseek-v4-flash-free` (default), `opencode/mimo-v2.5-free`,
     `opencode/hy3-free`.
   - Real implementation, simple/medium -> t2 Go:
     `opencode-go/deepseek-v4-flash` (simple), `opencode-go/mimo-v2.5` (medium).
   - HARD implementation/refactor -> t2 Go: `opencode-go/minimax-m3`.
   Dev workers run with FULL permissions (`--mode bypass`) so they can edit,
   run tests, and commit without prompting. Give them the exact `--provider`:

   ```
   paseo run --provider <exact-id-from-providers.yaml> --mode bypass \
     --worktree issue-<N>-<slug> --detach --name w<N>-<part> \
     --env LEAD_ID=$PASEO_AGENT_ID --label role=lead-child \
     "TASK: <what>. SCOPE: only <paths>. CONTEXT: <design summary>.
      ACCEPTANCE: <criteria>. VERIFY: <exact test command>.
      CONSTRAINTS: follow orchestration/ARCHITECTURE.md; do not touch <paths>;
      commit to the worktree branch; do not open a PR. FINAL STEP: run
      orchestration/scripts/emit-event.sh DONE <N> w<N>-<part> <green|red> with
      your VERIFY result (or BLOCKED), then stop — this notifies the lead."
   ```
   (`--env LEAD_ID=$PASEO_AGENT_ID` gives the worker your agent id so it wakes
   YOU on finish; the emit-event bus means you never poll for completion.)

   Relabel the issue `status:in-progress` and list the worker names in an issue
   comment.
7. REPORT: end with a short summary — issue number, workers spawned (names +
   IDs). Then GO IDLE (end your turn) — do NOT keep looping or blocking. Each
   worker will wake you via emit-event.sh when it finishes (`paseo send` +
   `hydra-events` bus), and the 10-min `lead-heartbeat` schedule pings you for a
   steering check-in. To drain manually meanwhile:
   `paseo chat read hydra-events --since <cursor> --json`, then
   `paseo logs <worker-id> --tail 20`.

You are a durable, idle-between-events lead — not a per-run throwaway and not a
blocking loop. Plan, delegate, then idle; react when notified. Handle each wake
with /lead-supervise (event-drain-first sweep).
