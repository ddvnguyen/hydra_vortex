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
  │  Hydra.Core :9000            │  C# / .NET 10
  │  Single binary: HTTP API     │  System.IO.Pipelines
  │  + Store RPC (:9500)         │  Socket.SendFileAsync
  │  + embedded Coordinator      │
  └──────┬─────────────┬─────────┘
         │ HTTP         │ HTTP
         ▼              ▼
  ┌──────────┐    ┌──────────┐
  │ llama    │    │ llama    │  C++ fork
  │ RTX      │    │ P100     │  hydra-state-streaming
  │ :8080    │    │ :8086    │
  │  │ RPC   │    │  │ RPC   │
  │  ▼       │    │  ▼       │
  └────┬─────┘    └────┬─────┘
       │ StateGet/Put  │
       └───────┬───────┘
               ▼
  ┌──────────────────────────────┐
  │  Store RPC :9500 + tmpfs     │
  │  KV state chunks             │
  │  Content-addressed (M2)      │
  │  /mnt/llm-ram/store/         │
  └──────────────────────────────┘
```

## Language Decisions (final)
| Component   | Language       | Reason                                            |
|-------------|----------------|---------------------------------------------------|
| Hydra.Core  | C# / .NET 10   | System.IO.Pipelines, Socket.SendFileAsync, team   |
| llama-server| C++ (fork)     | +3 streaming state endpoints, no other changes    |

## Architecture Notes
Hydra.Core is a single C# binary with an embedded coordinator. It contacts
llama-servers directly via HTTP (no intermediate Agent layer). KV state ops use
binary RPC (StateGet/StatePut) directly to llama-server's hydra RPC port (RTX :9503,
P100 :9502). Store RPC (Put/Get) is internal to Hydra.Core, backed by tmpfs.

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
| Concern          | Hydra.Core (C#)    |
|------------------|---------------------|
| Async runtime    | async/await + IOCP  |
| Binary protocol  | System.IO.Pipelines + BinaryPrimitives |
| HTTP server      | ASP.NET Core Kestrel|
| HTTP client      | HttpClient          |
| Logging          | Serilog (JSON)      |
| Config           | appsettings.json    |
| Testing          | xUnit + Moq         |
| Metrics          | prometheus-net      |
| Tracing          | OpenTelemetry       |
| Zero-copy I/O    | Socket.SendFileAsync|
| Deployment       | NativeAOT binary    |

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
│   ├── Hydra.Core/              C# — single binary: store + coordinator + session mgmt
│   │   ├── StorageEngine.cs     raw file I/O on tmpfs (PUT/GET/DEL/STAT/LIST)
│   │   ├── ChunkEngine.cs       1 MB chunk + SHA-256 hash pipeline
│   │   ├── ChunkStore.cs        content-addressed chunk storage + manifest management
│   │   ├── StoreServer.cs       RPC handlers (PUT_CHUNKED, GET_CHUNKED, GET_MANIFEST …)
│   │   ├── StoreMetrics.cs      Prometheus counters/histograms
│   │   ├── StoreConfig.cs       appsettings binding
│   │   ├── Coordinator.cs       request routing, session lifecycle, P/D split
│   │   ├── LlamaClient.cs       HTTP client for llama-server state endpoints
│   │   ├── CoreMetrics.cs       Prometheus counters/histograms (coordinator + store)
│   │   ├── CoreConfig.cs        appsettings binding
│   │   └── Program.cs
│   │
│   ├── Tests.Shared/            xUnit — Protocol, RpcClient, RpcServer
│   ├── Tests.Core/              xUnit — ChunkEngine, ChunkStore, Coordinator, LlamaClient
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
