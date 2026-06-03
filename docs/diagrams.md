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

    subgraph Coordinator["Coordinator :9000  [Python / FastAPI]"]
        CO[Router + Session Manager\nrouting.py · session_table.py\nstate_manager.py]
    end

    subgraph Host["Host Machine  (i7-12700K · 64 GB)"]
        subgraph AgentRTX["Agent RTX :9601  [C# / .NET 10]"]
            AR[AgentServer\nStateHandler\nLlamaClient]
        end
        subgraph LlamaRTX["llama-server :8080  [C++ fork]"]
            LR[RTX 5060 Ti 16 GB\nsm_120 · CUDA 13.2]
        end
        subgraph Store["Hydra Store :9500  [C# / .NET 10]"]
            ST[StoreServer\nStorageEngine\ntmpfs 30 GB]
        end
    end

    subgraph VM["KVM VM  (192.168.122.21)"]
        subgraph AgentP100["Agent P100 :9602  [C# / .NET 10]"]
            AP[AgentServer\nStateHandler\nLlamaClient]
        end
        subgraph LlamaP100["llama-server :8086  [C++ fork]"]
            LP[Tesla P100 16 GB\nsm_60 · CUDA 12.9]
        end
    end

    C1 & C2 & C3 -->|OpenAI-compat HTTP| CO

    CO -->|Hydra RPC| AR
    CO -->|Hydra RPC| AP

    AR -->|HTTP local| LR
    AP -->|HTTP local| LP

    AR -->|Hydra RPC| ST
    AP -->|Hydra RPC| ST

    style Store fill:#d4edda,stroke:#28a745
    style AgentRTX fill:#cce5ff,stroke:#004085
    style AgentP100 fill:#cce5ff,stroke:#004085
    style Coordinator fill:#fff3cd,stroke:#856404
    style LlamaRTX fill:#f8d7da,stroke:#721c24
    style LlamaP100 fill:#f8d7da,stroke:#721c24
```

---

## 2. Normal Inference Request Flow

A request arriving while a session's KV state already lives on one GPU.

```mermaid
sequenceDiagram
    participant Client
    participant Coord as Coordinator :9000
    participant Agent as Agent (chosen GPU) :9601/9602
    participant Llama as llama-server :8080/:8086

    Client->>+Coord: POST /v1/chat/completions\n(OpenAI-compat JSON)

    Note over Coord: 1. Look up session_id in session_table\n2. routing.py picks best GPU\n3. Session already active on chosen GPU

    Coord->>+Agent: RPC COMPLETION (0x25)\nkey=session_id, payload=request JSON

    Agent->>+Llama: POST /v1/chat/completions\n(HTTP local, n_tokens > n_past ⚠️)

    Llama-->>-Agent: SSE token stream / JSON response

    Agent-->>-Coord: RPC response\nmeta={n_past, tokens_out}

    Coord-->>-Client: SSE stream / JSON\n(OpenAI format)
