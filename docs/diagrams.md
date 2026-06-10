# Hydra — System Workflow Diagrams

> All diagrams use [Mermaid](https://mermaid.js.org/) syntax and render in GitHub,
> GitLab, VS Code (Mermaid Preview), and most modern markdown viewers.

---

## 1. High-Level Architecture

Overall component topology and protocol boundaries.

```mermaid
graph TB
    subgraph Clients["Clients (external)"]
        C1[Cline / IDE]
        C2[OpenWebUI]
        C3[curl / script]
    end

    subgraph HydraCore["Hydra.Core :9000  [C# / .NET 10]"]
        HC["Router + Session Manager\nStateHandler + Store RPC\nLlamaClient + ChunkEngine"]
    end

    subgraph Host["Host Machine  (i7-12700K · 64 GB) — all containers"]
        subgraph LlamaRTX["llama-server :8080 + :9503  [C++ fork]  separate compose"]
            LR[RTX 5060 Ti 16 GB\nsm_120 · CUDA 13.2]
        end
        subgraph Store["Hydra Store RPC :9500  [embedded in Hydra.Core]"]
            ST[StorageEngine\ntmpfs 30 GB]
        end
    end

    subgraph VM["KVM VM  (192.168.122.21) — bare process"]
        subgraph LlamaP100["llama-server :8086 + :9502  [C++ fork]"]
            LP[Tesla P100 16 GB\nsm_60 · CUDA 12.9]
        end
    end

    C1 & C2 & C3 -->|"OpenAI-compat HTTP"| HC

    HC -->|"HTTP (completions)"| LR
    HC -->|"HTTP (completions)"| LP
    HC -->|"binary RPC (state ops)"| LR
    HC -->|"binary RPC (state ops)"| LP

    style Store fill:#d4edda,stroke:#28a745
    style HydraCore fill:#fff3cd,stroke:#856404
    style LlamaRTX fill:#f8d7da,stroke:#721c24
    style LlamaP100 fill:#f8d7da,stroke:#721c24
```

---

## 2. Normal Inference Request Flow

A request arriving while a session's KV state already lives on one GPU.
**Completions are proxied Hydra.Core → llama-server over HTTP**, not via any
intermediary. The RPC channel is used for state save/restore and health.

```mermaid
sequenceDiagram
    participant Client
    participant Core as Hydra.Core :9000
    participant Llama as llama-server (chosen GPU) :8080/:8086

    Client->>+Core: POST /v1/chat/completions\n(OpenAI-compat JSON)

    Note over Core: 1. Look up session_id in session_table\n2. RouteRequest picks best GPU\n3. n_past guard: n_tokens > n_past ⚠️

    Core->>+Llama: HTTP POST {llama_url}/v1/chat/completions\n(direct, no Agent hop)

    Llama-->>-Core: SSE token stream / JSON response

    Note over Core: Update n_past from usage.total_tokens\nMaybe save prefix checkpoint

    Core-->>-Client: SSE stream / JSON\n(OpenAI format, X-Hydra-Node header)
```

---

## 3. KV State Migration Flow

The core value proposition: move an 800 MB KV cache between GPUs without re-prefill.

```mermaid
sequenceDiagram
    participant Client
    participant Core as Hydra.Core :9000
    participant LlamaRTX as llama-server RTX :8080/:9503
    participant Store as Store RPC :9500
    participant LlamaP100 as llama-server P100 :8086/:9502

    Client->>Core: POST /v1/chat/completions\n(session on P100 needed)

    Note over Core: Session table shows KV state\ncurrently on RTX. Must migrate.

    rect rgb(255, 243, 205)
        Note over Core,Store: ── Phase 1: Save from source GPU (chunked dedup) ──
        Core->>+LlamaRTX: RPC STATE_GET (0x30) → GET /slots/0/state\n(binary stream ~800 MB)
        LlamaRTX-->>-Core: HTTP 200 + raw KV bytes\nX-Hydra-N-Past: 2968
        Core->>+Store: RPC PUT_CHUNKED (0x10) + PUT_META (0x14)\nkey="kv/{session_id}"
        Store-->>-Core: RPC OK\n(deduped chunks → tmpfs)
        Core->>LlamaRTX: POST /slots/{id}/erase\n(free source VRAM)
    end

    rect rgb(204, 229, 255)
        Note over Core,LlamaP100: ── Phase 2: Restore to destination GPU ──
        Core->>+Store: RPC GET_CHUNKED (0x11, known hashes)\n+ GET_MANIFEST (0x33)
        Store-->>-Core: RPC OK + missing chunks\n(sendfile() zero-copy from tmpfs)
        Core->>+LlamaP100: RPC STATE_PUT (0x31) → PUT /slots/0/state\n(reassembled ~800 MB)
        LlamaP100-->>-Core: {"restored":true, "n_past":2968}
    end

    Note over Core: Update session_table:\nsession now on P100, slot 0

    Core->>+LlamaP100: HTTP POST /v1/chat/completions\n(direct; n_tokens > 2968 ⚠️)
    LlamaP100-->>-Core: response (cache_n=2968 ✅)
    Core-->>Client: response\n(no re-prefill — 12 min saved)
```

---

## 4. RPC Protocol Message Structure

Binary wire format for all inter-service communication.

```mermaid
packet-beta
  title Hydra RPC — Request Header (16 bytes)
  0-15: "Magic 0x4859 (2B)"
  16-23: "Op (1B)"
  24-31: "Flags (1B)"
  32-47: "key_len (2B LE)"
  48-111: "payload_len (8B LE)"
  112-127: "trace_len (2B LE)"
```

```mermaid
packet-beta
  title Hydra RPC — Response Header (12 bytes)
  0-7: "Status (1B)"
  8-31: "meta_len (3B LE)"
  32-95: "payload_len (8B LE)"
```

### Operation Code Map

```mermaid
mindmap
  root((Hydra RPC Op Codes))
    Store Ops
      0x01 PUT
      0x02 GET
      0x03 DEL
      0x04 STAT
      0x05 LIST
    Store Chunked
      0x10 PUT_CHUNKED
      0x11 GET_CHUNKED
      0x14 PUT_META
      0x33 GET_MANIFEST
    State Ops (Core → llama)
      0x30 STATE_GET
      0x31 STATE_PUT
      0x32 STATE_META
```

---

## 5. Service Startup & Dependency Order

Which services must be running before others can start.

```mermaid
graph LR
    tmpfs["tmpfs mounted\n/mnt/llm-ram"]
    model["Model file\nQwopus3.6-35B-A3B.gguf"]

    subgraph layer1["Layer 1 — Foundations"]
        tmpfs
        model
    end

    subgraph layer2["Layer 2 — Inference Engines"]
        LR["llama-server RTX\n:8080 + :9503\n(patched binary sm_120)"]
        LP["llama-server P100\n:8086 + :9502\n(patched binary sm_60)"]
    end

    subgraph layer3["Layer 3 — Hydra.Core"]
        HC["Hydra.Core\n:9000 + :9500\n(C#/.NET 10)"]
    end

    tmpfs --> HC
    tmpfs --> LR
    tmpfs --> LP
    model --> LR
    model --> LP

    LR --> HC
    LP --> HC

    style layer1 fill:#f8f9fa,stroke:#dee2e6
    style layer2 fill:#f8d7da,stroke:#721c24
    style layer3 fill:#fff3cd,stroke:#856404
```

---

## 6. Milestone Dependency Graph

Development sequence — what blocks what.

```mermaid
graph TD
    M00["M0.0 — llama.cpp fork\n3 streaming endpoints\n~80 lines C++"]

    subgraph M01["M0.1 — Hydra.Shared (C# RPC lib)"]
        M011["M0.1.1 Protocol.cs\n16+12 byte headers"]
        M012["M0.1.2 RpcServer.cs\nSystem.IO.Pipelines"]
        M013["M0.1.3 RpcClient.cs\nreconnect + backoff"]
        M014["M0.1.4 HydraLogging.cs\nSerilog JSON"]
        M011 --> M012
        M011 --> M013
    end

    subgraph M02["M0.2 — Hydra.Core (merged Agent + Store)"]
        M021["M0.2.1 StorageEngine.cs\ntmpfs file I/O"]
        M022["M0.2.2 Store RPC Server\nsendfile zero-copy"]
        M023["M0.2.3 StateHandler\npipe llama↔store"]
        M024["M0.2.4 LlamaClient.cs\nHTTP streaming + RPC"]
        M025["M0.2.5 Router + Session Mgr"]
        M026["M0.2.6 Debug endpoint :9501"]
        M021 --> M022 --> M023 --> M024
        M024 --> M025
        M022 --> M026
    end

    subgraph M03["M0.3 — Tests"]
        M031["M0.3.1 Unit tests\nTests.Shared + Tests.Core"]
        M032["M0.3.2 E2E test\npytest tests/system\nReal GPUs"]
    end

    M00 --> M032
    M01 --> M02
    M02 --> M031
    M031 --> M032

    M1["M1 — Coordinator\nrouting · sessions\nmigration logic"]
    M2["M2 — Chunked Dedup\ncontent-addressed\nprefix checkpoints"]
    M3["M3 — Production\nGrafana · Langfuse\nmodel distribution"]

    M03 --> M1
    M1 --> M2
    M2 --> M3

    style M00 fill:#f8d7da,stroke:#721c24
    style M01 fill:#d1ecf1,stroke:#0c5460
    style M02 fill:#fff3cd,stroke:#856404
    style M03 fill:#d4edda,stroke:#155724
    style M1 fill:#e2d9f3,stroke:#6f42c1
    style M2 fill:#e2d9f3,stroke:#6f42c1
    style M3 fill:#e2d9f3,stroke:#6f42c1
```

---

## 7. State Handler — Stream Piping (Save Path)

How 800 MB flows from GPU VRAM to tmpfs **without touching disk or RAM** as a buffer.

```mermaid
flowchart LR
    subgraph LlamaRTX["llama-server RTX"]
        VRAM["GPU VRAM\n~800 MB KV state"]
    end

    subgraph HydraCore["Hydra.Core (StateHandler)"]
        GS["LlamaClient.GetStateAsync\nHttp ResponseHeadersRead\n→ Stream (not buffered)"]
        PUT["StoreClient.RequestAsync\nOpCode.PUT_CHUNKED\nPipeWriter streams\ndirectly to socket"]
    end

    subgraph Store["Store RPC (StorageEngine)"]
        PIPE["PipeReader reads\n256 KB chunks\nwrites to FileStream"]
        TMPFS["/mnt/llm-ram/store\nkv/{session_id}\ntmpfs → in RAM"]
    end

    VRAM -->|"GET /slots/0/state\nHTTP chunked"| GS
    GS -->|"Stream\n(no copy)"| PUT
    PUT -->|"TCP RPC\n800 MB"| PIPE
    PIPE -->|"FileStream.WriteAsync\nno malloc"| TMPFS

    style VRAM fill:#f8d7da,stroke:#721c24
    style TMPFS fill:#d4edda,stroke:#155724
```

---

## 8. Coordinator Routing Algorithm (4-tier)

Actual decision tree in `RouteRequest()`.

```mermaid
flowchart TD
    A([POST /v1/chat/completions]) --> B{session_table\nlookup}

    B -->|Found AND node healthy| T1["Tier 1 — Session Affinity\nRoute to existing node\n(zero overhead)"]
    B -->|Found AND has_store_state| T2["Tier 2 — Store Restore\nRESTORE_STATE_CHUNKED\non least-loaded worker"]
    B -->|Not found| C{estimated_tokens\n≥ long_prompt_threshold?}

    C -->|Yes| T3["Tier 3 — Long-prompt\nSelect highest-priority\nPREFILL worker\n(prefill_priority ASC, load ASC)"]
    C -->|No| T4["Tier 4 — Least-loaded\nload = busy_fraction\n= (total−idle+in_flight)/total\nRound-robin tiebreak"]

    T2 --> POST[Update session_table\nnode + n_past]
    T3 --> NEW[register() new session]
    T4 --> NEW

    POST --> GUARD{n_past guard:\nestimated < n_past × 0.85?}
    NEW --> PREFIX["Prefix checkpoint?\nRESTORE 'prefix/{prompt_hash}'\nif same system prompt seen before"]
    T1 --> GUARD

    PREFIX --> GUARD
    GUARD -->|Yes ⚠️| RESET["reset n_past=0\nSLOT_ERASE\n(re-prefill silently)"]
    GUARD -->|No ✅| FWD

    RESET --> FWD["Forward to llama-server\n(HTTP proxy)"]
    FWD --> STREAM([SSE stream → Client\nX-Hydra-Node header])
    STREAM --> NPAST["Update n_past from\nusage.total_tokens\nMaybe save prefix checkpoint"]

    style T1 fill:#d4edda,stroke:#155724
    style T2 fill:#cce5ff,stroke:#004085
    style T3 fill:#fff3cd,stroke:#856404
    style T4 fill:#f8f9fa,stroke:#6c757d
    style GUARD fill:#fff3cd,stroke:#856404
    style RESET fill:#f8d7da,stroke:#721c24
```

---

## 9. llama-server Fork — Patched Endpoints

The three endpoints added to `tools/server/server.cpp` in the `hydra-state-streaming` branch.

```mermaid
graph TD
    subgraph Fork["llama-server  (hydra-state-streaming branch)"]
        subgraph Existing["Existing endpoints (unchanged)"]
            E1["POST /v1/chat/completions"]
            E2["GET  /health"]
            E3["GET  /slots"]
            E4["POST /slots/{id}/erase"]
        end
        subgraph Patched["New endpoints (~80 lines C++)"]
            P1["GET  /slots/{id}/state\n→ stream binary KV+SSM state\nX-Hydra-N-Past header\n~800 MB response body"]
            P2["PUT  /slots/{id}/state\n← accept binary KV+SSM state\n{restored:true, n_past:N}"]
            P3["GET  /slots/{id}/state/meta\n→ {slot_id, n_past,\nstate_size, is_processing}\nno KV serialization (cheap)"]
        end
    end

    Core["Hydra.Core\nLlamaClient.cs"] -->|"GetStateAsync(slotId)"| P1
    Core -->|"PutStateAsync(slotId, stream)"| P2
    Core -->|"GetStateMetaAsync(slotId)"| P3
    Core -->|normal inference| E1
    Core -->|health check| E2

    style Patched fill:#fff3cd,stroke:#856404
    style Existing fill:#f8f9fa,stroke:#dee2e6
```

---

## 10. Performance & Scale Reference

Key numbers from POC verification.

```mermaid
xychart-beta
    title "Context Prefill Time: RTX vs P100 (tokens/sec)"
    x-axis ["RTX 5060 Ti (prefill)", "P100 (prefill)", "P100 (decode)"]
    y-axis "Throughput (tok/s)" 0 --> 200
    bar [185, 110, 28]
```

```mermaid
timeline
    title KV State Migration vs Re-prefill at 80K context
    section Re-prefill on P100
        12 min : 80K tokens × (1/110 tok/s)\n= ~727 seconds
    section Hydra Migration
        Save RTX→Store  : ~4 sec (800 MB / 200 MB/s tmpfs)
        Restore Store→P100 : ~4 sec (sendfile zero-copy)
        Total migration : ~8 sec  (98% faster)
```

---

## 11. P/D Disaggregation Flow (`run_mode = "concurrency"`)

Implemented in `Hydra.Core`. RTX prefills; P100 decodes. Targets M-Perf.3.

```mermaid
sequenceDiagram
    participant Client
    participant Core as Hydra.Core :9000
    participant LlamaRTX as llama RTX :8080/:9503
    participant Store as Store RPC :9500
    participant LlamaP100 as llama P100 :8086/:9502

    Client->>Core: POST /v1/chat/completions

    rect rgb(255, 243, 205)
        Note over Core,LlamaRTX: Phase 1 — Prefill on RTX (fast, sm_120)
        Core->>LlamaRTX: HTTP POST /v1/chat/completions (stream=false, n_predict=0, fill KV only)
        LlamaRTX-->>Core: {usage.total_tokens=N}  ← n_past after prefill
        Core->>LlamaRTX: RPC STATE_GET (0x30) → GET /slots/{id}/state (~800 MB)
        Core->>Store: RPC PUT_CHUNKED (0x10) + PUT_META (0x14)
        Core->>LlamaRTX: POST /slots/{id}/erase (free RTX VRAM)
    end

    rect rgb(204, 229, 255)
        Note over Core,LlamaP100: Phase 2 — Decode on P100 (parallel, HBM2)
        Core->>Store: RPC GET_CHUNKED (0x11, known hashes) + GET_MANIFEST (0x33)
        Core->>LlamaP100: RPC STATE_PUT (0x31) → PUT /slots/{id}/state
        Core->>LlamaP100: HTTP POST /v1/chat/completions (full request, stream=true, KV pre-loaded)
        LlamaP100-->>Core: SSE token stream (28 tok/s, no re-prefill)
    end

    Core-->>Client: SSE stream\nX-Hydra-Node: p100\nX-Hydra-Prefill-Node: rtx
```

---

*See `docs/architecture.md` for conceptual detail. Source of truth: `specs/rpc-protocol.md`, `src/core/Hydra.Core/`.*
