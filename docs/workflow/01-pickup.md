# 1. Pick up a task

**Goal:** choose one unit of work and mark it started in Plane.

1. **Look at both sources:**
   - **Plane board** (project "Hydra Vortex") via the Plane MCP — pick from the
     **active milestone module** (currently **M-Perf**). Modules are the milestones.
   - **Open GitHub findings:** `gh issue list --label review-finding --state open`.
   - Open monitoring/CI issues (`ci-failure` / `monitoring` labels) take priority if
     `main` is red or an alert is firing.
2. **Pick one** that is unblocked and highest-priority (P0 > P1 > P2; red `main` first).
3. **Claim it in Plane:** set the matching work item → **In Progress** (Plane MCP).
   If the task is a GitHub finding, note its issue `#` — you'll need it for the branch
   and PR, and for the Plane ↔ GitHub cross-link.
4. If the task exists in GitHub but not Plane (or vice-versa), create the missing side
   first so both reflect the work (see `07-issue-and-plane.md`).

→ Next: `02-implement.md`
