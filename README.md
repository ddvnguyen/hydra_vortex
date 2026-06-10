# Hydra

High-throughput multi-GPU LLM inference system with KV cache state management.

## Architecture

```
Client → Hydra.Core (:9000 HTTP, :9500 Store RPC) → llama-server (HTTP local + RPC)
```

Hydra.Core uses binary RPC for KV state ops (StateGet/StatePut) and HTTP for
OpenAI-compatible API. llama-servers contacted directly via HTTP (no intermediate Agent).

## Components

| Service     | Role                                    | Transport    |
|-------------|-----------------------------------------|--------------|
| Hydra.Core  | KV storage + request routing + session mgmt | HTTP + Binary RPC |
| llama-server| GPU inference                           | HTTP (C++ fork) |

## Milestones

| MS | Name       | Scope                                        |
|----|------------|----------------------------------------------|
| M0 | MVP Test   | Store + Agent + system test (save/restore)    |
| M1 | Core       | Coordinator + routing + session + migration  |
| M2 | Advanced   | Chunked dedup + prefix checkpoints           |
| M3 | Production | Persistence + Grafana + Langfuse + model dist|

## Verified Facts
- ✅ Cross-GPU save/restore works (cache_n=2964)
- ⚠️ SSM truncation broken (n_tokens must > n_past)
- 📊 P100 prefill: 110 tok/s, decode: 28 tok/s
- 📊 KV state at 60-80K: ~800 MB

## Quick Start
```bash
hydra-core                     # single binary, starts on :9000 + :9500
curl localhost:9000/v1/chat/completions -d '{"messages":[...]}'
```

## Docs
- `PROJECT_PLAN.md` — architecture, tech stack, project structure
- `docs/milestone-{0,1,2}.md` — detailed task breakdowns
- `specs/` — protocol, service contracts, data models, OpenAPI
