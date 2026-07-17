# Hydra — Project Instructions

## What Is This
Multi-GPU LLM inference system. Routes requests across **RTX 5060 Ti** (sm_120,
host), **RTX 3060** (sm_86, host), and **Tesla P100** (sm_60, KVM VM).
Hydra.Core (C#) is the coordinator; Hydra Head (Go) manages llama-engine per GPU
node. Migrates ~800 MB KV cache between GPUs. The 5060 Ti + 3060 are wired for
COMBINED engine mode (expert-split).

## Architecture
Client → Hydra.Core :9000 [C#] → Hydra Head [Go] → llama-engine [C++ fork] →
Hydra Store RPC :9500 + tmpfs. 5060 Ti + 3060 are same-host (pod_hydra-system);
P100 is a separate KVM VM (192.168.122.21). Full reference: `docs/architecture.md`.

## Language Decisions (FINAL)
| Service      | Language  | Why |
|--------------|-----------|-----|
| Hydra.Core   | C# .NET 10| Pipelines, SendFileAsync |
| Hydra Head   | Go        | Single binary, process mgmt, OCI pull |
| llama-engine | C++ fork  | +3 streaming endpoints, COMBINED-mode filter |

## Critical Facts
- RTX 5060 Ti: ~200 tok/s decode. RTX 3060: ~60 tok/s decode. P100: 28 tok/s decode.
- Cross-GPU save/restore: WORKS. P/D split verified end-to-end 2026-07-01.
- Prompt-cache reuse: FIXED via fork patch (ik_llama.cpp#1762 port).
- n_tokens MUST be > n_past or cache is nuked. Coordinator guards this.
- KV state at 60-80K context: ~800 MB.

## COMBINED engine mode (5060 Ti + 3060)
Two modes: **COMBINED-OT** (expert-split, MoE default) and **COMBINED-static**
(layer-split, Dense profile). Switch: `bash scripts/set-profile.sh {moe|dense}`.
Details: `docs/combined-engine-mode.md`.

## Hardware
- RTX 5060 Ti 16 GB sm_120, CUDA 13.2 — host (CUDA0, primary)
- RTX 3060 12 GB sm_86, CUDA 13.2 — host (CUDA1, SOLO + COMBINED peer, ggml-RPC :9504)
- Tesla P100 16 GB sm_60, CUDA 12.9 — KVM VM (192.168.122.21:8086, Q5_K-balanced)
- tmpfs 30 GB at /mnt/llm-ram. Model: Qwopus3.6-35B-A3B (Q3_K-mini on host, Q5_K-balanced on P100)

### Model Storage
- `/mnt/WorkDisk/LLM-Models` — NVMe, **production models**
- `/mnt/SSD` — SATA SSD (ntfs3), non-production models, mounted `:ro` as `/models`

## Build Environment Quirks
- **`go` is NOT in default PATH.** Use `~/go-sdk/go/bin/go` (v1.23.4): `export PATH=$HOME/go-sdk/go/bin:$PATH`
- **No sudo.** Use user-level tools or `podman exec` for root.
- **CUDA toolkits** at `/opt/software/cuda/{12.9, 13.2, 13.2.1, 13.3}/`. Set `DCUDAToolkit_ROOT` per arch (P100=12.9, RTX=13.2).
- **Podman storage** on `/mnt/containers/` (77 GB). `podman system prune` is safe.
- **ghcr.io auth** in `~/.config/containers/auth.json` + synced to `/run/user/1000/containers/auth.json` (tmpfs, deploy reads tmpfs copy).
- **`dotnet test`** needs `--settings src/Hydra.runsettings` for full-solution; per-project works without it.
- **Podman compose** needs `export HYDRA_HEAD_AUTH_TOKEN=$(cat .hydra-head-token)` before `up`.

## Milestones
| MS | Goal | Status |
|----|------|--------|
| M0–M2 | Core + routing + chunked dedup | ✅ done |
| M-Perf | Heterogeneous perf (spec-decode → P/D → pipeline) | ✅ done |
| **Llama-Engine** | **P/D split mix-quant** (RTX precise prefill / P100 quant decode) | **▶ now** |
| M3–M5 | Persistence + model mgmt + obs | Production (later) |

## Task Lifecycle (MANDATORY)
Every task follows this loop. **Read `docs/workflow/NN-*.md` when you reach each step.**
GitHub Projects is the single source of truth. Commands: `DevelopmentRunBook.md`.

1. **Pick up** — `gh project item-list` / GitHub MCP; set Status → In Progress.
   → `docs/workflow/01-pickup.md`
2. **Branch & implement** — never on `main`. → `docs/workflow/02-implement.md`
3. **Test / verify** — `dotnet test src/core/Tests.Shared/ && dotnet test src/core/Tests.Core/`
   + `pytest tests/system`. "E2E verify" = deploy to live, **never merge**.
   → `docs/workflow/03-test-verify.md`
4. **Commit & PR** — conventional commits + `Co-Authored-By`; `gh pr create … Closes #N`.
   **Merging requires explicit user confirmation.**
   → `docs/workflow/04-commit-pr.md`
5. **Deploy** (if runtime/fork) — build sm_120/sm_60; push fork + bump submodule.
   → `docs/workflow/05-deploy.md`
6. **Check monitoring** — Grafana :3000 + alerts. → `docs/workflow/06-monitoring.md`
7. **Close-out** — `gh issue create --label review-finding`; Status → Done on merge.
   → `docs/workflow/07-issue-and-close.md`

## GitHub Workflow
Findings → issues (`review-finding` label) → branch (`gh issue develop N`) →
fix → PR (`Closes #N`) → merge → deploy. **No manual board cross-linking.**

## Key Design Decisions (do not relitigate)
- **One GPU = one compute task** (invariant). Dual-role is capability switching, not concurrency.
- **Plan bold**: architecture-level solutions over local patches.
- No Ray (2 nodes, not needed). Store on tmpfs (sendfile + zero-copy). Full KV only.
- Content-addressed chunking at Store level. No shared filesystem.
- Hydra Head in Go: one binary per node, OTLP/HTTP log push.
- llama-server via OCI registry (ghcr.io), pulled at startup.

## Monitoring
Grafana :3000, Prometheus :9091, Loki :3100. Start: `bash scripts/start-env.sh`.
Full reference: `docs/monitoring-observability.md`.

## Coding Agent Rules
1. Ask for decisions when multiple options exist.
2. Track tasks with `todowrite`, one `in_progress` at a time.
3. Use sub-agents (2-3 parallel) for research/multi-file work >30s.
4. End with a `---` + summary block.

## Project Status File (MANDATORY)
`PROJECT_STATUS.md` is the single source of truth for milestones, implementation
status, and verified facts. **Every agent must update it when code changes land:**
- New feature merged → update "Current Implementation Status" table
- Milestone completed → update status column
- New verified fact → add to "Verified Facts" table
- Architecture changed → update diagrams and component tables
**Never let PROJECT_STATUS.md drift from the actual codebase.**

## References
- `PROJECT_STATUS.md` — vision, structure, milestones, implementation status
- `docs/architecture.md` — routing, run modes, session lifecycle
- `specs/rpc-protocol.md` — binary wire format + opcodes
- `docs/PORTS_AND_ENV.md` — every service, port, env var
- `docs/agent-coding-rules.md` — full agent rationale + examples
- `docs/hydra-system-pod.md` — starting/stopping/debugging hydra-core + hydra-head pod
- `docs/GITHUB_PROJECT_SETUP.md` — board layout
