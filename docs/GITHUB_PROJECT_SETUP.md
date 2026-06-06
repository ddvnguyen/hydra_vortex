# GitHub Projects — Planning Setup (source of truth)

The Hydra roadmap lives in one **GitHub Project v2 ("Hydra Vortex")** on top of the
repo's issues/PRs. One platform: issues = work items, PRs link via `Closes #N`, the
board status is automatic. Agents drive it with `gh project` / `gh issue` (Bash) or the
**GitHub MCP server**.

## Model
- **Milestones** (native) = the roadmap groups: `Phase 0 — Stabilize`,
  `M-Perf — Heterogeneous Performance`, `M3 — Persistence & Real Obs`,
  `M4 — Model Management & Multi-Modal`, `M5 — LLM Obs & Agentic`. (Already created.)
- **Issues** = work items. Roadmap items carry `task` + a priority label
  (`p1-high` / `p2-low`); review findings carry `review-finding`. M3.1.x are **sub-issues**
  of the M3 persistence umbrella.
- **Project board** custom fields: **Status** (Todo / In Progress / In Review / Done),
  **Priority** (P0 / P1 / P2). Views: Board (by Status) + Roadmap (by Milestone) + Table.

## One-time setup
### 1. Add the `project` scope (interactive — you)
```bash
gh auth refresh -s project          # also enables `read:project`
```

### 2. Create the Project + fields (CLI, after scope)
```bash
gh project create --owner ddvnguyen --title "Hydra Vortex"
# note the project number N from the output, then:
gh project field-create N --owner ddvnguyen --name Priority \
  --data-type SINGLE_SELECT --single-select-options "P0,P1,P2"
# (Status field exists by default: Todo/In Progress/Done — add "In Review" in the UI)
gh project link N --owner ddvnguyen --repo ddvnguyen/hydra_vortex
```

### 3. Enable built-in workflows (UI — Project → ⋯ → Workflows)
- **Auto-add to project**: repo `ddvnguyen/hydra_vortex`, issues + PRs.
- **Item added → Todo**; **PR merged → Done**; **Issue closed → Done** (last two are on
  by default). These remove all manual status/cross-link work.

### 4. Seed / refresh the roadmap issues
```bash
python scripts/gh_seed_project.py   # idempotent; creates milestone-tagged issues
```
The auto-add workflow then pulls every repo issue (roadmap + findings) onto the board.

## Agent access
- **`gh` CLI** (works via Bash with your existing auth + `project` scope): `gh project
  item-list N --owner ddvnguyen`, `gh project item-edit …`, `gh issue create/list`.
- **GitHub MCP server** — configured in `.mcp.json` (Claude Code) and `opencode.json`
  (opencode) as the hosted remote `https://api.githubcopilot.com/mcp/` (OAuth on first
  use). Tools: `projects_list`, `list_project_items`, `get_project_item`,
  `update_project_item`, plus issues/PRs. Alternative: run the server locally
  (`ghcr.io/github/github-mcp-server`, `GITHUB_TOOLSETS=repos,issues,pull_requests,projects`,
  a PAT with `repo`+`project`) if you prefer not to use the hosted endpoint.

## Lifecycle
See `CLAUDE.md` `## Task Lifecycle` + `docs/workflow/` — pickup → implement → test →
commit/PR (`Closes #N`) → deploy → monitoring → issue/close-out. No cross-platform
linking; the issue↔PR link and built-in workflows do the bookkeeping.