```

---

## 3. KV State Migration Flow

The core value proposition: move an 800 MB KV cache between GPUs without re-prefill.

```mermaid
sequenceDiagram
    participant Client
    participant Coord as Coordinator :9000
    participant AgentSrc as Agent RTX :9601
    participant LlamaSrc as llama-server RTX :8080
    participant Store as Hydra Store :9500
    participant AgentDst as Agent P100 :9602
    participant LlamaDst as llama-server P100 :8086

    Client->>Coord: POST /v1/chat/completions\n(session on P100 needed)

    Note over Coord: Session table shows KV state\ncurrently on RTX. Must migrate.

    rect rgb(255, 243, 205)
        Note over Coord,Store: ── Phase 1: Save from source GPU ──
        Coord->>+AgentSrc: RPC SAVE_STATE (0x20)\nkey=session_id
        AgentSrc->>+LlamaSrc: GET /slots/0/state\n(binary stream ~800 MB)
        LlamaSrc-->>-AgentSrc: HTTP 200 + raw KV bytes\nX-Hydra-N-Past: 2968
        AgentSrc->>+Store: RPC PUT (0x01)\nkey="kv/{session_id}"\npayload=stream (piped, no buffer)
        Store-->>-AgentSrc: RPC OK\n(bytes written to tmpfs)
        AgentSrc-->>-Coord: SaveResult\n{n_past:2968, bytes:800MB}
    end

    rect rgb(204, 229, 255)
        Note over Coord,LlamaDst: ── Phase 2: Restore to destination GPU ──
        Coord->>+AgentDst: RPC RESTORE_STATE (0x21)\nkey=session_id
        AgentDst->>+Store: RPC GET (0x02)\nkey="kv/{session_id}"
        Store-->>-AgentDst: RPC OK + stream\n(sendfile() zero-copy from tmpfs)
        AgentDst->>+LlamaDst: PUT /slots/0/state\n(binary stream ~800 MB)
        LlamaDst-->>-AgentDst: {"restored":true, "n_past":2968}
        AgentDst-->>-Coord: RestoreResult\n{n_past:2968}
    end

    Note over Coord: Update session_table:\nsession now on P100, slot 0

    Coord->>+AgentDst: RPC COMPLETION (0x25)\nn_tokens MUST be > 2968 ⚠️
    AgentDst->>+LlamaDst: POST /v1/chat/completions\n(includes full prior context)
    LlamaDst-->>-AgentDst: response (cache_n=2968 ✅)
    AgentDst-->>-Coord: tokens
    Coord-->>Client: response\n(no re-prefill — 12 min saved)
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
  root((Hydra RPC\nOp Codes))
    Store Ops
      0x01 PUT
      0x02 GET
      0x03 DEL
      0x04 STAT
      0x05 LIST
    Store Chunked M2
      0x10 PUT_CHUNKED
      0x11 GET_CHUNKED
      0x12 SYNC_PLAN
      0x13 PUSH_CHUNKS
    Agent Ops
      0x20 SAVE_STATE
      0x21 RESTORE_STATE
      0x22 SLOT_STATUS
      0x23 SLOT_ERASE
      0x24 NODE_HEALTH
      0x25 COMPLETION
    llama-server Direct
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
    model["Model file\nDarwin-36B.gguf"]

    subgraph layer1["Layer 1 — Foundations"]
        tmpfs
        model
    end

    subgraph layer2["Layer 2 — Inference Engines"]
        LR["llama-server RTX\n:8080\n(patched binary sm_120)"]
        LP["llama-server P100\n:8086\n(patched binary sm_60)"]
    end

    subgraph layer3["Layer 3 — Hydra Services"]
        ST["Hydra Store\n:9500"]
        AR["Agent RTX\n:9601"]
        AP["Agent P100\n:9602"]
    end

    subgraph layer4["Layer 4 — Coordinator"]
        CO["Coordinator\n:9000"]
    end

    tmpfs --> ST
    tmpfs --> LR
    tmpfs --> LP
    model --> LR
    model --> LP

    LR --> AR
    LP --> AP
    ST --> AR
    ST --> AP

    AR --> CO
    AP --> CO

    style layer1 fill:#f8f9fa,stroke:#dee2e6
    style layer2 fill:#f8d7da,stroke:#721c24
    style layer3 fill:#cce5ff,stroke:#004085
    style layer4 fill:#fff3cd,stroke:#856404
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

    subgraph M02["M0.2 — Hydra.Store"]
        M021["M0.2.1 StorageEngine.cs\ntmpfs file I/O"]
        M022["M0.2.2 StoreServer.cs\nsendfile zero-copy"]
        M023["M0.2.3 Debug endpoint :9501"]
        M021 --> M022 --> M023
    end

    subgraph M03["M0.3 — Hydra.Agent"]
        M031["M0.3.1 LlamaClient.cs\nHTTP streaming"]
        M032["M0.3.2 StateHandler.cs\npipe llama↔store"]
        M033["M0.3.3 AgentServer.cs\nRPC handlers"]
        M034["M0.3.4 Config + Debug :9611"]
        M031 --> M032 --> M033 --> M034
    end

    subgraph M04["M0.4 — Tests"]
        M041["M0.4.1 Integration tests\nStore ↔ Agent"]
        M042["M0.4.2 E2E test\ntest_e2e.py\nReal GPUs"]
    end

    M00 --> M042
    M01 --> M02
    M01 --> M03
    M02 --> M041
    M03 --> M041
    M041 --> M042

    M1["M1 — Coordinator\nrouting · sessions\nmigration logic"]
    M2["M2 — Chunked Dedup\ncontent-addressed\nprefix checkpoints"]
    M3["M3 — Production\nGrafana · Langfuse\nmodel distribution"]

    M04 --> M1
    M1 --> M2
    M2 --> M3

    style M00 fill:#f8d7da,stroke:#721c24
    style M01 fill:#d1ecf1,stroke:#0c5460
    style M02 fill:#d4edda,stroke:#155724
    style M03 fill:#cce5ff,stroke:#004085
    style M04 fill:#fff3cd,stroke:#856404
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

    subgraph AgentRTX["Agent RTX (StateHandler.cs)"]
        GS["llama.GetStateAsync\nHttp ResponseHeadersRead\n→ Stream (not buffered)"]
        PUT["store.RequestAsync\nOpCode.PUT\nPipeWriter streams\ndirectly to socket"]
    end

    subgraph Store["Hydra Store (StoreServer.cs)"]
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

## 8. Coordinator Session Routing Logic

Decision tree for routing an incoming request.

```mermaid
flowchart TD
    A([Incoming request\nPOST /v1/chat/completions]) --> B{session_id\nin request?}

    B -->|No| C[Create new session\nAssign to least-loaded GPU]
    B -->|Yes| D{session_table\nlookup}

    D -->|Not found| E[Return 404\nor create new]
    D -->|Found| F{KV state\nlocation?}

    F -->|On target GPU| G[Forward directly\nRPC COMPLETION → Agent]
    F -->|On other GPU| H{Target GPU\nhealthy?}
    F -->|In Store\nnot on any GPU| I[Restore to\nbest GPU first]

    H -->|Yes| J[Migrate KV state\nSAVE → PUT → RESTORE]
    H -->|No| K[Restore on\nhealthy GPU]

    I --> G
    J --> G
    K --> G
    C --> G

    G --> L{n_tokens >\nn_past? ⚠️}
    L -->|Yes ✅| M[Forward to Agent\nRPC COMPLETION]
    L -->|No ❌| N[Append dummy token\nor refuse — cache\nwould be nuked]

    M --> O([Stream response\nback to client])

    style L fill:#fff3cd,stroke:#856404
    style N fill:#f8d7da,stroke:#721c24
    style M fill:#d4edda,stroke:#155724
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

    Agent["Agent\nLlamaClient.cs"] -->|"GetStateAsync(slotId)"| P1
    Agent -->|"PutStateAsync(slotId, stream)"| P2
    Agent -->|"GetStateMetaAsync(slotId)"| P3
    Agent -->|normal inference| E1
    Agent -->|health check| E2

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

*Generated from architecture specs in `PROJECT_PLAN.md`, `specs/rpc-protocol.md`,
and `docs/milestone-0-mvp.md`.*
