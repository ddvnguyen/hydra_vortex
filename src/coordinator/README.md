# Hydra Coordinator (DEPRECATED — merged into Hydra.Core)

> **⚠️ DEPRECATED as of PR #203.** The Python coordinator has been removed and its
> functionality was merged into **Hydra.Core** — the single C#/.NET 10 binary
> (HTTP API on :9000, Store RPC on :9500). Agent containers (hydra-agent-rtx,
> hydra-agent-p100) no longer exist. llama-servers are contacted directly via
> HTTP/RPC from Hydra.Core. This document is retained for historical reference only.

---

Multi-GPU LLM inference request router. Dispatches chat-completion requests across heterogeneous
GPU nodes (RTX 5060 Ti sm_120 + Tesla P100 sm_60), orchestrating **P/D disaggregation** with
cross-GPU KV-cache migration via the Hydra Store.

```
Client (HTTP)
    │  POST /v1/chat/completions
    ▼
┌─────────────────────────────────────────────────────────┐
│                     Coordinator :9000                    │
│  ┌──────────┐  ┌──────────┐  ┌───────────┐            │
│  │ Scheduler │  │  Session │  │   State    │            │
│  │  (queue)  │  │  Table   │  │  Manager   │            │
│  └─────┬─────┘  └────┬─────┘  └─────┬──────┘            │
│        │              │              │                   │
│  ┌─────┴──────┐  ┌────┴──────┐  ┌───┴────────┐         │
│  │   Health   │  │  Worker   │  │   Routing   │         │
│  │  Monitor   │  │  Tracker  │  │  (pick/est) │         │
│  └────────────┘  └───────────┘  └─────────────┘         │
│                                                         │
│  ┌──────────────────────────────────────────────────┐   │
│  │                   Proxy (HTTP)                    │   │
│  └──────────────────────┬───────────────────────────┘   │
└─────────────────────────┼───────────────────────────────┘
            │ RPC (TCP)              │ HTTP
            ▼                       ▼
┌───────────────────────┐   ┌──────────────────┐
│    Agent RTX :9601    │   │   llama RTX :8080 │
│    Agent P100 :9602   │   │   llama P100:8086 │
└───────────┬───────────┘   └──────────────────┘
            │ RPC
            ▼
┌───────────────────────┐
│     Store :9500       │
│   /mnt/llm-ram/store  │
└───────────────────────┘
```

---

## Components

### 1. Scheduler (`scheduler.py`)
**Central request dispatcher.** `WorkerScheduler` owns the FIFO queue and runs an async
scheduling loop (`_run`). The loop wakes on two events (`_new_item`, `_worker_freed`),
pops the front item, picks a free worker, acquires it, and creates a task for `_process`.

```
request_received
      │
      ▼
  ┌─────────┐
  │  Queue  │  WorkItem {
  │  (FIFO) │    request, session_id, trace_id,
  └────┬────┘    estimated_tokens, future, phases
       │
       ▼
  _can_handle()
       │  picks best free worker with PREFILL capability
       ▼
  _process(item, worker)
       │
       ├── entry.slot_id && !slot_freed?
       │       ├── same node? ───► _execute_affinity (warm)
       │       └── different? ───► _execute_affinity (cross_node)
       │
       ├── entry.has_store_state?
       │       └──► _execute_store_restore (migration)
       │
       ├── _is_atomic()?
       │       └──► _execute_atomic (single-worker, no migration)
       │
       └──► _execute_concurrency (P/D disaggregation)
```

**Cold concurrency (P/D disaggregation) flow:**

```
_execute_concurrency(prefill_worker=RTX)
      │
      ├─ load model (skip if pre-loaded)
      ├─ maybe restore prefix checkpoint
      │
      ├─ prefill on RTX (nano IQ2, 110 tok/s)
      │      POST /v1/chat/completions {max_tokens:1, stream:false}
      │
      ├─ save KV to Store (BLOCKING — guarantees KV before slot eviction)
      │      Agent RPC: SaveStateChunked
      │
      ├─ mark_evicted (slot_freed=True, has_store_state=True)
      │
      ├─ pick best decode worker (P100 decode_priority=1, RTX fallback=2)
      │
      ├─ load decode model if needed (balanced Q5K)
      │
      ├─ restore KV to decode worker
      │      Agent RPC: RestoreStateChunked
      │
      └─ decode on decode worker (streaming SSE)
             POST /v1/chat/completions {stream:true}
             └─ save_session in BACKGROUND after completion
```

