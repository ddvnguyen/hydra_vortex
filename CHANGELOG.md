# Changelog

## [0.3.0] - 2026-05-28

### Added
- Versioning system: single `VERSION` file as source of truth
- `GET /version` endpoint on Coordinator, Agent, and Store
- `version` field in all structured log output (Python structlog, C# Serilog)
- `version` key in `/health` and `/status` responses on Coordinator
- `version` field in Agent `NodeHealth` RPC response
- `version` field in Agent and Store `/debug` responses
- `CHANGELOG.md` with keepachangelog format
- `Directory.Build.props` for shared C# version across all .NET projects
- `src/coordinator/version.py` reads `VERSION` file at runtime
- `pyproject.toml` reads `VERSION` dynamically via setuptools

### Fixed
- G-P0-1: Coordinator save/restore now uses chunked path (dedup active in production)
- G-P0-2: teeStream disposal order — final chunk hash no longer dropped
- G-P0-3: RestoreFromStoreChunked sends empty known-hashes, always fetches full data (no partial-cache corruption)
- G-P1-2: n_past tracked from `usage.total_tokens` after every completion
- G-P1-3: Session eviction background task runs every 60s
- G-P1-6: `active_sessions` metric per-node instead of total
- G-P2-1: Agent sets `LlamaHealthy`/`SlotsIdle` gauges on every health RPC
- G-P2-2: Debug HTTP endpoint error handling with try/catch
- M2-P0-001: Missing meta JSON closing brace in chunk meta
- M2-P0-002: Incomplete stream flush during chunk upload
- M2-P1-001: Full-cache-hit early-return removed from restore
- M2-P1-002: PUSH_CHUNKS writes manifest if none exists
- M2-P1-003: Prefix checkpoint uses chunked save/restore
- M2-P2-001: Dead `missingMeta` variable removed
- M2-P2-002: `GetChunked` streams via `SendFileAsync` instead of buffering in memory
- M2-P2-003: `StoreChunk` made async (`File.WriteAllBytesAsync`)
- M2-P2-004: Tautology dedup assertion fixed in integration test
- M2-P2-005: Dedicated opcodes `SaveStateChunked(0x26)`/`RestoreStateChunked(0x27)` added

## [0.2.0] - 2026-05-27

### Added
- M1: Coordinator routing, session management, health monitoring
- M2: Chunked dedup with content-addressed storage
- KV state save/restore/migrate via Store RPC
- Prometheus metrics across all services
- Docker Compose monitoring stack (Prometheus, Loki, Grafana)
- Prefix checkpoint save/restore endpoints

## [0.1.0] - 2026-05-26

### Added
- M0: llama.cpp fork with state streaming endpoints
- Hydra.Shared RPC protocol library
- Hydra.Store raw KV storage
- Hydra.Agent with KV state read/write
- E2E tests for save/restore cycle

## [0.0.1] - 2026-05-25

### Added
- Initial project scaffolding
- Hardware bringup (RTX 5060 Ti + P100)
- KV state migration POC verified
