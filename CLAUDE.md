# Hydra — Claude Handoff

## What Is This
Multi-GPU LLM inference system. Routes requests across RTX 5060 Ti and Tesla P100.
All Hydra services run as containers on the host; Hydra.Core is the single C# binary; only
llama-server P100 lives in a KVM VM (192.168.122.21:8086). Migrates ~800 MB KV
cache state between GPUs without re-prefill.

## Read These First
1. `PROJECT_PLAN.md` — vision, structure, milestones (10 min)
2. `docs/architecture.md` — implemented design: routing, run modes, session lifecycle,
   chunked dedup, prefix checkpoints, n_past guard (10 min)
3. `specs/rpc-protocol.md` — binary wire format + all opcodes (5 min)
4. `## Task Lifecycle` (below) + `docs/workflow/` — how to work a task end-to-end
5. Active milestone `docs/milestone-perf.md` (M-Perf) + `DevelopmentRunBook.md` for
   build/run/test. Live board: GitHub Project (`docs/GITHUB_PROJECT_SETUP.md`).

## Architecture
```
Client (HTTP) → Hydra.Core :9000 [C#/.NET 10]
                    │ HTTP            │ HTTP
                    ▼                 ▼
              Hydra Head RTX      Hydra Head P100 [Go]
              (container)         (VM systemd)
                   │                    │
                   ▼                    ▼
              llama RTX :8080     llama P100 :8086  [C++ fork]
              + node_exporter     + node_exporter
              + nvidia_exporter   + nvidia_exporter
              + promtail          + promtail
                   │                    │
                   │ RPC               │ RPC
                   └───────┬───────────┘
                           ▼
                     Store RPC :9500 + tmpfs
                     /mnt/llm-ram/store/
```

## Language Decisions (FINAL — do not change)
| Service      | Language  | Reason                                      |
|--------------|-----------|---------------------------------------------|
| Hydra.Core   | C# .NET 10| System.IO.Pipelines, Socket.SendFileAsync   |
| Hydra Head   | Go        | Single binary, process management, OCI pull |
| llama-server | C++ (fork)| +3 streaming state endpoints only           |

