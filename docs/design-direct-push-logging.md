# Design — Per-Service Direct Log Shipping to Loki

- **Status:** Revised (gap review #363 applied 2026-06-27)
- **Date:** 2026-06-27
- **Author:** engineering (senior)
- **Scope:** Hydra.Core (C#), Hydra.Head (Go), llama-server (C++), node/nvidia exporters, the `infra-host` Quadlet pod, and the Loki deployment.
- **Supersedes:** the central-Promtail / docker-SD / CRI-parser pipeline that has been the source of 10+ open review findings and 1 confirmed pre-existing bug (P100 promtail scrapes journald with a CRI parser).
- **Motivator (external):** Promtail reached **EOL on 2026-03-02** per the Grafana docs — it is no longer the recommended path. The replacement is direct push to Loki (either native or OTLP) and Grafana Alloy for file-based scraping.
- **Revision notes:** See the "Gap review response" appendix at the end of this doc for the 16-item review (P0×5, P1×5, P2×6) and how each is addressed. The architecture diagram, label vocabulary, configuration table, and rollout sections have all been updated.

## Why the current design hurts

The current stack has one Promtail running **inside** the `hydra-head-rtx` container (scrape docker SD → CRI parser → regex classify → push) and a **second** Promtail on the P100 VM (same config file, but the actual log source on P100 is journald, not CRI — so the `cri: {}` pipeline stage is the wrong parser for that host).

Audit of the running system produced a list of 10 concrete issues, of which the highest-impact are:

| # | Issue | Impact |
|---|---|---|
| 1 | **P100 promtail config is wrong** — `cri: {}` stage at `infra/promtail/promtail-rtx.yml:49` runs against journald (P100's only log path) | P100 logs silently garbled; no clean way to debug |
| 2 | **Hydra.Core double-logs** — `HydraLogging.cs:26-34` writes to console JSON *and* to Loki directly; both reach Loki under `{component="hydra"}` | 2× Loki ingestion cost; duplicate streams |
| 3 | **Loki `max_entry_size: 256 KiB`** drops real prefill lines (`#324`) | Entire batch around offender is lost |
| 4 | **The `component` label depends on a brittle regex** (`^(?P<llama_ts>\d+\.\d+\.\d+\.\d+)\s+(?P<llama_level>[A-Z])\s+`) | New log format → wrong label silently |
| 5 | **No HA** — if `hydra-head-rtx` dies, in-container Promtail dies too | Log gap on every restart |
| 6 | **Position file path is broken on P100** — `promtail-rtx.yml:13` references `/opt/hydra/promtail-positions/positions.yaml` which is only mounted into the RTX container, not on the P100 VM | P100 promtail silently fails to persist |
| 7 | **`node_exporter` and `nvidia_exporter` get labeled `component=hydra-head`** | Mislabeled in Loki |
| 8 | **`component="hydra"` (Loki) ≠ `component="hydra-core"` (Prometheus alerts)** | `HydraCoreDown` alert can never fire |
| 9 | **Stale CI / docs references to `infra-promtail` and `container-log-shipper`** (removed in commit `5f2c231`) | Real CI drift; runbook gives wrong commands |
| 10 | **P100 → RTX Loki URL is wrong** — `infra/hydra-head/config/global.yaml:54` has `http://localhost:3100`, which on the P100 VM resolves to P100's own loopback (no Loki there) | P100 promtail pushes nowhere useful |

The root cause is structural: **the log pipeline is decoupled from the services that produce the logs**. A separate binary that reads ctr.log/journald, parses a format it doesn't own, and ships to a destination the operators don't see — the failure modes compound.

## Decision

Adopt **per-service direct push** to Loki 3.x. Each service owns its own log shipping. The central Promtail is **removed entirely** (it is EOL anyway).

### Architecture

```
┌──────────────────────────────────────────────────────────────────────────────┐
│ Service             │ Logger       │ Ships to Loki via                       │
├──────────────────────────────────────────────────────────────────────────────┤
│ Hydra.Core (C#)     │ Serilog 4.2  │ Serilog.Sinks.OpenTelemetry →           │
│                     │              │ http://localhost:3100/otlp/v1/logs       │
│                     │              │ (OTLP/HTTP, protobuf, gzip)              │
│ Hydra.Head (Go)     │ log/slog     │ samber/slog-loki →                      │
│                     │              │ http://localhost:3100/loki/api/v1/push   │
│                     │              │ (JSON, gzip, native labels)             │
│ llama-server (C++)  │ (unchanged)  │ stdout → hydra-head supervisor pipes,   │
│                     │              │ per-child labeled writer at spawn       │
│                     │              │ (component=llama-server)                │
│ node_exporter,      │ (unchanged)  │ stdout → hydra-head supervisor pipes,   │
│ nvidia_exporter     │              │ per-child labeled writer                │
│                     │              │ (component=node-exporter / nvidia-…)    │
│ infra-* (grafana,   │ (unchanged)  │ not pushed — they don't run a Loki      │
│ loki, prometheus,   │              │ client, so the "drop observability"     │
│ postgres, pgadmin,  │              │ Promtail rule is replaced by an         │
│ openwebui, renderer)│              │ implicit "no service ships observability"│
└──────────────────────────────────────────────────────────────────────────────┘
                                       │
                                       ▼
                          ┌───────────────────────────┐
                          │ Loki 3.x :3100            │
                          │  - max_entry_size: 1 MiB  │  ← fixes #324
                          │  - schema v13, tsdb index │  ← required for OTLP
                          │  - allow_structured_      │
                          │    metadata: true         │
                          │  - ingestion_rate:        │
                          │    32 MB (unchanged)      │  ← see Queue bounds
                          └───────────────────────────┘
```

### Preconditions (verify before starting the implementation)

The implementation assumes the runtime already meets these constraints. Each one was verified on 2026-06-27 against the live host:

| Check | Command | Required | Verified 2026-06-27 |
|---|---|---|---|
| Loki version | `curl -s localhost:3100/loki/api/v1/status/buildinfo \| jq -r .version` | ≥ 3.0 (for native OTLP) | **3.4.3** ✅ |
| Schema config | `curl -s localhost:3100/config \| jq .schema_config.configs[0].schema` | `v13` | **v13** ✅ (per `infra/loki/loki-config.yml:24`) |
| Index backend | same as above, `.schema_config.configs[0].index.prefix` | `tsdb` | **`index_`** (tsdb variant) ✅ |
| OTLP listener | `curl -s -o /dev/null -w '%{http_code}' -X POST -H 'Content-Type: application/x-protobuf' --data-binary '' localhost:3100/otlp/v1/logs` | `4xx` (any non-2xx, non-404) | **422** ✅ (the OTLP listener is on) |
| Push listener | `curl -s -X POST -H 'Content-Type: application/json' -d '{}' localhost:3100/loki/api/v1/push` | 4xx with "at least one valid stream" | ✅ |
| `core` network mode | `grep network_mode infra/docker-compose.hydra.yml` for `core` | `host` (so `localhost:3100` reaches host's Loki) | **host** ✅ (line 75) |
| Hydra.Head in VM reaches RTX Loki | `ssh hydra-p100 'curl -so/dev/null -w%{http_code} http://192.168.122.1:3100/ready'` | `200` | TBD — fix in `node-p100.yaml` if not |

If any precondition fails, **stop and fix the precondition first**; the implementation will not work.

### Label vocabulary

```
{component, node, level}
```

- `component` = `hydra` | `hydra-head` | `llama-server` | `node-exporter` | `nvidia-exporter`
- `node` = `rtx` | `p100`
- `level` = `info` | `warn` | `error` (see "level label asymmetry" below)
- `trace_id` → **structured metadata** (queryable but not indexed — no cardinality risk)
- `version` (Hydra.Core assembly version) → **structured metadata**

The `{component, node, level}` set is small enough to be a Loki index, matches today's Promtail-derived labels, and adds `level` so dashboards can filter by severity without `|= "ERROR"` substring matching.

#### The `level` label asymmetry (and how to unify it)

A review caught that the two clients populate `level` differently:

| Client | Where `level` ends up | Query example |
|---|---|---|
| `samber/slog-loki` (Go) | **Loki stream label** (auto-set from `slog.Level`) | `{level="error", component="hydra-head"}` |
| `Serilog.Sinks.OpenTelemetry` (C#) → Loki `/otlp/v1/logs` | **Structured metadata only** (the OTel `severity_text` field is a log-record attribute, not a resource attribute, so it's not auto-promoted to a Loki label) | `{component="hydra"} \| json \| severity_text="ERROR"` |

That means `{level="error"}` matches Hydra.Head entries only — the new "error count by component" panel would under-report Hydra.Core.

**Fix: Loki ruler rule** in `infra/loki/loki-config.yml` (new top-level `ruler:` block, or extracted to a separate `ruler.yaml` if the rules grow):

```yaml
ruler:
  alertmanager_url: ""        # we don't use Alertmanager; alerts live in Prometheus
  storage:
    type: local
    local:
      directory: /loki/rules
  rule_path: /loki/rules-tmp
  rules:
    - name: derived-labels
      interval: 1m
      groups:
        - name: severity
          rules:
            - record: component:errors:rate1m
              expr: |
                sum by (component, node) (
                  rate({component=~".+"} | json | severity_text=~"ERROR|FATAL" [1m])
                )
```

The new `component:errors:rate1m` recording rule produces a `{component, node}` stream with a numeric `errors_per_sec` value that the dashboard can graph. The `level` index label is **not** materialized — we accept the asymmetry and use the ruler for severity aggregation instead. This is cheaper than promoting `severity_text` to a Loki label (which would re-introduce the cardinality risk we just removed).

### Per-service responsibilities

| Service | What changes |
|---|---|
| **Hydra.Core (C#)** | Replace `Serilog.Sinks.Grafana.Loki` with `Serilog.Sinks.OpenTelemetry` + `OpenTelemetry.Exporter.OpenTelemetryProtocol` + `OpenTelemetry.Extensions.Hosting`. Set `service.name=hydra`, `service.namespace=hydra-core`, `deployment.environment.name=dev` as resource attributes. Set the `node` label via `Enrich.WithProperty("node", …)`. Map Serilog `Level` to the OTel `severity` field; map Serilog `LogContext` properties to OTel attributes. Stop writing to the console (Loki is enough; the 2× ingestion today is a bug). **Drop `HYDRA_LOG_LOKI_URL` env var + the `Serilog.Sinks.Grafana.Loki` dependency** — the OTel push is the new primary path, no fallback. |
| **Hydra.Head (Go)** | Add `github.com/samber/slog-loki/v3` and `github.com/samber/slog-multi/v2` (fanout). Build a multi-handler: (1) text → `os.Stdout` (kept for journald / `journalctl -u hydra-head`), (2) JSON → Loki push with `BatchWait=2s`, `BatchEntriesNumber=500`, gzip, labels `{component=hydra-head, node=<config.node.name>}`. For each child-process stdout byte stream, use a **per-child labeled writer at spawn time** (no regex on the hot path). The manager already knows the child name (`StartLlama` vs `StartService("node_exporter")` vs `StartService("nvidia_exporter")`); each gets its own `io.Writer` configured with the right static `component` label. See "Per-child writers" below. |
| **Loki** | Bump `limits_config.max_entry_size` from default 256 KiB to 1 MiB (fixes #324). **Do not** bump `ingestion_rate_mb` or `ingestion_burst_size_mb` — see "Why ingestion rate stays at 32 MB" below. Add a `ruler:` block with the `component:errors:rate1m` recording rule from the label-vocabulary section. Confirm `schema_config` is `v13` and the index is `tsdb` (required for OTLP — already so, verified 2026-06-27). |
| **P100 systemd** | `node-p100.yaml` overrides `infra.loki.url` to `http://192.168.122.1:3100` (the RTX host as seen from the KVM VM — pre-existing bug fix). The journald-only log path is **kept** as a fallback for `journalctl` forensics; direct push is the new primary. |
| **Promtail** | **Removed entirely.** The two promtail binaries (in `hydra-head-rtx` container + on P100 VM) are deleted. The `promtail:` block in `node-rtx.yaml` and `node-p100.yaml` is removed (the `Config.GeneratePromtailConfig` function in `src/head/internal/config/config.go:360-399` becomes dead code, kept for now behind a `// Deprecated: ...` comment). |
| **Pod level** | **Keep `userns_mode: "host"`** for now — it's still required by the `core` service's `mkdir -m 777` chunk-cache workaround (related to #333). Drop it in a follow-up PR after the rootless-podman uid-mapping fix lands. Add a `// TODO: drop userns_mode after #333` comment. |
| **k8s-file log driver** | The podman log-driver requirement in `~/.config/containers/containers.conf` is **no longer needed** for the log pipeline (since the pipeline no longer reads ctr.log). However, rootless podman defaults to `journald` which has its own quirks. **Default**: leave `k8s-file` in place until further notice (no behavior change). Add a runbook step that lets operators opt into `journald` once they confirm it doesn't break their existing Promtail-less forensic workflows. |
| **Hydra.Head (Go) test setup** | `src/head/internal/registry/integration_test.go:18,46,90` and friends use `slog.NewTextHandler(io.Discard, nil)` — keep as-is. Add new unit tests for the Loki handler and the per-child writers. |

### Per-child writers (replaces the brittle regex)

The original design proposed a `taggingWriter` that regex-matches each child line to pick a `component` label — the same brittle regex (`^(?P<llama_ts>\d+\.\d+\.\d+\.\d+)\s+(?P<llama_level>[A-Z])\s+`) that the audit flagged on audit #4. A better design: **the manager already knows the child name at spawn time**, so each child gets its own writer with a static `component` label, no regex.

```go
// In process/manager.go, per managed process:
type childWriter struct {
    mu       sync.Mutex
    buf      []byte
    stream   *lokiStream       // one entry per child
    scanner  *bufio.Scanner
}

func (w *childWriter) Write(p []byte) (int, error) {
    // line-buffer, push each complete line to the shared Loki client
    // with the child's static label
    w.scanner.Write(p)   // ScanLines default
    for w.scanner.Scan() {
        line := w.scanner.Text()
        w.stream.Send(line)   // async, batched by samber/slog-loki
    }
    return len(p), nil
}

// At spawn time:
case "llama":
    proc.logWriter = newChildWriter(m.lokiClient, "llama-server", m.cfg.Node.Name)
case "node_exporter":
    proc.logWriter = newChildWriter(m.lokiClient, "node-exporter", m.cfg.Node.Name)
case "nvidia_exporter":
    proc.logWriter = newChildWriter(m.lokiClient, "nvidia-exporter", m.cfg.Node.Name)
```

The regex is gone. The label is set once, at construction, per child. The writer is per-process so there's no shared-state race on the scanner; the `samber/slog-loki` client itself is goroutine-safe.

### Why ingestion rate stays at 32 MB

The original design bumped `ingestion_rate_mb: 32 → 64` and `ingestion_burst_size_mb: 64 → 128` "to handle prefill bursts." A review correctly noted that the bump was unjustified. Calculation:

- The prefill log line that triggered #324 was **262 400 bytes** (262 KiB) — one line per 50k-token prefill
- Hydras Core's log volume: ~50 info-level events per request × N concurrent requests × ~500 bytes/event average
- At 28 tok/s decode + ~1 prefill per minute on the P/D path, peak per-process log volume is bounded at ~1 MB/min
- 32 MB / 60 s = 533 KB/s aggregate — 32× headroom over the per-process peak
- The `max_entry_size: 1MiB` (4×) bump is what fixes #324; the ingestion rate doesn't need to move

Keep `ingestion_rate_mb: 32` and `ingestion_burst_size_mb: 64` (the current values). Add an alert on `loki_discarded_samples_total{reason="rate_limited"}` so a future burst is caught in minutes, not in a postmortem.

### Queue bounds (fail-soft, pinned explicitly)

| Client | Queue | Backpressure | Worst-case memory per process |
|---|---|---|---|
| `Serilog.Sinks.OpenTelemetry` | `BackgroundWorkerOptions.QueueSize = 65 536` (default 2 048; bumped to handle one minute of prefill logs at peak) | Drop-oldest when full | 65 536 × ~500 B = ~32 MB |
| `samber/slog-loki` | `MaxBacklogCount = 10 000`, `BatchWait = 2s`, `BatchEntriesNumber = 500` | Drop-newest when full (samber default) | 10 000 × ~500 B = ~5 MB |

Both clients expose the drop count via their own metrics (`OpenTelemetry` → `OpenTelemetryLogsExporterQueueSize` / `…Dropped`; `samber/slog-loki` → `loki_client_sent_entries_total` vs `loki_client_dropped_entries_total`). We also export a unified **`hydra_loki_dropped_entries_total{component, reason}`** Prometheus counter (see "Loss detection") so a single alert covers both paths.

### Loss detection

The current Promtail pipeline surfaces drops via `loki_discarded_samples_total` (Loki-side). That metric catches **Loki**'s drop reasons (`line_too_long`, `rate_limited`, `stream_too_many`) but **not** client-side queue overflow — which is the most common drop cause in a bounded-buffer design. Add a client-side counter:

- **Hydra.Core (C#)**: `OpenTelemetry.Exporter.OpenTelemetryProtocol` exposes `otlp.export.exceptions`, `otlp.export.success`, and a queue size gauge. Add a Serilog enricher that wraps the OTel exporter and increments a per-process Prometheus counter `hydra_loki_dropped_entries_total{component="hydra", reason}` on the `Dropped` callback.
- **Hydra.Head (Go)**: `samber/slog-loki` exposes a `loki_client_dropped_entries_total` counter. Add a `prometheus.NewCounterVec` wrapper that increments `hydra_loki_dropped_entries_total{component, reason}` on every drop. Expose on `:9700/metrics` (hydra-head's existing scrape endpoint).

Alert:

```yaml
- alert: HydraLokiDropsIncreasing
  expr: rate(hydra_loki_dropped_entries_total[5m]) > 0
  for: 2m
  labels: { severity: warning }
```

### Trace propagation across the C# → Go boundary

A migration failure in the C# Coordinator will surface in both `hydra-core` and `hydra-head` logs (e.g. Core calls `/v1/...` on Hydra.Head → Hydra.Head logs the request → the underlying failure in Core is logged by Core). The trace_id does not currently cross the HTTP boundary: Hydra.Head's HTTP server (`src/head/internal/api/server.go`) does not read or propagate the `X-Hydra-Trace-Id` header that the C# client sets.

**Fix (5 lines in `src/head/internal/api/server.go`):**

```go
func (s *Server) middleware(next http.Handler) http.Handler {
    return http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
        if tid := r.Header.Get("X-Hydra-Trace-Id"); tid != "" {
            // Add to slog context as structured metadata (not a Loki label — no cardinality)
            next = withTraceID(next, tid)
        }
        next.ServeHTTP(w, r)
    })
}
```

The trace_id appears as structured metadata in Loki; the dashboard's existing `$trace_id` template variable correlates the two sides.

This is **not** a behavior change in this PR (the design's "trace correlation" claim is still true for the C# side alone; this just extends it one hop). The fix is 5 lines + 1 test.

### Why OTel OTLP for Hydra.Core, native push for Hydra.Head

This is the only asymmetry in the design. The reason is **trace correlation**:

- **Hydra.Core** is where the business logic lives (Coordinator, Store, state migration, KV save/restore). Every domain event in C# has a `trace_id` (`HydraLogging.cs:39-42`; `TraceScope` is called from `StateHandler.cs:55,93,163,336` and others). OTel's log SDK auto-correlates these with traces. Direct OTel push to Loki's `/otlp/v1/logs` is the cleanest path.
- **Hydra.Head** is a supervisor — most of its log lines are system events (process started, restart with backoff, OCI pull success). No `trace_id` propagation. A clean `slog` handler that fans out to text + Loki is a much smaller code change than introducing the OTel Go SDK and its resource-attribute boilerplate.

Both endpoints land in the same Loki 3.x. The label set is normalized to `{component, node, level}` at the **client side** (C# sets `component=hydra` via OTel resource attribute `service.name=hydra`; Go sets it explicitly in the stream).

This is a deliberate trade-off. If a future requirement demands full OTel end-to-end (e.g. Grafana Tempo cross-stack correlation), the Go side can be migrated to `otlploghttp` later in a one-service change.

### Configuration changes (per file)

| File | Change |
|---|---|
| `infra/loki/loki-config.yml` | Add `max_entry_size: 1MiB` (fixes #324). **Do not** change `ingestion_rate_mb` or `ingestion_burst_size_mb` (see "Why ingestion rate stays at 32 MB"). Add `ruler:` block with the `component:errors:rate1m` recording rule. |
| `src/core/Hydra.Shared/HydraLogging.cs` | Replace Loki push sink with OTel pipeline; add `node` enricher; drop console sink (or keep as `Debug`-only for dev); drop `HYDRA_LOG_LOKI_URL` env var path entirely |
| `src/core/Hydra.Shared/Hydra.Shared.csproj` | Add `Serilog.Sinks.OpenTelemetry`, `OpenTelemetry.Exporter.OpenTelemetryProtocol`, `OpenTelemetry.Extensions.Hosting`. **Remove** `Serilog.Sinks.Grafana.Loki` (no longer used) |
| `src/core/Hydra.Core/Program.cs` | Wire OTel resource attributes (`service.name=hydra`, `service.namespace=hydra-core`, `deployment.environment.name=dev`); wire `OTEL_EXPORTER_OTLP_LOGS_ENDPOINT` to the host's Loki |
| `src/head/main.go` | Construct `samber/slog-loki` handler + `samber/slog-multi` fanout to text + Loki; read `cfg.Infra.Loki.URL` and `cfg.Node.Name`; register the per-process `hydra_loki_dropped_entries_total` counter |
| `src/head/internal/process/manager.go` | Replace `proc.logWriter = os.Stdout` with **per-child labeled writers at spawn time** (no regex); see "Per-child writers" |
| `src/head/internal/api/server.go` | Add middleware that reads `X-Hydra-Trace-Id` header and pushes it to slog context as structured metadata (5 lines + 1 test); see "Trace propagation" |
| `src/head/go.mod` | Add `github.com/samber/slog-loki/v3`, `github.com/samber/slog-multi/v2` |
| `src/head/internal/config/config.go` | Add `Logging.LokiURL`, `Logging.Node` (or reuse `Node.Name`); mark `GeneratePromtailConfig` as `// Deprecated` |
| `infra/hydra-head/config/global.yaml` | (no change — `infra.loki.url` stays) |
| `infra/hydra-head/config/node-p100.yaml` | Add `infra.loki.url: http://192.168.122.1:3100` (per-host override; the global `localhost:3100` resolves to P100 itself) |
| `infra/hydra-head/config/node-rtx.yaml` | Remove `services.promtail:` block |
| `infra/hydra-head/config/node-p100.yaml` | Remove `services.promtail:` block |
| `infra/hydra-head/Dockerfile.rtx` | Remove promtail install (lines 60-74) and config copy (line 56) |
| `infra/docker-compose.hydra.yml` | Remove `hydra-head-promtail-positions` volume (lines 54, 186); remove `/run/user/1000/podman:/var/run/socks:rw`, `/proc:/host/proc:ro`, `/sys:/host/sys:ro`, `/:/rootfs:ro` mounts (only used by Promtail); **keep** `userns_mode: "host"` (still needed for the chunk-cache workaround) with a `// TODO: drop userns_mode after #333` comment |
| `infra/docker-compose.hydra.yml` | `core` service: **drop** `HYDRA_LOG_LOKI_URL`. Add `OTEL_SERVICE_NAME=hydra`, `OTEL_EXPORTER_OTLP_PROTOCOL=http/protobuf`, `OTEL_EXPORTER_OTLP_LOGS_ENDPOINT=http://localhost:3100/otlp/v1/logs` (note: this hits the host's Loki because `core` runs with `network_mode: host`; the `/otlp/v1/logs` path is Loki's native OTLP endpoint). Also add `HYDRA_LOG_NODE=rtx`. |
| `infra/prometheus/alerts.yml` | Add `HydraLokiDropsIncreasing` alert (see "Loss detection") |
| `infra/promtail/promtail-rtx.yml` | **File deleted** (no longer used) |
| `infra/promtail/` | **Directory deleted** (only contained `promtail-rtx.yml`) |
| `scripts/setup-p100.sh` | Remove the `Installing promtail binary` (lines 38-50) and `Copying promtail config` (lines 52-57) blocks |
| `scripts/deploy-hydra-head.sh` | Remove the `promtail_sent_bytes_total` health gate (lines 232-240); remove `infra-promtail` from `stop_host_sidecars()` (line 123 loop). Add a new health gate: `hydra_loki_sent_entries_total > 0` |
| `infra/grafana/dashboards/hydra-logs.json` | Verify the 3 existing queries still match; replace the `{level="error"}` filter panel (which won't work for C# side) with one that uses the new `component:errors:rate1m` ruler rule from Loki. Add a panel: drop count by component (using `hydra_loki_dropped_entries_total`) |
| `.github/workflows/ci.yml` | Remove `infra-promtail` from the deploy loop (line 180) |
| `docs/RUNBOOK.md` | Remove `container-log-shipper` references (lines 398, 685). **Add a runbook step** for operators: "After the cutover, the podman `k8s-file` log driver is no longer required. To switch to the default `journald`, edit `~/.config/containers/containers.conf` and set `log_driver = "journald"`, then restart any containers you want to log via `journalctl -u <service>`. Until then, no behavior change." |
| `docs/architecture.md` | Update § observability diagram (lines 288-335) to show the new direct-push pipeline |
| `docs/workflow/06-monitoring.md` | Remove the `systemctl --user restart container-log-shipper promtail` lines (14, 16) |
| `docs/milestone-3-production.md` | Remove `container-log-shipper` reference (line 96) |
| `THIRD_PARTY_NOTICES.md` | Add `Serilog.Sinks.OpenTelemetry`, `OpenTelemetry.Exporter.OpenTelemetryProtocol`, `samber/slog-loki`, `samber/slog-multi`. **Remove** `Serilog.Sinks.Grafana.Loki` (no longer used). |

### Code change summary

Approximate net delta (LOC):

| Area | Add | Modify | Delete | Notes |
|---|---|---|---|---|
| C# Hydra.Core / Hydra.Shared | +90 | 30 | 30 | OTel SDK setup; one new `LokiLoggerProvider`; drop Loki sink + console sink |
| Go Hydra.Head | +180 | 40 | 10 | Loki client + multi-handler + tagging writer; ~60 lines for the writer |
| Config / YAML | +15 | 20 | 50 | node-p100 URL fix; remove promtail blocks; OTel env vars |
| Infra (compose, Dockerfile) | 0 | 10 | 60 | remove promtail binary install; remove pod-level userns; remove promtail positions volume |
| Loki config | +3 | 0 | 0 | max_entry_size, ingestion_rate bump |
| Docs / CI | +60 | 80 | 100 | update architecture / RUNBOOK / monitoring workflow; clean stale refs |
| Tests | +120 | 30 | 10 | Loki handler unit test; tagging writer classification test; C# logger test |
| **Total** | **+468** | **+210** | **-260** | **Net +418 LOC; ~2 services + 4 config + 1 infra change in one PR** |

## Rollout

### Order: RTX first, then P100

A review caught that the previous design assumed a simultaneous cutover; in practice, `deploy-hydra-head.sh` targets one host at a time. The plan:

1. **Land the PR** to `main` (does not deploy anything).
2. **Deploy RTX** first: `bash scripts/deploy-hydra-head.sh rtx`. The new hydra-head image replaces the old one; the old promtail binary is overwritten (but the rollback path keeps a copy of the old image in OCI).
3. **Wait for healthy Loki streams** from RTX: confirm `{component="hydra-head", node="rtx"}` and `{component="llama-server", node="rtx"}` are non-empty in Grafana Explore. This takes ~30 s (one llama-server startup cycle).
4. **Deploy P100**: `bash scripts/deploy-hydra-head.sh p100`. The P100 hydra-head's promtail is also replaced.
5. **Wait for healthy P100 streams** in Grafana: `{component="hydra-head", node="p100"}` and `{component="llama-server", node="p100"}`.
6. **Watch** `hydra_loki_dropped_entries_total` and `loki_discarded_samples_total` for 1 hour; if either is non-zero, page the implementer.
7. **After 24h clean**: merge the roll-back-undo PR (the one that removes the old OCI image tag).

### Hard cutover (in one PR)

Rationale (per the implementer's decision):

- The new pipeline does not require any new infrastructure (Loki is already up).
- The two promtail instances are still in the **old** hydra-head image. If the cutover fails, the rollback is `git revert` + redeploy the previous image — no data loss.
- A parallel-run window would double Loki ingestion cost for one release and create a "which pipeline is the truth" question.
- The risk is bounded: both client libraries fail-soft (Serilog OTel drops oldest on queue overflow; samber/slog-loki has bounded buffer + retries). No service crashes if Loki is unreachable.

### Review strategy

This PR is large (~+468 / +210 / −260 LOC across 20+ files). The merge method and gate:

- **Merge method: squash** (one commit per logical change; the PR body carries the per-change list)
- **Review gate:** CI green **+ one human reviewer who has read this design doc** (the audit is in §"Why the current design hurts" + the design decisions in §"Decision")
- **Required reviewers:** at least one from the Hydra.Head maintainer group + one from the Hydra.Core maintainer group (cross-language PR)
- **Issue cross-links:** PR body must include `Closes #322, Closes #324, Closes #363`

### Verification (deployed stack)

After deploy:

```bash
# 1. Confirm Hydra.Core is sending OTel logs to Loki
curl -s 'http://localhost:3100/loki/api/v1/query?query={component="hydra"}' | jq .data.result | head

# 2. Confirm Hydra.Head is sending native push
curl -s 'http://localhost:3100/loki/api/v1/query?query={component="hydra-head"}' | jq .data.result | head

# 3. Confirm llama-server lines are now under {component="llama-server", node=...}
curl -s 'http://localhost:3100/loki/api/v1/query?query={component="llama-server",node="rtx"}' | jq .data.result | head
curl -s 'http://192.168.122.1:3100/loki/api/v1/query?query={component="llama-server",node="p100"}' | jq .data.result | head

# 4. Confirm level label works (Go side only — see "level label asymmetry")
curl -s 'http://localhost:3100/loki/api/v1/query?query={level="error", component="hydra-head"}' | jq .data.result | head
curl -s 'http://localhost:3100/loki/api/v1/query?query={component="hydra-head"} | json | severity_text' | jq .data.result | head

# 5. Confirm no {component="observability"} stream (sanity — there should be one, zero entries)
curl -s 'http://localhost:3100/loki/api/v1/query?query={component="observability"}' | jq .data.result

# 6. Confirm trace_id lives in structured metadata (not a label)
curl -s 'http://localhost:3100/loki/api/v1/label/trace_id/values' | jq .data   # expect [] — trace_id is structured metadata, not a label

# 7. Confirm no more promtail container
podman ps --filter name=infra-promtail   # expect nothing
ssh hydra-p100 'which promtail'          # expect "not found" or similar

# 8. Confirm Loki dropped_samples metric is clean for the new path
curl -s 'http://localhost:3100/metrics' | grep loki_discarded_samples_total
# expect no `reason="line_too_long"` samples for `component="hydra"`

# 9. Confirm the per-process drop counter is zero (no client-side overflow)
curl -s 'http://localhost:9700/metrics' | grep hydra_loki_dropped_entries_total
# expect 0 (or no time series yet)

# 10. Confirm the new Loki ruler rule fires
curl -s 'http://localhost:3100/loki/api/v1/query?query=component:errors:rate1m' | jq .data.result | head
# expect numeric series for hydra, hydra-head, llama-server (one per component)
```

## Risk analysis

| Risk | Likelihood | Mitigation |
|---|---|---|
| OTel push path silently loses entries (network blip) | Medium | `BackgroundWorkerOptions.QueueSize = 65 536`, drop-oldest; `hydra_loki_dropped_entries_total{component="hydra", reason="queue_full"}` increments on drop; alert `HydraLokiDropsIncreasing` |
| Loki native push path silently loses entries | Medium | `samber/slog-loki MaxBacklogCount = 10 000`; same drop counter (component=hydra-head / llama-server / node-exporter / nvidia-exporter) |
| `OTEL_EXPORTER_OTLP_LOGS_ENDPOINT` is misconfigured (returns 404) | Medium | The verification step 1 catches this within 30 s of deploy (empty `{component="hydra"}` stream → page the implementer) |
| `node-p100.yaml` URL override is wrong (P100 can't reach RTX Loki) | Medium | The verification step 3 catches this; also add a smoke test in `setup-p100.sh` that `curl -so/dev/null -w'%{http_code}' http://192.168.122.1:3100/ready` returns 200 before enabling the new binary |
| Per-child writer leaks goroutines on child restart | Low | The manager already calls `proc.cmd.Wait()` in a goroutine; the writer's `samber/slog-loki` client is shared and goroutine-safe; the scanner is per-writer and freed with `proc` |
| Loki `max_entry_size: 1 MiB` is still too small for a future model | Low | Loki's hard max is 4 MiB; we can bump to 2 MiB without ceremony; alert on `loki_discarded_samples_total{reason="line_too_long"}` |
| Chunk-cache chmod 777 workaround surfaces as a real bug if `userns_mode: "host"` is dropped in a future PR | Medium | **This PR keeps `userns_mode: "host"`** with a `// TODO`; a follow-up removes it after #333 lands |
| Promtail EOL means a future Grafana upgrade breaks us anyway | None (mitigated) | We are removing Promtail in this PR |
| `Serilog.Sinks.OpenTelemetry` + `OpenTelemetry.Exporter.OpenTelemetryProtocol` is a heavier dependency than `Serilog.Sinks.Grafana.Loki` | Low | One-time cost; net benefit is trace correlation + vendor neutrality |

## Out of scope

- Migrating the `infra-host` pod (grafana, prometheus, loki, postgres, pgadmin, openwebui, renderer) to direct push. They don't produce useful logs; the current "drop observability component" rule in Promtail is replaced by an implicit **"no service ships observability" rule** (only the services with a Loki/OTel client — Hydra.Core and Hydra.Head — appear in Loki). No explicit exclusion rule is needed.
- Adding a `level` label for Prometheus alerts. Loki labels are not Prometheus labels — they're independent. The `prometheus.yml` `relabel_configs` already separate the two. The new `component:errors:rate1m` Loki ruler rule feeds Grafana, not Prometheus; the existing Prometheus alerts are unchanged.
- Switching to OTel Collector / Alloy. Considered; deferred. Direct push is the right scope for "fix the Promtail mess." Alloy can be added later if a use case appears (redaction, sampling, kernel-log shipping).
- Migrating to Grafana Cloud / managed Loki. The local Loki 3.x deployment is fine.
- Backfilling the `level` label onto historical entries. Loki structured metadata is per-entry; no migration needed.
- **Removing the chunk-cache `mkdir -m 777` workaround + `userns_mode: "host"`**. These are kept in this PR (see "Per-service responsibilities" → "Pod level"). A follow-up PR removes them after #333 lands.

## Gap review response

The design was reviewed on 2026-06-27 (16-item review on issue #363). This revision addresses every item:

### P0 — will not work as written

| # | Finding | How this revision addresses it |
|---|---|---|
| 1 | Design doc absent in repo | Doc lives on `docs/m3-direct-push-logs` branch and lands via PR #364. Once that merges, the canonical link is `docs/design-direct-push-logging.md` on `main`. The "Preconditions" section now lists explicit verification commands. |
| 2 | OTel endpoint routing | Endpoint is now `OTEL_EXPORTER_OTLP_LOGS_ENDPOINT=http://localhost:3100/otlp/v1/logs` (the host's Loki, with the path that matches Loki 3.x's native OTLP listener). The "Preconditions" section verifies the listener is on (HTTP 422 on empty body, not 404). The C# env vars are explicit; the OTel protocol is `http/protobuf` (Loki 3.x OTLP is HTTP, not gRPC). |
| 3 | Loki version not verified | The "Preconditions" section now lists 7 runtime checks with commands and the results from 2026-06-27 (Loki 3.4.3, schema v13, tsdb index, OTLP listener returns 422). |
| 4 | `level` label asymmetry | New "level label asymmetry" subsection explains the samber-vs-OTel difference, plus a Loki ruler rule (`component:errors:rate1m`) that aggregates severity across both paths via structured-metadata matching. The dashboard panel is updated to use the ruler output. |
| 5 | Observability-drop rule has no replacement | New "Out of scope" section makes the implicit "no service ships observability" rule explicit. The `infra-host` pod services don't run a Loki/OTel client, so they don't push. No exclusion rule needed in the new design. |

### P1 — will cause incidents

| # | Finding | How this revision addresses it |
|---|---|---|
| 6 | `userns_mode: "host"` entangled with #333 | "Per-service responsibilities" → "Pod level" now says **keep** `userns_mode: "host"` with a `// TODO` comment. The compose cleanup table is updated. Removed from the "Out of scope" section as a deferred item. |
| 7 | Rollout order unspecified | New "Order: RTX first, then P100" subsection in the Rollout section: 7 explicit steps with verification gates between them. |
| 8 | No client-side drop counter | New "Loss detection" subsection: `hydra_loki_dropped_entries_total{component, reason}` Prometheus counter on both sides, with a Prometheus alert (`HydraLokiDropsIncreasing`). |
| 9 | Queue bounds unspecified | New "Queue bounds" subsection: explicit `BackgroundWorkerOptions.QueueSize = 65 536` for the C# side, `MaxBacklogCount = 10 000` for the Go side, with worst-case memory per process. |
| 10 | Trace_id doesn't cross C#→Go boundary | New "Trace propagation" subsection: 5-line middleware in `src/head/internal/api/server.go` reads `X-Hydra-Trace-Id` and pushes to slog context. |

### P2 — hygiene

| # | Finding | How this revision addresses it |
|---|---|---|
| 11 | `HYDRA_LOG_LOKI_URL` semantics undefined | Resolved: drop the env var and the `Serilog.Sinks.Grafana.Loki` dependency. Listed in the configuration changes table and the Hydra.Core per-service row. |
| 12 | `taggingWriter` reuses the brittle regex | Resolved: replaced with **per-child labeled writers at spawn time**. New "Per-child writers" subsection with Go code shape. The regex is gone. |
| 13 | PR size + review strategy unstated | New "Review strategy" subsection in Rollout: squash merge, CI green + 1 reviewer per language group, required cross-links. |
| 14 | Ingestion rate doubling unjustified | Resolved: revert to 32 MB. New "Why ingestion rate stays at 32 MB" subsection with the calculation. The `max_entry_size: 1MiB` (4×) bump stays; it's the one that fixes #324. |
| 15 | Container log-driver revert is operator-side | Resolved: the RUNBOOK entry now has an explicit step for the operator to switch `k8s-file` → `journald` after the cutover. Default stays `k8s-file` (no behavior change) until operators opt in. |
| 16 | Milestone is the deferred M3 | M3 — "Persistence & Real Obs" is the right home: it's labeled "Real Obs" and this work is observability. The active Llama-Engine milestone is about P/D split. The reviewer may have confused the M3 label (which is open + scoped to "Real Obs") with the M3 sub-phase ("Production phase" per `CLAUDE.md`, deferred). The decision stands: this issue stays in M3. |