**Stream generators** handle client disconnect:
- `finally` releases worker + sets `_worker_freed`
- `_cancel_slot()` fires `SlotErase` RPC to clear GPU slot

---

### 2. Session Table (`session_table.py`)
In-memory session registry. One `SessionEntry` per active conversation.

```
SessionEntry {
    session_id:    "sess_a1b2c3..."
    node_name:     "rtx" | "p100"
    slot_id:       0 | 1 | None
    n_past:        27765          // cumulative tokens (prompt + generated)
    has_store_state: bool         // KV saved to Store?
    slot_freed:    bool           // slot released by llama-server?
    prefix_hash:   "e7a6848eba..." // system-prompt content hash
}
```

**Lifecycle:**
```
register() ──► warm use ──► mark_evicted() ──► store_restore ──► remove()
                  │
                  └── n_past guard → free slot → mark evicted
```

---

### 3. State Manager (`state_manager.py`)
Orchestrates KV cache state between Agent nodes and the Store.

```
save_session(sess_id, host, port)
    │
    ├─ lookup session → get slot_id
    ├─ Agent RPC: SaveStateChunked(slot_id)
    ├─ Agent RPC: PutMeta (session_id, n_past, chunked, save_ms)
    └─ session.has_store_state = True

restore_session(sess_id, host, port, slot_id=0)
    │
    ├─ Agent RPC: RestoreStateChunked(slot_id)
    ├─ Agent RPC: PutMeta (n_past)
    └─ session.slot_id = slot_id

evict_lru(node_name, host, port)
    │
    ├─ get_lru_session(not slot_freed)
    ├─ save_session → SlotErase
    └─ mark_evicted
```

**Prefix checkpoints:** System prompt KV is cached under key `prefix/{hash}:{slot_id}`.
On cold requests, the scheduler restores the prefix before processing the user prompt delta.

---

### 4. Health Monitor (`health.py`)
Polls agents every 20s via `NodeHealth` RPC + `GET /slots` HTTP. Tracks stuck slots.

```
poll loop (every 20s)
    │
    ├─ RPC: NodeHealth → agent
    ├─ HTTP: GET {llama_url}/slots
    │
    ├─ stuck-slot watchdog:
    │     n_past not progressing for 3 cycles → SlotErase + tracker release
    │
    └─ update NodeInfo {
          healthy, slots_total, slots_idle, stuck_slots
        }
```

---

### 5. Worker Tracker (`worker_tracker.py`)
Free/busy state machine per worker.

```
Worker RTX:
  free ── acquire("prefill") ──► prefill ── release() ──► free
                                          ── on_error() ×3 ──► unhealthy

Worker P100:
  free ── acquire("decode")  ──► decode  ── release() ──► free
```

Used by scheduler to route requests. Unhealthy workers are excluded from `free_workers()`.

---

### 6. Routing (`routing.py`)
Pure functions for dispatch decisions.

| Function | Role |
|----------|------|
| `derive_session_id(messages)` | SHA-256(role:content) → `sess_<hex>` |
| `estimate_request_tokens(messages)` | len(content) / chars_per_token |
| `compute_prefix_hash(messages)` | SHA-256(system message) → 16-char hex |
| `pick_best_prefill_worker(...)` | Lowest `prefill_priority`, within `max_prefill_tokens` |
| `pick_best_decode_worker(...)` | Lowest `decode_priority` |
| `verify_warm_slot(...)` | 4 checks: slot exists, not stuck, n_past covers, prefix matches |

---

### 7. Proxy (`proxy.py`)
HTTP tunnel to llama-server instances.

```
proxy_completion(node_url, body, trace_id)
    │
    └─ POST {node_url}/v1/chat/completions → return dict

proxy_completion_stream(node_url, body, trace_id)
    │
    └─ POST {node_url}/v1/chat/completions → stream SSE chunks
```

Uses shared `httpx.AsyncClient` with `connect=10s, read=1800s, pool=10s` timeouts.

---

### 8. Metrics (`metrics.py`)
Prometheus metrics exposed at `GET /metrics`.

