# Agent Instructions

The instructions for this project live in **`CLAUDE.md`** (main) and
**`docs/workflow/`** (the per-step task lifecycle). Read `CLAUDE.md` first, then
follow its `## Task Lifecycle` and the linked `docs/workflow/NN-*.md` for each step.

Quick map:
- **Planning / status → GitHub Projects** (`gh project` / GitHub MCP). Board layout:
  `docs/GITHUB_PROJECT_SETUP.md`.
- **Code / PRs / CI issues → GitHub** (`gh`; review findings use the `review-finding`
  label).
- **Build / run / test commands → `DevelopmentRunBook.md` and `docs/build-environment.md`.**
  - Full-solution `dotnet test` requires `--settings src/Hydra.runsettings` to serialize assemblies (avoid PG contention); alternatively run per-project.
- **Build env quirks** (read `docs/build-environment.md` first):
  - **`go` is NOT in default PATH.** It lives at `~/go-sdk/go/bin/go`
    (v1.23.4). Run `export PATH=$HOME/go-sdk/go/bin:$PATH` first.
    `command -v go` from a fresh shell will return nothing.
  - **No sudo.** `apt install` / `systemctl` (root) won't work. Use
    user-level tools or podman exec for root.
  - **CUDA toolkits** at `/opt/software/cuda/{12.9, 13.2, 13.2.1, 13.3}/`.
    The C++ build needs `DCUDAToolkit_ROOT=...` set explicitly per
    target arch (P100 = 12.9, RTX = 13.2) because CMake caches the
    first one it sees.
  - **Podman storage** is on the 77 GB `/mnt/containers/` partition.
    `podman system prune` is safe but check first.
  - **Auth for ghcr.io push** is in `~/.config/containers/auth.json`
    (persistent) AND synced to `/run/user/1000/containers/auth.json`
    (tmpfs). The deploy script reads the tmpfs copy. The token
    needs `write:packages` scope (a classic `gho_` is read-only).
