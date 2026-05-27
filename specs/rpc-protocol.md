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
0x10  PUT_CHUNKED   Store with content-addressable chunking
0x11  GET_CHUNKED   Retrieve, sending only chunks client lacks
0x12  SYNC_PLAN     Client sends hashes, server returns missing list
0x13  PUSH_CHUNKS   Client sends batch of new chunks
```

### Agent Operations
```
0x20  SAVE_STATE      Save slot KV state → push to Store
0x21  RESTORE_STATE   Pull from Store → restore to slot
0x22  SLOT_STATUS     Get slot metadata (n_past, is_processing)
0x23  SLOT_ERASE      Erase a slot (free VRAM)
0x24  NODE_HEALTH     Get llama-server health + slot summary
0x25  COMPLETION      Proxy a chat completion request
```

### llama-server Direct Operations  (via --rpc-port, implemented in M0)
Agent calls llama-server directly for KV state transfer.
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
