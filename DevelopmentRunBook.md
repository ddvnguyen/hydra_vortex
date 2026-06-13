# Hydra — Development Runbook

## Service Map

```
Client (HTTP) → Hydra.Core :9000             [C#/.NET 10 — HTTP API + Store + Router]
                 │ HTTP completions                │ HTTP completions
                 │ RPC StateGet/Put (:9503)        │ RPC StateGet/Put (:9502)
                 ▼                                 ▼
            Hydra Head RTX                    Hydra Head P100            [Go]
            │  llama :8080                    │  llama :8086            [C++ fork]
            │  hydra RPC :9503                │  hydra RPC :9502
            │  node_exporter :9100            │  node_exporter :9100
            │  nvidia_exporter :9835          │  nvidia_exporter :9835
            │  promtail :9080                 │  promtail :9081
            ▼                                 ▼

            /mnt/llm-ram/store/ (tmpfs, managed by Hydra.Core)
```

| Port    | Service       | Lang     | Node  | Purpose                     |
|---------|---------------|----------|-------|-----------------------------|
| 9000    | Hydra.Core    | C#       | host  | HTTP API (OpenAI-compat)    |
| 9500    | Hydra.Core    | C#       | host  | Store RPC (internal)        |
| 9501    | Hydra.Core    | C#       | host  | Metrics endpoint            |
| 9700    | Hydra Head    | Go       | RTX   | Head API (/status, /health) |
| 8080    | llama RTX     | C++      | RTX   | HTTP completions            |
| 9503    | llama RTX     | C++      | RTX   | hydra RPC (StateGet/Put)    |
| 9100    | node_exporter | Go       | RTX   | Host metrics                |
| 9835    | nvidia_exporter | Go    | RTX   | GPU metrics                 |
| 9700    | Hydra Head    | Go       | P100  | Head API (/status, /health) |
| 8086    | llama P100    | C++      | P100  | HTTP completions (VM)       |
| 9502    | llama P100    | C++      | P100  | hydra RPC (StateGet/Put)    |
| 9100    | node_exporter | Go       | P100  | Host metrics                |
| 9835    | nvidia_exporter | Go    | P100  | GPU metrics                 |

---

## Prerequisites

