# Hydra — Claude Handoff

## What Is This
Multi-GPU LLM inference system. Routes requests across RTX 5060 Ti and Tesla P100
(in KVM VM), migrates 800 MB KV cache state between GPUs without re-prefill.

## Read These First
1. `PROJECT_PLAN.md` — architecture, structure, tech stack (10 min)
2. `specs/rpc-protocol.md` — binary wire format (5 min)
3. `docs/milestone-0-mvp.md` — start here for implementation

## Architecture
```
Client (HTTP) → Coordinator :9000 [Python/FastAPI]
                    │ RPC           │ RPC
                    ▼               ▼
              Agent RTX :9601   Agent P100 :9602  [C#/.NET 8]
                │ HTTP local      │ HTTP local
                ▼                 ▼
           llama :8080        llama :8081          [C++ fork]
                │ RPC               │ RPC
                └────────┬──────────┘
                         ▼
                   Store :9500                     [C#/.NET 8]
                   /mnt/llm-ram/store/ (tmpfs)
```

## Language Decisions (FINAL — do not change)
| Service     | Language  | Reason                                      |
|-------------|-----------|---------------------------------------------|
| Store       | C# .NET 8 | System.IO.Pipelines, Socket.SendFileAsync   |
| Agent       | C# .NET 8 | Same RPC lib as Store, team expertise       |
| Coordinator | Python    | Langfuse, pydantic, best LLM tooling        |
| llama-server| C++ (fork)| +3 streaming state endpoints only           |

## Critical Facts (POC verified)
- P100 prefill: 110 tok/s → 80K context = 12 minutes. RTX handles large prefill.
- P100 decode: 28 tok/s — acceptable.
- Cross-GPU save/restore: WORKS. cache_n=2964 after restore.
- SSM truncation: BROKEN. --cache-prompt useless for qwen35moe.
- n_tokens MUST be > n_past or cache is nuked. Coordinator must guard this.
- KV state at 60-80K context: ~800 MB.

## llama.cpp Fork (hydra-state-streaming branch)
Three new endpoints added to tools/server/server.cpp:
- GET /slots/{id}/state      → stream binary KV state out
- PUT /slots/{id}/state      → stream binary KV state in
- GET /slots/{id}/state/meta → metadata (n_past, state_size)

These eliminate disk round-trips. Agent pipes stream directly llama↔Store.
Without these patches, nothing else in the system makes sense.
Build RTX: GGML_CUDA_FORCE_CUBLAS=ON, sm_120. Build P100: sm_60.

## Milestones
| MS | Goal                                          | Est.      |
|----|-----------------------------------------------|-----------|
| M0 | llama fork + Store + Agent + E2E test         | 3-4 days  |
| M1 | Coordinator + routing + session + migration   | 1-2 weeks |
| M2 | Chunked dedup + prefix checkpoints            | 1 week    |
| M3 | Persistence + Grafana + Langfuse              | 1-2 weeks |

## Starting Point
1. M0.0 first: fork llama.cpp, add 3 endpoints (~80 lines C++), verify with curl
2. Then M0.1: Hydra.Shared (C# RPC library) — everything else depends on it
3. M0.2 (Store) and M0.3 (Agent) can be built in parallel after M0.1

## Key Design Decisions (do not relitigate)
- No Ray until possible M4+ (2 nodes, not needed)
- Store backed by tmpfs not S3/MinIO (sendfile + zero-copy)
- Full KV state only (delta export impossible — SSM truncation broken)
- Content-addressed chunking at Store level, not llama.cpp level (M2)
- No shared filesystem between nodes (Hydra Store RPC replaces NFS/virtiofs)
- llama.cpp fork minimal: only 3 endpoints in server.cpp, no core changes

## Hardware
- RTX 5060 Ti 16 GB sm_120, CUDA 13.2 — host machine, i7-12700K, 64 GB
- Tesla P100 16 GB sm_60, CUDA 12.9 — KVM VM at 192.168.122.21
- tmpfs 30 GB at /mnt/llm-ram on host, shared to VM via virtiofs
- Model: Darwin-36B-Opus-APEX-I-Balanced.gguf (~25.5 GB, qwen35moe arch)