| Metric | Type | Purpose |
|--------|------|---------|
| `hydra_requests_total` | Counter | Requests per node and reason |
| `hydra_prefill_seconds` | Histogram | Prefill duration |
| `hydra_decode_seconds` | Histogram | Decode duration |
| `hydra_save_kv_seconds` | Histogram | KV save time |
| `hydra_restore_kv_seconds` | Histogram | KV restore time |
| `hydra_model_load_seconds` | Histogram | Model load time (label: model) |
| `hydra_active_sessions` | Gauge | Sessions per node |
| `hydra_prefix_cache_hits` / `_misses` | Counter | Prefix checkpoint rate |
| `hydra_worker_busy_seconds` | Gauge | Stale-acquire leak detection |

---

### 9. Router (`router.py`)
FastAPI endpoints.

| Method | Path | Description |
|--------|------|-------------|
| POST | `/v1/chat/completions` | Chat completion (OpenAI-compatible) |
| GET | `/health` | Node + Store health summary |
| GET | `/status` | Full system state |
| GET | `/metrics` | Prometheus scrape |
| GET | `/sessions` | List all sessions |
| DELETE | `/sessions/{id}` | Evict + erase a session |
| POST | `/sessions/{id}/migrate` | Manual cross-GPU migration |

---

## Code Flow Graphs

### Complete Request Lifecycle

```
Client:  POST /v1/chat/completions {messages, max_tokens, stream}
              │
              ▼
router.py: chat_completion()
              │
              ├─ derive_session_id(messages) → "sess_a1b2..."
              ├─ estimate_request_tokens(messages) → 534
              ├─ compute_prefix_hash(messages) → "e7a6848e..."
              │
              ├─ scheduler.submit(request, messages, session_id, max_tokens, prefix_hash)
              │       │
              │       ├─ create WorkItem(request, messages, ..., future)
              │       ├─ queue.append(item)
              │       ├─ _new_item.set()           ← wake scheduler loop
              │       └─ await item.future          ← block until dispatched
              │
              └─ return StreamingResponse / JSONResponse
```

### Scheduler Loop

```
_run()  ← asyncio task, runs forever
    │
    while _running:
        │
        ├─ queue empty? → _wait_for_wakeup()
        │       │
        │       ├─ _new_item.clear()
        │       ├─ _worker_freed.clear()
        │       ├─ re-check: free_workers && queue? → return
        │       └─ await asyncio.wait([_worker_freed, _new_item], timeout=30s)
        │
        ├─ front = queue[0]
        ├─ worker = _can_handle(front)
        │       │
        │       └─ for w in free_workers():
        │             w.worker_type & WORKER_PREFILL?  ← RTX=3, P100=2→skip
        │             _routable(w)?                     ← heathy?
        │             max_prefill_tokens check
        │
        ├─ acquire(worker, "prefill")
        ├─ queue.pop(0)
        ├─ asyncio.create_task(_process(item, worker))
        └─ continue  ← back to top
```

### Client Disconnect Handling

```
Client disconnects mid-stream
    │
    ▼
stream_concurrency() generator
    │
    ├─ asyncio.CancelledError thrown into generator
    │
    ├─ except Exception: skipped (CancelledError is BaseException)
    ├─ finally:
    │     ├─ release(decode_worker)
    │     ├─ _worker_freed.set()          ← wake scheduler
    │     └─ create_task(_cancel_slot())  ← erase GPU slot
    │
    └─ raise CancelledError

_process() generator
    │
    ├─ except CancelledError:
    │     ├─ log "request_cancelled"
    │     ├─ cancel_slot()               ← erase remaining slot
    │     ├─ item.future.cancel()
    │     └─ raise
    │
    └─ finally:
          ├─ release(worker)
          └─ _worker_freed.set()
```

### Health + Stuck-Slot Recovery

```
HealthMonitor._poll_all()  ← every 20s
    │
    ├─ for each worker:
    │     ├─ RPC: NodeHealth → Agent
    │     ├─ HTTP: GET /slots
    │     ├─ Compare slot n_past with previous poll
    │     │
    │     ├─ no progress for MAX_STALL_CYCLES (3)?
    │     │     ├─ RPC: SlotErase → Agent
    │     │     └─ tracker.release(worker)
    │     │
    │     └─ update NodeInfo {healthy, slots, stuck_slots}
    │
    └─ probe Store: RPC Stat → Store
```

---

## Configuration

All settings via environment variables prefixed `HYDRA_COORD_`.

