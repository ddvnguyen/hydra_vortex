# Hydra RPC Protocol Specification

## Overview
Binary TCP protocol for all inter-service communication in Hydra.
Designed for high-throughput transfer of large binary payloads (800 MB KV states).

## Wire Format

### Request Header (16 bytes)
```
Offset  Size  Type     Field
0       2     bytes    magic = 0x4859 ("HY")
2       1     uint8    op — operation code
3       1     uint8    flags — 0x00=normal, 0x01=compressed
4       2     uint16   key_len — length of key string (LE)
6       8     uint64   payload_len — length of payload bytes (LE)
14      2     uint16   trace_len — length of trace_id string (LE)

Total: 16 bytes
```

### Request Body
```
Offset        Size       Field
0             key_len    key — UTF-8 string
key_len       trace_len  trace_id — UTF-8 string (for logging correlation)
key+trace     payload_len payload — raw bytes (streamed, not buffered)
```

### Response Header (12 bytes)
```
Offset  Size  Type     Field
0       1     uint8    status — 0x00=OK, 0x01=NOT_FOUND, 0x02=ERROR, 0x03=PARTIAL
1       3     uint24   meta_len — length of JSON metadata (LE)
4       8     uint64   payload_len — length of payload bytes (LE)

Total: 12 bytes
```

### Response Body
```
Offset        Size         Field
0             meta_len     meta — UTF-8 JSON (operation-specific metadata)
meta_len      payload_len  payload — raw bytes (streamed)
```

## Operation Codes

### Store Operations
```
0x01  PUT        Store raw payload under key
0x02  GET        Retrieve raw payload by key (sendfile from tmpfs)
0x03  DEL        Delete key
0x04  STAT       Get metadata (size, exists) — no payload transfer
0x05  LIST       List keys matching prefix (prefix in key field)
```

### Store Chunked Operations [M2]
```
0x10  PUT_CHUNKED   Store with content-addressable chunking (1 MB chunks, SHA-256)
                    Request:  key="kv/{session_id}", payload=raw KV stream
                    Response: meta={"stored":true,"total_chunks":<N>,"deduped_chunks":<N>}
0x11  GET_CHUNKED   Retrieve missing chunks only (client sends known hash list)
                    Request:  key="kv/{session_id}", payload=JSON ["<hash>",...]
                    Response: meta={"total_size":<N>,"missing_count":<N>}
                              payload=[4B index][4B size][chunk data]... (missing chunks only)
0x12  SYNC_MISSING  Delta-save step 1: of the hashes the client intends to store, return
                    the subset the global chunk index does NOT already have.
                    Request:  key="kv/{session_id}", payload=JSON ["<hash>",...] (full ordered set)
                    Response: meta={"missing_count":<N>,"candidate_count":<N>}
                              payload=JSON {"missing_hashes":["<hash>",...]}
0x13  PUSH_CHUNKS   Delta-save step 2: upload only the missing chunk bodies. Pure blob
                    writes (content-addressed dedup); does NOT touch the manifest.
                    Request:  key="kv/{session_id}", payload=[4B size LE][body]...
                    Response: meta={"stored":<N>,"received":<N>}
0x15  PUT_MANIFEST  Delta-save step 3: write the authoritative ordered manifest. Refuses
                    (status PARTIAL) if any referenced chunk is not resident.
                    Request:  key="kv/{session_id}",
                              payload=JSON {"n_past":N,"total_size":T,"chunks":[{"index":i,"hash":h,"size":s},...]}
                    Response: meta={"written":true,"chunks":<N>,"n_past":<N>}
0x14  PUT_META      Store n_past metadata before chunk manifest exists
                    Request:  key="kv/{session_id}", payload={"n_past":<N>}
                    Response: meta={"stored":true}
0x33  GET_MANIFEST  Retrieve chunk manifest (chunk list + n_past) for a key
                    Request:  key="kv/{session_id}", payload_len=0
                    Response: meta={} payload={"n_past":<N>,"chunks":[{"index":<N>,"hash":"<hex>","size":<N>},...]}
```

### Agent Operations (retired — Agent merged into Hydra.Core as of PR #203)
These opcodes were used for Coordinator↔Agent RPC. Since the Agent was merged into
Hydra.Core as a single C# binary, these opcodes are no longer used on the wire.
KV state save/restore is now orchestrated internally within Hydra.Core via direct
llama-server RPC (0x30–0x32) + Store RPC (0x01–0x15).

