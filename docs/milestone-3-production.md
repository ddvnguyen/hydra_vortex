# Milestone 3 — Production Readiness

## Goal
Persistence across restarts. Grafana dashboards for monitoring. Langfuse integration
for LLM request tracing. Model weight distribution via Store (deferred from M0).

## What "Done" Means
```
✅ Reboot → KV sessions restored from NVMe within 30s
✅ Grafana dashboard shows: request latency, cache hit rate, migration count, GPU utilization
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

### M3.2.1: Prometheus Metrics (`shared/metrics.py`)
- Use `prometheus_client` library
- Coordinator metrics:
  - `hydra_requests_total` (counter, labels: node, reason)
  - `hydra_request_latency_seconds` (histogram, labels: node)
  - `hydra_cache_hit_total` (counter)
  - `hydra_migration_total` (counter, labels: from_node, to_node)
  - `hydra_migration_latency_seconds` (histogram)
  - `hydra_active_sessions` (gauge, labels: node)
- Store metrics:
  - `hydra_store_ops_total` (counter, labels: op)
  - `hydra_store_bytes_transferred` (counter, labels: direction)
  - `hydra_store_chunks_total` (gauge)
  - `hydra_store_dedup_ratio` (gauge)
- Agent metrics:
  - `hydra_agent_save_seconds` (histogram)
  - `hydra_agent_restore_seconds` (histogram)
  - `hydra_agent_slot_utilization` (gauge, labels: node)
- Each service exposes GET /metrics on its debug HTTP port
- **Lines:** ~60
- **Test:** verify metrics endpoint returns valid Prometheus format
- **Done when:** `curl :9501/metrics` returns counters

### M3.2.2: Grafana Dashboard (`infra/grafana/hydra-dashboard.json`)
- Provisioned dashboard JSON
- Panels: request rate, latency p50/p95/p99, cache hit rate, migration count,
  per-node slot utilization, store disk usage, dedup ratio
- Docker compose adds Prometheus + Grafana
- **Done when:** `docker compose up` shows working dashboard

### M3.2.3: Request Trace Logging
- Structured log enrichment: every log line includes trace_id, duration_ms, component
- Log aggregation: all services write to `/var/log/hydra/{component}.log`
- `hydra-tail` CLI tool: `hydra-tail --trace abc123` shows full request lifecycle
- **Lines:** ~40 (CLI tool)
- **Done when:** single trace_id shows complete flow across all 3 services

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
| M3.2.3  | shared      | 40    | M1         | Medium    |
| M3.3.1  | coordinator | 60    | M1         | Low       |
| M3.4.1  | store+agent | 90    | M1         | Low       |
| M3.4.2  | infra       | —     | all above  | Medium    |

**Parallel work:** M3.1 (persistence), M3.2 (grafana), M3.3 (langfuse) are independent.
M3.4 (model distribution) can happen anytime after M1.
