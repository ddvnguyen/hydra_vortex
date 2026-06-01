# 7. Problem → issue, and close-out

**Goal:** capture any new problem as a GitHub issue, and confirm the task is closed.

## New problem found (review, E2E, or monitoring)
- `gh issue create --label review-finding` — title `[M{n}] short title` (or
  `[M{n}-P{sev}-{seq}]`); assign the **Milestone** + a priority label. The Project's
  **auto-add** workflow puts it on the board. Monitoring/CI problems are auto-filed by
  `monitor.yml` / `ci.yml` — don't duplicate.

## Closing out the task you finished
- The merged PR (`Closes #N`) closes the issue, and the built-in workflow sets the
  item's **Status → Done** automatically.
- If the work didn't go through a PR, set **Status → Done** yourself
  (`gh project item-edit` / GitHub MCP `update_project_item`).
- Milestone progress updates automatically when the issue closes.

Loop complete → back to `01-pickup.md`.
