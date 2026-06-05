# Agent Task Lifecycle — Overview

The MANDATORY loop every coding agent follows. `CLAUDE.md` (`## Task Lifecycle`) is the
always-loaded skeleton; **this folder holds the per-step detail** — open the relevant
`docs/workflow/NN-*.md` when you reach that step.

**GitHub Projects is the single source of truth.** Issues = work items; PRs link to
issues via `Closes #N`; one Project board (v2) sits on the same issues/PRs with automatic
status. There is nothing to cross-link by hand.

```
pick up ─► implement ─► test/verify ─► commit ─► PR ─► CI/merge
   ▲                                                       │  (Closes #N → Done)
   └──────── issue + close-out ◄─ check monitoring ◄─ deploy ◄┘
```

| # | Step | Doc | Primary tool |
|---|------|-----|--------------|
| 1 | Pick up a task | `01-pickup.md` | `gh project` / GitHub MCP |
| 2 | Branch & implement | `02-implement.md` | git + editor |
| 3 | Test / verify | `03-test-verify.md` | `dotnet test`, `pytest` |
| 4 | Commit & PR | `04-commit-pr.md` | git, `gh` |
| 5 | Deploy (if runtime) | `05-deploy.md` | `DevelopmentRunBook.md` |
| 6 | Check monitoring | `06-monitoring.md` | Grafana |
| 7 | Issue + close-out | `07-issue-and-close.md` | `gh` + GitHub MCP |

Principles:
- **One platform.** Roadmap = GitHub Project "Hydra Vortex"; milestones = native GitHub
  Milestones; findings = issues (`review-finding`). PRs `Closes #N`. Built-in workflows
  set Status (item added → Todo; PR merged / issue closed → Done).
- **Commands live in `DevelopmentRunBook.md`** — these docs reference them, not copy them.
- Track sub-steps with todos; exactly one in-progress.