```bash
# Worker definitions (JSON array)
HYDRA_COORD_WORKERS='[
  {"name":"rtx","host":"localhost","rpc_port":9601,"llama_url":"http://localhost:8080",
   "worker_type":3,"slots":2,"prefill_priority":1,"decode_priority":2,
   "decode_speed_tps":200,"prefill_model_name":"nano","decode_model_name":"balanced"},
  {"name":"p100","host":"localhost","rpc_port":9602,"llama_url":"http://192.168.122.21:8086",
   "worker_type":2,"slots":1,"prefill_priority":2,"decode_priority":1,
   "decode_speed_tps":28}
]'

# Run mode
HYDRA_COORD_RUN_MODE=concurrency       # concurrency | fast
HYDRA_COORD_MIX_PRECISION_ENABLED=true  # P/D split with different quants

# Store connection
HYDRA_COORD_STORE_HOST=localhost
HYDRA_COORD_STORE_PORT=9500

# Performance tuning
HYDRA_COORD_LLAMA_REQUEST_TIMEOUT_S=1800   # upstream read budget
HYDRA_COORD_HEALTH_POLL_INTERVAL_S=20      # agent health poll
HYDRA_COORD_HEALTH_MAX_FAILURES=3          # failures → unhealthy
HYDRA_COORD_ATOMIC_THRESHOLD=2048          # new-prompt tokens ≤ this → single-worker atomic route
HYDRA_COORD_WARM_THRESHOLD=5120            # incremental new-prompt tokens ≤ this → reuse warm slot
```

---

## Worker Types

| Constant | Value | Worker | Capability |
|----------|-------|--------|-----------|
| `WORKER_PREFILL` | 1 | — | Can prefill (initiate context) |
| `WORKER_DECODE` | 2 | P100 | Can decode (generate tokens) |
| `WORKER_MIXED` | 3 | RTX | Both prefill + decode |

For mix-precision: RTX prefills with nano IQ2 (fast, 110 tok/s), P100 decodes with
balanced Q5K (high quality, 23 tok/s). RTX falls back to decode only when P100 is busy.

---

## KV Cache Flow

```
                         RTX (prefill)            P100 (decode)
                              │                       │
  Request ► [nano] ◄─────────►│                       │
            prefill ◄─────────►│                       │
            save KV ──────────►│                       │
                              ││                       │
                              ││  Store (tmpfs)        │
                              ││  ┌──────────┐         │
                              │└─►│  63 MiB  │◄────────┘│ restore KV
                              └──►│  351 MiB │          │ [balanced]
                                  └──────────┘          │ decode
                                                        │ 800 tokens
                                                        │
                              ┌─────────────────────────┘
                              │  background save (73 MiB)
                              ▼
                           Store
```

Prefix checkpoints (system prompt KV) are cached in Store and restored on cold
requests to avoid re-prefilling the system prompt.

---

## RPC Protocol

Binary wire format defined in `specs/rpc-protocol.md`. Key opcodes used:

| OpCode | Value | Direction | Purpose |
|--------|-------|-----------|---------|
| `SaveStateChunked` | 0x26 | C → A | Save KV from Agent to Store |
| `RestoreStateChunked` | 0x27 | C → A | Restore KV from Store to Agent |
| `SlotErase` | 0x23 | C → A | Clear a GPU slot |
| `NodeHealth` | 0x24 | C ⇄ A | Health probe |
| `PutMeta` | 0x14 | C → S | Store session metadata |
| `Stat` | 0x04 | C → S | Store health probe |

C = Coordinator, A = Agent, S = Store

---

## Testing

```bash
# Unit tests (no live services needed)
pytest tests/coordinator/ -x -q

# System tests (requires live stack)
pytest tests/system/ -x -q
```

### Test Coverage

| Module | Tests | Focus |
|--------|-------|-------|
| `session_table.py` | 9 | CRUD, eviction, LRU selection |
| `worker_tracker.py` | 23 | Acquire/release, errors, expiry |
| `health.py` | 13 | Polling, stuck-slot watchdog |
| `state_manager.py` | 5 | Save/restore/migrate flows |
| `routing.py` | 14 | Worker selection, estimation |
| `warm_slot.py` | 11 | Slot verification criteria |
| `scheduler.py` | 9 | Queue, dispatch, routing |
| `proxy.py` | 3 | HTTP proxy |
| `router.py` | 15 | HTTP endpoints |
| **Total** | **99** | |

---

## Running

```bash
# From source (development)
HYDRA_COORD_WORKERS='[...]' python -m coordinator.main

# Containerized
podman run --network host --env-file=coordinator.env localhost/hydra-coordinator:latest
```
