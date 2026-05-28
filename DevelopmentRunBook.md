# Hydra — Development Runbook

## Service Map

```
Client (HTTP) → Coordinator :9000     [Python/FastAPI]
                │ RPC                      │ RPC
                ▼                          ▼
              Agent RTX :9601           Agent P100 :9602    [C# .NET 10]
                │ HTTP local               │ HTTP local
                ▼                          ▼
           llama :8080                 llama :8086          [C++ fork]
                │ RPC                       │ RPC
                └────────────┬──────────────┘
                             ▼
                       Store :9500                           [C# .NET 10]
                       /mnt/llm-ram/store/ (tmpfs)
```

| Port | Service    | Lang     | Debug Config              |
|------|------------|----------|---------------------------|
| 9000 | Coordinator| Python   | `Coordinator (:9000)`     |
| 9500 | Store      | C#       | `Store (:9500)`           |
| 9501 | Store debug| HTTP     | —                         |
| 9601 | Agent RTX  | C#       | `Agent RTX (:9601)`       |
| 9611 | Agent debug| HTTP     | —                         |
| 9602 | Agent P100 | C#       | `Agent P100 (:9602)`      |
| 9622 | Agent debug| HTTP     | —                         |
| 8080 | llama RTX  | C++      | external                  |
| 8086 | llama P100 | C++      | external (in VM)          |

---

## Prerequisites

- .NET 10 SDK
- Python >= 3.13 with `pip install -e .[all]` (from project root)
- VS Code extensions: C# Dev Kit, Python, Pylance, EditorConfig
- tmpfs mount: `sudo bash infra/setup-ramdisk.sh`

---

## Quick Start (all services locally)

```bash
# Terminal 1 — Store
dotnet run --project src/Hydra.Store

# Terminal 2 — Agent (RTX node)
dotnet run --project src/Hydra.Agent

# Terminal 3 — Coordinator
hydra-coordinator

# Terminal 4 — llama-server (RTX, patched hydra-state-streaming branch)
./build-rtx/bin/llama-server \
  -m /path/to/model.gguf \
  --port 8080 \
  --rpc-port 9601 \
  --n-gpu-layers 99 \
  --ctx-size 81920
```

---

## VS Code Debug

1. Open `Hydra.sln` in VS Code
2. Run > Start Debugging (F5), select a configuration:

### Individual services
| Config                | Starts           |
|-----------------------|------------------|
| `Store (:9500)`       | Store RPC + debug HTTP |
| `Agent RTX (:9601)`   | Agent connected to local Store + llama |
| `Agent P100 (:9602)`  | Agent configured for P100 node |
| `Coordinator (:9000)` | FastAPI with hot-reload |

### Compound launch (all at once)
Select **All Services (Store + Agent RTX + Coordinator)** from the Run dropdown — starts all three with one click.

### Tests
| Config                    | Runs                     |
|---------------------------|--------------------------|
| `Tests (all .NET)`        | Full suite (160+ tests)  |
| `Tests (Shared only)`     | RPC protocol + client/server |
| `Tests (Store only)`      | Storage engine + Store RPC + ChunkEngine + ChunkStore |
| `Tests (Agent only)`      | LlamaClient + StateHandler + LocalChunkCache |
| `Tests (Integration)`     | Store ↔ Agent integration + chunked ops |

---

## Running Tests

```bash
# All .NET tests (160+)
dotnet test Hydra.sln --verbosity normal

# Individual projects
dotnet test src/Tests.Shared           # 29 tests
dotnet test src/Tests.Store            # 44 tests (23 M0 + 21 M2)
dotnet test src/Tests.Agent            # 23 tests (13 M0 + 10 M2)
dotnet test src/Tests.Integration      # 18 tests (6 M0 + 12 M2)

# M2-specific tests
dotnet test src/Tests.Store --filter "FullyQualifiedName~Chunk" -v m
dotnet test src/Tests.Agent --filter "FullyQualifiedName~ChunkCache" -v m
dotnet test src/Tests.Integration --filter "FullyQualifiedName~Chunked" -v m

# Python tests
pytest src/coordinator/tests -v        # 46 tests
pytest src/coordinator/tests/test_prefix_checkpoint.py -v  # 4 M2 tests
pytest tests/e2e -v -m e2e             # E2E (requires all services)
```

---

## Environment Variables

See [`.env.example`](.env.example) for all configurable values.

| Variable | Default | Service |
|----------|---------|---------|
| `HYDRA_COORD_HOST` | `0.0.0.0` | Coordinator |
| `HYDRA_COORD_PORT` | `9000` | Coordinator |
| `HYDRA_COORD_STORE_HOST` | `127.0.0.1` | Coordinator |
| `HYDRA_COORD_STORE_PORT` | `9500` | Coordinator |
| `HYDRA_COORD_LOG_LEVEL` | `INFO` | Coordinator |
| `HYDRA_COORD_NODES__0__*` | RTX config | Coordinator node list |
| `HYDRA_COORD_PREFIX_CHECKPOINT_NAME` | `system_prompt` | Coordinator prefix checkpoint name |
| `HYDRA_COORD_PREFIX_CHECKPOINT_ENABLED` | `true` | Coordinator prefix checkpoint on/off |
| `HYDRA_AGENT_CHUNK_CACHE_DIR` | `/tmp/hydra-chunk-cache` | Agent local chunk hash cache |

