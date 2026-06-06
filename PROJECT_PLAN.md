# Hydra — Project Plan

## Vision
High-throughput multi-GPU LLM inference system that manages KV cache state
across heterogeneous GPU nodes, enabling session migration without re-prefill.

## Architecture
```
		Clients (Cline, OpenWebUI, curl)
		OpenAI-compatible HTTP
					│ 
					▼
  ┌──────────────────────────────┐
  │  Coordinator :9000           │  Python / FastAPI
  │  Routes requests             │  Best LLM tooling ecosystem
  │  Manages sessions            │  (Langfuse, pydantic, structlog)
  └──────┬─────────────┬─────────┘
         │ RPC         │ RPC
         ▼             ▼
  ┌──────────┐    ┌──────────┐
  │ Agent    │    │ Agent    │  C# / .NET 10
  │ RTX      │    │ P100     │  System.IO.Pipelines
  │ :9601    │    │ :9602    │  Socket.SendFileAsync
  │  │ HTTP  │    │  │ HTTP  │  (local only)
  │  ▼       │    │  ▼       │
  │ llama    │    │ llama    │  Unmodified llama.cpp
  │ :8080    │    │ :8086    │  + hydra-state-streaming branch
  └────┬─────┘    └────┬─────┘
       │ RPC           │ RPC
       ▼               ▼
  ┌──────────────────────────────┐
  │  Store :9500                 │  C# / .NET 10
  │  KV state chunks             │  tmpfs-backed
  │  Content-addressed (M2)      │  sendfile() zero-copy
  └──────────────────────────────┘
```

## Language Decisions (final)
| Component   | Language       | Reason                                            |
|-------------|----------------|---------------------------------------------------|
| Store       | C# / .NET 10   | System.IO.Pipelines, Socket.SendFileAsync, team   |
| Agent       | C# / .NET 10   | Same RPC lib, Socket streaming, team expertise    |
| Coordinator | Python/FastAPI | Best LLM ecosystem (Langfuse, pydantic, structlog)|
| llama-server| C++ (fork)     | +3 streaming state endpoints, no other changes    |

## Rule: One Protocol
All inter-service traffic uses Hydra binary RPC.
HTTP only at two edges: Client→Coordinator and Agent→local llama-server.

## Worker Node Model
Each GPU node is configured as a `WorkerNodeConfig`:

| Field | Default | Meaning |
|---|---|---|
| `worker_type` | `3` | Bitwise: `1`=prefill-only, `2`=decode-only, `3`=mixed |
| `prefill_priority` | `1` | Lower = preferred for prefill (1 is best) |
| `decode_priority` | `1` | Lower = preferred for decode |
| `decode_speed_tps` | `30.0` | Estimated decode tok/s (scheduling hint) |

**Run modes** (`HYDRA_COORD_RUN_MODE`):
- `fast` (default) — session affinity; one GPU handles both prefill and decode per session
- `concurrency` — P/D disaggregation: prefill on RTX, KV saved to Store, decode on P100

See `docs/architecture.md` for the 4-tier routing algorithm and session lifecycle detail.

## Tech Stack Detail
| Concern          | C# Services      | Python Coordinator |
|------------------|------------------|--------------------|
| Async runtime    | async/await + IOCP| asyncio            |
| Binary protocol  | System.IO.Pipelines + BinaryPrimitives | struct module |
| HTTP client      | HttpClient       | httpx              |
| Logging          | Serilog (JSON)   | structlog (JSON)   |
| Config           | appsettings.json | pydantic-settings  |
| Testing          | xUnit + Moq      | pytest-asyncio     |
| Metrics (M3)     | prometheus-net   | prometheus-client  |
| Tracing (M3)     | OpenTelemetry    | Langfuse SDK       |
| Zero-copy I/O    | Socket.SendFileAsync| asyncio.sendfile |
| Deployment       | NativeAOT binary | uvicorn            |