```
0x20  (retired)  Was SAVE_STATE
0x21  (retired)  Was RESTORE_STATE
0x22  (retired)  Was SLOT_STATUS
0x23  (retired)  Was SLOT_ERASE — slot erase now handled via llama HTTP DELETE /slots/{id}
0x24  (retired)  Was NODE_HEALTH — health now checked via llama HTTP GET /health
0x25  (retired)  Was COMPLETION. Completions go Hydra.Core→llama-server over HTTP,
                 never through the Agent. Opcode removed from enums.
0x26  (retired)  Was SAVE_STATE_CHUNKED
0x27  (retired)  Was RESTORE_STATE_CHUNKED
```

### llama-server Direct Operations (via --rpc-port, active)
Hydra.Core calls llama-server directly for KV state transfer via the llama RPC port
(RTX :9503, P100 :9502).
key = slot_id as ASCII decimal string (e.g. "0").
llama-server knows nothing about Store or sessions — it only manages its own slots.

```
0x30  STATE_GET   Stream full KV state out as response payload
                  Request:  key="<slot_id>", payload_len=0
                  Response: meta={"n_past":<N>,"state_size":<N>}
                            payload=<raw KV bytes, ~800 MB>

0x31  STATE_PUT   Restore KV state from request payload
                  Request:  key="<slot_id>", payload=<raw KV bytes>
                  Response: meta={"restored":true,"bytes":<N>}
                            payload_len=0

0x32  STATE_META  Slot metadata only — no KV serialization (cheap)
                  Request:  key="<slot_id>", payload_len=0
                  Response: meta={"slot_id":<N>,"n_past":<N>,
                                  "state_size":<N>,"is_processing":<bool>}
                            payload_len=0
```

**n_past definition:** `n_prompt_tokens_cache + n_decoded` (prompt tokens in KV cache
plus tokens generated in this session). The next completion request sent to this slot
MUST have `n_tokens > n_past` or the KV cache will be invalidated.

### llama-engine HTTP Endpoints (via --port, active)
llama-engine exposes HTTP endpoints for easier testing and interaction with Hydra.Core.
These endpoints use standard HTTP/1.1 with Server-Sent Events (SSE) for streaming.
Default port: 8080 (configurable via `--port`).

```
GET  /health                    Liveness check
                                Response: {"status":"ok"}

GET  /version                   Engine version information
                                Response: {"version":"E1","engine":"llama-engine"}

GET  /slots                     List all slot states
                                Response: [] (currently returns empty array)

GET  /slots/:id/state/meta      Get slot metadata (n_past, state_size)
                                Response: {"slot_id":<N>,"n_past":<N>,"state_size":<N>}

POST /v1/chat/completions       OpenAI-compatible chat completion API
                                Request body: OpenAI chat completion format
                                Response: OpenAI chat completion format
                                Streaming: Server-Sent Events (SSE) when stream=true
```

**Why HTTP/1.1 + SSE instead of HTTP/3?**
- HTTP/3 (QUIC) requires massive C++ dependencies (nghttp3, quiche, boringssl)
- Contradicts our minimal-dependency principle
- HTTP/1.1 + SSE provides good streaming that's easy to test with curl
- Can add HTTP/2/3 later if needed without breaking changes

**Testing with curl:**
```bash
# Health check
curl http://localhost:8080/health

# Version info
curl http://localhost:8080/version

# Slot metadata
curl http://localhost:8080/slots/0/state/meta

# Chat completion (non-streaming)
curl -X POST http://localhost:8080/v1/chat/completions \
  -H "Content-Type: application/json" \
  -d '{"messages":[{"role":"user","content":"Hello"}]}'

# Chat completion (streaming with SSE)
curl -X POST http://localhost:8080/v1/chat/completions \
  -H "Content-Type: application/json" \
  -d '{"messages":[{"role":"user","content":"Hello"}],"stream":true}'
```

## Status Codes
```
0x00  OK          Operation succeeded
0x01  NOT_FOUND   Key or session not found
0x02  ERROR       Operation failed (error detail in meta JSON)
0x03  PARTIAL     Partial result (used in chunked operations)
0x04  BUSY        Slot or node is busy, retry later
```

## Streaming Behavior
- Payloads > 1 MB MUST be streamed in chunks (default 256 KB read/write buffer)
- Server MUST NOT buffer entire payload in memory before processing
- For GET: server uses sendfile() when payload is a file on tmpfs
- For PUT: server writes directly to tmpfs file while reading from socket

## Connection Lifecycle
- Persistent connections: client opens once, sends multiple requests
- Server reads requests in a loop until client disconnects
- No request pipelining: wait for response before sending next request

## Error Handling
- If magic bytes don't match: close connection immediately
- If payload_len exceeds configured max: respond with ERROR, close
- Network errors: client retries with exponential backoff (3 attempts)
