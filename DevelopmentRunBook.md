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
dotnet test Hydra.sln --settings Hydra.runsettings --verbosity normal

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

The fork lives in `src/llama-cpp` (submodule, branch `hydra-state-streaming`).
Build dirs are at `/mnt/WorkDisk/Workplace/hydra_vortex/src/llama-cpp/build_sm{60,120}/`.
Binaries are picked up by the RTX container (volume mount) and copied to the P100 VM.

**Prerequisites:** CUDA 12.9 + CUDA 13.2 at `/opt/software/cuda/`, GCC 14 at `/usr/bin/gcc-14`.

```bash
WORK=/mnt/WorkDisk/Workplace/hydra_vortex/src/llama-cpp
cd $WORK
```

#### RTX (Blackwell sm_120, CUDA 13.2)

```bash
export PATH=/opt/software/cuda/13.2/bin:/opt/software/gcc/14/bin:$PATH
export CC=gcc-14 CXX=g++-14
cmake -B build_sm120 \
  -DCMAKE_CUDA_ARCHITECTURES=120 \
  -DGGML_CUDA=ON \
  -DGGML_CUDA_FORCE_CUBLAS=ON \
  -DGGML_NATIVE=ON \
  -DCMAKE_CUDA_COMPILER=/opt/software/cuda/13.2/bin/nvcc
cmake --build build_sm120 --target llama-server -j$(nproc)
```

#### P100 (Pascal sm_60, CUDA 12.9)

```bash
export PATH=/opt/software/cuda/12.9/bin:/opt/software/gcc/14/bin:$PATH
export CC=gcc-14 CXX=g++-14
cmake -B build_sm60 \
  -DCMAKE_CUDA_ARCHITECTURES=60 \
  -DGGML_CUDA=ON \
  -DGGML_CUDA_FORCE_CUBLAS=ON \
  -DGGML_NATIVE=ON \
  -DCMAKE_CUDA_COMPILER=/opt/software/cuda/12.9/bin/nvcc
cmake --build build_sm60 --target llama-server -j$(nproc)
```

#### Deploy RTX

The RTX container (`infra/llama-rtx-node/docker-compose.yml`) mounts
`/mnt/WorkDisk/Workplace/hydra_vortex/src/llama-cpp/build_sm120/` at `/llama`.
Restarting the container picks up the new binary automatically:

```bash
cd infra/llama-rtx-node && podman-compose restart
```

#### Deploy P100

The P100 VM runs llama-server via a user systemd service. The binary lives at
`/opt/software/llama-cpp-hydra-sm60/hydra-sm60/bin/llama-server` on the VM.
Sudo is required to replace it (VM has passwordless sudo for user `vm1`).

```bash
# 1. Copy to VM (scp uses ~/.ssh/config alias hydra-p100 → vm1@192.168.122.21)
scp build_sm60/bin/llama-server hydra-p100:/tmp/llama-server-new

# 2. Deploy on the VM (must be run interactively — sudo needs a terminal)

ssh hydra-p100
sudo mv /tmp/llama-server-new /opt/software/llama-cpp-hydra-sm60/hydra-sm60/bin/llama-server
sudo chmod +x /opt/software/llama-cpp-hydra-sm60/hydra-sm60/bin/llama-server
systemctl --user restart llama-p100
# Wait ~30s for model to load, then verify:
curl http://192.168.122.21:8086/health
```

> **CI alternative:** The `deploy-llama` GitHub workflow handles P100 deploy via
> `rsync` + SSH (CI runner has direct terminal access).

#### Verify id_slot in response

After deploying both, confirm the fork change took effect:

