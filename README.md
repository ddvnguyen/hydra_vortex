# Hydra

High-throughput multi-GPU LLM inference system with KV cache state management.

## Architecture

```
Client → Coordinator (:9000, HTTP) → Agent (:9601/:9602, RPC) → llama-server (local HTTP)
                                   → Store (:9500, RPC, tmpfs-backed)
```

All inter-service communication uses Hydra binary RPC protocol.
HTTP only at edges: client-facing API and agent-to-local-llama-server.

## Components

| Service     | Role                                    | Transport    |
|-------------|-----------------------------------------|--------------|
| Store       | KV state storage, content-addressed     | Binary RPC   |
| Agent       | Sidecar per GPU node, wraps llama-server| Binary RPC   |
| Coordinator | Request routing, session management     | HTTP in, RPC out |

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

## Quick Start (after M1)
```bash
pip install -e ".[dev]"
hydra-store                    # start store on :9500
hydra-agent --node-name rtx    # start RTX agent on :9601
hydra-agent --node-name p100   # start P100 agent on :9602
hydra-coordinator              # start coordinator on :9000
curl localhost:9000/v1/chat/completions -d '{"messages":[...]}'
```

## Docs
- `docs/PROJECT_PLAN.md` — architecture, tech stack, project structure
- `docs/milestone-{0,1,2,3}.md` — detailed task breakdowns
- `specs/` — protocol, service contracts, data models, OpenAPI
