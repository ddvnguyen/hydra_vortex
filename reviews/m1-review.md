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
**Status:** open
**Issue:** #29
**Assigned:** —

```python
if estimated <= decision.n_past:
    session_table.update_n_past(sess_id, 0)
    log.warning("n_past_guard_triggered", ...)
```

Resetting n_past to 0 in the session table prevents the guard from triggering again, but the
GPU slot still holds stale KV state. The next request routes via affinity to the same node/slot,
and llama will compare the request's `n_tokens` against the stale `n_past` in the slot — if
stale n_past is still > 0 at the llama level, the cache gets nuked silently.

**Fix:** When the guard triggers, also send `SlotErase` to the agent for the affected slot so
llama clears the KV state.

---

### [M1-P1-003] get_lru_session can return already-evicted sessions
**File:** `src/coordinator/session_table.py:56–60`
**Status:** open
**Issue:** #29
**Assigned:** —

```python
def get_lru_session(self, node_name: str) -> Optional[SessionEntry]:
    sessions = self.get_sessions_on_node(node_name)
    return min(sessions, key=lambda s: s.last_used)
```

`get_sessions_on_node` returns all sessions including those with `slot_id=None` (already
evicted to store). `evict_lru` then tries to save an already-saved session and sends
`SlotErase` with `sid=""`, which the Agent rejects with an error. Since `evict_lru` doesn't
catch this, the eviction call propagates an `RpcError`.

**Fix:** Add `if s.slot_id is not None` filter in `get_lru_session`:
```python
sessions = [s for s in self.get_sessions_on_node(node_name) if s.slot_id is not None]
```

---

### [M1-P1-004] save_session sends only session_id to Agent — slot unknown
**File:** `src/coordinator/state_manager.py:28–29`
**Status:** open
**Issue:** #29
**Assigned:** —

```python
resp = await client.request(OpCode.SaveState, session_id, trace_id=trace_id)
```

The Agent's `HandleSaveStateAsync` parses the key with `int.TryParse` to find the slot. A
session ID like `sess_abc123` won't parse, so the Agent calls `FindIdleSlotAsync()` and picks
any idle slot — not necessarily the one holding this session's KV state. This means save will
capture the wrong slot's state.

**Fix:** Pass `{session_id}:{slot_id}` as the key. The Agent already parses `:` separators in
`HandleRestoreStateAsync`; add the same to `HandleSaveStateAsync`.

---

### [M1-P2-001] store_restore routing ignores node load
**File:** `src/coordinator/routing.py:86–98`
**Status:** open
**Issue:** #30
**Assigned:** —

```python
if entry.has_store_state:
    target = next(
        (n for n in nodes if n.name in healthy_nodes), None
    )
```

Always picks the first healthy node in config order (rtx, then p100). Under load, restored
sessions pile onto RTX regardless of slot availability.

**Fix:** Sort `healthy_nodes` by load (same `load()` function used for least_loaded routing)
before picking the restore target.

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