```bash
# Non-streaming request should include "id_slot" in OAI response
curl -s -X POST http://localhost:8080/v1/chat/completions \
  -H "Content-Type: application/json" \
  -d '{"model":"qwen35moe","messages":[{"role":"user","content":"hi"}],"max_tokens":1,"stream":false}' \
  | python3 -c "import sys,json; print(json.load(sys.stdin).get('id_slot','MISSING'))"
# Expected: id_slot=0 or id_slot=1
```

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
| `dotnet test Hydra.sln` hangs | Parallel project execution → PG port/connection contention | Use `--settings Hydra.runsettings` (serializes assemblies) or run per-project (`src/Tests.Store`, `src/Tests.Agent`, etc.) |
| GC removed in-use chunks | GC ran while session active | GC only removes chunks NOT referenced by any manifest. Active sessions have manifests. Run GC only during idle periods. |
| Logs not appearing in Grafana | Promtail container not running | `cd infra && podman-compose -f docker-compose.infra.yml restart promtail` — see **Monitoring** |
| Promtail scrape errors in promtail logs | Docker SD config pointing at wrong socket | Check socket path in `infra/promtail/promtail-config.yml` and volume mount |

---

## Dev Environment — Operational Notes

### P100 VM Access

```bash
# SSH config alias (in ~/.ssh/config):
#   Host hydra-p100 192.168.122.21
#     HostName 192.168.122.21
#     User vm1
#     IdentityFile ~/.ssh/vm_agent_01
#     IdentitiesOnly yes

ssh hydra-p100 "systemctl --user status llama-p100"
```

### P100 llama-server — Force Restart

When the P100 llama-server is stuck in a CUDA kernel (stuck slot, 97% GPU util),
`systemctl --user stop` will hang (SIGTERM waits for CUDA). Force kill:

```bash
ssh hydra-p100 "sudo kill -9 \$(pgrep llama-server)"
sleep 2
ssh hydra-p100 "systemctl --user reset-failed llama-p100"
ssh hydra-p100 "systemctl --user start llama-p100"
```

Model reload takes ~30-40s on P100 (35B Qwopus MoE).

### Coordinator Rebuild & Deploy

The coordinator is a Python container. `podman-compose up -d --build` often uses
cached layers that don't pick up source changes. Force a clean rebuild:

```bash
cd /path/to/hydra_vortex
podman build --no-cache --target coordinator -t hydra-coordinator-fix -f infra/Dockerfile .
podman tag hydra-coordinator-fix localhost/hydra-core_coordinator:latest
podman-compose -f infra/docker-compose.hydra.yml up -d coordinator
```

Verify the fix took effect:
```bash
podman exec hydra-core_coordinator_1 grep <function_name> /app/src/coordinator/<file>.py
```

### C# Services Rebuild

```bash
cd infra
podman-compose -f docker-compose.hydra.yml build --no-cache store agent-rtx agent-p100
podman-compose -f docker-compose.hydra.yml up -d
```

### Worker Configuration

Workers are defined via `HYDRA_COORD_WORKERS` in `infra/docker-compose.hydra.yml`:

```json
[
  {"name":"rtx","host":"localhost","rpc_port":9601,"llama_url":"http://localhost:8080",
   "worker_type":3,"slots":2,"prefill_priority":1,"decode_priority":2,"decode_speed_tps":200},
  {"name":"p100","host":"localhost","rpc_port":9602,"llama_url":"http://192.168.122.21:8086",
   "worker_type":2,"slots":1,"prefill_priority":2,"decode_priority":1,"decode_speed_tps":28}
]
```

| Field | Meaning |
|-------|---------|
| `worker_type` | Bitwise: 1=PREFILL, 2=DECODE, 3=MIXED |
| `prefill_priority` | Lower = better prefill worker (RTX=1, P100=2) |
| `decode_priority` | Lower = better decode worker (P100=1, RTX=2) |
| `decode_speed_tps` | Estimated decode speed (used in concurrency decision) |
| `max_prefill_tokens` | Optional: cap prefill size for this worker (-1 = unlimited) |

P100 is set to `worker_type=2` (DECODE only) — it will never be selected for
prefill. The scheduler will wait for RTX to become free instead of falling back
to P100's slow prefill (28 tok/s vs RTX's 285 tok/s).

### Prefix Checkpoints (System Prompt Cache)

The coordinator caches the system prompt KV to Store and restores it for subsequent
requests. Enabled by default (`prefix_checkpoint_enabled=true`).

**How it works:**
1. Request arrives with `{"role":"system","content":"..."}` in messages
2. Coordinator computes `prefix_hash = sha256(system_content)[:16]`
3. Tries to restore `prefix/{hash}` from Store → if found, KV loaded in ~44ms
4. If not found, full prefill runs and prefix is saved to Store afterward
5. Next request with same system prompt restores instantly

