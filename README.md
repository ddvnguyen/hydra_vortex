# Hydra

[![License: AGPL v3](https://img.shields.io/badge/License-AGPL_v3-blue.svg)](LICENSE)

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

## License

Hydra is free and open source under the **GNU Affero General Public License v3.0
(AGPL-3.0)** — see [LICENSE](LICENSE).

You are free to use, study, modify, and redistribute Hydra. In return, the AGPL
requires that **if you run a modified version of Hydra and offer it to others
over a network, you must make your modified source available to those users**
(AGPL §13). This keeps improvements to Hydra open for everyone and prevents
closed-source forks of a network service.

Third-party dependency licenses are documented in
[THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).

### Copyright

Copyright © 2026 ddvnguyen. "Hydra" and its design are the work of the original
author. Contributions are welcome under the project license; contributors retain
copyright to their contributions while licensing them under AGPL-3.0 to the
project.

### Commercial licensing

The AGPL-3.0 is not suitable for every organization (some cannot use AGPL
software, or wish to build a proprietary/closed-source product on top of Hydra).
A separate **commercial license** can be made available for those cases. Contact
the author (ddvnguyen@gmail.com) to discuss commercial terms.
