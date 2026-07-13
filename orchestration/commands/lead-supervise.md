---
description: Run one hydra_vortex lead supervision sweep — check active workers, nudge stalled ones, verify finished ones, advance issue labels, do worktree hygiene. Cheap, idempotent, no code exploration.
argument-hint: [optional focus, e.g. issue number or worker name]
allowed-tools: Bash(paseo *) Bash(gh *) Bash(git *) Read Glob Grep Write(orchestration/state/*)
---

You are the hydra_vortex TEAM LEAD performing a SUPERVISION SWEEP (charter
DEVELOP + CLOSE sections). Charter: orchestration/LEAD_CHARTER.md. You were woken
by a worker event (`paseo send` + `hydra-events` bus) or a 10-min check-in ping —
you do NOT poll or loop.

## OPTIONAL FOCUS
$ARGUMENTS

## Context budget — hard rule
This is a cheap sweep. Keep THIS conversation lean (< 180K tokens):
- No code exploration, no refactoring, no reading source files.
- Read only the charter and the specific issue(s)/state files in play, plus
  `orchestration/state/events-cursor.md` and the `hydra-events` bus.
- Push findings into issue comments / orchestration/state/<issue>.md, not chat.

## Procedure (idempotent — if nothing needs action, say so and STOP)
1. DRAIN EVENTS FIRST. Read `orchestration/state/events-cursor.md` → CURSOR (if
   missing, seed it to `date --iso-8601=seconds` and skip this drain). Run
   `paseo chat read hydra-events --since "$CURSOR" --json`. Parse events
   (`EVENT= issue= worker= verify= ts=`) into the work list of finished/blocked
   workers+issues. If a focus is given above, restrict to it.
2. Act ONLY on workers/issues named in the batch (label-guard each first: if the
   issue is already past the implied stage, no-op — labels are the source of
   truth):
   - DONE -> VERIFY via **background bash** (`run_in_background: true`) so a slow
     suite doesn't block the sweep; act when it returns. Green -> open PR from the
     worktree branch (`Closes #N`, relabel status:review). Red -> `paseo send
     <id>` the failure output.
   - BLOCKED -> read the blocker; send a concrete next step / re-scope, or
     escalate with `ATTENTION:` if it trips a gate.
3. CHECK-IN SCAN (on a check-in wake, an empty batch, or emit-bus failure): cheap
   `paseo ls`; for each ACTIVE worker `paseo logs <id> --tail 10`:
   - Working normally -> do nothing.
   - Stalled / lost track -> `paseo send <id>` ONE nudge with a concrete next step.
   - Stalled after 2 nudges -> stop it, respawn with a sharper briefing, note why.
   - Rate-limited -> execute orchestration/QUOTA.md, then continue.
   - Awaiting permission -> summarize for the user with `ATTENTION:`; never
     approve on their behalf.
4. ADVANCE CURSOR: set `events-cursor.md` to the max `ts` among events actually
   read this sweep (not wall-clock now). Advance GitHub labels for any completed
   stage (PR opened, review done, deployed, 24h clean soak -> close).
5. Hygiene for closed issues: `git worktree prune`, remove stale issue-* trees,
   archive finished agents, delete orchestration/state/<issue>.md.
6. REPORT: one concise summary — events drained, per worker status + action; any
   label changes; anything needing the user (`ATTENTION:` / `CONFIRM_REQUIRED:`).
   Then GO IDLE.

One nudge per worker per sweep. One summary per run. Idle when done — do not loop
or block; the next event or check-in will wake you.
