# Agent Instructions

The instructions for this project live in **`CLAUDE.md`** (main) and
**`docs/workflow/`** (the per-step task lifecycle). Read `CLAUDE.md` first, then
follow its `## Task Lifecycle` and the linked `docs/workflow/NN-*.md` for each step.

Quick map:
- **Planning / status → Plane** (project "Hydra Vortex"; milestones = modules; driven
  via the Plane MCP server). Setup: `docs/PLANE_SETUP.md`.
- **Code / PRs / CI issues → GitHub** (`gh`; review findings use the `review-finding`
  label). There is no native Plane↔GitHub sync — you are the bridge; cross-link by hand.
- **Build / run / test commands → `DevelopmentRunBook.md`.**
  - Full-solution `dotnet test` requires `--settings src/Hydra.runsettings` to serialize assemblies (avoid PG contention); alternatively run per-project.**
