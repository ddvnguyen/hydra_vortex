# Plane (Cloud) + Agent Integration

Plane is the **roadmap / planning layer** for Hydra. Both Claude Code and opencode
agents drive it through the official Plane MCP server. We use **Plane Cloud** with
an **agent-layer bridge** to GitHub: agents talk to Plane (via MCP) and to GitHub
(via `gh`) directly — we do *not* rely on Plane's native GitHub sync.

```
              Claude Code  ─┐                ┌─ Plane MCP ─→ Plane Cloud (roadmap: modules + work items)
                            ├─ agents ───────┤
              opencode     ─┘                └─ gh CLI ────→ GitHub (code, PRs, CI auto-issues)
```

Division of responsibility:
- **Plane** = milestones, roadmap, planning, status of the Revolution-plan work.
- **GitHub** = source of truth for code, PRs, CI/monitoring auto-issues (unchanged).
- Agents keep both in step; there is no automatic issue-level sync to misconfigure.

---

## One-time setup

### 1. Create the Plane Cloud workspace (manual — browser)
1. Sign in at <https://app.plane.so>.
2. Create (or pick) a workspace. The **workspace slug** is the segment in the URL:
   `https://app.plane.so/<workspace-slug>/...`.

### 2. Generate an API key (manual — browser)
1. Profile **Settings → Personal Access Tokens → Add personal access token**.
2. Set an expiry, create it, and copy the token (shown once).

### 3. Export credentials (never commit these)
`.env` is already gitignored. Put the values in your shell profile or a local
`.env` you source — do **not** hardcode them in `.mcp.json` / `opencode.json`
(both reference env vars).

```bash
export PLANE_API_KEY=plane_api_xxxxxxxxxxxxxxxx
export PLANE_WORKSPACE_SLUG=your-workspace-slug
# PLANE_BASE_URL defaults to https://api.plane.so (cloud) — no need to set it.
```

### 4. Tooling
`uvx` (from `uv`) runs the MCP server with no install. Verify: `uvx --version`.
(Already present at `~/.local/bin/uvx` on this box.)

---

## Seed the roadmap

Creates a **Hydra Vortex** project and the Revolution-plan milestones as Plane
**modules** (Phase 0, M-Perf, M3, M4, M5), with concrete tasks as **work items**
linked to each. Idempotent — safe to re-run as the plan evolves.

```bash
python scripts/plane_seed_roadmap.py
```

> Not yet run against a live workspace (needs your key). On first run, watch for any
> `! ... failed [4xx]` lines — they print the API error so endpoint/field mismatches
> are easy to spot. The script falls back between the `work-items` and `issues` API
> names automatically.

---

## Wire the agents

Both config files are committed and use env-var substitution (no secrets):

- **Claude Code** — `.mcp.json` (`mcpServers.plane`, run via `uvx … stdio`).
  Reload: restart Claude Code, then `/mcp` should list **plane** as connected.
- **opencode** — `opencode.json` (`mcp.plane`, `type: local`).
  Restart opencode; the plane tools should appear in its MCP tool list.

Both expand `PLANE_API_KEY` / `PLANE_WORKSPACE_SLUG` from your environment, so the
exports in step 3 must be present in the shell that launches each agent.

---

## Agent-bridge convention (how agents should use it)

- **Plan / status** → Plane. When picking up or finishing roadmap work, update the
  matching Plane work item (state, comments). Milestones are the Plane modules above.
- **Code / review / CI** → GitHub via `gh`, exactly as `CLAUDE.md` already mandates
  (issue → branch → PR → merge). CI/monitoring auto-issues stay on GitHub.
- **Linking** → when a Plane work item gets a GitHub PR/issue, paste the GitHub URL
  into the Plane item (and reference the Plane item in the PR body). Manual, explicit,
  no fragile sync.

Milestone ↔ Plane module map:

| Roadmap | Plane module |
|---------|--------------|
| Phase 0 — Stabilize | `Phase 0 — Stabilize` |
| M-Perf — Heterogeneous Performance | `M-Perf — Heterogeneous Performance` |
| M3 — Persistence & Real Obs | `M3 — Persistence & Real Obs` |
| M4 — Model Management & Multi-Modal | `M4 — Model Management & Multi-Modal` |
| M5 — LLM Obs & Agentic | `M5 — LLM Obs & Agentic` |

---

## Security notes
- Keys live only in your environment / a gitignored `.env`; the committed configs
  contain `${PLANE_API_KEY}` / `{env:PLANE_API_KEY}` placeholders.
- Scope the Personal Access Token to the single Hydra workspace and set an expiry.
- Rotate the token if it is ever printed into logs or shared.
