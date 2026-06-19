# Hydra — Architecture Reference

> Reflects the **implemented** system as of M-Perf / Phase 0 (post Agent→Core merge).
> For diagrams see `docs/diagrams.md`. For wire-format detail see `specs/rpc-protocol.md`.

---

## 1. Service Map

| Service | Lang | Port(s) | Runs on |
|---|---|---|---|
| Hydra.Core | C# / .NET 10 | `:9000` (HTTP API), `:9500` (Store RPC), `:9501` (debug/metrics) | host container |
| llama-server RTX | C++ fork | `:8080` (HTTP), `:9503` (hydra RPC) | host container |
| llama-server P100 | C++ fork | `:8086` (HTTP), `:9502` (hydra RPC) | **KVM VM** `192.168.122.21` |

Hydra.Core runs via `infra/docker-compose.hydra.yml` (hydra core, host networking) and
`infra/docker-compose.infra.yml` (observability stack). The P100 llama-server at
`192.168.122.21:8086` is reached over the NAT bridge into the VM.
The KVM VM hosts only the P100 llama-server.

**Protocol rule:** All inter-service traffic between Hydra.Core and llama-servers uses
direct HTTP (completions) and binary RPC (state ops). Client → Core is HTTP (OpenAI-compat).

---

## 2. Worker Node Configuration

