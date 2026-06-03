# Hydra — Architecture Reference

> Reflects the **implemented** system as of M2 / Phase 0.
> For diagrams see `docs/diagrams.md`. For wire-format detail see `specs/rpc-protocol.md`.

---

## 1. Service Map

| Service | Lang | Port(s) | Runs on |
|---|---|---|---|
| Coordinator | Python / FastAPI | `:9000` (HTTP) | host container |
| Agent RTX | C# / .NET 10 | `:9601` (RPC), `:9611` (HTTP debug/metrics) | host container |
| Agent P100 | C# / .NET 10 | `:9602` (RPC), `:9622` (HTTP debug/metrics) | host container (connects to llama in VM) |
| Hydra Store | C# / .NET 10 | `:9500` (RPC), `:9501` (HTTP debug/metrics) | host container |
| llama-server RTX | C++ fork | `:8080` (HTTP) | host container (`llama-cpp`, separate compose) |
| llama-server P100 | C++ fork | `:8086` (HTTP) | **KVM VM** `192.168.122.21` (bare process, not containerised) |

All host services run via `infra/docker-compose.yml`. Agent P100's
`HYDRA_AGENT_LLAMA_URL=http://192.168.122.21:8086` crosses the NAT bridge into the VM.
The KVM VM hosts only the P100 llama-server; it has no Agent, Store, or Coordinator.

**Protocol rule:** All inter-service traffic is Hydra binary RPC except the two HTTP
edges: `Client → Coordinator` (OpenAI-compat) and `Agent → local llama-server`.

---

## 2. Worker Node Configuration

Each GPU node is represented as a `WorkerNodeConfig` (Python) / `NodeConfig` (C# Agent).

```python
class WorkerNodeConfig(BaseModel):
    name: str                    # "rtx" or "p100"
    host: str                    # IP of the agent
    rpc_port: int                # Agent RPC port (9601/9602)
    llama_url: str               # Base URL of local llama-server
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

Coordinator `run_mode` controls the P/D strategy:

### `fast` (default)
One worker handles both prefill and decode for a session. Minimises KV migration.
Session affinity keeps requests on the same GPU across turns.

```
Client → Coordinator → [session affinity or least-loaded] → Agent → llama-server
                                                                  ↕ (KV stays on-GPU)
```

### `concurrency` (P/D disaggregation — implemented, not yet benchmarked)
Prefill on one worker, decode on another. Enables the RTX (fast prefill) + P100
(parallel decode) split targeted by M-Perf.

Flow for a new session in `concurrency` mode:

```
1. Route request to a PREFILL-capable worker (worker_type & 1)
2. Send max_tokens=1 request (fills KV without generating output)
3. Resolve slot used by prefill (query /slots, match n_past)
4. SAVE_STATE_CHUNKED → Store (kv/{session_id} in tmpfs chunks)
5. SLOT_ERASE on the prefill GPU (free VRAM)
6. Select a DECODE-capable worker (worker_type & 2), exclude prefill GPU if possible
7. RESTORE_STATE_CHUNKED on the decode GPU
8. Stream real completion from decode GPU
```

The `X-Hydra-Prefill-Node` and `X-Hydra-Node` response headers name the two GPUs used.

---

## 4. Routing Algorithm

`routing.py:route_request()` applies four tiers in order:

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

The Coordinator checks after routing:

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
(0x27). The raw 0x20/0x21 ops exist but are not called by the Coordinator.

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
Agent: SaveHashesAsync(session_id, hashes) to LocalChunkCache
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

## 9. Agent Save/Restore (StateHandler.cs)

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

## 10. LocalChunkCache (Agent-side)

Each Agent maintains a cache of chunk data on its local disk (not tmpfs) to support
partial-cache restores without fetching every chunk from the Store over the network.

- Populated during every `SaveToStoreChunkedAsync` via `ChunkHashTeeStream`
- LRU eviction (`EvictLRU`) prevents unbounded growth
- `LoadHashesAsync(session_id)` returns the set of hashes the agent already has
- `GetChunkDataAsync(session_id, hash)` returns raw bytes for assembly

---

## 11. Health Monitor

`health.py:HealthMonitor` polls each worker's Agent via `NODE_HEALTH` (0x24) every
`health_poll_interval_s` (default 20 s). After `health_max_failures` (default 3)
consecutive failures the node is marked unhealthy and excluded from routing.

The Coordinator's `/health` endpoint reports per-node status and store health.
Prometheus metrics: `hydra_agent_llama_healthy`, `hydra_agent_slots_idle`.

---

## 12. Observability

| Endpoint | What |
|---|---|
| `:9000/metrics` | Coordinator: requests, sessions, routing stats |
| `:9611/metrics` | Agent RTX: save/restore durations, ops, llama health |
| `:9622/metrics` | Agent P100: same as RTX |
| `:9501/metrics` | Store: put/get duration, bytes stored, chunk ops |
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

## 13. llama.cpp Fork

Branch: `hydra-state-streaming` off llama.cpp mainline.
Only `tools/server/server.cpp` is modified (~80 lines, 3 endpoints).

**Active model (RTX):** `Qwopus3.6-35B-A3B-v1-APEX-MTP-I-Balanced.gguf` (qwen35moe arch).
RTX llama config uses `--spec-type draft-mtp --spec-draft-n-max 3` (MTP speculative decoding
already enabled), `--mmproj` (vision), `--ctx-size 360000`, `--parallel 2`.

| Endpoint | Direction | Size | Notes |
|---|---|---|---|
| `GET /slots/{id}/state` | llama → Agent | ~800 MB | binary KV+SSM state |
| `PUT /slots/{id}/state` | Agent → llama | ~800 MB | returns `{restored, n_past}` |
| `GET /slots/{id}/state/meta` | llama → Agent | bytes | `{n_past, state_size, is_processing}` |

`get_text_tokens()` is used (not `get_tokens()`) to avoid `GGML_ASSERT(!has_mtmd)`
abort and to filter `LLAMA_TOKEN_NULL` in multimodal-safe builds.

Build flags:
- RTX host: `GGML_CUDA_FORCE_CUBLAS=ON`, `-arch sm_120`
- P100 KVM VM: `-arch sm_60`