- .NET 10 SDK
- Python >= 3.13 with `pip install -e .[all]` (from project root, for system tests)
- VS Code extensions: C# Dev Kit, EditorConfig
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
- Hydra.Core (single C# binary) via `infra/docker-compose.hydra.yml`
- Infra/observability (Loki + Promtail + Prometheus + Grafana) via `infra/docker-compose.infra.yml`
- Hydra Head RTX via container (`infra/hydra-head/Dockerfile.rtx`)
- Hydra Head P100 via SSH + user systemd on the VM

Requires pre-built hydra-head binary and llama-server OCI images (see **Hydra Head** section).
Use `--skip-p100` if the P100 VM is unavailable.

```bash
bash scripts/start-env.sh --skip-p100   # RTX only
```

### Manual (if needed)

```bash
# Infra/observability (Loki, Promtail, Prometheus, Grafana)
cd infra && podman-compose -f docker-compose.infra.yml up -d

# Hydra.Core (single C# binary with host networking)
podman-compose -f docker-compose.hydra.yml up -d

# Hydra Head — RTX (container)
bash scripts/deploy-hydra-head.sh rtx

# Hydra Head — P100 (VM systemd)
bash scripts/deploy-hydra-head.sh p100

# Both nodes
bash scripts/deploy-hydra-head.sh all

# Verify
curl -s http://localhost:9000/health
curl -s http://localhost:9700/status       # RTX Hydra Head
curl -s http://192.168.122.21:9700/status  # P100 Hydra Head
```

> **Note:** Hydra Head manages llama-server + node_exporter + nvidia_exporter + promtail
> on each node. The old `infra/llama-rtx-node/` container and `llama-p100` systemd
> service are [DEPRECATED]. Hydra.Core contacts llama-server directly via HTTP.

---

## VS Code Debug

1. Open `src/Hydra.sln` in VS Code
2. Run > Start Debugging (F5), select a configuration:

### Individual services
| Config                | Starts           |
|-----------------------|------------------|
| `Hydra.Core (:9000)`  | Hydra.Core HTTP API + Store RPC |

### Compound launch (all at once)
Select **All Services (Hydra.Core)** from the Run dropdown — starts the single binary.

### Tests
| Config                    | Runs                     |
|---------------------------|--------------------------|
| `Tests (all .NET)`        | Full suite               |
| `Tests (Shared only)`     | RPC protocol + client/server |
| `Tests (Core only)`       | Storage engine + Store RPC + ChunkEngine + ChunkStore |

---

## Running Tests

```bash
# All .NET tests — projects run sequentially to avoid PG contention
dotnet test src/Hydra.sln --settings src/Hydra.runsettings --verbosity normal

# Individual projects
dotnet test src/core/Tests.Shared           # 29 tests
dotnet test src/core/Tests.Core             # Core tests (Store + Chunk + Routing)

# M2-specific tests
dotnet test src/core/Tests.Core --filter "FullyQualifiedName~Chunk" -v m
dotnet test src/core/Tests.Core --filter "FullyQualifiedName~ChunkCache" -v m

# System tests
pytest tests/system/test_system.py -v -m system                  # M0 System test (RPC save/restore)
pytest tests/system/test_full_workflow_system.py -v -m system    # M1+M2 full workflow via Hydra.Core HTTP
```

---

## Environment Variables

See [`.env.example`](.env.example) for all configurable values.

| Variable | Default | Service |
|----------|---------|---------|
| `HYDRA_CORE_HOST` | `0.0.0.0` | Hydra.Core |
| `HYDRA_CORE_PORT` | `9000` | Hydra.Core |
| `HYDRA_STORE_PORT` | `9500` | Hydra.Core |
| `HYDRA_STORE_DIR` | `/mnt/llm-ram/store` | Hydra.Core |
| `HYDRA_METRICS_PORT` | `9501` | Hydra.Core |
| `HYDRA_CORE_LOG_LEVEL` | `INFO` | Hydra.Core |
| `HYDRA_CORE_WORKERS` | (JSON) | Hydra.Core worker config |
| `HYDRA_CHUNK_CACHE_DIR` | `/tmp/hydra-chunk-cache` | Hydra.Core local chunk hash cache |

Config is compiled-in with defaults. Use environment variables or
`appsettings.json` to override.

---

## Infrastructure

*(tmpfs for Store is managed automatically by compose — no host setup needed)*

### Hydra Head (Go node agent)

Build and deploy Hydra Head, which manages llama-server + 3 sub-services on each GPU node.

```bash
# Build
go build -C src/head -o ../../bin/hydra-head .

# Run locally (RTX config, requires model files)
bin/hydra-head -global infra/hydra-head/config/global.yaml \
               -node infra/hydra-head/config/node-rtx.yaml \
               -api-port 9700

# Test
go test -C src/head ./internal/...

# Deploy to both nodes
bash scripts/deploy-hydra-head.sh all
```

**APIs:** `GET /status`, `GET /health`, `POST /restart?name=<service>`, `POST /update`

### llama-server from source (build only)

The fork lives in `src/llama-cpp` (submodule, branch `hydra-state-streaming`).
Build dirs are at `/mnt/WorkDisk/Workplace/hydra_vortex/src/llama-cpp/build_sm{60,120}/`.
These builds produce the binaries that get pushed to OCI registry (ghcr.io).

> **Note:** llama-server is deployed via OCI registry, not manual copy. Use the build
> commands below only when you need to update the OCI images. See "Deploy via Hydra Head"
> above for the actual deployment workflow.

**Prerequisites:** CUDA 12.9 + CUDA 13.2 at `/opt/software/cuda/`, GCC 14 at `/usr/bin/gcc-14`.

```bash
WORK=/mnt/WorkDisk/Workplace/hydra_vortex/src/llama-cpp
cd $WORK
```

#### RTX (Blackwell sm_120, CUDA 13.2)

Built at `/opt/software/cuda/13.2` with GCC 14 via `CMAKE_CUDA_HOST_COMPILER`.

```bash
CUDA_PATH=/opt/software/cuda/13.2
cmake -B build_sm120 \
  -DCMAKE_CUDA_ARCHITECTURES="120" \
  -DCPACK_PACKAGE_NAME="ik-llama-sm120-cuda13.2" \
  -DGGML_CUDA=ON \
  -DGGML_CUDA_FORCE_CUBLAS=ON \
  -DGGML_RPC=ON \
  -DGGML_NATIVE=ON \
  -DCMAKE_BUILD_TYPE=Release \
  -DBUILD_SHARED_LIBS=OFF \
  -DCMAKE_INSTALL_PREFIX=/usr/local \
  -DCMAKE_INSTALL_RPATH="$CUDA_PATH/lib64" \
  -DCMAKE_BUILD_WITH_INSTALL_RPATH=ON \
  -DCMAKE_INTERPROCEDURAL_OPTIMIZATION=ON \
  -DLLAMA_BUILD_EXAMPLES=OFF \
  -DLLAMA_BUILD_TESTS=OFF
cmake --build build_sm120 --target llama-server -j$(nproc)
```

#### P100 (Pascal sm_60, CUDA 12.9)

Pascal requires **GCC ≤ 14** as the CUDA host compiler (CUDA 12.9 caps at GCC 14;
GCC 15+ fails with `error: unrecognized command-line option '-###'`).

```bash
CUDA_PATH=/opt/software/cuda/12.9
cmake -B build_sm60 \
  -DCMAKE_CUDA_ARCHITECTURES="60" \
  -DCMAKE_CUDA_HOST_COMPILER="/usr/bin/g++-14" \
  -DGGML_RPC=ON \
  -DGGML_CUDA=ON \
  -DGGML_CUDA_FORCE_CUBLAS=ON \
  -DGGML_CUDA_FORCE_MMQ=OFF \
  -DGGML_CUDA_FA_ALL_QUANTS=OFF \
  -DGGML_NATIVE=ON \
  -DCPACK_INCLUDE_COMMANDS=ON \
  -DCMAKE_INSTALL_RPATH="$CUDA_PATH/lib64" \
  -DCMAKE_BUILD_TYPE=Release \
  -DLLAMA_BUILD_TESTS=OFF \
  -DBUILD_SHARED_LIBS=OFF \
  -DLLAMA_BUILD_EXAMPLES=OFF \
  -DCMAKE_INTERPROCEDURAL_OPTIMIZATION=ON
cmake --build build_sm60 --target llama-server -j$(nproc)
```

#### Deploy via Hydra Head

llama-server is now deployed via Hydra Head, not manual container/systemd management.
Hydra Head pulls the llama-server binary from ghcr.io at startup (see OCI Registry in CLAUDE.md).

```bash
# Deploy Hydra Head to RTX (container with OCI pull)
bash scripts/deploy-hydra-head.sh rtx

# Deploy Hydra Head to P100 (systemd + config push)
bash scripts/deploy-hydra-head.sh p100

# Both nodes
bash scripts/deploy-hydra-head.sh all
```

The deploy script handles building the hydra-head binary, building/pushing the container image
(RTX), copying configs and systemd unit (P100), and restarting services.

To push updated llama-server binaries to OCI registry:
```bash
# Build sm60/sm120, then push to ghcr.io
podman tag localhost/llama-server-sm60 ghcr.io/ddvnguyen/llama-server-sm60:$(git rev-parse --short HEAD)
podman tag localhost/llama-server-sm120 ghcr.io/ddvnguyen/llama-server-sm120:$(git rev-parse --short HEAD)
podman push ghcr.io/ddvnguyen/llama-server-sm60:$(git rev-parse --short HEAD)
podman push ghcr.io/ddvnguyen/llama-server-sm120:$(git rev-parse --short HEAD)
# Then update the tag in infra/hydra-head/config/node-{rtx,p100}.yaml
```

To pull the latest binary without restarting Hydra Head:
```bash
curl -X POST http://localhost:9700/update      \
  -H "Content-Type: application/json"           \
  -d '{"name":"llama-server","source":"ghcr.io/ddvnguyen/llama-server-sm120:NEWSHA"}'
curl -X POST http://localhost:9700/restart?name=llama
```

> **Old approach (deprecated):** `infra/llama-rtx-node/docker-compose.yml` (RTX container),
> `systemctl --user restart llama-p100` (P100 systemd). Replaced by hydra-head.

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
| `:9000/metrics` | Hydra.Core HTTP API |
| `:9501/metrics` | Hydra.Core Store RPC |
| `:8080/metrics` | llama-server RTX |
| `:9100/metrics` | Node exporter (host) |
| `:9835/metrics` | GPU exporter |

See `CLAUDE.md` `## Monitoring & Observability` for dashboards, alerts, and panel details.

---

## Architecture Notes

- **Single binary**: Hydra.Core handles HTTP API, Store RPC, routing, and llama-server communication
- **State streaming**: Hydra.Core pipes llama state directly via RPC — no disk round-trip
- **sendfile**: Store GET uses `Socket.SendFileAsync` for zero-copy file transfers
- **RPC wire format**: 16-byte request header (magic `0x4859`), 12-byte response header, binary payload
- **Reconnection**: RPC client retries with 100ms / 500ms / 2s backoff (3 attempts)
- **Semaphore**: RPC client serializes all calls through `SemaphoreSlim(1,1)` — one request at a time per client
- **Trace IDs**: Every RPC call carries a `trace_id` propagated through Serilog JSON logs
- **M2 chunked dedup**: KV state split into 1 MB chunks, SHA-256 hashed, content-addressed store. Repeated saves only store delta.
- **ChunkHashTeeStream**: Hydra.Core computes SHA-256 on-the-fly while streaming state from llama → Store, no second pass.
- **LocalChunkCache**: Hydra.Core persists `session_id → [chunk_hashes]` as JSON on disk. LRU eviction prevents unbounded growth.
- **llama shortcut**: If all chunk hashes known in local cache, llama PUT is skipped entirely (restore is no-op).

---

## Project Structure

```
src/
├── Hydra.Shared/          # M0.1 — RPC protocol, client, server base, logging
├── Hydra.Core/            # M0.2/M1/M2 — HTTP API + Store + ChunkEngine + ChunkStore + Router
├── core/                  # C# .NET — Hydra.Shared, Core
│   ├── Tests.Shared/      # RPC round-trips, reconnect, streaming
│   └── Tests.Core/        # Storage engine + ChunkEngine + ChunkStore + Router
├── llama-cpp/             # C++ fork (submodule)
└── tests/                 # Python system tests
```

---

## Common Commands

```bash
# Watch + rebuild on file changes (requires dotnet-watch)
dotnet watch --project src/core/Hydra.Core

# Add a new test project
dotnet new xunit -n Tests.Xyz -o src/Tests.Xyz
dotnet sln add src/Tests.Xyz

# Check all services are healthy
curl -s :9000/health
curl -s :9501/metrics | head
```

---

## Troubleshooting

| Symptom | Likely Cause | Fix |
|---------|-------------|-----|
| Store PUT fails after 30s | Missing tmpfs | `sudo bash infra/setup-ramdisk.sh` |
| Hydra.Core can't connect to Store | Store not initialized | Start Hydra.Core, verify :9500 health |
| `cache_n=0` after restore | `n_tokens <= n_past` | Ensure prompt has more tokens than cached state |
| RPC `InvalidDataException` | Wrong magic byte | Check client/server protocol version match |
| Pipe deadlock in tests | Pipe threshold (32 KB) | Use concurrent reader/writer pattern |
| Chunked save slow | First save = all chunks new (800 MB → ~800 chunks) | Normal. Second save of same session should be fast (delta only) |
| Chunk cache not persisting | `ChunkCacheDir` not writable | Check `HYDRA_CHUNK_CACHE_DIR` (default: `/tmp/hydra-chunk-cache`) |
| `PUT_CHUNKED` returns error | Manifest already exists with different session_id | Session IDs must be unique. Delete manifest first or use different ID |
| `dotnet test src/Hydra.sln` hangs | Parallel project execution → PG port/connection contention | Use `--settings src/Hydra.runsettings` (serializes assemblies) or run per-project |
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

**Retired section:** The Python coordinator was removed in PR #203. Coordinator logic
is now embedded in Hydra.Core. No separate Python coordinator container exists.

### C# Service Rebuild

Hydra.Core is the single C# binary. Rebuild with:

```bash
cd infra
podman-compose -f docker-compose.hydra.yml build --no-cache hydra-core
podman-compose -f docker-compose.hydra.yml up -d
```

### Worker Configuration

Workers are defined in `WorkerConfig` (C#) and configured via
`HYDRA_CORE_WORKERS` in environment or config file:

```json
[
  {"name":"rtx","llama_url":"http://localhost:8080","llama_rpc_port":9503,
   "worker_type":3,"slots":2,"prefill_priority":1,"decode_priority":2,"decode_speed_tps":200,
   "can_prefill":true,"can_decode":true},
  {"name":"p100","llama_url":"http://192.168.122.21:8086","llama_rpc_port":9502,
   "worker_type":2,"slots":1,"prefill_priority":2,"decode_priority":1,"decode_speed_tps":28,
   "can_prefill":false,"can_decode":true}
]
```

| Field | Meaning |
|-------|---------|
| `worker_type` | 1=PREFILL, 2=DECODE, 3=MIXED |
| `prefill_priority` | Lower = better prefill worker (RTX=1, P100=2) |
| `decode_priority` | Lower = better decode worker (P100=1, RTX=2) |
| `decode_speed_tps` | Estimated decode speed (used in concurrency decision) |
| `max_prefill_tokens` | Optional: cap prefill size for this worker |
| `llama_rpc_port` | Port for hydra RPC (StateGet/Put/Meta) on llama-server

P100 is set to `worker_type=2` (DECODE only) — it will never be selected for
prefill. The scheduler will wait for RTX to become free instead of falling back
to P100's slow prefill (28 tok/s vs RTX's 285 tok/s).

### Prefix Checkpoints (System Prompt Cache)

Hydra.Core caches the system prompt KV to Store and restores it for subsequent
requests. Enabled by default (`prefix_checkpoint_enabled=true`).

**How it works:**
1. Request arrives with `{"role":"system","content":"..."}` in messages
2. Hydra.Core computes `prefix_hash = sha256(system_content)[:16]`
3. Tries to restore `prefix/{hash}` from Store → if found, KV loaded in ~44ms
4. If not found, full prefill runs and prefix is saved to Store afterward
5. Next request with same system prompt restores instantly

**Verify:**
```bash
# Check prefix saves
podman logs hydra-core | grep prefix_checkpoint_saved

# Check prefix restores
podman logs hydra-core | grep prefix_checkpoint_restored

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
# Watch Hydra.Core status
curl -s http://localhost:9000/status | python3 -m json.tool

# Watch Hydra.Core logs (non-health)
podman logs -f hydra-core | grep -v "health_ok\|GET /health\|GET /metrics"

# Watch for specific events
podman logs -f hydra-core 2>&1 | grep -E "concurrency|store_restore|prefix|state_saved"

# Check Store health
curl -s http://localhost:9000/health | python3 -c "import sys,json; d=json.load(sys.stdin); print(d['store'])"

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
ssh hydra-p100 'systemctl --user restart llama-p100'

# Option 2: Restart the Core (clears tracker state)
podman restart hydra-core
```

---

## Eval Tests

See `docs/eval-tests.md` for full methodology, pass/fail criteria, and monitoring.

### Quick Eval (NIAH passkey retrieval)

```bash
# 2K context, 50% needle depth
bash scripts/eval/run-niah.sh -c 2000 -d 50

# Sweep: 2K + 5K + 8K
bash scripts/eval/run-niah.sh -c 2000,5000,8000 -d 50

# All eval tiers (small: NIAH only, full: NIAH + perplexity)
bash scripts/eval/run-all.sh --small
```

### Checking progress during eval

```bash
# Phase durations (save_kv_ms, restore_kv_ms)
podman logs -f hydra-core 2>&1 | grep "request_timeline\|save_kv\|restore_kv"

# Llama-server activity (both nodes)
podman logs -f llama-cpp 2>&1 | grep "STATE_GET\|STATE_PUT\|n_past\|slot"
ssh hydra-p100 'journalctl --user -u llama-p100 -n 30 --no-pager | grep "STATE\|restored"'

# Token deltas (which GPU did the work)
watch -n 2 '
echo -n "RTX:  "; curl -s http://localhost:8080/metrics 2>/dev/null | grep "^llamacpp:prompt_tokens_total\|^llamacpp:tokens_predicted_total" | tr "\n" " " && echo ""
echo -n "P100: "; curl -s http://192.168.122.21:8086/metrics 2>/dev/null | grep "^llamacpp:prompt_tokens_total\|^llamacpp:tokens_predicted_total" | tr "\n" " " && echo ""
'

# Core health and slot state
curl -s http://localhost:9000/health | python3 -m json.tool
curl -s http://localhost:8080/slots | python3 -c "import sys,json; d=json.load(sys.stdin); [print(f'id={s[\"id\"]} past={s.get(\"n_past\",0)} proc={s.get(\"is_processing\",False)}') for s in d]"
```

### Verifying content quality directly (bypass Core)

When P/D split infrastructure is unstable, test content quality directly on RTX:

```bash
# Send prompt directly to RTX llama-server
curl -s http://localhost:8080/v1/chat/completions \
  -H "Content-Type: application/json" \
  -d '{"model":"balanced","messages":[{"role":"user","content":"What is 2+2?"}],"max_tokens":5}' | python3 -m json.tool

# Check both content AND reasoning_content for passkey in NIAH tests
# Model with --reasoning on puts thinking in reasoning_content,
# content may be empty if max_tokens is too low
```

### P100 recovery (CUDA hang)

Symptoms: `/v1/chat/completions` hangs, RPC StatePut times out, `systemctl` show `Failed with result 'timeout'`.

```bash
# 1. Stop P100 (may take 30s)
ssh hydra-p100 'systemctl --user stop llama-p100'

# 2. Wait for GPU cleanup
sleep 10; ssh hydra-p100 nvidia-smi | grep "MiB"

# 3. Restart + wait for model (~90s)
ssh hydra-p100 'systemctl --user start llama-p100'
watch -n 3 'curl -s http://192.168.122.21:8086/health'

# 4. Verify completions work
curl -s -m15 http://192.168.122.21:8086/v1/chat/completions \
  -H "Content-Type: application/json" \
  -d '{"model":"balanced","messages":[{"role":"user","content":"Hi"}],"max_tokens":3}'

# 5. Restart Core to clear stuck classifier
podman restart hydra-core
```

### Result storage

Test results are written to `tests/results/` (markdown reports + raw JSON + captured logs)
and `/tmp/hydra-eval-results/` (latest HTTP responses + extracted logs).
