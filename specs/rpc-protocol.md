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
                  Response: meta={"n_past":<N>,"state_size":<N>,
                                  "model_alias":"<alias or filename>",
                                  "model_hash":"<64-char hex SHA-256 of GGUF>",
                                  "model_path":"<absolute path to GGUF>"}
                            payload=<raw KV bytes, ~800 MB>
                  M-Perf.9 #289: model identity is included in the response so
                  the Coordinator can record which model built the KV.

0x31  STATE_PUT   Restore KV state from request payload
                  Request:  key="<slot_id>", payload=<raw KV bytes>
                  Response: meta={"restored":true,"bytes":<N>}
                            payload_len=0

0x32  STATE_META  Slot metadata only — no KV serialization (cheap)
                  Request:  key="<slot_id>", payload_len=0
                  Response: meta={"slot_id":<N>,"n_past":<N>,
                                  "state_size":<N>,"is_processing":<bool>,
                                  "model_alias":"<alias or filename>",
                                  "model_hash":"<64-char hex SHA-256 of GGUF>",
                                  "model_path":"<absolute path to GGUF>"}
                            payload_len=0
                  M-Perf.9 #289: model_alias / model_hash / model_path are
                  added in #289. Pre-feature servers return the response
                  without these fields (back-compat).
```

**n_past definition:** `n_prompt_tokens_cache + n_decoded` (prompt tokens in KV cache
plus tokens generated in this session). The next completion request sent to this slot
MUST have `n_tokens > n_past` or the KV cache will be invalidated.

### llama-engine Control Operations (via --rpc-port, active)
Hydra.Core drives the `llama-engine` binary entirely over binary RPC. These opcodes
control prefill, decode, and state transfer end-to-end without falling through to HTTP.
The engine **only** owns its own slots and KV cache; Store/Session routing is the
Coordinator's responsibility (Coordinator calls Store RPC, then engine RPC). The
opcodes live in the `0x40-0x46` range so the `0x33-0x3F` range stays free for future
Coordinator↔Store extensions (currently `GET_MANIFEST = 0x33`).

key = slot_id as ASCII decimal string (e.g. `"0"`).

```
0x40  CONFIGURE         Set engine params (n_predict, sampler, batch size, ctx, …)
                        Request:  key="<slot_id>", payload=JSON {"n_predict":N,"temperature":F,
                                       "top_p":F,"top_k":N,"seed":N,"batch":N,"ctx":N,...}
                        Response: meta={"applied":true,"params":{...echo...}}
                                  payload_len=0

