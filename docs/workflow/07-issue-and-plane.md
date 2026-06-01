# 7. Problem → issue, and Plane close-out

**Goal:** capture any new problem in both systems, and close the task in Plane.

## New problem found (from review, E2E, or monitoring)
1. **File a GitHub issue** (source of truth):
   `gh issue create --label review-finding` — title `[M{n}] short title` (or
   `[M{n}-P{sev}-{seq}]`). Monitoring/CI problems are auto-filed by `monitor.yml` /
   `ci.yml`; don't duplicate those.
2. **Mirror to Plane:** add a work item in the **Backlog — Findings** module (Plane
   MCP), with the GitHub `#` in its description. Cross-linked, both sides reflect it.

## Closing out the task you just finished
3. Confirm the merged PR **closed its GitHub issue** (`Closes #N`).
4. Set the **Plane work item → Done** (Plane MCP). Make sure the PR URL is on the item.
5. Update any milestone status if this completes a milestone item.

Loop complete → back to `01-pickup.md` for the next task.
