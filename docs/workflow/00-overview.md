# Agent Task Lifecycle — Overview

The MANDATORY loop every coding agent follows on this project. `CLAUDE.md`
(`## Task Lifecycle`) is the always-loaded skeleton; **this folder holds the detail
for each step**. Open the relevant `docs/workflow/NN-*.md` when you reach that step.

```
pick up ─► implement ─► test/verify ─► commit ─► PR ─► CI/merge
   ▲                                                        │
   │                                                        ▼
 Plane Done ◄─ issue+Plane ◄─ check monitoring ◄─ deploy ◄─┘
```

| # | Step | Doc | Primary tool |
|---|------|-----|--------------|
| 1 | Pick up a task | `01-pickup.md` | Plane MCP + `gh` |
| 2 | Branch & implement | `02-implement.md` | `git` + editor |
| 3 | Test / verify | `03-test-verify.md` | `dotnet test`, `pytest` |
| 4 | Commit & PR | `04-commit-pr.md` | `git`, `gh` |
| 5 | Deploy (if runtime) | `05-deploy.md` | `DevelopmentRunBook.md` |
| 6 | Check monitoring | `06-monitoring.md` | Grafana |
| 7 | Issue + Plane close-out | `07-issue-and-plane.md` | `gh` + Plane MCP |

Principles:
- **Plane = planning/status** (project "Hydra Vortex"; milestones = modules).
  **GitHub = code/PRs/CI** (findings = `review-finding` label). No native sync — you
  are the bridge; cross-link by hand.
- **Commands live in `DevelopmentRunBook.md`** — these docs reference them, not copy them.
- Track sub-steps with todos; exactly one in-progress at a time.
