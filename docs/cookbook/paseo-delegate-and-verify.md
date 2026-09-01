# Delegate a task to a Paseo agent, then verify it

Governing doc: `AGENTS.md` (operating model + zero-trust verification rule).

## Spin up / reuse a delegate

Check for an existing idle agent on the same task before spawning a new one
(`mcp__paseo__list_agents`, filter by `cwd` and recent `sinceHours`) — reuse via
`send_agent_prompt` rather than duplicating.

```
mcp__paseo__create_agent
  provider: "opencode/opencode-go/<model>"   # e.g. opencode/opencode-go/hy3
  cwd: <repo path, or a disposable worktree — see reconcile-epic-branch.md>
  prompt: <self-contained task brief — the agent has no session context>
```

Model choice: see the roster and rationale in `orchestration/state/agent-management-log.md`
— not fixed here, it's a live comparison.

For risky/exploratory work (rebases, merges, anything that could leave a tree half-
fixed), point the agent at a **disposable worktree** (`/tmp/<name>-work`), not the
primary checkout, so a stuck or wrong agent can't corrupt the tree you're relying on.

## While it runs

- `mcp__paseo__get_agent_status` / `list_agents` to check progress; `requiresAttention`
  + `attentionReason` flags finished/errored/permission-blocked states.
- A run with no progress for >30 min, or **repeating the same reasoning verbatim** across
  turns, is a degenerate loop — don't wait it out. Redirect with something concrete and
  tool-based (e.g. "diff against the upstream reference instead of hand-counting braces")
  rather than a more open-ended nudge; open-ended re-prompting is what got it stuck in
  the first place.

## When it reports done — verify before trusting

**Never take a "done"/"all tests pass" report at face value.** Re-run what it claims,
yourself, in the same worktree:

```bash
cd /tmp/<name>-work
<the actual build/test command, not a subset>
```

If the delegate's claimed failure count or root cause doesn't match what you observe,
don't just accept the discrepancy — form a hypothesis, rule out environmental causes
(port contention, resource limits, flakiness — check by rerunning in isolation), and
compare against a clean baseline in a *second* worktree if needed:

```bash
git worktree add /tmp/<name>-baseline-check origin/<branch>   # pre-change state
cd /tmp/<name>-baseline-check && <same build/test command>
```

If the baseline passes cleanly and the changed tree doesn't, the regression is real —
send the delegate (or do yourself) a redirect with the specific evidence (file, exact
failure, what you ruled out), not a generic "please fix the tests."

## Distinguishing "stale baseline" from "genuinely flaky"

When a comparison-against-checked-in-fixture test drifts (e.g. a golden-trace/snapshot
test), don't assume the drift is safe to accept just because a delegate says so. Rerun
the regeneration **3× in isolation** and diff the outputs against each other:
- Identical across all 3 runs → deterministic; the drift is a real behavior change,
  safe to re-baseline if you can explain *why* the behavior changed.
- Differs between runs → the scenario has a real non-determinism (e.g. a race between
  two concurrent operations) — re-baselining would just check in one lucky run and mask
  the flake. File it as an issue instead of "fixing" it by regenerating.

## Cleanup

Remove disposable worktrees once done: `git worktree remove /tmp/<name>-work --force`.
Log cost/retries/verification outcome in `orchestration/state/agent-management-log.md`'s
cost-tracking table regardless of which model was used — that's what makes future model
routing decisions evidence-based instead of guesswork.
