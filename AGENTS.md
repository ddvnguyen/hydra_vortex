# Agent Instructions

The instructions for this project live in **`CLAUDE.md`** (main) and
**`docs/workflow/`** (the per-step task lifecycle). Read `CLAUDE.md` first, then
follow its `## Task Lifecycle` and the linked `docs/workflow/NN-*.md` for each step.

Quick map:
- **Planning / status → GitHub Projects** (`gh project` / GitHub MCP). Board layout:
  `docs/GITHUB_PROJECT_SETUP.md`.
- **Code / PRs / CI issues → GitHub** (`gh`; review findings use the `review-finding`
  label).
- **Build / run / test commands → `DevelopmentRunBook.md`.**
  - Full-solution `dotnet test` requires `--settings src/Hydra.runsettings` to serialize assemblies (avoid PG contention); alternatively run per-project.**
