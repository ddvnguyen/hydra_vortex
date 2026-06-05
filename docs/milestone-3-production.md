# Milestone 3 — Persistence & Real Obs

> **Restructured 2026-06.** This milestone was narrowed. Its scope is now **NVMe
> write-behind persistence + observability hardening**. The other former-M3 work was
> re-homed: **Langfuse → M5** (`docs/milestone-5-agentic.md`); **model distribution
> + systemd lifecycle → M4** (`docs/milestone-4-models.md`). **M3.2 Observability is
> already built** (metrics modules + Grafana/Loki/Prometheus). The big remaining
> piece is **M3.1 persistence — and it must be RE-SPEC'd for C#** (the task below is
> written in Python/aiosqlite, but the Store is C#/.NET: use `Microsoft.Data.Sqlite`
> + a write-behind `BackgroundService` + startup recovery).

## Goal
Persistence across restarts (Store is volatile tmpfs today) and hardened, real
observability. *(Original goal text below retained for the persistence/obs tasks;
ignore its Langfuse/model-distribution mentions — those moved to M5/M4.)*

## What "Done" Means
```
✅ Reboot → KV sessions restored from NVMe within 30s
✅ Grafana dashboard shows: request rate, active sessions, store performance, agent save/restore latency, logs with trace_id filter
✅ Langfuse shows full request traces with token counts and routing decisions
✅ Model weights served from Store to new nodes (no manual scp)
✅ systemd manages full lifecycle: ramdisk → store → agents → coordinator
```

## Prerequisites
- M2 complete

---

## Task M3.1: NVMe Write-Behind Persistence

### M3.1.1: SQLite Metadata (`store/metadata.py`)
- `class MetadataDB(db_path: Path)` using aiosqlite
- Schema:
  ```sql
  CREATE TABLE sessions (
      session_id TEXT PRIMARY KEY,
      n_past INTEGER, total_size INTEGER,
      manifest_json TEXT, created_at REAL, last_used_at REAL,
      backed_up BOOLEAN DEFAULT FALSE
  );
  CREATE TABLE chunks (
      hash TEXT PRIMARY KEY,
      size INTEGER, backed_up BOOLEAN DEFAULT FALSE
  );
  ```
- Methods: upsert_session, get_session, list_sessions, mark_backed_up, get_unbacked_chunks
- **Lines:** ~80
- **Test:** test_metadata.py — CRUD operations, backup tracking
- **Done when:** tests pass

### M3.1.2: Write-Behind Task (`store/persistence.py`)
- Background asyncio task runs every 10s
- Copies unbacked chunks: tmpfs → NVMe backup dir
- Marks chunks as backed_up in SQLite
- On startup: restore flow: NVMe → tmpfs for sessions in metadata
- **Lines:** ~80
- **Test:** test_persistence.py — mock filesystem, verify backup + restore sequence
- **Done when:** kill store, restart, sessions recoverable

### M3.1.3: Startup Recovery (`store/main.py` updated)
- On start: if tmpfs empty but NVMe has backups, restore hot sessions (sorted by last_used_at)
- Configurable: `restore_top_n: int = 10` (restore 10 most recent sessions)
- Log progress: "Restoring session X (800 MB)... done in 120ms"
- **Done when:** reboot → store starts → sessions available within 30s

---

## Task M3.2: Observability — Grafana

### M3.2.1: Prometheus Metrics (`src/coordinator/metrics.py`, `src/core/Hydra.Store/StoreMetrics.cs`, `src/core/Hydra.Agent/AgentMetrics.cs`)
- **Store** (.NET, prometheus-net): `hydra_store_ops_total`, `hydra_store_bytes_stored`, `hydra_store_bytes_sent`, `hydra_store_op_duration_seconds` — exposed on `:9501/metrics`
- **Agent** (.NET, prometheus-net): `hydra_agent_save_ops_total`, `hydra_agent_restore_ops_total`, `hydra_agent_save_duration_seconds`, `hydra_agent_restore_duration_seconds`, `hydra_agent_slots_idle`, `hydra_agent_llama_healthy` — exposed on `:9611/metrics`
- **Coordinator** (Python, prometheus_client): `hydra_requests_total`, `hydra_cache_hits_total`, `hydra_migrations_total`, `hydra_active_sessions` — exposed on `:9000/metrics`
- Each service adds a `GET /metrics` endpoint alongside its debug HTTP server
- `Prometheus` in docker-compose scrapes all three targets
- **Done when:** `docker compose -f docker-compose.infra.yml -f docker-compose.hydra.yml up` — Prometheus scrapes all targets (check `:9090/targets`)

