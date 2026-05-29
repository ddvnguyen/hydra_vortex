---
milestone: M1
reviewer: claude-sonnet-4-6
date: 2026-05-28
status: open
---

## Summary

M1 covers the Coordinator: session table, routing, health monitor, state manager, and HTTP
layer. The architecture is correct — routing priority (affinity → store_restore → long_prompt
→ least_loaded) matches the spec. The n_past guard is implemented. Several correctness bugs:
slot 0 collision for all new sessions, LRU eviction can pick already-evicted sessions, n_past
guard doesn't erase the GPU slot it is guarding against. Restore routing always picks the first
healthy node ignoring load.

## Findings

---

### [M1-P1-001] All new sessions registered with slot_id=0
**File:** `src/coordinator/router.py:94–96`
**Status:** open
**Issue:** #29
**Assigned:** —

```python
if not decision.session_found:
    session_table.register(sess_id, decision.node_name, decision.slot_id or 0)
```

`decision.slot_id` is `None` for new sessions (routing doesn't know which slot llama assigned).
The `or 0` fallback means every new session on a node is recorded as occupying slot 0. If two
sessions are active on the same node, affinity routing sends both to slot 0, and a `SaveState`
or `SlotErase` targeting slot 0 will hit the wrong session.

**Fix:** Slot assignment must come from the Agent after the first completion. Either (a) add a
response header from llama-server that reports the assigned slot, or (b) issue a `SlotStatus`
RPC to the Agent after routing and update the session table with the actual slot.

---

### [M1-P1-002] n_past guard resets n_past but does not erase the GPU slot
**File:** `src/coordinator/router.py:103–112`
**Status:** resolved
**Issue:** #29

**Fix applied:** Router now sends `SlotErase` to the agent after resetting n_past to 0, ensuring llama clears the stale KV state on the GPU.

---

### [M1-P1-003] get_lru_session can return already-evicted sessions
**File:** `src/coordinator/session_table.py:56–60`
**Status:** resolved
**Issue:** #29

**Fix applied:** Added `if s.slot_id is not None` filter in `evict_lru`:
```python
sessions = [s for s in self.get_sessions_on_node(node_name) if s.slot_id is not None]
```

---

### [M1-P1-004] save_session sends only session_id to Agent — slot unknown
**File:** `src/coordinator/state_manager.py:28–29`
**Status:** resolved
**Issue:** #29

**Fix applied:** Now passes `{session_id}:{slot_id}` as the key instead of just `session_id`. The Agent parses both in `HandleSaveStateAsync` and `HandleRestoreStateAsync`.

---

### [M1-P2-001] store_restore routing ignores node load
**File:** `src/coordinator/routing.py:86–98`
**Status:** resolved
**Issue:** #30

**Fix applied:** `routing.py` now sorts `rtx_nodes` by `load()` before picking the restore target, consistent with least_loaded routing.

---

### [M1-P2-002] rtux_nodes typo — variable is misspelled
**File:** `src/coordinator/routing.py:66`
**Status:** open
**Issue:** #30
**Assigned:** —

```python
rtux_nodes = { ... if info.get("gpu_type") == "rtx5060ti" }
```

Variable name `rtux_nodes` has a spurious `u`. Works because it's used consistently within the
function, but misleads readers.

**Fix:** Rename to `rtx_nodes`.

---

### [M1-P2-003] Health monitor startup race — nodes marked unhealthy until first poll
**File:** `src/coordinator/health.py:45–67`
**Status:** open
**Issue:** #30
**Assigned:** —

`NodeInfo` initializes with `healthy=False`. `start()` creates the background poll task but
doesn't `await` the first poll. Between `start()` and the first `_poll_all()` completing (which
could take several seconds if agents are slow), `get_health_summary()` reports all nodes
unhealthy, and any request arriving in that window gets a 503.

**Fix:** In `start()`, await the first poll before returning:
```python
async def start(self):
    await self._poll_all()         # ensure first-pass health is populated
    self._task = asyncio.create_task(self._poll_loop())
```

---

### [M1-P2-004] proxy.py creates a new httpx.AsyncClient per request
**File:** `src/coordinator/proxy.py:9–56`
**Status:** open
**Issue:** #30
**Assigned:** —

Both `proxy_completion` and `proxy_completion_stream` use `async with httpx.AsyncClient() as client:`.
A new client per request means no connection pooling to llama-servers. Under any sustained
load, every request opens and tears down a TCP connection.

**Fix:** Instantiate one `httpx.AsyncClient` per node at app startup (in `app.py`) and pass it
to the proxy functions.
