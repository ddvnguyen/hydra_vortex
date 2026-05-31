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
├── CLAUDE.md
├── .cursorrules
├── .gitignore
├── .gitmodules
├── README.md
├── PROJECT_PLAN.md
├── pyproject.toml
├── Hydra.sln
│
├── src/                         # ALL source code
│   │
│   ├── Hydra.Shared/            C# — protocol, models, RPC base
│   │   ├── Protocol.cs          wire format, header pack/unpack
│   │   ├── RpcServer.cs         base TCP RPC server
│   │   ├── RpcClient.cs         base TCP RPC client
│   │   ├── Models.cs            C# records (SlotInfo, SessionEntry, etc.)
│   │   ├── HydraLogging.cs      Serilog setup, trace scope
│   │   └── Constants.cs         op codes, status codes
│   │
│   ├── Hydra.Store/             C# — KV state store
│   │   ├── StorageEngine.cs     file I/O on tmpfs
│   │   ├── StoreServer.cs       RPC handlers, sendfile
│   │   ├── StoreConfig.cs       appsettings binding
│   │   └── Program.cs           entry point
│   │
│   ├── Hydra.Agent/             C# — GPU node sidecar
│   │   ├── LlamaClient.cs       httpx wrapper for llama-server
│   │   ├── StateHandler.cs      save/restore orchestration
│   │   ├── AgentServer.cs       RPC handlers
│   │   ├── AgentConfig.cs       appsettings binding
│   │   └── Program.cs           entry point
│   │
│   ├── Tests.Shared/            xUnit tests
│   ├── Tests.Store/             xUnit tests
│   ├── Tests.Agent/             xUnit tests
│   ├── Tests.Integration/       xUnit integration tests
│   │
│   ├── coordinator/             Python — Coordinator service
│   │   ├── __init__.py
│   │   ├── app.py
│   │   ├── router.py
│   │   ├── routing.py
│   │   ├── session_table.py
│   │   ├── state_manager.py
│   │   ├── health.py
│   │   └── proxy.py
│   │
│   ├── python-shared/           Python — shared lib (RPC client, models)
│   │   ├── __init__.py
│   │   ├── rpc_client.py        Python RPC client (protocol impl)
│   │   ├── models.py            Pydantic schemas
│   │   ├── logging.py           structlog setup
│   │   └── tail.py
│   │
│   ├── llama-cpp/               git submodule — hydra-state-streaming branch
│   │
│   └── tests/                   Python tests (coordinator + system)
│       ├── __init__.py
│       ├── coordinator/
│       ├── integration/
│       └── system/
│
├── specs/                       # protocol & service specs
├── infra/                       # deployment scripts
└── docs/                        # milestone docs
```

## Milestones
| MS | Name         | Scope                                          | Est.      |
|----|--------------|------------------------------------------------|-----------|
| M0 | MVP Test     | llama fork + Store + Agent + system verify     | 3-4 days  |
| M1 | Core System  | Coordinator + routing + session + migration    | 1-2 weeks |
| M2 | Advanced     | Chunked dedup + prefix checkpoints             | 1 week    |
| M3 | Production   | Persistence + Grafana + Langfuse + model dist  | 1-2 weeks |

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