**Verify:**
```bash
# Check prefix saves
podman logs hydra-core_coordinator_1 | grep prefix_checkpoint_saved

# Check prefix restores
podman logs hydra-core_coordinator_1 | grep prefix_checkpoint_restored

# Check Store for prefix data
curl -s http://localhost:9501/metrics | grep put_manifest
```

**Prefix size examples:**
- OpenCode CLAUDE.md + workflow docs: 424 MB (~11K tokens)
- Simple system prompt: 72 MB (~500 tokens)

### Live Testing Workflow

```bash
# Small atomic request (RTX only)
curl -s -X POST http://localhost:9000/v1/chat/completions \
  -H "Content-Type: application/json" \
  -d '{"model":"qwen35moe","messages":[{"role":"user","content":"hi"}],"max_tokens":50,"stream":false}'

# Large concurrency request (RTX prefill → P100 decode)
curl -s -X POST http://localhost:9000/v1/chat/completions \
  -H "Content-Type: application/json" \
  -d '{"model":"qwen35moe","messages":[{"role":"user","content":"Write a long essay..."}],"max_tokens":32000,"stream":false}'

# With system prompt (triggers prefix checkpoint)
curl -s -X POST http://localhost:9000/v1/chat/completions \
  -H "Content-Type: application/json" \
  -d '{"model":"qwen35moe","messages":[{"role":"system","content":"You are a helpful assistant."},{"role":"user","content":"Explain GPUs."}],"max_tokens":500,"stream":false}'

# Multi-turn (same session_id)
curl -s -X POST http://localhost:9000/v1/chat/completions \
  -H "Content-Type: application/json" \
  -d '{"model":"qwen35moe","messages":[{"role":"user","content":"Explain GPUs."}],"max_tokens":100,"stream":false,"session_id":"multi-1"}'
```

### Monitoring Live Requests

```bash
# Watch coordinator status
curl -s http://localhost:9000/status | python3 -m json.tool

# Watch coordinator logs (non-health)
podman logs -f hydra-core_coordinator_1 | grep -v "health_ok\|GET /health\|GET /metrics"

# Watch for specific events
podman logs -f hydra-core_coordinator_1 | grep -E "concurrency|store_restore|prefix|state_saved"

# Check Store state
curl -s http://localhost:9501/debug | python3 -m json.tool

# Check GPU
nvidia-smi --query-gpu=index,utilization.gpu,memory.used,temperature.gpu --format=csv,noheader

# Check RTX slots
curl -s http://localhost:8080/slots | python3 -c "
import sys,json
slots=json.load(sys.stdin)
for s in slots if isinstance(slots,list) else slots.get('slots',[]):
 nt=s.get('next_token',[{}])[0] if s.get('next_token') else {}
 print(f\"  slot {s['id']}: proc={s['is_processing']} n_past={s['n_past']} n_remain={nt.get('n_remain','?')}\")
"
```

### Request Flow (by route)

| Route | Condition | Flow |
|-------|-----------|------|
| Affinity | Session slot still warm + n_past guard passes | Skip prefill → decode on same GPU |
| Store Restore | Session has `has_store_state=True` | Restore KV from Store → decode on best decode worker |
| Cold Atomic | `estimated_new_tokens ≤ atomic_threshold` | Prefill + decode on same GPU → save KV to Store |
| Cold Concurrency | `estimated_new_tokens > atomic_threshold` | Prefill on RTX → save KV → restore on P100 → decode |

### Stuck Slot Recovery

A stuck slot means the llama-server slot is `is_processing=true` with `n_remain=0`
but was never freed. The health monitor detects this but doesn't auto-recover (#171).

**Manual recovery:**
```bash
# Option 1: Restart the stuck llama-server
ssh hydra-p100 "sudo kill -9 \$(pgrep llama-server); systemctl --user start llama-p100"

# Option 2: Restart the coordinator (clears tracker state)
podman-compose -f infra/docker-compose.hydra.yml restart coordinator
```
