# Milestone 1 — Core System

## Goal
Coordinator routes OpenAI-compatible requests to GPU nodes via RPC.
Sessions stick to nodes. State migration works automatically.
Full structured logging with trace_id across all 3 services.

## What "Done" Means
```
✅ curl localhost:9000/v1/chat/completions → streams response from correct GPU
✅ Multi-turn conversation stays on same node (cache_n > 0 on subsequent turns)
✅ Long prompt (>4K tokens) routes to RTX
✅ Session migration: RTX → Store → P100 when RTX slots full
✅ /health shows all component status
✅ /status shows sessions, routing stats
✅ All requests traceable by trace_id across coordinator, agent, store logs
✅ Works with Cline and OpenWebUI
```

## Prerequisites
- M0 complete and all tests passing

---

## Task M1.1: Coordinator Routing

### M1.1.1: Session Table (`coordinator/session_table.py`)
- In-memory dict: session_id → SessionEntry
- `lookup(session_id) → SessionEntry | None`
- `register(session_id, node_name, slot_id, n_past)`
- `update_last_used(session_id)`
- `mark_evicted(session_id)` — set slot_id=None, has_store_state=True
- `get_sessions_on_node(node_name) → list`
- `get_lru_session(node_name) → SessionEntry | None`
- `remove(session_id)`
- Thread-safe via asyncio (single event loop, no lock needed)
- **Lines:** ~60
- **Test:** `tests/coordinator/test_session_table.py`
  - Register + lookup round-trip
  - LRU ordering correct after updates
  - Mark evicted: slot_id becomes None
  - Get sessions on node filters correctly
- **Done when:** all tests pass

### M1.1.2: Routing Logic (`coordinator/routing.py`)
- `derive_session_id(request) → str` — hash system_prompt + first user message
- `estimate_tokens(request) → int` — sum message content lengths / chars_per_token
- `route_request(request, session_table, health_info) → RoutingDecision`
  - Priority 1: session affinity (warm cache on a healthy node)
  - Priority 2: store restore (evicted session, state on disk)
  - Priority 3: long prompt → RTX
  - Priority 4: least loaded node
  - Fallback: any healthy node
- **Lines:** ~80
- **Test:** `tests/coordinator/test_routing.py`
  - Affinity hit → returns same node
  - Evicted session with store state → returns store_restore
  - Long prompt → RTX
  - Short prompt, RTX busy → P100
  - Both nodes down → error
  - n_tokens ≤ n_past guard → triggers slot erase
- **Done when:** all routing scenarios tested

### M1.1.3: Health Monitor (`coordinator/health.py`)
- Background task polling each Agent via RPC NODE_HEALTH every 10s
- Tracks consecutive failures → marks unhealthy after 3
- Caches slot info for routing decisions
- `is_healthy(node) → bool`
- `get_node_info(node) → NodeInfo`
- **Lines:** ~60
- **Test:** `tests/coordinator/test_health.py`
  - Healthy after successful poll
  - Unhealthy after 3 failures
  - Recovery: healthy again after successful poll
- **Done when:** all tests pass

---

## Task M1.2: State Migration via RPC

### M1.2.1: State Manager (`coordinator/state_manager.py`)
- Uses RPC clients to Agent(s) and Store
- `save_session(session_id, node_name)` — RPC SAVE_STATE to agent
- `restore_session(session_id, target_node) → slot_id` — RPC RESTORE_STATE
- `migrate_session(session_id, from_node, to_node)`
  1. SAVE_STATE on from_node agent
  2. SLOT_ERASE on from_node agent
  3. RESTORE_STATE on to_node agent
  4. Update session_table
- `evict_lru(node_name)` — save LRU session, erase slot, return freed slot_id
- **Lines:** ~100
- **Test:** `tests/coordinator/test_state_manager.py`
  - Mock RPC clients
  - Save flow: verify RPC calls in correct order
  - Restore flow: verify RPC calls + session table update
  - Migrate: save + erase + restore + table update
  - Evict: picks LRU, saves, erases
- **Done when:** all tests pass

---

## Task M1.3: HTTP Layer

### M1.3.1: Streaming Proxy (`coordinator/proxy.py`)
- `async proxy_completion(node_url, request, trace_id)` → for non-streaming
- `async proxy_completion_stream(node_url, request, trace_id)` → async generator of SSE bytes
- Uses httpx to forward to llama-server directly (via Agent COMPLETION or direct HTTP)
- Adds `hydra` metadata to non-streaming responses
- **Lines:** ~60
- **Test:** mock httpx, verify SSE lines forwarded correctly
- **Done when:** streaming and non-streaming proxy verified

### M1.3.2: Route Handlers (`coordinator/router.py`)
```
POST /v1/chat/completions  → route + proxy + record session
GET  /health               → aggregated health from all agents + store
GET  /status               → sessions, routing stats, node details
GET  /sessions             → list all active sessions
DELETE /sessions/{id}      → evict session
POST /sessions/{id}/migrate → force migration
```
- **Lines:** ~80
- **Test:** `tests/coordinator/test_router.py` (FastAPI TestClient)
  - POST completion → response has correct format
  - GET health → shows all nodes
  - GET status → shows session count + routing stats
- **Done when:** all endpoints return correct responses

### M1.3.3: App Factory (`coordinator/app.py`, `coordinator/main.py`)
- FastAPI app with startup/shutdown hooks
- On startup: create RPC clients to agents + store, start health monitor
- On shutdown: close connections, stop monitor
- **Lines:** ~40
- **Done when:** `python -m coordinator.main` starts and serves requests

---

## Task M1.4: Integration + E2E

### M1.4.1: Coordinator ↔ Agent Integration (`tests/integration/test_coordinator_agent.py`)
- Start Store, 2 Agents (mocked llama), Coordinator
- Send completion → routed to correct agent
- Send second turn → same agent (affinity)
- Simulate RTX full → migration to P100

### M1.4.2: Full E2E (`tests/e2e/test_e2e.py` extended)
- Start all services + real llama-servers
- Multi-turn conversation through coordinator
- Verify cache_n > 0 on turns 2+
- Force migration via /sessions/{id}/migrate
- Verify continuation works on new node after migration

### M1.4.3: Client Compatibility
- Test with curl (streaming + non-streaming)
- Test with OpenWebUI (configure as OpenAI endpoint http://host:9000)
- Test with Cline (configure as custom endpoint)
- **Done when:** all 3 clients can chat through coordinator

---

## Task Summary

| Task    | Component   | Lines | Test                         | Depends On  |
|---------|-------------|-------|------------------------------|-------------|
| M1.1.1  | coordinator | 60    | test_session_table.py        | M0          |
| M1.1.2  | coordinator | 80    | test_routing.py              | M1.1.1      |
| M1.1.3  | coordinator | 60    | test_health.py               | M0.1.3      |
| M1.2.1  | coordinator | 100   | test_state_manager.py        | M1.1.1, M0  |
| M1.3.1  | coordinator | 60    | test_proxy.py                | —           |
| M1.3.2  | coordinator | 80    | test_router.py               | M1.1-M1.3.1 |
| M1.3.3  | coordinator | 40    | manual                       | all above   |
| M1.4.1  | integration | 80    | test_coordinator_agent.py    | all above   |
| M1.4.2  | e2e         | 80    | test_e2e.py                  | all above   |
| M1.4.3  | e2e         | —     | manual with 3 clients        | all above   |

**Parallel work:** M1.1 (routing) and M1.2 (state mgmt) can be built in parallel.
M1.3 depends on both. M1.4 integrates everything.