## Project Structure
All source code lives under `src/`.
```
├── CLAUDE.md                    # agent instructions (single source of truth)
├── docs/architecture.md         # architecture reference (this doc's detail layer)
├── docs/diagrams.md             # Mermaid diagrams for all major flows
├── specs/rpc-protocol.md        # binary wire format + opcode reference
├── pyproject.toml
├── src/Hydra.sln
│
├── src/
│   ├── Hydra.Shared/            C# — protocol, RPC base, shared types
│   │   ├── Protocol.cs          wire format, header pack/unpack, OpCode/StatusCode enums
│   │   ├── RpcServer.cs         base TCP RPC server (System.IO.Pipelines)
│   │   ├── RpcClient.cs         TCP RPC client (reconnect, stream body)
│   │   ├── ChunkModels.cs       ChunkRef record (index, hash, size)
│   │   ├── AsyncEnumerableStream.cs  IAsyncEnumerable<byte[]> → Stream adapter
│   │   └── HydraLogging.cs      Serilog setup, trace scope helpers
│   │
│   ├── Hydra.Store/             C# — KV state store (tmpfs-backed)
│   │   ├── StorageEngine.cs     raw file I/O on tmpfs (PUT/GET/DEL/STAT/LIST)
│   │   ├── ChunkEngine.cs       1 MB chunk + SHA-256 hash pipeline
│   │   ├── ChunkStore.cs        content-addressed chunk storage + manifest management
│   │   ├── StoreServer.cs       RPC handlers (PUT_CHUNKED, GET_CHUNKED, GET_MANIFEST …)
│   │   ├── StoreMetrics.cs      Prometheus counters/histograms
│   │   ├── StoreConfig.cs       appsettings binding
│   │   └── Program.cs
│   │
│   ├── Hydra.Agent/             C# — GPU node sidecar
│   │   ├── LlamaClient.cs       HTTP client for llama-server state endpoints
│   │   ├── StateHandler.cs      save/restore orchestration (raw + chunked)
│   │   │                          incl. ChunkHashTeeStream, ValueStopwatch
│   │   ├── LocalChunkCache.cs   agent-side cache of chunk data (partial-restore support)
│   │   ├── AgentServer.cs       RPC handlers (SAVE/RESTORE_STATE_CHUNKED, NODE_HEALTH …)
│   │   ├── AgentMetrics.cs      Prometheus counters/histograms
│   │   ├── AgentConfig.cs       appsettings binding
│   │   └── Program.cs
│   │
│   ├── Tests.Shared/            xUnit — Protocol, RpcClient, RpcServer
│   ├── Tests.Store/             xUnit — ChunkEngine, ChunkStore, StorageEngine, StoreServer
│   ├── Tests.Agent/             xUnit — StateHandler, LlamaClient, LocalChunkCache, AgentServer
│   ├── Tests.Integration/       xUnit integration — Agent↔Store, chunked dedup spike
│   │
│   ├── coordinator/             Python — Coordinator service (FastAPI)
│   │   ├── app.py               FastAPI app factory
│   │   ├── router.py            /v1/chat/completions, /sessions, /prefix, /migrate
│   │   ├── routing.py           4-tier routing algorithm + load metric
│   │   ├── session_table.py     SessionEntry, SessionTable (in-memory)
│   │   ├── state_manager.py     save/restore/migrate/evict/prefix-checkpoint
│   │   ├── health.py            HealthMonitor (polls NODE_HEALTH every 20 s)
│   │   ├── proxy.py             HTTP proxy to llama-server (streaming + non-streaming)
│   │   ├── config.py            CoordinatorConfig + WorkerNodeConfig (pydantic-settings)
│   │   ├── metrics.py           Prometheus metrics (requests, sessions, migrations)
│   │   └── version.py           reads VERSION file
│   │
│   ├── coordinator/tests/       pytest — router, routing, session_table, state_manager, …
│   │
    │   ├── lib/                     Python — shared lib (inside coordinator)
    │   │   ├── rpc_client.py        Python RPC client (async, full protocol impl)
    │   │   └── log_config.py        structlog JSON setup, trace_id generator
│   │
│   ├── llama-cpp/               git submodule — hydra-state-streaming branch
│   │
│   └── tests/                   Python system/E2E tests
│
├── infra/                       docker-compose monitoring stack (Prometheus, Loki, Grafana)
├── specs/                       protocol & service specs
└── docs/                        milestone docs + architecture + diagrams
```

## Milestones
Core M0–M2 is built. The roadmap was **restructured 2026-06** around the Tier-1
heterogeneous-performance track: **M-Perf supersedes the old monolithic "M3
Production"**, and the old M3 scope was re-homed into M3/M4/M5 below. Live roadmap
is in **GitHub Projects "Hydra Vortex"** (see `docs/GITHUB_PROJECT_SETUP.md`);
per-milestone detail in `docs/milestone-*.md`.

| MS      | Name                           | Scope                                                       | Status   |
|---------|--------------------------------|-------------------------------------------------------------|----------|
| M0      | MVP Test                       | llama fork + Store + Agent + system verify                  | ✅ done   |
| M1      | Core System                    | Coordinator + routing + session + migration                 | ✅ done   |
| M2      | Advanced                       | Chunked dedup + prefix checkpoints                          | ✅ done   |
| Phase 0 | Stabilize                      | Green CI/CD, restore obs, rebase local onto remote          | ▶ now    |
| M-Perf  | Heterogeneous Performance      | spec-decode → P/D streaming → pipeline (Tier-1, ~6–8 wk)    | ▶ next   |
| M3      | Persistence & Real Obs         | NVMe write-behind persistence (**C# re-spec**) + obs harden | planned  |
| M4      | Model Management & Multi-Modal  | model distribution, dynamic load, vision/embed/audio        | planned  |
| M5      | LLM Obs & Agentic              | Langfuse tracing, A/B testing, agentic system               | planned  |

## Verified Facts
| Fact                         | Value        |
|------------------------------|--------------|
| P100 prefill                 | 110 tok/s    |
| P100 at 80K context          | ~12 min      |
| P100 decode                  | 28 tok/s     |
| Cross-GPU restore            | ✅ confirmed  |
| cache_n after restore        | 2964 / 2968  |
| KV state at 60-80K           | ~800 MB      |
| --cache-prompt on qwen35moe  | BROKEN ❌    |
| n_tokens must be > n_past    | CRITICAL ⚠️  |