### M3.2.2: Grafana Dashboard (`infra/grafana/dashboards/hydra-dashboard.json`)
- Provisioned dashboard JSON, auto-loaded by Grafana at startup
- Panels (Prometheus): request rate, active sessions, store ops/s, store bytes/s, save/restore p50/p95
- Logs panel (Loki): all service logs with label filters
- **Trace ID filter**: textbox variable `$trace_id` in dashboard — enter a trace ID to filter
- Docker compose: Prometheus + Loki + Grafana provisioned automatically
- **Done when:** `docker compose -f docker-compose.infra.yml -f docker-compose.hydra.yml up` → Grafana at `:3000` shows working dashboard

### M3.2.3: Request Trace Logging (Loki + Grafana)
- Serilog JSON stdout includes `@t`, `@mt`, `component`, `trace_id`, `source_context`
- Log pipeline: `container-log-shipper` (host systemd --user) tails `podman logs -f` to
  `/tmp/container-logs/<name>.log` → `promtail` (host systemd --user) scrapes files → Loki
- Promtail runs on the host (not in Docker) because podman 5.7.0's Docker API has a
  NULL-JSON bug breaking `docker_sd_configs`, and journald files are root-owned mode 600
- Grafana dashboard includes a **Trace ID** textbox variable `$trace_id`
  - Enter a trace ID → filter all log panels by `{service=~".*"} |~ "$trace_id"`
  - Shows the full request lifecycle across Store, Agent, and Coordinator
- No `hydra-tail` CLI — replaced by Grafana Explore or dashboard filter
- **Done when:** enter trace_id in dashboard → logs from all services appear

---

## Task M3.3: Langfuse Integration

### M3.3.1: Langfuse Trace Reporter (`coordinator/langfuse.py`)
- Optional: enabled only if LANGFUSE_PUBLIC_KEY is set
- On each completion: create Langfuse trace with:
  - Generation span: model, prompt tokens, completion tokens, latency
  - Routing metadata: node, reason, cache_hit, session_id
  - Migration span (if migration occurred): from_node, to_node, duration
- Uses langfuse Python SDK (async)
- **Lines:** ~60
- **Test:** mock langfuse client, verify correct spans created
- **Done when:** traces visible in Langfuse UI

---

## Task M3.4: Model Weight Distribution

### M3.4.1: Model Serving from Store
- Store accepts large files via PUT (raw, not chunked — model is immutable)
- `hydra-upload-model` CLI tool: reads GGUF from disk, PUTs to store
- Agent startup: if local model file missing, GET from store → write to local tmpfs
- Config: `model_key: str = "model/darwin-36b.gguf"`
- **Lines:** ~60 (CLI) + ~30 (agent startup check)
- **Test:** upload 100 MB test file → agent downloads on startup
- **Done when:** new agent node starts without manual model copy

### M3.4.2: systemd Service Units (`infra/`)
- `hydra-ramdisk.service` — mount tmpfs, copy model from NVMe
- `hydra-store.service` — start store after ramdisk
- `hydra-agent-rtx.service` — start agent + llama-server after store
- `hydra-agent-p100.service` — start agent + llama-server in VM
- `hydra-coordinator.service` — start coordinator after agents
- Boot order: ramdisk → store → agents → coordinator
- **Done when:** `sudo reboot` → system comes up automatically

---

## Task Summary

| Task    | Component   | Lines | Depends On | Priority  |
|---------|-------------|-------|------------|-----------|
| M3.1.1  | store       | 80    | M2         | High      |
| M3.1.2  | store       | 80    | M3.1.1     | High      |
| M3.1.3  | store       | 40    | M3.1.2     | High      |
| M3.2.1  | shared      | 60    | M1         | Medium    |
| M3.2.2  | infra       | —     | M3.2.1     | Medium    |
| M3.2.3  | infra       | —     | M1         | Medium    |
| M3.3.1  | coordinator | 60    | M1         | Low       |
| M3.4.1  | store+agent | 90    | M1         | Low       |
| M3.4.2  | infra       | —     | all above  | Medium    |

**Parallel work:** M3.1 (persistence), M3.2 (grafana), M3.3 (langfuse) are independent.
M3.4 (model distribution) can happen anytime after M1.