Each GPU node is represented as a `WorkerNodeConfig` (now a C# class in Hydra.Core).

```python
class WorkerNodeConfig(BaseModel):
    name: str                    # "rtx" or "p100"
    host: str                    # IP of the llama-server
    llama_url: str               # Base URL of local llama-server (HTTP)
    llama_rpc_port: int          # llama-server hydra RPC port (9503 RTX, 9502 P100)
    worker_type: int = 3         # Bitwise: 1=prefill-only, 2=decode-only, 3=mixed
    slots: int = 1               # Number of llama inference slots
    prefill_priority: int = 1    # Lower = preferred for prefill (ties allowed)
    decode_priority: int = 1     # Lower = preferred for decode (ties allowed)
    decode_speed_tps: float = 30.0  # Estimated decode tok/s for scheduling
```

**`worker_type` values:**
- `1` — prefill-only (e.g., RTX configured for large-context ingestion)
- `2` — decode-only (e.g., P100 configured for streaming token generation)
- `3` — mixed: both prefill and decode (default; either GPU can do both)

Set via `HYDRA_COORD_WORKERS` (JSON array) or `HYDRA_COORD_CONFIG_FILE`.

---

## 3. Run Modes

Hydra.Core `run_mode` controls the P/D strategy:

### `fast` (default)
One worker handles both prefill and decode for a session. Minimises KV migration.
Session affinity keeps requests on the same GPU across turns.

```
Client → Hydra.Core → [session affinity or least-loaded] → llama-server
                                                                  ↕ (KV stays on-GPU)
```

### `concurrency` (P/D disaggregation — implemented, not yet benchmarked)
Prefill on one worker, decode on another. Enables the RTX (fast prefill) + P100
(parallel decode) split targeted by M-Perf.

Flow for a new session in `concurrency` mode:

```
1. Route request to a PREFILL-capable worker
2. Prefill (n_predict=0, fills KV without generating output)
3. StateGet RPC → llama hydra RPC port → Store RPC Put (kv/{session_id})
4. Select a DECODE-capable worker
5. Store RPC Get → StatePut RPC → llama hydra RPC port
6. Stream real completion from decode GPU
```

The `X-Hydra-Prefill-Node` and `X-Hydra-Node` response headers name the two GPUs used.

**Engine-mode decode path (post-#273):** In engine mode (`HYDRA_LLAMA_ENGINE=true`),
the control-RPC plane (opcodes 0x40–0x45) is used **only for prefill and KV state
transfer** (`EnginePrefill` 0x42 → KV blob inline, `StateGet` 0x30, `StatePut` 0x31).
The actual decode (token streaming back to the client) **always** uses the HTTP
`/v1/chat/completions` SSE path — even when the llama binary is the `llama-engine`
fork. The engine-RPC `EngineDecode` (0x43) and `EngineDecodeStreamAsync` client
methods are retained for the future E2 (expert-mode) spike but are not on the
production path. This split exists because the raw-bytes `EngineDecode` payload
collapsed `reasoning_content` → `content` and dropped `finish_reason` / `id_slot`
/ `timings`, breaking the OAI schema that the Coordinator must return to clients.

---

## 4. Routing Algorithm

`RouteRequest()` applies four tiers in order:

### Tier 1 — Session affinity
If the session is in `session_table` **and** its node is healthy → route there directly.
No migration, no store lookup.

### Tier 2 — Store restore
If the session was evicted to Store (`has_store_state=True`) → pick the **least-loaded**
healthy worker, then `RESTORE_STATE_CHUNKED` before forwarding.

### Tier 3 — Long-prompt prefill routing
If `estimated_tokens >= long_prompt_threshold` (default 8 192) and no session exists →
pick the highest-priority **prefill-capable** worker (`prefill_priority ASC, load ASC`).

### Tier 4 — Least-loaded with round-robin tiebreak
New session with short prompt → least-loaded healthy worker; ties broken by a global
round-robin counter (`_rr_counter`) to distribute across equal-load nodes.

**Load metric:** `busy_fraction = (total_slots - idle_slots + in_flight) / total_slots`.
The `_in_flight` counter is incremented **before** any await so concurrent coroutines
see accurate load without waiting for the next health poll.

---

## 5. Session Lifecycle

```
register(session_id, node, slot_id, n_past=0)
  │
  │  [turns: update_n_past, update_last_used]
  │
  ├─ evict_lru() / evict_session()
  │    └─ SAVE_STATE_CHUNKED → Store
  │       SLOT_ERASE
  │       mark_evicted()  → has_store_state=True, slot_id=None
  │
  ├─ [next request with has_store_state=True]
  │    └─ RESTORE_STATE_CHUNKED from Store
  │       register() on new node with restored n_past
  │
  └─ evict_stale(timeout_s=3600)  [background task, every 60 s]
       └─ remove() — session table entry deleted (Store blob retained)
```

**`n_past`** tracks how many tokens are in the KV cache for a session.
It is read from `usage.total_tokens` in each completion response.

---

## 6. n_past Guard

The KV cache is invalidated if `n_tokens ≤ n_past` (llama invariant).

Hydra.Core checks after routing:

```python
if estimated_tokens < stored_n_past * 0.85:
    update_n_past(session_id, 0)
    SLOT_ERASE(slot_id)   # free slot so llama can repopulate
    # request proceeds with n_past=0 (fresh prefill)
```

This prevents cache corruption when the client sends a shorter message than the
previously cached context (e.g., a new conversation that reuses an old session_id).

---

## 7. Prefix Checkpoint Mechanism

Caches system-prompt KV state so all new sessions sharing the same system prompt
skip re-prefilling it.

**Save** (once per unique system-prompt + node combination):

```python
prompt_hash = sha256(system_message.content)[:16]
prefix_key = f"{node_name}:{prompt_hash}"
if prefix_key not in _saved_prefixes:
    SAVE_STATE_CHUNKED("prefix/{prompt_hash}", node, slot_id)
    _saved_prefixes.add(prefix_key)
```

**Restore** (for every new session with the matching system prompt):

```python
meta = RESTORE_STATE_CHUNKED("prefix/{prompt_hash}", node, slot_id)
n_past_after_restore = meta["n_past"]
update_n_past(session_id, n_past_after_restore)
```

Keys are namespaced `prefix/` in the Store so they are separate from session KV blobs.

---

## 8. M2 Chunked Dedup

All production saves/restores use `SAVE_STATE_CHUNKED` (0x26) / `RESTORE_STATE_CHUNKED`
(0x27). The raw 0x20/0x21 ops exist but are not called by Hydra.Core.

### Chunk engine

- Chunk size: **1 MB** (`ChunkEngine.CHUNK_SIZE`)
- Hash: **SHA-256** (hex-lowercase)
- Store layout on tmpfs:
  ```
  /mnt/llm-ram/store/
    chunks/             # one file per unique chunk hash (content-addressed)
    manifests/          # {session_id}.json → {n_past, chunks:[{index,hash,size}]}
    meta/               # {session_id}.json → {n_past} (written before manifest)
  ```

### Save path (`StateHandler.SaveToStoreChunkedAsync`)

```
llama GET /slots/{id}/state
  → TeeStream (hashes chunks + writes to LocalChunkCache as it streams)
  → RPC PUT_CHUNKED (0x10) to Store
      → ChunkEngine.ChunkAndHashFromPipeAsync
      → StoreChunk: skip if hash known (dedup), else write to chunks/
      → write manifest
  → PUT_META (0x14) with n_past
Hydra.Core: SaveHashesAsync(session_id, hashes) to LocalChunkCache
```

### Restore path (`StateHandler.RestoreFromStoreChunkedAsync`)

```
LocalChunkCache.LoadHashesAsync(session_id) → known_hashes (Set<string>)
RPC GET_CHUNKED (0x11, known_hashes as JSON payload)
  → Store responds: meta={total_size, missing_count}
                    payload=[index+size+data for each missing chunk]

if missing_count == 0:
    # Full cache hit — check llama n_past > 0, skip network transfer
else:
    GET_MANIFEST (0x33) → full chunk list with index/hash/size
    Allocate completeState[total_size]
    Fill from LocalChunkCache (local chunks) + GET_CHUNKED payload (missing chunks)
    llama PUT /slots/{id}/state ← completeState stream
```

---

## 9. KV State Save/Restore (StateHandler)

Both chunked and non-chunked paths share the same `llama` → `store` piping pattern:

| Step | Chunked | Raw |
|---|---|---|
| Get llama state | `GetStateAsync(slotId)` → HTTP stream | same |
| Send to store | `PUT_CHUNKED` (0x10) via TeeStream | `PUT` (0x01) |
| Local cache | `LocalChunkCache.SaveHashesAsync` | — |
| Verify n_past | `GetStateMetaAsync` post-restore | same |

`GetStateMetaAsync` calls `GET /slots/{id}/state/meta` (cheap, no KV serialization)
to confirm `n_past` after restore — the value from the PUT response alone is not trusted.

---

## 10. LocalChunkCache (Hydra.Core)

Hydra.Core maintains a cache of chunk data on local disk (not tmpfs) to support
partial-cache restores without fetching every chunk from the Store over the network.

- Populated during every `SaveToStoreChunkedAsync` via `ChunkHashTeeStream`
- LRU eviction (`EvictLRU`) prevents unbounded growth
- `LoadHashesAsync(session_id)` returns the set of hashes Hydra.Core already has
- `GetChunkDataAsync(session_id, hash)` returns raw bytes for assembly

---

## 11. Health Monitor

Hydra.Core's health monitor polls each llama-server directly via HTTP (`GET /health`) every
`health_poll_interval_s` (default 20 s). After `health_max_failures` (default 3)
consecutive failures the node is marked unhealthy and excluded from routing.

Hydra.Core's `/health` endpoint reports per-node status and store health.
Prometheus metrics: `hydra_core_llama_healthy`, `hydra_core_slots_idle`.

---

## 12. Observability

| Endpoint | What |
|---|---|
| `:9000/metrics` | Hydra.Core HTTP API: requests, sessions, routing stats |
| `:9501/metrics` | Hydra.Core Store: put/get duration, bytes stored, chunk ops |
| `:9100/metrics` | Node exporter: host CPU/RAM |
| `:9835/metrics` | DCGM GPU exporter: utilization, temp, power, mem |

Grafana at `:3000`. All services emit structured JSON logs with a `trace_id` field for
cross-service correlation (`X-Trace-Id` header).

### Log Pipeline

```
Container (k8s-file)
  └─ ctr.log (CRI format: <ts> stderr F <msg>)
       └─ Promtail (docker_sd_configs ← podman socket)
            ├─ relabel_configs: component/node/container/job labels from Docker labels
            ├─ drop: component=observability (avoids scraping self/grafana/loki)
            └─ cri: {} parser → timestamp, stream (stdout/stderr), message
                └─ Loki :3100
                    └─ Grafana Explore / Hydra dashboard
```

**Prerequisite:** Podman must use `log_driver = "k8s-file"` (set in
`~/.config/containers/containers.conf`). The default `journald` driver provides no
file-backed `ctr.log` files for Promtail to scrape via `docker_sd_configs`.

Promtail runs as a container (`hydra_promtail_1`) in the same compose stack. It mounts
the podman socket at `/run/user/1000/podman/podman.sock` for container discovery and
`/mnt/containers/` for reading `ctr.log` files.  The `docker_sd_configs` refresh interval
is 5s.

---

## 13. Container Orchestration

The `infra/` directory contains **two independent compose files** with separate lifecycles:

| File | Project name | Services | Networking |
|---|---|---|---|
| `docker-compose.hydra.yml` | `hydra-core` | Hydra.Core | `network_mode: host` |
| `docker-compose.infra.yml` | `hydra-infra` | Prometheus, Grafana, Loki, Promtail, node-exporter, nvidia-exporter | host for prometheus/grafana/exporters; default bridge for loki/promtail |

### Rationale for the split

- **Independent lifecycle** — restarting hydra core (code deploy, config change) does not bounce observability. Prometheus continues scraping, Grafana dashboards stay live, Loki retains logs.
- **Host networking for hydra** — Hydra.Core binds `:9000` and `:9500` on the host; llama-servers are contacted directly at their HTTP/RPC ports.
- **Infra stability** — observability images (prom/prometheus, grafana/grafana, grafana/loki) change infrequently and do not need per-deploy rebuilds. The CI deploy job only touches `docker-compose.hydra.yml`.

### Inter-stack communication

Despite being separate compose projects, infra and hydra interact seamlessly because:

- **Prometheus uses `host` networking** — its scrape config targets `localhost:PORT` for all hydra services (Hydra.Core `:9000` and `:9501`), the same way it scrapes node-exporter and nvidia-exporter.
- **Promtail uses Docker service discovery** — it reads the podman socket to discover all running containers regardless of compose project. Hydra services carry `component: hydra-core` labels used as Loki selectors. Infra services carry `component: observability` and are dropped from scraping (`relabel_configs`) to avoid self-scrape loops.

### Networking model

```
┌─────────────────────────────────────────────────────────┐
│  host network                                           │
│                                                         │
│  ┌─ hydra-core (docker-compose.hydra.yml) ───────────┐  │
│  │  hydra-core:9000 (HTTP API), :9500 (Store RPC),   │  │
│  │  :9501 (debug/metrics)                             │  │
│  └────────────────────────────────────────────────────┘  │
│                                                         │
│  ┌─ hydra-infra (docker-compose.infra.yml) ──────────┐  │
│  │  prometheus:9091 (host), grafana:3000 (host),      │  │
│  │  node-exporter:9100 (host), nvidia-exporter:9835,  │  │
│  │  loki:3100 (bridge), promtail (bridge)              │  │
│  │  └── prometheus scrapes hydra services via host     │  │
│  └────────────────────────────────────────────────────┘  │
│                                                         │
│  ┌─ llama-servers (separate compose / VM) ───────────┐  │
│  │  rtx :8080 (HTTP), :9503 (hydra RPC)               │  │
│  │  p100 :8086 (HTTP), :9502 (hydra RPC, KVM VM)     │  │
│  └────────────────────────────────────────────────────┘  │
└─────────────────────────────────────────────────────────┘
```

### Port allocation

With host networking, hydra services bind directly to host ports. Avoid conflicts with:

| Port | Service |
|---|---|
| `:9500` | Hydra.Core Store RPC |
| `:9501` | Hydra.Core debug/metrics |
| `:9000` | Hydra.Core HTTP API + metrics |
| `:9503` | llama RTX hydra RPC |
| `:9502` | llama P100 hydra RPC |
| `:8080` | llama RTX HTTP |
| `:8086` | llama P100 HTTP |

### Typical commands

```bash
# Start everything
docker compose -f docker-compose.infra.yml up -d
docker compose -f docker-compose.hydra.yml up -d

# Restart hydra only (e.g. after deploy)
docker compose -f docker-compose.hydra.yml down -t 0
docker compose -f docker-compose.hydra.yml up -d

# Observability stays up during hydra restarts
docker compose -f docker-compose.infra.yml ps
```

---

## 14. llama.cpp Fork

Branch: `hydra-state-streaming` off llama.cpp mainline.
Only `tools/server/server.cpp` is modified (~80 lines, 3 endpoints).

**Active model:** `Qwopus3.6-35B-A3B-v1-APEX-MTP-I-Balanced.gguf` (qwen35moe arch).
RTX llama config uses `--spec-type draft-mtp --spec-draft-n-max 3` (MTP speculative decoding
enabled), `--mmproj` (vision), `--ctx-size 360000`, `--parallel 2`.

| Endpoint | Direction | Size | Notes |
|---|---|---|---|
| `GET /slots/{id}/state` | llama → Hydra.Core | ~800 MB | binary KV+SSM state |
| `PUT /slots/{id}/state` | Hydra.Core → llama | ~800 MB | returns `{restored, n_past}` |
| `GET /slots/{id}/state/meta` | llama → Hydra.Core | bytes | `{n_past, state_size, is_processing}` |

`get_text_tokens()` is used (not `get_tokens()`) to avoid `GGML_ASSERT(!has_mtmd)`
abort and to filter `LLAMA_TOKEN_NULL` in multimodal-safe builds.

Build flags:
- RTX host: `GGML_CUDA_FORCE_CUBLAS=ON`, `-arch sm_120`
- P100 KVM VM: `-arch sm_60`