## Critical Facts (POC verified)
- P100 prefill: 110 tok/s → 80K context = 12 minutes. RTX handles large prefill.
- P100 decode: 28 tok/s — acceptable.
- Cross-GPU save/restore: WORKS. cache_n=2964 after restore.
- Prompt-cache reuse: FIXED for qwen35moe via the fork patch (recurrent/hybrid context
  checkpoints, port of ik_llama.cpp#1762). Follow-up turns now reuse cached KV
  (`restored context checkpoint`) instead of full re-prefill — verified live 2026-06-04
  (turn-2 cached_tokens 1229/1251). Was: "SSM truncation BROKEN; --cache-prompt useless."
- n_tokens MUST be > n_past or cache is nuked. Coordinator must guard this.
- KV state at 60-80K context: ~800 MB.

## Hydra Head (Go node agent)
Replaces the old Agent containers + manual llama-server deployment. Single Go binary per GPU
node that manages 4 sub-services: llama-server, node_exporter, nvidia_exporter, promtail.

### Source & Deploy
| What | Where |
|------|-------|
| Go module | `src/head/` (module `github.com/ddvnguyen/hydra_vortex/hydra-head`) |
| Config files | `infra/hydra-head/config/global.yaml` + per-node overrides |
| Deploy script | `scripts/deploy-hydra-head.sh` |
| RTX Dockerfile | `infra/hydra-head/Dockerfile.rtx` (based on CUDA base `Dockerfile_26.04_cuda13.2`) |
| P100 systemd unit | `infra/hydra-head/hydra-head.service` |

### 4-Service Management
Hydra Head owns lifecycle (start/stop/restart/auto-restart with backoff) of:
- llama-server
- node_exporter (P100 only; RTX uses host-level exporter in infra-host pod)
- nvidia_exporter (P100 only; RTX uses host-level exporter in infra-host pod)
- promtail

Each service is controlled via per-node `services:` YAML config (`enabled`, `binary`, `config`, `port`, `args`).

### OCI Registry
llama-server binary pulled from ghcr.io at startup via `crane` library:
- `ghcr.io/ddvnguyen/llama-server-sm60:69e9835ab` (P100, 62 MB)
- `ghcr.io/ddvnguyen/llama-server-sm120:69e9835ab` (RTX, 84 MB)

Built `FROM scratch` with the statically-linked CUDA binary as the single file.
No more mount-based deploys of `build_sm120/` or `build_sm60/`.

### Log Separation
Promtail detects llama-server log patterns (`^\d+\.\d+\.\d+\.\d+\s+[A-Z]\s+`) and labels
them `component=llama-server` vs `component=hydra-head` in Loki.

### Deprecated infra (replaced by hydra-head)
| Old file | Replacement |
|----------|-------------|
| `infra/quadlets/hydra-agent-rtx.container` | Agents merged into Hydra.Core |
| `infra/quadlets/hydra-agent-p100.container` | Agents merged into Hydra.Core |
| `infra/systemd/llama-p100-user.service` | Managed by hydra-head |
| `infra/llama-rtx-node/docker-compose.yml` | `infra/hydra-head/Dockerfile.rtx` |
| `scripts/deploy-llama.sh` | `scripts/deploy-hydra-head.sh` |

## llama.cpp Fork (hydra-state-streaming branch)
Three new endpoints added to tools/server/server.cpp:
- GET /slots/{id}/state      → stream binary KV state out
- PUT /slots/{id}/state      → stream binary KV state in
- GET /slots/{id}/state/meta → metadata (n_past, state_size)

These eliminate disk round-trips. Hydra.Core pipes stream directly llama↔Store.
Without these patches, nothing else in the system makes sense.
Build RTX: GGML_CUDA_FORCE_CUBLAS=ON, sm_120. Build P100: sm_60.

## Milestones
Core M0–M2 built. Roadmap **restructured 2026-06** around the Tier-1 performance
track (**M-Perf supersedes the old "M3 Production"**). Tracked in the GitHub Project
"Hydra Vortex" + native Milestones (`docs/GITHUB_PROJECT_SETUP.md`); detail in
`docs/milestone-*.md`.

| MS      | Goal                                                       | Status  |
|---------|------------------------------------------------------------|---------|
| M0      | llama fork + Store + Agent + System test                   | ✅ done  |
| M1      | Coordinator + routing + session + migration                | ✅ done  |
| M2      | Chunked dedup + prefix checkpoints                         | ✅ done  |
| Phase 0 | Stabilize: green CI, restore obs, rebase onto remote       | ▶ now   |
| M-Perf  | Heterogeneous perf: spec-decode → P/D streaming → pipeline | ▶ next  |
| M3      | Persistence (NVMe write-behind, **C# re-spec**) + obs harden | planned |
| M4      | Model mgmt & multi-modal (dist, dynamic load, vision/…)    | planned |
| M5      | LLM obs & agentic (Langfuse, A/B testing, agentic)         | planned |
| Phase 5 | Semantic KV: KV DAG + git-aware prefix cache (#107)        | planned |

Phase 5 (Store v2 "Semantic KV", #107) design: `docs/kv-dag-architecture.md` (KV DAG, git-aware
reuse, content-defined chunking; quantization excluded), decomposed as issues #107-A … #107-I.

## Task Lifecycle (MANDATORY)
Every unit of work follows this loop. Each step's detail is in `docs/workflow/` —
**open the linked doc when you reach that step**. **GitHub Projects is the single
source of truth** (issues = work items, PRs link via `Closes #N`, board status is
automatic — no cross-linking). Commands live in `DevelopmentRunBook.md`.

1. **Pick up** — choose from the **GitHub Project board** (`gh project item-list` /
   GitHub MCP), filtered by Milestone (currently M-Perf), or
   `gh issue list --label review-finding --state open`; set the item's Status →
   In Progress. → `docs/workflow/01-pickup.md`
2. **Branch & implement** — never on `main`; `fix/…` from the issue or `feat/…`;
   follow the milestone doc. → `docs/workflow/02-implement.md`
3. **Test / verify** — unit (`dotnet test src/core/Tests.Shared/ && dotnet test src/core/Tests.Core/`) + E2E
   (`pytest tests/system`) green before PR.
   → `docs/workflow/03-test-verify.md`
4. **Commit & PR** — conventional commits + `Co-Authored-By`; `gh pr create …
   Closes #N` (this link auto-moves the Project item). → `docs/workflow/04-commit-pr.md`
5. **Deploy** (if runtime/fork) — build sm_120/sm_60; push the fork + bump the
   `src/llama-cpp` submodule pointer. → `docs/workflow/05-deploy.md`
6. **Check monitoring** — Grafana :3000 + alerts; no regressions.
   → `docs/workflow/06-monitoring.md`
7. **Issue + close-out** — new problem → `gh issue create --label review-finding`
   (auto-added to the Project); finished item's Status → Done (auto on PR-merge/close).
   → `docs/workflow/07-issue-and-close.md`

## GitHub Workflow (MANDATORY for all coding agents)

The full development cycle: **feature → issue → implement → review → merge → deploy → monitoring → (problem → issue)**

### Findings → Issues
Review findings are tracked **directly as GitHub issues** labelled `review-finding`
(grouped per milestone, e.g. `[M2] …`). There is **no** `reviews/` markdown tree,
`sync_reviews_to_github.py`, or `reviews.yml` — those were removed. File findings
with `gh issue create --label review-finding`; list them with
`gh issue list --label review-finding --state open`.

Title convention: `[M{n}] short title`, or `[M{n}-P{sev}-{seq}]` for a specific
finding. P0 = correctness/data-loss, P1 = behavioural bug, P2 = minor/perf.

### Fix → Branch → PR
1. Pick the finding: `gh issue list --label review-finding --state open`
2. Branch from it: `gh issue develop N --name fix/mN-Psev-seq`
3. Implement the fix.
4. Open the PR: `gh pr create --title "fix: [MN-Psev-seq] short title" --body "Closes #N"`

### Monitoring issues
Auto-created by `monitor.yml` (Prometheus alerts) and `ci.yml` failure handlers.
Do not manually close a monitoring issue without investigating the root cause.

## Planning (GitHub Projects)
Roadmap/planning lives in the **GitHub Project v2 "Hydra Vortex"** (same repo as code).
Milestones = native GitHub **Milestones** (`Phase 0`, `M-Perf`, `M3`, `M4`, `M5`);
work items = **issues** with Status / Priority fields on the board. PRs link to issues
via `Closes #N`; built-in workflows auto-add items and set **Status → Done** on
merge/close — **no manual cross-linking**. Drive it with `gh project` / `gh issue`
(Bash) or the **GitHub MCP** (configured in `.mcp.json` / `opencode.json`). Board
layout + setup: `docs/GITHUB_PROJECT_SETUP.md`.


## Starting Point
Core M0–M2 are done. Start from the **GitHub Project board**, filtered to the active
Milestone (currently **M-Perf** — `docs/milestone-perf.md`), and follow the **Task
Lifecycle** above. Build/run/test commands are in `DevelopmentRunBook.md`.

## Key Design Decisions (do not relitigate)
- No Ray until possible M4+ (2 nodes, not needed)
- Store backed by tmpfs not S3/MinIO (sendfile + zero-copy)
- Full KV state only (delta export impossible — SSM truncation broken)
- Content-addressed chunking at Store level, not llama.cpp level (M2)
- No shared filesystem between nodes (Hydra Store RPC replaces NFS/virtiofs)
- llama.cpp fork minimal: only 3 endpoints in server.cpp, no core changes
- Hydra Head in Go: single binary per GPU node, 4-service management (llama + exporters + promtail)
- llama-server distributed via OCI registry (ghcr.io), pulled at startup — no shared mounts
- 2-layer YAML config for hydra-head: global.yaml + per-node overrides

## Hardware
- RTX 5060 Ti 16 GB sm_120, CUDA 13.2 — host machine, i7-12700K, 64 GB
- Tesla P100 16 GB sm_60, CUDA 12.9 — KVM VM at 192.168.122.21 (llama-server only)
- tmpfs 30 GB at /mnt/llm-ram (compose-managed inside Store container)
- Model: Qwopus3.6-35B-A3B-v1-APEX-MTP-I-Balanced.gguf (qwen35moe arch, MTP spec-decode, vision mmproj)

## Monitoring & Observability
Prometheus + Loki + Grafana + Promtail run as Quadlet systemd user services
(files in `infra/quadlets/`); Hydra services (Hydra.Core) also run via
podman compose. Grafana at :3000, Prometheus at :9091, Loki at :3100.

### Start everything
```bash
# Install Quadlet files and start all services
bash scripts/start-env.sh

# Or start individual stacks:
bash scripts/start-infra.sh           # infra observability only
bash scripts/start-hydra.sh           # hydra core + hydra-head on RTX
bash scripts/deploy-hydra-head.sh all # deploy hydra-head to both nodes
```

### Key dashboards/metrics endpoints
- Grafana: http://localhost:3000 (anonymous admin)
- Prometheus: http://localhost:9091
- Core metrics: http://localhost:9501/metrics
- Core API metrics: http://localhost:9000/metrics
- llama RTX metrics: http://localhost:8080/metrics
- Node exporter: http://localhost:9100/metrics
- GPU exporter: http://localhost:9835/metrics
- Hydra Head API: http://localhost:9700/status (RTX), http://192.168.122.21:9700/status (P100)

### Logs
Container logs shipped via containerized Promtail → Loki using Docker service
discovery (`docker_sd_configs`). Promtail discovers all containers from the
podman socket and reads k8s-file (CRI-format) logs directly from
`/mnt/containers/overlay-containers/<id>/userdata/ctr.log`.

View in Grafana Explore (Loki datasource) or the Logs panel in the Hydra dashboard.
Filter by `$trace_id` template variable to correlate logs across services.

**Log pipeline:** `k8s-file` → `ctr.log` (CRI) → `docker_sd_configs` →
`relabel_configs` (component/node/container/job) → `cri` parser → Loki.

**P100 logs:** Promtail reads journald for `hydra-head.service` unit, then regex-splits
llama-server lines from hydra-head lines via log pattern detection (see Log Separation above).

**Prerequisite:** Podman's log driver must be `k8s-file` (set in
`~/.config/containers/containers.conf`) — journald has no file-backed logs for
Promtail to scrape.

### Alerts
Prometheus alerting rules in `infra/prometheus/alerts.yml` — covers service down, high latency, GPU memory/temp, migration issues.

### Dashboard panels
1. Service Metrics: request rate, sessions, store ops, bytes, cache hit rate, migrations
2. KV Save/Restore Performance: save/restore p50/p95 duration
3. Host & GPU: utilization, memory, temperature, power, CPU, RAM
4. llama-server: tokens/s, requests processing, KV cache usage
5. Service Health: up/down table, llama health per node, worker slot status
6. Logs: all service logs with trace_id filter

## Coding Agent Rules

### 1. Ask for decisions via `question` tool
When there are multiple options, solutions, or design choices — always use the `question` tool with structured selections to get a clear decision from the user before proceeding.

**Example:**
```
question(questions=[{
  "header": "Storage backend",
  "question": "Which storage backend should we use for KV cache?",
  "options": [
    {"label": "Redis", "description": "In-memory, fast but volatile"},
    {"label": "tmpfs", "description": "Local RAM disk, simplest setup"},
    {"label": "S3", "description": "Persistent, slower but durable"}
  ]
}])
```

### 2. Track tasks with `todowrite` always
Always use `todowrite` to track work, even for seemingly simple tasks. Keeps progress visible and ensures nothing is skipped.

**Pattern:**
```
todowrite(todos=[
  {content: "Implement Store RPC server", status: "in_progress", priority: "high"},
  {content: "Add integration tests",      status: "pending",     priority: "medium"},
  {content: "Update docs",                status: "pending",     priority: "low"}
])
```
Update status as work progresses — exactly one `in_progress` at a time. Mark `completed` only after verification (test pass, lint clean, etc.).

### 3. Use sub-agents aggressively (2-3 in parallel)

Always launch parallel sub-agents via the `task` tool when work can be decomposed.
This is **not optional** for multi-file or multi-domain tasks.

**When to use:**
- Research / exploration — e.g., search codebase for patterns across services (Hydra.Core C#,
  llama-server C++) simultaneously
- Multi-file changes — e.g., one agent implements the Store change, another the Agent change,
  a third updates tests
- Decomposition — break a large feature into 2-3 parallel scouting agents, then implement
  based on their findings
- Anything that would take you >30s to do serially

**How to use:**
```
task(description="Explore Store codebase", prompt="Find all ...",
      subagent_type="explore")
task(description="Check Coordinator tests", prompt="Read all ...",
      subagent_type="explore")
```

- Use `explore` for quick codebase searches, `general` for complex multi-step work.
- Launch them in a single message (parallel tool calls).
- Each agent returns its findings in one message — consolidate and proceed.

**Don't use sub-agents for:** trivial single-file edits, reading a file you already know
the path of, running a single command.

### 4. End with a final result block
After completing work, output a clear summary block prefixed with `---` or a code-free section that highlights what was done, changed, or needs attention. Make the result stand out so the user can quickly understand the outcome.

**Example:**
```
---

**Summary:**
- Implemented `SlotService` in `src/core/Hydra.Store/Services/SlotService.cs`
- Added `GET /slots/{id}/state` endpoint to llama.cpp fork
- Fixed: n_tokens guard in Coordinator (must be > n_past)
- Pending: integration test for cross-GPU migration
```
