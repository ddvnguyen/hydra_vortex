# Quota protocol — 5-hour rolling token windows

Cloud subscription providers (tier 1, and some tier 2) enforce ~5-hour rolling
usage windows. Limits cannot be prevented — design for graceful pause, timed
resume, and bounded fallback to unlimited local models (tier 3).

## Detection

A worker (or you, the lead) is rate-limited when its output shows a usage/rate
limit error from the provider harness. The Paseo app also shows plan usage per
provider. The heartbeat log sweep is the detection point.

## Protocol when a WORKER hits a limit

1. CHECKPOINT — capture enough to resume cold:
   ```
   paseo logs <id> --tail 40
   ```
   Write/update `orchestration/state/issue-<N>.md`:
   task, worktree branch, what is done, what remains, last error, next step,
   the exact resume command.

2. EXTRACT RESET TIME from the error message. If absent, estimate:
   first-message-time-of-window + 5h. When in doubt, add 15 min of margin.

3. SCHEDULE THE RESUME (survives daemon/agent restarts):
   ```
   ./orchestration/scripts/quota-resume.sh "<reset-time>" <N> "<worker-name>"
   ```
   which runs `paseo schedule run-once` with a resume prompt that re-reads the
   checkpoint and either `paseo send <id> "continue from checkpoint"` or spawns
   a fresh worker in the same worktree.

4. OPTIONAL DRAFT FALLBACK while waiting — ONLY if the remaining work is
   low-risk per providers.yaml tier-3 scope (tests, docs, lint, scaffolding,
   mechanical refactors). Spawn a tier-3 worker in the SAME worktree with the
   same briefing plus:
   "You are producing a DRAFT. Commit with prefix 'draft:'. Do not open a PR."
   Add label `draft:needs-review` to the issue. A tier-1/2 agent must review
   the draft after reset before it can count as done.
   NEVER fall back to tier 3 for: migrations, public APIs, security-sensitive
   code, deploy steps, or anything under the big-change gate.

5. Do not retry-spam a rate-limited provider. One scheduled resume, no polling.

## Protocol when the LEAD hits a limit

Do nothing clever. The heartbeat schedule keeps firing on its own cadence and
each run starts by reading state from GitHub labels + `orchestration/state/`,
so the next successful heartbeat resumes supervision automatically. This is
why every heartbeat must be idempotent and stateless.

## Budget hygiene

- Heartbeats are metered too. Keep them cheap: log tails, label checks, short
  nudges. No code exploration during heartbeats.
- Prefer tier-2 for routine implementation to preserve tier-1 for planning,
  review, and merges (see providers.yaml routing).
- If tier-1 AND tier-2 are simultaneously limited: park issues (comment the
  state), let tier-3 produce drafts, and let scheduled resumes drain the queue.
