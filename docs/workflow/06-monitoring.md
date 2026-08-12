# 6. Monitoring: post-deploy + RUN-TIME protocol + test report

**Goal:** confirm a change didn't regress the live system AND drive test runs with
live telemetry — the user mandate from epic #470 (live telemetry during runs, not
just verdicts). Two distinct phases: (A) post-deploy check, (B) run-time
monitoring during LiveRig/E2E/agent-workload runs, (C) a clean report after each
run or on request.

---

## A. Post-deploy check (existing)

Details: `docs/monitoring-observability.md`.

1. **Grafana** (http://localhost:3000) — Hydra dashboard. Around your change:
   request rate / sessions, store ops & bytes, save/restore & migration latency,
   llama tokens/s & KV usage, host/GPU temp + memory, service up/down table.
2. **Alerts** — Prometheus (http://localhost:9091) + `infra/prometheus/alerts.yml`.
   No new firing alerts. `monitor.yml` auto-creates/closes `monitoring` issues —
   **do not close a monitoring issue without root-causing it**.
3. **OTel Collector** (host log gateway) — verify active:
   ```bash
   systemctl --user is-active infra-otel-collector
   curl -so/dev/null -w'%{http_code}\n' http://localhost:13133/
   ```
4. **Logs** — Grafana Explore (Loki); filter by `$trace_id` to follow a request
   across Coordinator / Hydra.Head / Store.
5. **Engine pin check** — every deploy: confirm all heads run the intended engine
   (system_fingerprint / digests, not just "healthy"). See #632 — a healthy head
   can silently run stale pins.

→ Next: `07-issue-and-close.md`

---

## B. Run-time monitoring protocol (MANDATORY during runs)

**When:** every LiveRig / E2E / agent-workload run. **Cadence:** sweep every
7-8 min (matches the lead heartbeat). **Who:** the lead/support does the sweep;
the heartbeat prompt carries the checklist.

### B.1 The sweep — 5 signal groups

| # | Signal | Source | What it means | Threshold / action |
|---|--------|--------|---------------|--------------------|
| 1 | **Queue depth / head age** | `hydra_main_queue_depth`, `hydra_queue_head_age_seconds` (:9501/metrics) | Requests queueing behind a busy rig | depth>0 sustained or head_age>30s → investigate slot occupancy |
| 2 | **Terminal errors** | `requests_total` vs `engine_prefill_fallbacks_total`, coordinator logs "terminal error", "pipeline_crashed" | RPC/engine failures | any terminal error → flag with trace |
| 3 | **p100 Gate-A rejects** | llama-server logs `Gate A reject` / `caps_xor=` | Model-identity mismatch on restore | any reject → #589/#609/#631 family check |
| 4 | **Slots / nodes** | head `/slots` + coordinator /status | Per-node capacity (rtx 2, rtx3060 0 by design, p100 1) | stuck_slots>0 (`is_processing && n_remain==0`) → watchdog check |
| 5 | **Memory / storage** | node exporter :9100, `chunk_cache_l1_bytes` (tmpfs), GPU :9835 | tmpfs/L1 near-full → save failures (#615 family) | L1 bytes > ~80% of tmpfs → eviction/LRU check |

**Signals that caught real bugs (epic #470 evidence):**
- queue_head_age 94s+ → the concurrency bug (Aug 10)
- `bg_save_failed` ×166 + tmpfs 100% → **#615** (never evicts) — invisible to pass/fail counts
- `restore_kv_merged_skip_state_put` + empty replies → #616/#622 reasoning drop
- healthy head running stale engine digest → #632 silent regression

### B.2 Classification rule (test calibration vs system regression)

When a test fails, ALWAYS check telemetry first:
1. **Rig regressed?** Any of: terminal errors, Gate-A rejects, queue explosion,
   tmpfs full, save failures, engine digest mismatch → **system issue**, file with
   evidence, do NOT edit the test.
2. **Test budget wrong?** Test failed but rig telemetry clean (0 errors, queues
   empty, restore working) → **test calibration** (timeout, threshold, max_tokens,
   eval keywords) — adjust the test.
3. **Ambiguous?** Re-run the single test before touching anything (state/order
   dependence is real: same binary, different result by order — dense-27b case).

### B.3 What NOT to do
- No exploratory code reading during the sweep — it is a telemetry pass, cheap by design.
- Do not close monitoring issues without root cause (per §A.2).
- Do not approve worker permission requests on the user's behalf (lead charter).

---

## C. Test report (after run completes, or on user request)

After each run (or when the user asks for status), produce a CLEAN report in this
shape — one block per test + a summary table + classified failures.

### C.1 Report shape

```markdown
## LiveRig run <run-id> — <date> <result summary>
- Runtime: <Xm Ys> | Engine: <tag/digest> | Core: <commit> | Branch: <ref>

| Test | Result | Duration | Key telemetry | Notes |
|------|--------|----------|---------------|-------|
| Smoke_WarmAffinityMultiturn | PASS | 54s | n_past 501→1435, model_load 0 | — |
| ... | FAIL | 1m17s | prompt_ms=5962 | calibration: threshold |

### Classified failures
| Test | Class | Evidence | Action |
|------|-------|----------|--------|
| Smoke_DenseMultiturnTiming | system (MTP ctx) | engine `failed to create MTP context` → T3 abort | #648 |

### Run telemetry summary
- Queue: depth 0, head_age 0s throughout (or peak + window)
- Terminal errors: 0 (or N + traces)
- Gate-A rejects: 0 (or N + model pairs)
- Save/restore: save_kv ok, restore hits, L1 bytes <X>/30G
- Slots: rtx 2/2 idle at end, p100 1/1, stuck 0
- Memory: peak <X>/91G
```

### C.2 Where it goes
- Full report → `orchestration/state/digests/YYYY-MM-DD.md` (daily) and/or the run
  journal entry in `orchestration/state/470-epic-liverig.md`.
- 1-3 line summary → the paseo thread / user chat.
- TRX artifact (CI runs) → parse + compare vs baseline run (e.g. 20/33).

### C.3 Report metric inventory (all real, :9501/metrics)

**Throughput & latency**
- `requests_total`, `request_latency_seconds`, `queue_wait_seconds`,
  `prefill_seconds`, `decode_seconds`, `decode_init_ms` (TTFT proxy),
  `upstream_timeouts_total`

**Routing & model**
- `active_sessions`, `warm_session_starts` / `cold_session_starts` /
  `migration_session_starts`, `cache_hits_total` / `cache_misses_total`,
  `cross_node_affinity_total` / `_skipped_total`, `mix_precision_enabled`,
  `model_identity_mismatch_total`, `model_load_seconds`

**KV / migration**
- `save_kv_seconds` (+ `save_kv_rpc_seconds`, `save_kv_store_seconds`),
  `restore_kv_seconds`, `restore_slot_ms`, `migrations_total`,
  `migration_latency_seconds`, `prefix_saves_total`, `prefix_save_failures_total`

**Caching (L1 tmpfs / L2 PG)**
- `chunk_cache_l1_bytes` / `_hits_total` / `_misses_total` / `_evicted_*`,
  `chunk_cache_l2_bytes` / `_hits` / `_misses` / `_evicted_*` / `_oldest_chunk_age_seconds`

**Multi-engine / COMBINED**
- `multiengine_attempts_total` / `_active_total` / `_fallback_total` /
  `_active_sessions`, `engine_peer_up`

**Health / queues / leaks**
- `main_queue_depth`, `prefill_queue_depth`, `decode_queue_depth`,
  `stuck_slots`, `worker_busy_seconds`, `slot_release_lag_seconds`,
  `slot_release_errors_total`, `engine_vs_coordinator_decode_ms`

**Fallbacks / incidents**
- `engine_prefill_fallbacks_total`, `model_reload_exceeded_documented_seconds`,
  `streaming_metrics_received_total`, `decode_request_ids_issued_total`

---

→ Next: `07-issue-and-close.md`
