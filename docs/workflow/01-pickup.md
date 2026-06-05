# 1. Pick up a task

**Goal:** choose one unit of work and mark it In Progress on the board.

1. **Look at the GitHub Project board** "Hydra Vortex" — `gh project item-list <n>
   --owner ddvnguyen` (or the GitHub MCP `list_project_items`). Filter by the active
   **Milestone** (currently **M-Perf**). Open findings: `gh issue list --label
   review-finding --state open`. Red `main` / firing alerts take priority.
2. **Pick one** unblocked, highest-priority item (P0 > P1 > P2; red `main` first).
3. **Claim it:** set the item's **Status → In Progress** (`gh project item-edit` /
   GitHub MCP `update_project_item`). Note the issue `#` — you'll need it for the
   branch and PR.
4. If the work has no issue yet, create one (`gh issue create`, assign the Milestone +
   labels); the Project's **auto-add** workflow puts it on the board.

→ Next: `02-implement.md`
