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

| Port    | Service       | Lang     | Debug Config              |
|---------|---------------|----------|---------------------------|
| 9000    | Coordinator   | Python   | `Coordinator (:9000)`     |
| 9500    | Store         | C#       | `Store (:9500)`           |
| 9501    | Store debug   | HTTP     | —                         |
| 9601    | Agent RTX     | C#       | `Agent RTX (:9601)`       |
| 9611    | Agent debug   | HTTP     | —                         |
| 9602    | Agent P100    | C#       | `Agent P100 (:9602)`      |
| 9622    | Agent debug   | HTTP     | —                         |
| 8080    | llama RTX     | C++      | external (HTTP)           |
| **9501**| **llama RTX** | **C++**  | **external (RPC)**        |
| 8086    | llama P100    | C++      | external in VM (HTTP)     |
| **9502**| **llama P100**| **C++**  | **external in VM (RPC)**  |

---

## Prerequisites

- .NET 10 SDK
- Python >= 3.13 with `pip install -e .[all]` (from project root)
- VS Code extensions: C# Dev Kit, Python, Pylance, EditorConfig
- tmpfs mount: `sudo bash infra/setup-ramdisk.sh`
- Podman log driver set to `k8s-file` (create/edit `~/.config/containers/containers.conf`):
  ```ini
  [containers]
  log_driver = "k8s-file"
  ```
  **Required:** Promtail scrapes container log files directly via `docker_sd_configs`;
  the default `journald` driver has no file-backed logs to scrape. Existing containers
  must be recreated after changing this setting.

---

## Quick Start

### One command (recommended)

```bash
bash scripts/start-env.sh
```

Idempotent — checks what is already running and only starts what is missing. Handles:
- Hydra core services (Store + Agents + Coordinator, host networking) via `infra/docker-compose.hydra.yml`
- Infra/observability (Loki + Promtail + Prometheus + Grafana) via `infra/docker-compose.infra.yml`
- llama-server RTX via the `infra/llama-rtx-node/` container (mounts `build_sm120/` directly)
- llama-server P100 via SSH + user systemd on the VM (no sudo needed)

Requires pre-built llama binaries (see **Infrastructure** section). Use `--skip-p100` if the
P100 VM is unavailable.

```bash
bash scripts/start-env.sh --skip-p100   # RTX only
```

### Manual (if needed)

```bash
# Infra/observability (Loki, Promtail, Prometheus, Grafana)
cd infra && podman-compose -f docker-compose.infra.yml up -d

# Hydra core services (Store, Agents, Coordinator — host networking)
podman-compose -f docker-compose.hydra.yml up -d

# llama-server RTX (containerised, mounts build_sm120/ directly)
cd infra/llama-rtx-node && podman-compose up -d

# llama-server P100 (user systemd on VM, no sudo)
ssh hydra-p100 "systemctl --user start llama-p100"

# Verify
curl -s http://localhost:9000/health
```

> **Note:** The Agent connects to llama via **HTTP** (slots/state endpoints), not via
> the RPC port. The llama RPC port (`--rpc-port`) is used for direct state access
> (e.g., Python/python_shared RPC client).

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
# All .NET tests (160+) — projects run sequentially to avoid PG contention
dotnet test Hydra.sln -c Release --settings Hydra.runsettings -m:1 --verbosity normal

# (the -m:1 disables MSBuild-level parallel project execution, preventing PG contention)

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
pytest tests/system/test_system.py -v -m system                  # M0 System test (RPC save/restore)
pytest tests/system/test_full_workflow_system.py -v -m system    # M1+M2 full workflow via Coordinator HTTP
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

*(tmpfs for Store is managed automatically by compose — no host setup needed)*

### llama-server patched build

Build dirs are gitignored — binaries live only on the host filesystem and are picked up
by the RTX container (via volume mount) and rsynced to the P100 VM by CI/deploy scripts.

```bash
cd src/llama-cpp

# RTX (Blackwell sm_120, cuBLAS) — output: build_sm120/bin/llama-server
cmake -B build_sm120 -G Ninja \
  -DCMAKE_CUDA_ARCHITECTURES=120 \
  -DGGML_CUDA=ON \
  -DGGML_CUDA_FORCE_CUBLAS=ON \
  -DGGML_NATIVE=ON
cmake --build build_sm120 --target llama-server -j4

# P100 (Pascal sm_60) — output: build_sm60/bin/llama-server
# Build on this host then deploy via start-env.sh or setup-p100.sh
cmake -B build_sm60 -G Ninja \
  -DCMAKE_CUDA_ARCHITECTURES=60 \
  -DGGML_CUDA=ON \
  -DGGML_NATIVE=ON
cmake --build build_sm60 --target llama-server -j4
```

The RTX container (`infra/llama-rtx-node/docker-compose.yml`) mounts `build_sm120/` directly —
restarting the container picks up any newly built binary automatically.

### P100 VM — first-time setup

```bash
# One-time: install user systemd service on the VM (no sudo required)
bash scripts/setup-p100.sh
```

This deploys the binary, installs `~/.config/systemd/user/llama-p100.service` on the VM,
and enables it for auto-start.

After first-time setup, day-to-day use is just `bash scripts/start-env.sh`.

### CI/CD deploy

The `deploy-llama` workflow (`.github/workflows/ci.yml`) reuses the local build cache:
- **RTX**: `podman-compose up -d` in `infra/llama-rtx-node/` — container mounts `build_sm120/` via absolute volume path
- **P100**: `rsync build_sm60/bin/llama-server hydra-p100:...` then `systemctl --user restart llama-p100`

Triggered manually via `workflow_dispatch` (set `deploy-llama=true`) or by pushing a
`llama.cpp*` tag after rebuilding the binaries.

---

## Monitoring

### Container log shipping (Promtail in Docker)

Promtail runs in the Infra stack (`docker-compose.infra.yml`). It discovers containers
via the podman socket (`docker_sd_configs`) and reads CRI-format log files directly.

```bash
# Status (check the container)
podman ps --filter name=promtail

# Restart (after deploy or if logs stop appearing)
cd infra && podman-compose -f docker-compose.infra.yml restart promtail

# Configs
cat infra/promtail/promtail-config.yml                  # label mapping + targets
```

**Prerequisite:** Podman's log driver must be `k8s-file` (set in
`~/.config/containers/containers.conf`) — journald has no file-backed logs for
Promtail to scrape.

### Metrics endpoints

| Endpoint | What |
|---|---|
| `:9000/metrics` | Coordinator |
| `:9501/metrics` | Store |
| `:9611/metrics` | Agent RTX |
| `:9622/metrics` | Agent P100 |
| `:8080/metrics` | llama-server RTX |
| `:9100/metrics` | Node exporter (host) |
| `:9835/metrics` | GPU exporter |

See `CLAUDE.md` `## Monitoring & Observability` for dashboards, alerts, and panel details.

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
├── tests/                 # Python system tests
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
curl -s :9622/debug | jq .
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
| `dotnet test Hydra.sln` hangs | MSBuild-level parallel project execution → PG port/connection contention | Use `--settings Hydra.runsettings -m:1` (serializes assemblies + single MSBuild node) or run per-project |
| GC removed in-use chunks | GC ran while session active | GC only removes chunks NOT referenced by any manifest. Active sessions have manifests. Run GC only during idle periods. |
| Logs not appearing in Grafana | Promtail container not running | `cd infra && podman-compose -f docker-compose.infra.yml restart promtail` — see **Monitoring** |
| Promtail scrape errors in promtail logs | Docker SD config pointing at wrong socket | Check socket path in `infra/promtail/promtail-config.yml` and volume mount |
