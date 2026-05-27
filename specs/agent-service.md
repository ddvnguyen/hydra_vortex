# Agent Service Specification

## Identity
- Name: hydra-agent-{node_name} (e.g., hydra-agent-rtx, hydra-agent-p100)
- Transport: Hydra binary RPC on TCP :9601 (RTX) / :9602 (P100)
- Role: Sidecar on each GPU node. Bridges Hydra RPC ↔ local llama-server HTTP.

## Dependencies
- Local llama-server instance (HTTP, localhost)
- Hydra Store (RPC, remote)

## Operations

### SAVE_STATE (0x20)
- Key: session_id
- Payload: none
- Behavior:
  1. Call llama-server: POST /slots/{slot_id}?action=save {"filename": session_id}
  2. Read saved file from local --slot-save-path
  3. Connect to Hydra Store, PUT "kv/{session_id}" with file contents
  4. Delete local file (optional, keep as cache)
- Response meta: `{"session_id": str, "n_past": int, "size": int, "store_ms": int}`
- Errors: slot busy → BUSY, save failed → ERROR

### RESTORE_STATE (0x21)
- Key: session_id
- Payload: none (or slot_id in meta)
- Behavior:
  1. Connect to Hydra Store, GET "kv/{session_id}"
  2. Write to local --slot-save-path as {session_id}.bin
  3. Call llama-server: POST /slots/{slot_id}?action=restore {"filename": session_id}
  4. Delete local file (optional)
- Response meta: `{"session_id": str, "slot_id": int, "n_past": int, "restore_ms": int}`
- Errors: not found in store → NOT_FOUND, restore failed → ERROR

### SLOT_STATUS (0x22)
- Key: none or slot_id as string
- Behavior: GET llama-server /slots, parse response
- Response meta:
```json
{
  "slots": [
    {"id": 0, "n_past": 2968, "is_processing": false},
    {"id": 1, "n_past": 0, "is_processing": false}
  ]
}
```

### SLOT_ERASE (0x23)
- Key: slot_id as string
- Behavior: POST /slots/{slot_id}?action=erase
- Response meta: `{"slot_id": int, "erased": true}`

### NODE_HEALTH (0x24)
- Key: none
- Behavior: GET llama-server /health + /slots + /metrics
- Response meta:
```json
{
  "healthy": true,
  "node_name": "rtx",
  "slots_total": 2,
  "slots_idle": 1,
  "gpu_type": "rtx5060ti",
  "llama_url": "http://localhost:8080"
}
```

### COMPLETION (0x25) [M1]
- Key: none
- Payload: JSON-encoded ChatCompletionRequest
- Behavior: proxy to llama-server POST /v1/chat/completions
- Response payload: JSON-encoded response (or SSE stream)
- This avoids coordinator needing direct HTTP to llama-server

## Configuration

```python
class AgentConfig(BaseSettings):
    host: str = "0.0.0.0"
    port: int = 9601
    node_name: str = "rtx"
    llama_url: str = "http://localhost:8080"
    store_host: str = "127.0.0.1"
    store_port: int = 9500
    slot_save_path: Path = Path("/tmp/hydra-kv")
    debug_http_port: int = 9611

    class Config:
        env_prefix = "HYDRA_AGENT_"
```

## Health / Debug
HTTP GET :9611/debug returns:
```json
{
  "status": "ok",
  "node_name": "rtx",
  "llama_healthy": true,
  "slots": [{"id": 0, "n_past": 2968}, {"id": 1, "n_past": 0}],
  "pending_ops": 0,
  "local_kv_files": 2,
  "store_connected": true,
  "uptime_s": 3600
}
```
