# Hydra

[![License: AGPL v3](https://img.shields.io/badge/License-AGPL_v3-blue.svg)](LICENSE)

High-throughput multi-GPU LLM inference system with KV cache state management.

## Features

- **Cross-GPU session migration** — KV cache state (~800 MB at 60-80K context)
  moves between heterogeneous GPUs without re-prefill.
- **Heterogeneous GPU pooling** — routes a single inference pool across an
  RTX 5060 Ti, RTX 3060, and Tesla P100 (sm_120 / sm_86 / sm_60), not just
  matched hardware.
- **COMBINED engine mode** — the same-host RTX 5060 Ti + RTX 3060 pair can act
  as one logical engine: expert-split (MoE) or layer-split (Dense).
- **P/D split (prefill/decode disaggregation)** — precise prefill on RTX,
  quantized decode on P100, streamed KV state directly over binary RPC
  (no disk round-trip).
- **Prompt-cache reuse** — recurrent/hybrid context checkpoints let follow-up
  turns reuse cached KV instead of a full re-prefill.
- **Content-addressed chunked dedup** — KV chunks are hashed and deduped at
  the Store level, with prefix checkpoints for shared system prompts.
- **Auto-routing** — a 4-step algorithm (warm affinity → candidate filtering
  → hardware feasibility → swap-cost preference) picks the best model/worker
  plan per request.
- **OpenAI-compatible API** — drop-in `/v1/chat/completions`.
- **Zero-copy I/O** — tmpfs-backed Store with `Socket.SendFileAsync`, no
  S3/MinIO round-trip.
- **Full observability** — Prometheus + Loki + Grafana + OpenTelemetry
  tracing out of the box.

## Architecture

```
Client → Hydra.Core :9000 [C#] → Hydra Head [Go, per GPU node] → llama-engine [C++ fork]
                │
                └→ Hydra Store RPC :9500 + tmpfs
```

Hydra.Core is a single C# binary with an embedded coordinator, routing requests
across three heterogeneous GPUs — RTX 5060 Ti + RTX 3060 (same host, containers)
and a Tesla P100 (KVM VM) — via a Hydra Head (Go) node agent per GPU. KV state
ops use binary RPC (StateGet/StatePut) directly to llama-engine's hydra RPC
port; the 5060 Ti + 3060 pair can also act as one logical engine in COMBINED
mode (expert-split or layer-split). See `PROJECT_STATUS.md` for the full
diagram and `docs/architecture.md` for routing/session-lifecycle detail.

## Components

| Service      | Role                                         | Transport          |
|--------------|----------------------------------------------|---------------------|
| Hydra.Core   | KV storage + request routing + session mgmt  | HTTP + Binary RPC   |
| Hydra Head   | Per-GPU node agent (process mgmt, OCI pull)   | HTTP                |
| llama-engine | GPU inference (C++ fork, +streaming state)    | HTTP + Binary RPC   |

## Milestones

| MS           | Scope                                                       | Status   |
|--------------|--------------------------------------------------------------|----------|
| M0–M2        | llama fork + Store + Coordinator + routing + chunked dedup   | ✅ done   |
| M-Perf       | Heterogeneous perf: spec-decode → P/D streaming → pipeline   | ✅ done   |
| Llama-Engine | **P/D split mix-quant** (RTX precise prefill / P100 quant decode) | ▶ now |
| M3–M5        | Persistence, model mgmt & multi-modal, LLM obs & agentic      | Production (later) |

See `PROJECT_STATUS.md` for the full milestone table, sub-phase breakdown, and
current implementation status — it's the single source of truth for project
state and is kept in sync with every merged PR.

## Verified Facts
- ✅ Cross-GPU save/restore works (cache_n=2964)
- ✅ Prompt-cache reuse works (fixed via fork patch; follow-up turns reuse cached KV)
- ⚠️ `n_tokens` must be `> n_past` or the KV cache is nuked — coordinator guards this
- 📊 RTX 5060 Ti decode: ~200 tok/s · RTX 3060 decode: ~60 tok/s
- 📊 P100 prefill: 110 tok/s, decode: 28 tok/s
- 📊 KV state at 60-80K context: ~800 MB

## Quick Start
```bash
hydra-core                     # single binary, starts on :9000 + :9500
curl localhost:9000/v1/chat/completions -d '{"messages":[...]}'
```

## Docs
- `PROJECT_STATUS.md` — architecture, milestones, current implementation status
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
