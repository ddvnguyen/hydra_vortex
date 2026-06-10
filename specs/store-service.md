# Store Service Specification

## Identity
- Name: hydra-core (Store subsystem; previously standalone service hydra-store, now
  embedded in the Hydra.Core C# binary as of PR #203)
- Transport: Hydra binary RPC on TCP :9500
- Backend: tmpfs directory (default: /mnt/llm-ram/store/)
- Role: Central data store for KV cache states

## MVP Operations (M0)

### PUT (0x01)
- Key: string identifier (e.g., "kv/sess_abc")
- Payload: raw binary data
- Behavior: write payload to `{store_dir}/raw/{key}` (create parent dirs)
- Response meta: `{"size": <bytes_written>}`
- Errors: disk full → ERROR

### GET (0x02)
- Key: string identifier
- Behavior: read from `{store_dir}/raw/{key}`, send via sendfile()
- Response meta: `{"size": <file_size>}`
- Errors: file not found → NOT_FOUND

### DEL (0x03)
- Key: string identifier
- Behavior: delete `{store_dir}/raw/{key}`
- Response meta: `{"deleted": true}`
- Errors: not found → NOT_FOUND (but still return OK)

### STAT (0x04)
- Key: string identifier
- Behavior: stat file, return metadata without reading content
- Response meta: `{"exists": bool, "size": int, "modified_at": float}`
- No payload in response

### LIST (0x05)
- Key: prefix string (e.g., "kv/")
- Behavior: list files matching prefix
- Response meta: `{"keys": ["kv/sess_abc", "kv/sess_def"], "count": 2}`

## M2 Operations (Chunked Dedup)

### PUT_CHUNKED (0x10)
### GET_CHUNKED (0x11)
### SYNC_PLAN (0x12)
### PUSH_CHUNKS (0x13)
See rpc-protocol.md for details. Not implemented in M0/M1.

## Storage Layout

```
/mnt/llm-ram/store/
├── raw/                    ← M0: raw key-value files
│   └── kv/
│       ├── sess_abc.bin
│       └── sess_def.bin
├── chunks/                 ← M2: content-addressed 1MB chunks
│   ├── a3f7e2...
│   └── b8c4d1...
├── manifests/              ← M2: session chunk manifests
│   └── sess_abc.json
└── metadata.db             ← M3: SQLite session index
```

## Configuration

```python
class StoreConfig(BaseSettings):
    host: str = "0.0.0.0"
    port: int = 9500
    store_dir: Path = Path("/mnt/llm-ram/store")
    max_payload_bytes: int = 4 * 1024**3          # 4 GB max per PUT
    read_buffer_size: int = 256 * 1024             # 256 KB streaming buffer
    debug_http_port: int = 9501                    # /debug endpoint

    class Config:
        env_prefix = "HYDRA_STORE_"
```

## Health / Debug
HTTP GET :9501/debug returns:
```json
{
  "status": "ok",
  "store_dir": "/mnt/llm-ram/store",
  "raw_files": 5,
  "raw_total_mb": 3200,
  "tmpfs_usage_pct": 42,
  "uptime_s": 3600,
  "ops": {"put": 12, "get": 8, "del": 2}
}
```