0x41  INFO              Report engine capabilities + two-engine status
                        Request:  key="<slot_id>", payload_len=0
                        Response: meta={"engine":"llama-server-hydra","version":"E1",
                                        "capabilities":["prefill","decode","state_transfer",
                                                         "expert_mode","quant_swap","preset","model_hash"],
                                        "preset_aliases":["<alias>",...],
                                        "solo_active":<bool>,
                                        "rpc_backend_active":<bool>,
                                        "mode":"solo|combined",
                                        "peer_addr":"<host:port, empty if not configured>",
                                        "peer_reachable":<bool>,
                                        "layer_split":"<combined tensor-name regex, empty if not configured>",
                                        "combined_head_attached":<bool>,
                                        "pipeline_capable":false}
                                  payload_len=0
                        Note (issue #348): these are independent capability booleans, replacing
                        the old single `role` string and the aliased `peer_connected`/`combined_capable`
                        pair. `solo_active` is always true — every engine now loads its model and
                        serves its own prefill/decode. `rpc_backend_active` is true when launched with
                        `--ggml-rpc-port` (this engine also exposes its local backend(s) as an embedded
                        ggml-RPC peer). `peer_reachable` is the startup TCP probe of `peer_addr`;
                        `combined_head_attached` reflects whether this engine successfully dual-loaded
                        expert tensors onto that peer at startup — NOT a live per-call health check.
                        `pipeline_capable` stays false until #287's PIPELINE half lands. These fields
                        are advisory/observability only: the Coordinator's `EngineInfo` DTO binds just
                        engine/version/capabilities/preset_aliases and ignores the rest.

0x42  PREFILL           Run prefill only (n_predict=0), return n_past and state blob inline
                        Request:  key="<slot_id>", payload=JSON request body
                                  (OpenAI chat-completions schema — messages / prompt / images)
                                  M-Perf.9 #289: optional `"model":"<alias>"` field. Absent or
                                  empty → no swap, prefill on resident model. Present +
                                  alias known to the engine → swap to that model first.
                                  Present + alias unknown → fall back to resident model
                                  and set `model_fallback:true` in the response.
                        Response: meta={"n_past":<N>,"tokens_processed":<N>,"prefill_ms":<N>,
                                         "model_alias":"<alias or filename>",
                                         "model_hash":"<64-char hex SHA-256 of GGUF>",
                                         "model_path":"<absolute path to GGUF>",
                                         "model_fallback":<bool>}
                                  payload=raw KV state bytes (sized as `payload_len`; ~800 MB at 60-80K ctx)
                        Note: caller is responsible for keeping `n_tokens > n_past` on the
                              next completion — otherwise the KV cache is invalidated (see
                              CLAUDE.md "Critical Facts").

0x43  DECODE            Run decode with streaming token output
                        Request:  key="<slot_id>", payload=JSON {"n_predict":N,"messages":[...]}
                        Response: meta={"tokens_generated":<N>,"n_past":<N>,"stop_reason":"<eos|length|n_tokens_limit>"}
                                  payload=streamed frames of [4B token_id][4B logprob][1B flags]
                                          (flags: 0x01=final; remaining bits reserved)
                        Streaming model: single RPC response, server writes payload
                                         incrementally as tokens are generated. Client
                                         reads via `RequestStreamAsync` (chunked reader
                                         in `RpcClient.cs`). `payload_len` in the
                                         response header is set to the total expected
                                         size; partial writes are flushed per token.

0x44  SET_EXPERT_MODE   Switch expert mode (solo / combined) — implemented, issue #287/#260
                        Request:  key="<slot_id>", payload="solo" | "combined"
                        Response: meta={"success":<bool>,"mode":"solo|combined"}
                                  payload_len=0
                        `mode` is the ACTUAL mode now in effect, which can be "solo" even
                        when "combined" was requested — the engine only honors "combined"
                        when it dual-loaded expert tensors onto its --rpc-engine peer at
                        startup (`combined_head_attached` in INFO). The Coordinator's
                        `ReportsSolo()` reads this `mode` key to detect the fallback.

0x45  SWAP_QUANT        Swap expert quantization — see E3 epic #161-E3
                        Request:  key="<slot_id>", payload=[2B quant_key_len LE][quant_key UTF-8][tensor_pattern UTF-8]
                                  (quant_key selects the new quant file; tensor_pattern limits scope, e.g. "blk\\.5\\.ffn_.*_exps")
                        Response: meta={"swapped":<N>,"bytes":<N>,"swap_ms":<N>,"kv_preserved":<bool>}
                                  payload_len=0

0x46  PIPELINE_ATTACH   Two-engine PIPELINE attach (prima.cpp-style) — see epic #161
                        Request:  key="<slot_id>", payload=JSON {"peer":"<host:port>","ot_split":"<regex>"}
                                  The head tells the worker which tensors to own; the worker loads
                                  them from its OWN local model file (no weight transfer), and only
                                  boundary activations cross the link afterwards.
                        Response: meta={"mode":"pipeline|solo","peer_connected":<bool>}
                                  (engine reports "solo"+peer_connected:false on failure →
                                  caller falls back to single-engine)
                                  payload_len=0
```

**Two-engine run modes (one binary, composable capability flags — issue #348):** every
engine always loads its model and serves its own SOLO prefill/decode. The old
mutually-exclusive `--role standalone|head|worker` is replaced by independent opt-in flags
— a node can be neither, either, or both of the following, alongside its always-on SOLO
duty:

- **Embedded ggml-RPC backend** (`--ggml-rpc-port <port>`, issue #348): expose this
  engine's local GPU backend(s) on `<port>` as an embedded ggml-RPC peer, sharing the
  SAME backend instances local inference already uses (not an independent context —
  `ggml_backend_rpc_start_server_with_backends`). Local decode and inbound RPC compute
  serialize via a per-device mutex engaged only when this flag is set, so a node can do
  its own SOLO decode AND act as a COMBINED-mode peer at once. Replaces the old
  `--role worker` (which loaded no model and so could not also serve SOLO).
- **COMBINED head** (issue #287/#260/#348): `--rpc-engine <peer host:port>
  --combined-ot-pattern "<tensor-name regex>"`. After loading its model, the head
  dual-loads every expert tensor whose name matches the pattern onto the peer's embedded
  RPC backend (one-time network copy — see `llama_hydra_load_combined_experts` in
  `include/llama-hydra.h`), gated by a peer-VRAM headroom check (the peer's VRAM is no
  longer guaranteed empty under the always-on dual-role design). Keeps its own local copy,
  so SOLO always still works; `SET_EXPERT_MODE` (`0x44`) flips between them per request.
  Replaces the old `--role head`. `--rpc-engine` without `--combined-ot-pattern` logs a
  warning and runs solo-only.
- **PIPELINE** (`0x46`, prima.cpp-style layer split): transport scaffolding only —
  stubbed `NOT_IMPLEMENTED` (issue #287, remaining half).

> **Migration note (#348):** `--role` is removed. Because it is no longer filtered out
> before `common_params_parse`, passing it now hard-errors as an unknown argument. Launch
> a COMBINED head with `--rpc-engine`+`--combined-ot-pattern`, an embedded COMBINED peer
> with `--ggml-rpc-port`, and a plain SOLO engine with neither.

See epic #161 and `docs/milestone-perf.md` for the launch topology.

**Error handling for control ops:**
- `BUSY` (0x04) — slot is currently processing another request; caller retries with
  exponential backoff (Coordinator scheduler decides).
- `ERROR` (0x02) — server-side failure; meta JSON carries `{"error":"<msg>"}`. Caller
  is responsible for deciding whether to fall back to HTTP (see `EnginePrefillAsync`
  fallback path in `WorkerSchedulerService.cs` — issue #279).
- The 0x40-0x46 opcodes are NOT supported by the legacy `llama-server` binary (which
  only implements 0x30-0x32). The Coordinator detects the binary mismatch and falls
  back to the HTTP prefill path (see `fix/m-perf-p1-279`).

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
0x00  OK               Operation succeeded
0x01  NOT_FOUND        Key or session not found
0x02  ERROR            Operation failed (error detail in meta JSON)
0x03  PARTIAL          Partial result (used in chunked operations)
0x04  BUSY             Slot or node is busy, retry later
0x05  BAD_REQUEST      Malformed payload (parse / schema error) — added M-Perf.9 #289
0x06  NOT_IMPLEMENTED  Opcode stubbed (e.g. `0x46` PIPELINE_ATTACH until #287) — added M-Perf.9 #289
```

The Coordinator distinguishes `ERROR` (real failure) from `NOT_IMPLEMENTED` (a known
stub on this server build) so it can fall back to solo mode cleanly for stubbed opcodes
without raising the "engine RPC failed" alert that `ERROR` would.

## Cross-Model KV Safety (M-Perf.9 #289)

The model identity (`model_alias` / `model_hash` / `model_path`) on `STATE_META` (0x32)
and `STATE_GET` (0x30) lets the Coordinator detect a model mismatch at restore time.
The KV cache is **quantization-dependent**: a Q3_K prefill produces different KV values
for the same input than a Q5_K prefill, so transferring KV across quantizations silently
corrupts decode output.

The Coordinator runs `CrossModelGuard.Decide(storedHash, slotHash, allowCrossModelKvReuse)`
in `WorkerSchedulerService.RestoreKvAsync` after the StatePut. Outcomes:

| Stored hash | Slot hash | Flag | Outcome | Coordinator action |
|---|---|---|---|---|
| empty | empty | — | `Skip` | Proceed (pre-#289 back-compat or META failure) |
| empty | known | — | `Skip` | Proceed (no stored identity to check) |
| known | empty | — | `Skip` | Proceed (META failed, can't verify) |
| known | known | matches | `Proceed` | Proceed (same model, same hash) |
| known | known | **differs** | `Abort` | Erase slot, re-prefill on the correct model |
| known | known | differs, `ALLOW=true` | `WarnAndProceed` | Log warning, proceed (likely corrupt decode) |

Operational implications:

- **Same model across P/D phases** is the only safe configuration. Workers that run
  the same GGUF file produce the same `model_hash`, so cross-worker restores
  (e.g. RTX prefill → P100 decode) work as long as both load the same model file.
- **Cross-quantization P/D is mathematically broken** (Q3_K prefill KV ≠ Q5_K weights).
  The guard would correctly `Abort` such a restore. Operators wanting this must set
  `HYDRA_COORD_ALLOW_CROSS_MODEL_KV_REUSE=true` and accept the corrupt-decode risk.
- The `model_hash` is computed once at model load time (SHA-256 of the GGUF file) and
  cached on the `llama_model` struct (`llama_model_hash()` in `include/llama.h`).
  The hash is stable for the model's lifetime; rebuilding the same GGUF produces a
  different hash.
- See `src/core/Hydra.Core/Services/CrossModelGuard.cs` for the pure function and
  `src/core/Tests.Core/CrossModelGuardTests.cs` for the test matrix. The
  `hydra_cross_model_kv_proceeded_total` / `_skipped_total` / `_warned_total` /
  `_aborted_total` Prometheus counters expose the decision distribution.

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
