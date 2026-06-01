# Hydra — Claude Handoff

## What Is This
Multi-GPU LLM inference system. Routes requests across RTX 5060 Ti and Tesla P100
(in KVM VM), migrates 800 MB KV cache state between GPUs without re-prefill.

## Read These First
1. `PROJECT_PLAN.md` — architecture, structure, tech stack (10 min)
2. `specs/rpc-protocol.md` — binary wire format (5 min)
3. `docs/milestone-0-mvp.md` — start here for implementation

## Architecture
```
Client (HTTP) → Coordinator :9000 [Python/FastAPI]
                    │ RPC           │ RPC
                    ▼               ▼
              Agent RTX :9601   Agent P100 :9602  [C#/.NET 10]
                │ HTTP local      │ HTTP local
                ▼                 ▼
           llama :8080        llama :8086          [C++ fork]
                │ RPC               │ RPC
                └────────┬──────────┘
                         ▼
                   Store :9500                     [C#/.NET 10]
                   /mnt/llm-ram/store/ (tmpfs)
```

## Language Decisions (FINAL — do not change)
| Service     | Language  | Reason                                      |
|-------------|-----------|---------------------------------------------|
| Store       | C# .NET 10 | System.IO.Pipelines, Socket.SendFileAsync   |
| Agent       | C# .NET 10 | Same RPC lib as Store, team expertise       |
| Coordinator | Python    | Langfuse, pydantic, best LLM tooling        |
| llama-server| C++ (fork)| +3 streaming state endpoints only           |

## Critical Facts (POC verified)
- P100 prefill: 110 tok/s → 80K context = 12 minutes. RTX handles large prefill.
- P100 decode: 28 tok/s — acceptable.
- Cross-GPU save/restore: WORKS. cache_n=2964 after restore.
- SSM truncation: BROKEN. --cache-prompt useless for qwen35moe.
- n_tokens MUST be > n_past or cache is nuked. Coordinator must guard this.
- KV state at 60-80K context: ~800 MB.

## llama.cpp Fork (hydra-state-streaming branch)
Three new endpoints added to tools/server/server.cpp:
- GET /slots/{id}/state      → stream binary KV state out
- PUT /slots/{id}/state      → stream binary KV state in
- GET /slots/{id}/state/meta → metadata (n_past, state_size)

These eliminate disk round-trips. Agent pipes stream directly llama↔Store.
Without these patches, nothing else in the system makes sense.
Build RTX: GGML_CUDA_FORCE_CUBLAS=ON, sm_120. Build P100: sm_60.

## Milestones
Core M0–M2 built. Roadmap **restructured 2026-06** around the Tier-1 performance
track (**M-Perf supersedes the old "M3 Production"**). Live roadmap in Plane
(`docs/PLANE_SETUP.md`); detail in `docs/milestone-*.md`.

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

## Planning (Plane)
Roadmap/planning lives in **Plane Cloud** (project "Hydra Vortex"; milestones =
Plane modules). Agents drive it via the **Plane MCP server** (configured in
`.mcp.json` for Claude Code and `opencode.json` for opencode). The GitHub bridge is
at the **agent layer**: use Plane for planning/status and GitHub (`gh`) for
code/PRs/CI issues — there is no native Plane↔GitHub sync. When a Plane work item
gets a PR, cross-link the URLs by hand. Setup + convention: `docs/PLANE_SETUP.md`.

## Starting Point
1. M0.0 first: fork llama.cpp, add 3 endpoints (~80 lines C++), verify with curl
2. Then M0.1: Hydra.Shared (C# RPC library) — everything else depends on it
3. M0.2 (Store) and M0.3 (Agent) can be built in parallel after M0.1

## Key Design Decisions (do not relitigate)
- No Ray until possible M4+ (2 nodes, not needed)
- Store backed by tmpfs not S3/MinIO (sendfile + zero-copy)
- Full KV state only (delta export impossible — SSM truncation broken)
- Content-addressed chunking at Store level, not llama.cpp level (M2)
- No shared filesystem between nodes (Hydra Store RPC replaces NFS/virtiofs)
- llama.cpp fork minimal: only 3 endpoints in server.cpp, no core changes

## Hardware
- RTX 5060 Ti 16 GB sm_120, CUDA 13.2 — host machine, i7-12700K, 64 GB
- Tesla P100 16 GB sm_60, CUDA 12.9 — KVM VM at 192.168.122.21
- tmpfs 30 GB at /mnt/llm-ram on host, shared to VM via virtiofs
- Model: Darwin-36B-Opus-APEX-I-Balanced.gguf (~25.5 GB, qwen35moe arch)

## Monitoring & Observability
Full prometheus + loki + promtail + grafana stack in infra/docker-compose.yml.
Grafana at :3000, Prometheus at :9091, Loki at :3100.

### Start everything
```bash
cd infra && docker compose up -d
```

### Key dashboards/metrics endpoints
- Grafana: http://localhost:3000 (anonymous admin)
- Prometheus: http://localhost:9091
- Store metrics: http://localhost:9501/metrics
- Agent RTX metrics: http://localhost:9611/metrics
- Agent P100 metrics: http://localhost:9622/metrics
- Coordinator metrics: http://localhost:9000/metrics
- llama RTX metrics: http://localhost:8080/metrics
- Node exporter: http://localhost:9100/metrics
- GPU exporter: http://localhost:9835/metrics

### Logs
All container logs shipped via Promtail → Loki.
View in Grafana Explore (Loki datasource) or the Logs panel in the Hydra dashboard.
Filter by `$trace_id` template variable to correlate logs across services.

### Alerts
Prometheus alerting rules in `infra/prometheus/alerts.yml` — covers service down, high latency, GPU memory/temp, migration issues.

### Dashboard panels
1. Service Metrics: request rate, sessions, store ops, bytes, cache hit rate, migrations
2. Agent Performance: save/restore p50/p95 duration
3. Host & GPU: utilization, memory, temperature, power, CPU, RAM
4. llama-server: tokens/s, requests processing, KV cache usage
5. Service Health: up/down table, llama health per node, agent slot status
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

### 3. End with a final result block
After completing work, output a clear summary block prefixed with `---` or a code-free section that highlights what was done, changed, or needs attention. Make the result stand out so the user can quickly understand the outcome.

**Example:**
```
---

**Summary:**
- Implemented `SlotService` in `src/Hydra.Store/Services/SlotService.cs`
- Added `GET /slots/{id}/state` endpoint to llama.cpp fork
- Fixed: n_tokens guard in Coordinator (must be > n_past)
- Pending: integration test for cross-GPU migration
```