Store and Agent config is compiled-in. Use VS Code launch `env` blocks or modify
`StoreConfig.cs` / `AgentConfig.cs` defaults for different instances.

---

## Infrastructure

### tmpfs (required for Store)
```bash
sudo bash infra/setup-ramdisk.sh
# Mounts 30 GB tmpfs at /mnt/llm-ram
```

### llama-server patched build
```bash
# RTX (Blackwell sm_120, cuBLAS)
cmake -B build-rtx -G Ninja \
  -DCMAKE_CUDA_ARCHITECTURES=120 \
  -DGGML_CUDA=ON \
  -DGGML_CUDA_FORCE_CUBLAS=ON
cmake --build build-rtx --target llama-server -j4

# P100 (Pascal sm_60, build inside VM)
cmake -B build-p100 -G Ninja \
  -DCMAKE_CUDA_ARCHITECTURES=60 \
  -DGGML_CUDA=ON
cmake --build build-p100 --target llama-server -j4
```

---

## Architecture Notes

- **State streaming**: Agent pipes llama state directly to Store via RPC — no disk round-trip
- **sendfile**: Store GET uses `Socket.SendFileAsync` for zero-copy file transfers
- **RPC wire format**: 16-byte request header (magic `0x4859`), 12-byte response header, binary payload
- **Reconnection**: RPC client retries with 100ms / 500ms / 2s backoff (3 attempts)
- **Semaphore**: RPC client serializes all calls through `SemaphoreSlim(1,1)` — one request at a time per client
- **Trace IDs**: Every RPC call carries a `trace_id` propagated through Serilog JSON logs
- **M2 chunked dedup**: KV state split into 1 MB chunks, SHA-256 hashed, content-addressed store. Repeated saves only store delta.
- **ChunkHashTeeStream**: Agent computes SHA-256 on-the-fly while streaming state from llama → Store, no second pass.
- **LocalChunkCache**: Agent persists `session_id → [chunk_hashes]` as JSON on disk. LRU eviction prevents unbounded growth.
- **Agent shortcut**: If all chunk hashes known in local cache, llama PUT is skipped entirely (restore is no-op).

---

## Project Structure

```
src/
├── Hydra.Shared/          # M0.1 — RPC protocol, client, server base, logging
├── Hydra.Store/           # M0.2/M2 — File-backed KV store + ChunkEngine + ChunkStore
├── Hydra.Agent/           # M0.3/M2 — Sidecar + LocalChunkCache + chunked save/restore
├── coordinator/           # M1/M2  — FastAPI router, session table, prefix checkpoint
├── Tests.Shared/          # 29 tests — RPC round-trips, reconnect, streaming
├── Tests.Store/           # 44 tests — Storage engine + ChunkEngine + ChunkStore
├── Tests.Agent/           # 23 tests — LlamaClient + StateHandler + LocalChunkCache
├── Tests.Integration/     # 18 tests — Store ↔ Agent + chunked ops + prefix checkpoint
├── tests/                 # Python E2E tests
└── python_shared/         # Shared Python utilities
```

---

## Common Commands

```bash
# Watch + rebuild on file changes (requires dotnet-watch)
dotnet watch --project src/Hydra.Store

# Add a new test project
dotnet new xunit -n Tests.Xyz -o src/Tests.Xyz
dotnet sln add src/Tests.Xyz

# Check all services are healthy
curl -s :9501/debug | jq .
curl -s :9611/debug | jq .
curl -s :9000/health
```

---

## Troubleshooting

| Symptom | Likely Cause | Fix |
|---------|-------------|-----|
| Store PUT fails after 30s | Missing tmpfs | `sudo bash infra/setup-ramdisk.sh` |
| Agent can't connect to Store | Store not started | Start Store first, then Agent |
| `cache_n=0` after restore | `n_tokens <= n_past` | Ensure prompt has more tokens than cached state |
| RPC `InvalidDataException` | Wrong magic byte | Check client/server protocol version match |
| Pipe deadlock in tests | Pipe threshold (32 KB) | Use concurrent reader/writer pattern |
| Chunked save slow | First save = all chunks new (800 MB → ~800 chunks) | Normal. Second save of same session should be fast (delta only) |
| Agent chunk cache not persisting | `ChunkCacheDir` not writable | Check `HYDRA_AGENT_CHUNK_CACHE_DIR` (default: `/tmp/hydra-chunk-cache`) |
| `PUT_CHUNKED` returns error | Manifest already exists with different session_id | Session IDs must be unique. Delete manifest first or use different ID |
| GC removed in-use chunks | GC ran while session active | GC only removes chunks NOT referenced by any manifest. Active sessions have manifests. Run GC only during idle periods. |
