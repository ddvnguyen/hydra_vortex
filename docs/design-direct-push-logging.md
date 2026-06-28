# Design — Per-Service Log Shipping via OTel Collector Gateway

- **Status:** Revised — OTel Collector gateway architecture (2026-06-27)
- **Date:** 2026-06-27
- **Author:** engineering (senior)
- **Scope:** Hydra.Core (C#), Hydra.Head (Go), llama-server (C++) + node/nvidia exporters, the `infra-host` Quadlet pod (one new container: OTel Collector), and the Loki deployment.
- **Supersedes:** the central-Promtail / docker-SD / CRI-parser pipeline that has been the source of 10+ open review findings and 1 confirmed pre-existing bug (P100 promtail scrapes journald with a CRI parser).
- **Motivator (external):** Promtail reached **EOL on 2026-03-02** per the Grafana docs. The Grafana-recommended replacement is direct push (native or OTLP) or Grafana Alloy for file-based scraping. We adopt **direct push to a single OTel Collector gateway** for the application log path.
- **Revision notes:** This is the second design revision on the `docs/m3-direct-push-logs` branch. The first revision (commit `09db377`) addressed the 16-item gap review with a direct-push design. The maintainer (ddvnguyen) chose to pivot to a gateway architecture after reviewing the asymmetry cost (separate Loki label path on Go side, OTel structured-metadata path on C# side, ruler rule as bridge, custom `X-Hydra-Trace-Id` middleware). This revision unifies all push paths through one OTel Collector and removes the bridge code.

## Why the current design hurts

The current stack has one Promtail running **inside** the `hydra-head-rtx` container (scrape `ctr.log` via the podman socket → cri parser → regex classify per line → push), and a **second** Promtail on the P100 VM (same config file, but the actual log source on P100 is **journald** — not `ctr.log` — so the `cri: {}` pipeline stage is the wrong parser for that host).

The audit (2026-06-27) uncovered **10 concrete issues**, of which the highest-impact are:

| # | Issue | File:line | Impact |
|---|---|---|---|
| 1 | **P100 promtail config is wrong** — `cri: {}` stage runs against journald, not `ctr.log` | `infra/promtail/promtail-rtx.yml:49` | P100 logs silently garbled; no clean way to debug |
| 2 | **Hydra.Core double-logs** — Serilog writes to console JSON *and* to Loki directly | `src/core/Hydra.Shared/HydraLogging.cs:26-34` | 2× Loki ingestion cost; duplicate streams |
| 3 | **Loki `max_entry_size: 256 KiB`** drops real prefill lines | `infra/loki/loki-config.yml` (no override; upstream default) | Whole batch around offender is lost (open: #324) |
| 4 | **`component` label depends on a brittle regex** | `infra/promtail/promtail-rtx.yml:68` | New log format → wrong label silently |
| 5 | **No HA** — if `hydra-head-rtx` dies, in-container Promtail dies too | (structural) | Log gap on every restart |
| 6 | **Position file path is broken on P100** | `infra/promtail/promtail-rtx.yml:13` | P100 promtail silently fails to persist |
| 7 | **`node_exporter` and `nvidia_exporter` get labeled `component=hydra-head`** | (Go process manager collapses child stdout; regex doesn't match) | Mislabeled in Loki |
| 8 | **`component="hydra"` (Loki) ≠ `component="hydra-core"` (Prometheus)** | `infra/prometheus/alerts.yml:6,13,19` | `HydraCoreDown` alert can never fire (deferred to separate follow-up — see "Out of scope") |
| 9 | **P100 → RTX Loki URL is wrong** — `http://localhost:3100` resolves to P100 itself | `infra/hydra-head/config/global.yaml:54` | P100 promtail pushes nowhere useful |
| 10 | **Stale CI / docs references to `infra-promtail` and `container-log-shipper`** (removed in `5f2c231`) | `.github/workflows/ci.yml:180`, `docs/RUNBOOK.md:398,685`, `docs/workflow/06-monitoring.md:14,16`, `docs/milestone-3-production.md:96` | Real CI drift; runbook gives wrong commands |

The root cause is structural: **the log pipeline is decoupled from the services that produce the logs**. A separate binary reads `ctr.log`/journald, parses a format it doesn't own, and ships to a destination the operators don't see — the failure modes compound.

## Decision

Adopt **per-service push to a single OTel Collector gateway**, which fans out to Loki 3.x.

```
┌──────────────────────────────────────────────────────────────────────────────┐
│ Service             │ Logger              │ Pushes via                       │
├──────────────────────────────────────────────────────────────────────────────┤
│ Hydra.Core (C#)     │ Serilog 4.2 +       │ OTLP HTTP →                      │
│                     │ OTel log SDK        │ http://localhost:4318/v1/logs    │
│ Hydra.Head (Go)     │ log/slog +          │ OTLP HTTP →                      │
│                     │ OTel log SDK        │ http://localhost:4318/v1/logs    │
│ llama-server (C++)  │ (unchanged)         │ stdout → hydra-head supervisor   │
│                     │                     │ per-child labeled writer at      │
│                     │                     │ spawn (sets service.name)        │
│ node_exporter,      │ (unchanged)         │ stdout → hydra-head supervisor   │
│ nvidia_exporter     │                     │ per-child labeled writer         │
│                     │                     │ (sets service.name)              │
│ infra-* (grafana,   │ (unchanged)         │ not pushed — they don't run an   │
│ loki, prometheus,   │                     │ OTel client                      │
│ postgres, pgadmin,  │                     │                                  │
│ openwebui, renderer)│                     │                                  │
└──────────────────────────────────────────────────────────────────────────────┘
                                       │
                                       ▼
                          ┌───────────────────────────┐
                          │ OTel Collector gateway    │
                          │ infra-host pod, :4318     │
                          │  (single container)       │
                          │                           │
                          │  receivers:               │
                          │    otlp: { http: 4318 }   │
                          │  processors:              │
                          │    batch                  │
                          │    memory_limiter         │
                          │  exporters:               │
                          │    loki →                 │
                          │      :3100/loki/api/v1/push│
                          │    prometheus :8888       │
                          └───────────────────────────┘
                                       │
                                       ▼
                          ┌───────────────────────────┐
                          │ Loki 3.x :3100            │
                          │  - max_entry_size: 1 MiB  │  ← fix #324
                          │  - ingestion_rate: 32 MB  │  ← see "Why ingestion rate stays at 32 MB"
                          │  - schema v13, tsdb index │  ← required for OTLP
                          │  - allow_structured_      │
                          │    metadata: true         │
                          └───────────────────────────┘
```

**P100 services** push to `http://192.168.122.1:4318` (the same RTX collector over the network). There is **one** OTel Collector for the whole system — it runs in the `infra-host` pod on the RTX host.

### Why one OTel Collector instead of direct push to Loki

A first-pass design (commit `09db377`) used **direct push to Loki** with two different client libraries: `Serilog.Sinks.OpenTelemetry` on the C# side, `samber/slog-loki` on the Go side. That works, but it creates asymmetry:

- C# logs land in Loki with `severity_text` in **structured metadata** (OTel-native, not a label).
- Go logs land in Loki with `level` as a **stream label** (samber-native, not metadata).

Bridging this requires either (a) a Loki ruler rule to aggregate severity across both paths, or (b) a custom Serilog sink that mimics samber's label behavior. Both are bridge code that the gateway makes unnecessary.

The gateway unifies on one push protocol (OTLP), one client library per service (`Serilog.Sinks.OpenTelemetry` on C#, `otlploghttp` on Go), one label vocabulary. Adding Tempo or S3 later is a one-line change in the collector config, not a service change.

### Preconditions (verify before starting the implementation)

The implementation assumes the runtime meets these constraints. Each was verified on 2026-06-27 against the live host:

| Check | Command | Required | Verified 2026-06-27 |
|---|---|---|---|
| Loki version | `curl -s localhost:3100/loki/api/v1/status/buildinfo \| jq -r .version` | ≥ 3.0 (for native OTLP) | **3.4.3** ✅ |
| Schema config | `curl -s localhost:3100/config \| jq .schema_config.configs[0].schema` | `v13` | **v13** ✅ (`infra/loki/loki-config.yml:24`) |
| Index backend | same as above, `.schema_config.configs[0].index.prefix` | `tsdb` | **`index_`** (tsdb variant) ✅ |
| OTLP listener | `curl -s -o /dev/null -w '%{http_code}\n' -X POST -H 'Content-Type: application/x-protobuf' --data-binary '' localhost:3100/otlp/v1/logs` | 4xx non-404 | **422** ✅ |
| Push listener | `curl -s -X POST -H 'Content-Type: application/json' -d '{}' localhost:3100/loki/api/v1/push` | 4xx with "at least one valid stream" | ✅ |
| `core` network_mode | `grep network_mode infra/docker-compose.hydra.yml` for `core` | `host` | **host** ✅ (line 75) |
| OTel contrib image pullable | `podman pull --authfile ~/.config/containers/auth.json otel/opentelemetry-collector-contrib:latest` | exit 0 | **192 MB pulled** ✅ |
| Port 4318 free on RTX | `ss -tln \| grep :4318` | empty | **free** ✅ |
| Port 4317 free on RTX | `ss -tln \| grep :4317` | empty | **free** ✅ |
| P100 → RTX collector | `ssh hydra-p100 'curl -so/dev/null -w%{http_code} http://192.168.122.1:4318/'` | 200 (after deploy) | **000** (no collector yet — expected; lands in Phase 2) |
| P100 → RTX Loki | `ssh hydra-p100 'curl -so/dev/null -w%{http_code} http://192.168.122.1:3100/ready'` | 200 | **200** ✅ |
| `allow_structured_metadata` | `curl -s localhost:3100/config \| jq .limits_config.allow_structured_metadata` | `true` | **`true`** ✅ |

If any precondition fails, **stop and fix the precondition first**; the implementation will not work.

### Label vocabulary (uniform across all services)

```
{component, node, level}
```

- `component` = `hydra` | `hydra-head` | `llama-server` | `node-exporter` | `nvidia-exporter` (set via OTel resource attribute `service.name`)
- `node` = `rtx` | `p100` (set via OTel resource attribute `service.instance.id` mapped to a `node` label by the OTel Collector's `transform` processor)
- `level` = `info` | `warn` | `error` (set via OTel resource attribute `service.level` mapped to a Loki label by the collector; same path for C# and Go)
- `trace_id` → **structured metadata** (queryable but not indexed; W3C trace context propagated natively by the OTel SDK on both sides)
- `version` → **structured metadata** (Hydra.Core assembly version, Hydra.Head binary version)

The unified OTel path means **no label asymmetry, no Loki ruler rule, no custom middleware for trace_id** — all of that bridge code disappears with the gateway.

### OTel Collector (single container for the whole system)

The OTel Collector runs in the `infra-host` Quadlet pod on the RTX host, port `4318` bound to the host's network. P100 services reach it at `http://192.168.122.1:4318`. There is no collector on P100.

**Quadlet file** (new): `infra/quadlets/infra-otel-collector.container`

```ini
[Unit]
Description=OTel Collector — per-service log gateway
BindsTo=infra-host-pod.service
After=infra-host-pod.service

[Container]
ContainerName=infra-otel-collector
Image=otel/opentelemetry-collector-contrib:latest
Pod=infra-host.pod
Volume=/mnt/WorkDisk/Workplace/hydra_vortex/infra/otel-collector/config.yaml:/etc/otelcol/config.yaml:ro
Exec=--config=/etc/otelcol/config.yaml
HealthCmd=wget -q -O /dev/null http://localhost:13133/
HealthInterval=15s
HealthRetries=3
HealthStartPeriod=10s
HealthTimeout=5s

[Service]
Restart=on-failure

[Install]
WantedBy=default.target
```

**Collector config** (new): `infra/otel-collector/config.yaml`

```yaml
receivers:
  otlp:
    protocols:
      http: { endpoint: 0.0.0.0:4318 }

processors:
  batch:
    timeout: 5s
    send_batch_size: 8192
  memory_limiter:
    check_interval: 1s
    limit_percentage: 80
    spike_limit_percentage: 25
  transform:
    trace_logs:
      # Map OTel resource attributes to Loki stream labels.
      # (component, node) and (level) come from the OTel SDK; the
      # collector normalizes them to the {component, node, level}
      # Loki label vocabulary.
      log_statements:
        - context: log
          statements:
            - set(attributes["component"], resource["service.name"])
            - set(attributes["node"], resource["service.instance.id"])
            - set(attributes["level"], resource["service.level"])

exporters:
  loki:
    endpoint: http://localhost:3100/loki/api/v1/push
  prometheus:
    endpoint: 0.0.0.0:8888
    resource_to_telemetry_conversion:
      enabled: true

service:
  pipelines:
    logs:
      receivers: [otlp]
      processors: [memory_limiter, transform, batch]
      exporters: [loki]
  telemetry:
    metrics: { address: 0.0.0.0:8888 }
```

(Notes: the `transform` processor is a sketch; the implementation may use the more idiomatic `resource/log` mapping in the `loki` exporter's `default_labels_enabled` block. The full implementation lands in Phase 2.)

### Per-service responsibilities

| Service | What changes |
|---|---|
| **Hydra.Core (C#)** | Replace `Serilog.Sinks.Grafana.Loki` with `Serilog.Sinks.OpenTelemetry` + `OpenTelemetry.Exporter.OpenTelemetryProtocol` + `OpenTelemetry.Extensions.Hosting`. Set OTel resource attributes `service.name=hydra`, `service.namespace=hydra-core`, `service.instance.id=<node>`, `deployment.environment.name=dev`, `service.level=<level>`. Set the `node` via `Enrich.WithProperty("node", …)` (env-driven: `HYDRA_LOG_NODE`). Map Serilog `Level` to the OTel `service.level` resource attribute. Map Serilog `LogContext` properties to OTel attributes. **Stop writing to the console** (Loki is enough; the 2× ingestion today is a bug). |
| **Hydra.Head (Go)** | Drop `github.com/samber/slog-loki/v3` and `github.com/samber/slog-multi/v2`. Add `go.opentelemetry.io/otel/sdk/log` + `go.opentelemetry.io/otel/exporters/otlp/otlplog/otlploghttp`. Build an OTel log handler that exports OTLP HTTP to `cfg.OTel.URL` (default `http://localhost:4318`, P100 override `http://192.168.122.1:4318`). Set OTel resource attributes: `service.name=hydra-head`, `service.namespace=hydra-core`, `service.instance.id=<cfg.Node.Name>`, `service.level=<level>`. For each child-process stdout byte stream (`llama-server`, `node_exporter`, `nvidia_exporter`), replace `proc.LogWriter = os.Stdout` with a **per-child labeled writer at spawn time** (no regex on the hot path). The manager already knows the child name (`StartLlama` vs `StartService("node_exporter")`); each gets its own OTel log record producer with the right `service.name` resource attribute. |
| **OTel Collector** | New container in the `infra-host` pod. Receives OTLP HTTP on `:4318`, normalizes resource attributes to Loki labels via `transform` processor, fans out to Loki (`/loki/api/v1/push`) and Prometheus (`:8888`). |
| **Loki** | Bump `limits_config.max_entry_size` from default 256 KiB to 1 MiB (fixes #324). **Do not** bump `ingestion_rate_mb` (32 → 32) or `ingestion_burst_size_mb` (64 → 64) — see "Why ingestion rate stays at 32 MB". Confirm `schema_config` is `v13` and the index is `tsdb` (required for OTLP — already so, verified 2026-06-27). |
| **P100 systemd** | `node-p100.yaml` overrides `cfg.OTel.URL` to `http://192.168.122.1:4318` (the RTX collector). The journald-only log path is **kept** as a fallback for `journalctl` forensics; OTLP push is the new primary. |
| **Promtail** | **Removed entirely.** The two promtail binaries (in `hydra-head-rtx` container + on P100 VM) are deleted. The `promtail:` block in `node-rtx.yaml` and `node-p100.yaml` is removed. The `Config.GeneratePromtailConfig` function in `src/head/internal/config/config.go:360-399` is marked `// Deprecated: per-service direct push since 2026-06`; the test in `config_test.go:248-263` stays. |
| **Pod level** | **Keep `userns_mode: "host"`** for now — the `core` service's `mkdir -m 777` chunk-cache workaround still needs it (related to #333). Drop it in a follow-up PR after the rootless-podman uid-mapping fix lands. Add a `// TODO: drop userns_mode after #333` comment. |
| **k8s-file log driver** | The podman log-driver requirement in `~/.config/containers/containers.conf` is **no longer needed** for the log pipeline (since the pipeline no longer reads `ctr.log`). **Default**: leave `k8s-file` in place until further notice (no behavior change). Add a runbook step that lets operators opt into `journald` once they confirm it doesn't break their forensic workflows. |

### Per-child writers (replaces the brittle regex — unchanged from 09db377)

The original Promtail pipeline regex-matched each child line to pick a `component` label — the same brittle regex (`^(?P<llama_ts>\d+\.\d+\.\d+\.\d+)\s+(?P<llama_level>[A-Z])\s+`) that audit #4 flagged. The new design removes the regex entirely: **the manager already knows the child name at spawn time**, so each child gets its own OTel log producer with a static `service.name` resource attribute.

```go
// In process/manager.go, per managed process:
func (m *Manager) spawnChild(name string) {
    var serviceName string
    switch name {
    case "llama":
        serviceName = "llama-server"
    case "node_exporter":
        serviceName = "node-exporter"
    case "nvidia_exporter":
        serviceName = "nvidia-exporter"
    default:
        serviceName = "hydra-head"
    }

    // Each child gets its own OTel log producer with a static
    // service.name. The producer is goroutine-safe and shared across
    // the child's lifetime.
    producer := m.otelLogProvider.Producer(serviceName, m.cfg.Node.Name)
    proc := &managedProc{
        name:      name,
        cmd:       exec.Command(bin, args...),
        logWriter: newChildWriter(producer),  // each child's stdout
                                            // is shaped into OTel
                                            // log records with the
                                            // right service.name
    }
    proc.cmd.Stdout = proc.logWriter
    proc.cmd.Stderr = proc.logWriter
    m.procs[name] = proc
}
```

The regex is gone. The `service.name` is set once, at construction, per child. The OTel log producer is goroutine-safe; the per-writer `bufio.Scanner` is freed with `proc`.

### Why ingestion rate stays at 32 MB

A first-pass design bumped `ingestion_rate_mb: 32 → 64` and `ingestion_burst_size_mb: 64 → 128` "to handle prefill bursts." A review correctly noted that the bump was unjustified. Calculation:

- The prefill log line that triggered #324 was **262 400 bytes** (262 KiB) — one line per 50k-token prefill.
- Hydra.Core's log volume: ~50 info-level events per request × N concurrent requests × ~500 bytes/event average.
- At 28 tok/s decode + ~1 prefill per minute on the P/D path, peak per-process log volume is bounded at ~1 MB/min.
- 32 MB / 60 s = 533 KB/s aggregate — 32× headroom over the per-process peak.
- The `max_entry_size: 1MiB` (4×) bump is what fixes #324; the ingestion rate doesn't need to move.

Keep `ingestion_rate_mb: 32` and `ingestion_burst_size_mb: 64` (the current values). Add an alert on `loki_discarded_samples_total{reason="rate_limited"}` so a future burst is caught in minutes, not in a postmortem.

### Queue bounds (fail-soft, pinned explicitly)

| Client | Queue | Backpressure | Worst-case memory per process |
|---|---|---|---|
| `Serilog.Sinks.OpenTelemetry` (C#) | `BackgroundWorkerOptions.QueueSize = 65 536` (default 2 048; bumped to handle one minute of prefill logs at peak) | Drop-oldest when full | 65 536 × ~500 B = ~32 MB |
| `otlploghttp` (Go) | `QueueSize = 65 536` (matches C#; default is unbounded) | Drop-oldest when full | 65 536 × ~500 B = ~32 MB |
| OTel Collector `batch` processor | `send_batch_size = 8192`, `timeout = 5s` | Backpressure when exporter is slow | 8 192 × ~500 B = ~4 MB per pipeline |

Both services expose the drop count via their own OTel metrics. We also export a unified **`hydra_loki_dropped_entries_total{component, reason}`** Prometheus counter (see "Loss detection") so a single alert covers both paths.

### Loss detection

The current Promtail pipeline surfaces drops via `loki_discarded_samples_total` (Loki-side). That metric catches **Loki**'s drop reasons (`line_too_long`, `rate_limited`, `stream_too_many`) but **not** client-side queue overflow — which is the most common drop cause in a bounded-buffer design. Add a client-side counter:

- **Hydra.Core (C#)**: `OpenTelemetry.Exporter.OpenTelemetryProtocol` exposes `otlp.export.exceptions`, `otlp.export.success`, and a queue size gauge. Add a Serilog enricher that wraps the OTel exporter and increments a per-process Prometheus counter `hydra_loki_dropped_entries_total{component="hydra", reason}` on the `Dropped` callback.
- **Hydra.Head (Go)**: the OTel Go log SDK exposes a `otelcol_exporter_send_failed_log_records` metric. Add a `prometheus.NewCounterVec` wrapper that increments `hydra_loki_dropped_entries_total{component, reason}` on every drop. Expose on `:9700/metrics` (hydra-head's existing scrape endpoint).
- **OTel Collector**: the `loki` exporter exposes `otelcol_exporter_sent_log_records`, `otelcol_exporter_send_failed_log_records`, and the `batch` processor exposes `otelcol_processor_batch_batch_send_size` / `otelcol_processor_batch_timeout_trigger_send`. Scrape on `:8888/metrics`.

Alert:

```yaml
- alert: HydraLokiDropsIncreasing
  expr: rate(hydra_loki_dropped_entries_total[5m]) > 0
  for: 2m
  labels: { severity: warning }
```

### Trace propagation (native via W3C trace context)

The OTel SDK on both C# and Go sides implements W3C trace context propagation automatically:

- C# side: `Activity` API (built into .NET) produces and injects `traceparent` headers on outgoing HTTP requests.
- Go side: `otelhttp` middleware (or equivalent) extracts `traceparent` from incoming HTTP requests and creates a `context.Context` that the OTel log SDK reads to attach the trace_id to each log record.

The OTel Collector sees the same `trace_id` field on log records from both sides, regardless of which service produced them. **No custom `X-Hydra-Trace-Id` middleware is needed.** This is one of the two reasons the gateway architecture is preferred over the direct-push design.

The trace_id appears as structured metadata in Loki (not a label), queryable via `{component="hydra"} | json | trace_id="<id>"`. The dashboard's `$trace_id` template variable correlates logs across services.

### Configuration changes (per file)

| File | Change |
|---|---|
| `infra/loki/loki-config.yml` | Add `max_entry_size: 1MiB` (fixes #324). **Do not** change `ingestion_rate_mb` or `ingestion_burst_size_mb` (see "Why ingestion rate stays at 32 MB"). |
| `infra/otel-collector/config.yaml` (new) | OTLP HTTP receiver on `:4318`; `transform` processor maps OTel resource attributes to Loki stream labels; `loki` exporter to `http://localhost:3100/loki/api/v1/push`; `prometheus` exporter on `:8888`. |
| `infra/quadlets/infra-otel-collector.container` (new) | Quadlet for the OTel Collector, joins the `infra-host` pod. |
| `scripts/start-infra.sh` | Install + start the new Quadlet alongside existing infra-host services. |
| `src/core/Hydra.Shared/Hydra.Shared.csproj` | Add `Serilog.Sinks.OpenTelemetry`, `OpenTelemetry.Exporter.OpenTelemetryProtocol`, `OpenTelemetry.Extensions.Hosting`. **Remove** `Serilog.Sinks.Grafana.Loki`. |
| `src/core/Hydra.Shared/HydraLogging.cs` | Replace `WriteTo.GrafanaLoki(...)` with OTel pipeline; set resource attributes; drop console JSON sink. |
| `src/core/Hydra.Core/Program.cs` | Wire OTel resource attributes; wire `OTEL_EXPORTER_OTLP_ENDPOINT` env var. |
| `src/head/go.mod` | Add `go.opentelemetry.io/otel/sdk/log`, `go.opentelemetry.io/otel/exporters/otlp/otlplog/otlploghttp`. **Remove** `github.com/samber/slog-loki/v3` and `github.com/samber/slog-multi/v2`. |
| `src/head/internal/logging/otel_handler.go` (new) | OTel log SDK handler with OTLP HTTP exporter; sets `service.name`, `service.namespace`, `service.instance.id`, `service.level` resource attributes. |
| `src/head/main.go` | Wire the OTel handler as the slog backend; read `cfg.OTel.URL`. |
| `src/head/internal/process/manager.go` | Replace `proc.LogWriter = os.Stdout` with per-child labeled writers at spawn time (see "Per-child writers"). |
| `src/head/internal/config/config.go` | Add `OTel.URL` config field; mark `GeneratePromtailConfig` as `// Deprecated`. |
| `infra/hydra-head/config/global.yaml` | Add `infra.otel.url: http://localhost:4318` (default for RTX). |
| `infra/hydra-head/config/node-p100.yaml` | Override `infra.otel.url: http://192.168.122.1:4318` (P100 reaches RTX collector over the network). Remove `services.promtail:` block. |
| `infra/hydra-head/config/node-rtx.yaml` | Remove `services.promtail:` block. |
| `infra/hydra-head/Dockerfile.rtx` | Remove promtail install (lines 60-74) and config copy (line 56). |
| `infra/docker-compose.hydra.yml` | `core` service: set `OTEL_SERVICE_NAME=hydra`, `OTEL_EXPORTER_OTLP_ENDPOINT=http://localhost:4318`, `OTEL_EXPORTER_OTLP_PROTOCOL=http/protobuf`, `HYDRA_LOG_NODE=rtx`. **Keep** `userns_mode: "host"` (line 50) with `// TODO: drop userns_mode after #333` comment. Remove `hydra-head-promtail-positions` volume, `/run/user/1000/podman:/var/run/socks:rw`, `/proc:/host/proc:ro`, `/sys:/host/sys:ro`, `/:rootfs:ro` mounts. |
| `scripts/setup-p100.sh` | Remove the `Installing promtail binary` (lines 38-50) and `Copying promtail config` (lines 52-57) blocks. |
| `scripts/deploy-hydra-head.sh` | Remove `infra-promtail` from the deploy loop (line 123). Replace the `promtail_sent_bytes_total` health gate (lines 232-240) with `hydra_loki_sent_entries_total > 0` (a metric the OTel Collector exposes on `:8888/metrics`). |
| `infra/grafana/dashboards/hydra-logs.json` | Verify the 3 existing queries still match: `{component="hydra"}` (C# sets `service.name=hydra`), `{component="llama-server", node="rtx"}`, `{component="llama-server", node="p100"}`. Add a new panel: drop count by component using `hydra_loki_dropped_entries_total`. |
| `.github/workflows/ci.yml:180` | Remove `infra-promtail` from the deploy loop. |
| `docs/RUNBOOK.md` | Remove `container-log-shipper` references (lines 398, 685). **Add a runbook section** for the OTel Collector (start, stop, restart, health check on `:13133`, log path). **Add a runbook step** for operators: "After the cutover, the podman `k8s-file` log driver is no longer required. To switch to the default `journald`, edit `~/.config/containers/containers.conf` and set `log_driver = "journald"`, then restart any containers you want to log via `journalctl -u <service>`. Until then, no behavior change." |
| `docs/architecture.md` | Update § observability diagram (lines 288-335) to show the new gateway pipeline. |
| `docs/workflow/06-monitoring.md` | Remove the `systemctl --user restart container-log-shipper promtail` lines (14, 16). Add an OTel Collector health-check runbook step. |
| `docs/milestone-3-production.md` | Remove `container-log-shipper` reference (line 96). |
| `docs/hydra-system-pod.md` | Note that the `k8s-file` log driver requirement is **no longer needed** (operators can revert to `journald` default). |
| `THIRD_PARTY_NOTICES.md` | Add `Serilog.Sinks.OpenTelemetry`, `OpenTelemetry.Exporter.OpenTelemetryProtocol`, `go.opentelemetry.io/otel/sdk/log`, `go.opentelemetry.io/otel/exporters/otlp/otlplog/otlploghttp`. **Remove** `Serilog.Sinks.Grafana.Loki`. |

### Implementation checklist (in PR order)

The next dev should land these in **one PR** (hard cutover — see "Rollout" below). Each sub-task is one commit.

- [ ] **Loki config** — `infra/loki/loki-config.yml`:
  - `max_entry_size: 1MiB` (fixes #324)
  - `ingestion_rate_mb: 32` (unchanged)
  - `ingestion_burst_size_mb: 64` (unchanged)
- [ ] **OTel Collector Quadlet** — `infra/quadlets/infra-otel-collector.container` (new) + `infra/otel-collector/config.yaml` (new) + `scripts/start-infra.sh` (install Quadlet)
- [ ] **Hydra.Head deps** — `src/head/go.mod`:
  - Add `go.opentelemetry.io/otel/sdk/log`, `go.opentelemetry.io/otel/exporters/otlp/otlplog/otlploghttp`
  - Remove `github.com/samber/slog-loki/v3`, `github.com/samber/slog-multi/v2`
- [ ] **OTel log handler (Go)** — `src/head/internal/logging/otel_handler.go` (new) — OTel log SDK handler with OTLP HTTP exporter; sets resource attributes
- [ ] **Wire OTel handler in main** — `src/head/main.go` — replace the samber handler with the OTel handler
- [ ] **Per-child labeled writers (structural fix)** — `src/head/internal/process/manager.go` — replace `proc.LogWriter = os.Stdout` with `proc.LogWriter = newChildWriter(otelProducer, "llama-server", cfg.Node.Name)` etc.
- [ ] **Hydra.Core deps** — `src/core/Hydra.Shared/Hydra.Shared.csproj`:
  - Add `Serilog.Sinks.OpenTelemetry`, `OpenTelemetry.Exporter.OpenTelemetryProtocol`, `OpenTelemetry.Extensions.Hosting`
  - Remove `Serilog.Sinks.Grafana.Loki`
- [ ] **Hydra.Core OTel pipeline** — `src/core/Hydra.Shared/HydraLogging.cs`, `src/core/Hydra.Core/Program.cs`:
  - Replace `WriteTo.GrafanaLoki(...)` with OTel pipeline
  - Set resource attributes (`service.name=hydra`, `service.namespace=hydra-core`, `service.instance.id=<node>`, `deployment.environment.name=dev`, `service.level=<level>`)
  - Drop console JSON sink
  - Add `node` enricher (env-driven: `HYDRA_LOG_NODE`)
  - Map Serilog `Level` to `service.level` resource attribute
- [ ] **Hydra.Core compose** — `infra/docker-compose.hydra.yml`:
  - `OTEL_SERVICE_NAME=hydra`
  - `OTEL_EXPORTER_OTLP_ENDPOINT=http://localhost:4318`
  - `OTEL_EXPORTER_OTLP_PROTOCOL=http/protobuf`
  - `HYDRA_LOG_NODE=rtx`
  - **Keep** `userns_mode: "host"` with `// TODO: drop userns_mode after #333` comment
- [ ] **P100 OTel URL override** — `infra/hydra-head/config/node-p100.yaml`:
  - Add `infra.otel.url: http://192.168.122.1:4318`
  - Remove `services.promtail:` block
- [ ] **RTX drop promtail block** — `infra/hydra-head/config/node-rtx.yaml`:
  - Remove `services.promtail:` block
- [ ] **Drop promtail from hydra-head image** — `infra/hydra-head/Dockerfile.rtx` — remove promtail install (lines 60-74) and config copy (line 56)
- [ ] **Compose cleanup** — `infra/docker-compose.hydra.yml`:
  - Remove `hydra-head-promtail-positions` volume (lines 54, 186)
  - Remove `/run/user/1000/podman:/var/run/socks:rw`, `/proc:/host/proc:ro`, `/sys:/host/sys:ro`, `/:rootfs:ro` mounts (only used by Promtail)
  - **Keep** `userns_mode: "host"` (line 50) with TODO comment
  - **Keep** the `mkdir -m 777` workaround in the `core` service (line 102) — fixes separate #333 rootless-podman bug
- [ ] **Drop promtail from deploy scripts**:
  - `scripts/setup-p100.sh` lines 31, 38-57: drop `Installing promtail binary` and `Copying promtail config` blocks
  - `scripts/deploy-hydra-head.sh:123`: remove `infra-promtail` from `stop_host_sidecars()` loop
  - `scripts/deploy-hydra-head.sh:232-240`: replace `promtail_sent_bytes_total` health gate with `hydra_loki_sent_entries_total > 0` (scraped from OTel Collector `:8888/metrics`)
- [ ] **CI cleanup** — `.github/workflows/ci.yml:180`:
  - Remove `infra-promtail` from the deploy loop
- [ ] **Grafana dashboard** — `infra/grafana/dashboards/hydra-logs.json`:
  - Verify the 3 existing queries still match: `{component="hydra"}` (C# sets `service.name=hydra`), `{component="llama-server", node="rtx"}`, `{component="llama-server", node="p100"}`
  - Add a panel: drop count by component using `hydra_loki_dropped_entries_total`
- [ ] **Tests**:
  - `src/core/Tests.Shared/HydraLoggingTests.cs` (new) — assert OTel resource attributes (`service.name=hydra`, `service.instance.id=rtx`, `service.level=error`) are set on the pipeline; assert the previous Loki direct-push sink is gone; assert the console JSON sink is gone
  - `src/head/internal/process/child_writer_test.go` (new) — assert a child writer for `llama-server` produces OTel log records with `service.name=llama-server`, `service.instance.id=rtx`; same for `node-exporter` and `nvidia-exporter`; assert an unknown child falls back to `service.name=hydra-head`
  - `src/head/internal/logging/otel_handler_test.go` (new) — assert the OTel handler produces OTLP-shaped records (use a fake OTLP exporter)
  - `infra/otel-collector/config_test.go` (new) — assert the collector config validates
- [ ] **Docs cleanup**:
  - `docs/RUNBOOK.md` lines 398, 685 — remove `container-log-shipper` references; add OTel Collector runbook section; add k8s-file → journald opt-in step
  - `docs/architecture.md` § observability (lines 288-335) — rewrite the diagram to show the gateway pipeline
  - `docs/workflow/06-monitoring.md` lines 14, 16 — drop `container-log-shipper` from the runbook
  - `docs/milestone-3-production.md` line 96 — drop `container-log-shipper` reference
  - `docs/hydra-system-pod.md` § log-driver — note that `k8s-file` is no longer required
- [ ] **THIRD_PARTY_NOTICES.md** — add `Serilog.Sinks.OpenTelemetry`, `OpenTelemetry.Exporter.OpenTelemetryProtocol`, `go.opentelemetry.io/otel/sdk/log`, `go.opentelemetry.io/otel/exporters/otlp/otlplog/otlploghttp`. Remove `Serilog.Sinks.Grafana.Loki`.
- [ ] **Verification** (post-deploy, see "Verification" section below)
- [ ] **Close related issues** — link PR with `Closes #322`, `Closes #324`, `Closes #363`; any sub-issues the implementer files along the way.

## Sub-issues (existing)

| Issue | Title | Disposition |
|---|---|---|
| #322 | promtail-rtx.yml hardcodes docker.sock; in-container promtail ships 0 bytes to Loki | Closed by the Promtail removal (this PR) |
| #324 | Loki rejects hydra-core log entries >262144 bytes (max_entry_size) | Closed by the `max_entry_size: 1MiB` config bump (this PR) |
| #363 | (this issue) | Closes when the implementation PR merges |

## Acceptance criteria

The implementation PR is "done" when **all** of the following are true:

- [ ] `dotnet test src/core/Tests.Shared/ && dotnet test src/core/Tests.Core/` is green
- [ ] `go test ./...` in `src/head/` is green
- [ ] `podman build` for `hydra-head:rtx` succeeds
- [ ] `bash scripts/deploy-hydra-head.sh all` deploys to both nodes
- [ ] The OTel Collector Quadlet starts and stays healthy on RTX
- [ ] All 10 verification commands in the "Verification (deployed stack)" section pass
- [ ] The Grafana dashboard renders without errors
- [ ] The CI `deploy` step succeeds (the `infra-promtail` line is gone)
- [ ] PR is `Closes #322`, `Closes #324`, `Closes #363`

## Verification (deployed stack)

After deploy:

```bash
# 1. OTel Collector is healthy on RTX
curl -so/dev/null -w'%{http_code}\n' http://localhost:13133/   # expect 200

# 2. Hydra.Core is sending OTel logs to the collector → Loki
curl -s 'http://localhost:3100/loki/api/v1/query?query={component="hydra"}' \
  | jq '.data.result | length'   # expect > 0

# 3. Hydra.Head is sending OTel logs to the collector → Loki
curl -s 'http://localhost:3100/loki/api/v1/query?query={component="hydra-head"}' \
  | jq '.data.result | length'   # expect > 0

# 4. llama-server lines are now under {component="llama-server", node=...}
curl -s 'http://localhost:3100/loki/api/v1/query?query={component="llama-server",node="rtx"}' \
  | jq '.data.result | length'   # expect > 0
curl -s 'http://192.168.122.1:3100/loki/api/v1/query?query={component="llama-server",node="p100"}' \
  | jq '.data.result | length'   # expect > 0 (NOTE: 192.168.122.1 is the host as seen from the VM)

# 5. node_exporter + nvidia_exporter are under their own component labels
curl -s 'http://localhost:3100/loki/api/v1/query?query={component="node-exporter"}' \
  | jq '.data.result | length'   # expect > 0
curl -s 'http://localhost:3100/loki/api/v1/query?query={component="nvidia-exporter"}' \
  | jq '.data.result | length'   # expect > 0

# 6. level label works (uniform across all components via OTel)
curl -s 'http://localhost:3100/loki/api/v1/query?query={level="error"}' \
  | jq '.data.result | length'   # expect > 0 (after first error)

# 7. trace_id lives in structured metadata (not a label) — W3C trace context
curl -s 'http://localhost:3100/loki/api/v1/label/trace_id/values' | jq .data
# expect [] — trace_id is structured metadata, not a label

# 8. No more promtail container
podman ps --filter name=infra-promtail   # expect nothing
ssh hydra-p100 'which promtail'          # expect "not found" or similar

# 9. Loki metrics: no more `line_too_long` errors
curl -s 'http://localhost:3100/metrics' | grep loki_discarded_samples_total
# expect no `reason="line_too_long"` samples for `component="hydra"`

# 10. OTel Collector metrics: no client-side drops
curl -s 'http://localhost:8888/metrics' | grep otelcol_exporter_send_failed_log_records
# expect 0
curl -s 'http://localhost:9700/metrics' | grep hydra_loki_dropped_entries_total
# expect 0 (or no time series yet)
```

## Rollout

### Order: RTX first, then P100

A review caught that the previous design assumed a simultaneous cutover; in practice, `deploy-hydra-head.sh` targets one host at a time. The plan:

1. **Land the implementation PR** to `main` (does not deploy anything).
2. **Start the OTel Collector Quadlet on RTX**: `systemctl --user start infra-otel-collector` (after `start-infra.sh` has installed the Quadlet).
3. **Verify the collector**: `curl -so/dev/null -w'%{http_code}\n' http://localhost:13133/` returns 200.
4. **Deploy RTX**: `bash scripts/deploy-hydra-head.sh rtx`. The new `hydra-head-rtx` image rolls out; the OTel pipeline replaces Promtail.
5. **Wait for healthy Loki streams from RTX** (~30 s, one llama-server startup cycle): confirm `{component="hydra"}`, `{component="hydra-head"}`, `{component="llama-server", node="rtx"}` are non-empty in Grafana Explore.
6. **Deploy P100**: `bash scripts/deploy-hydra-head.sh p100`. The P100 `hydra-head` pushes to `http://192.168.122.1:4318` (the RTX collector over the network).
7. **Wait for healthy P100 streams**: same three queries for `node="p100"`.
8. **Watch** `hydra_loki_dropped_entries_total`, `loki_discarded_samples_total`, and `otelcol_exporter_send_failed_log_records` for 1 hour; if any is non-zero, page the implementer.
9. **After 24h clean**: remove the old OCI image tag (the one with Promtail).

### Hard cutover (in one PR)

Rationale (per the implementer's decision):

- The new pipeline does not require any new infrastructure beyond the OTel Collector (one container, no state).
- The two promtail instances are still in the **old** `hydra-head` image. If the cutover fails, the rollback is `git revert` + redeploy the previous image — no data loss.
- A parallel-run window would double Loki ingestion cost for one release and create a "which pipeline is the truth" question.
- The risk is bounded: all three clients (C# OTel, Go OTel, OTel Collector `batch`) fail-soft with bounded buffers. No service crashes if the collector or Loki is unreachable.

### Review strategy

This PR is large (~+550 / +100 / −260 LOC across 25+ files, plus 2 new files). The merge method and gate:

- **Merge method: squash** (one commit per logical change; the PR body carries the per-change list)
- **Review gate:** CI green **+ one human reviewer who has read this design doc** (the audit is in §"Why the current design hurts" + the design decisions in §"Decision")
- **Required reviewers:** at least one from the Hydra.Head maintainer group + one from the Hydra.Core maintainer group (cross-language PR)
- **Issue cross-links:** PR body must include `Closes #322, Closes #324, Closes #363`

## Risk analysis

| Risk | Likelihood | Mitigation |
|---|---|---|
| OTel Collector becomes a single point of failure | Medium | `BindsTo=infra-host-pod.service` + `Restart=on-failure` + health check on `:13133` + Prometheus alert on collector down |
| OTel push path silently loses entries (network blip) | Medium | Bounded queue (`QueueSize=65 536`); drop-oldest; `hydra_loki_dropped_entries_total{component, reason}` increments on drop; `HydraLokiDropsIncreasing` alert |
| Collector → Loki push silently loses entries | Medium | `otelcol_exporter_send_failed_log_records` metric; same alert |
| W3C trace context doesn't flow C# → Go (e.g., C# OTel SDK not setting `traceparent` correctly) | Low | C# OTel SDK auto-injects `traceparent`; verified in `HydraLoggingTests`; first integration test runs a full Coordinator → Hydra.Head request and asserts trace_id matches in Loki streams |
| Per-child writer leaks goroutines on child restart | Low | OTel log SDK is goroutine-safe; per-writer `bufio.Scanner` is freed with `proc` |
| `transform` processor config in the OTel Collector doesn't map resource attributes to Loki labels correctly | Medium | Phase 1 includes a unit test (`config_test.go`) that asserts the config validates; Phase 2 includes an integration test that pushes a sample log and verifies the Loki label matches |
| `OTEL_EXPORTER_OTLP_ENDPOINT` is misconfigured (e.g., `http://localhost:4318` from P100 would resolve to P100's own loopback) | Medium | `node-p100.yaml` override `http://192.168.122.1:4318` is the fix; verified in verification step 4 |
| `cfg.OTel.URL` is wrong on P100 | Low | `node-p100.yaml` override is the fix; verification step 4 catches this within 30 s of deploy |
| Grafana dashboard breaks (label query mismatch) | Low | The 3 existing queries all match the new pipeline (the labels come from the collector's `transform` processor); the new `hydra_loki_dropped_entries_total` panel is additive |
| Chunk-cache chmod 777 workaround surfaces as a real bug if `userns_mode: "host"` is dropped in a future PR | Medium | **This PR keeps `userns_mode: "host"`** with a `// TODO`; a follow-up removes it after #333 lands |
| Promtail EOL means a future Grafana upgrade breaks us anyway | None (mitigated) | We are removing Promtail in this PR |
| OTel Collector image is large (~192 MB) | Low | One-time pull; the `infra-host` pod already pulls `grafana/loki:3.4` (170 MB) and `prom/prometheus:latest` (280 MB), so the total pull size delta is small |

## Out of scope

- Migrating the `infra-host` pod services (grafana, prometheus, postgres, pgadmin, openwebui, renderer) to direct push. They don't produce useful logs; the "no service ships observability" rule applies.
- **Audit #8** (`component="hydra"` (Loki) vs `component="hydra-core"` (Prometheus) alert mismatch — `HydraCoreDown` alert can never fire): **deferred to a separate `review-finding` issue** to be filed alongside this PR. The alert is in `infra/prometheus/alerts.yml:6,13,19` and is independent of the log-pipeline rewrite.
- Removing the chunk-cache `mkdir -m 777` workaround + `userns_mode: "host"`. These are kept in this PR (see "Per-service responsibilities" → "Pod level"). A follow-up PR removes them after #333 lands.
- Switching to OTel Collector as a per-host agent (one collector per host). Considered; not needed for the current scope. The single-gateway architecture is correct for our 2-node system.
- Migrating to Grafana Cloud / managed Loki. The local Loki 3.x deployment is fine.
- Backfilling the `level` label onto historical entries. Loki structured metadata is per-entry; no migration needed.

## Fork-side coordination

**None.** This change touches Hydra.Core (C#), Hydra.Head (Go), Loki config, and infra YAMLs + one new Quadlet. **No `src/llama-cpp` (fork) change** is needed. `docs/workflow/08-llama-fork.md` does not apply.

## Handoff checklist (for the next dev)

- [ ] Read `docs/design-direct-push-logging.md` end-to-end (it's the spec)
- [ ] Set the Project board item **Status → In Progress** (per `docs/workflow/01-pickup.md`)
- [ ] Branch: `git checkout -b feat/m3-direct-push-logs`
- [ ] Run the implementation checklist above; one commit per sub-task
- [ ] Run `dotnet test src/core/Tests.Shared/ && dotnet test src/core/Tests.Core/` (per `docs/workflow/03-test-verify.md`)
- [ ] Run `go test ./...` in `src/head/`
- [ ] Open the PR with `Closes #322`, `Closes #324`, `Closes #363`
- [ ] After deploy, run the verification commands above
- [ ] Per `docs/workflow/06-monitoring.md`, check Grafana + alerts for 24h before declaring done
- [ ] Per `docs/workflow/07-issue-and-close.md`, the PR merge auto-closes this issue (Status → Done)

---

## Gap review response (preserved from 09db377)

The design was reviewed on 2026-06-27 (16-item review on issue #363). The first revision (commit `09db377`) addressed every item. The second revision (this commit) preserves all 16 fixes and additionally adopts the OTel Collector gateway architecture to drop the asymmetric push paths, the `level` label bridge, and the custom `X-Hydra-Trace-Id` middleware.

### P0 — will not work as written

| # | Finding | How this revision addresses it |
|---|---|---|
| 1 | Design doc absent in repo | Doc lives on `docs/m3-direct-push-logs` branch and lands via PR #364. Once that merges, the canonical link is `docs/design-direct-push-logging.md` on `main`. The "Preconditions" section now lists 12 explicit verification commands with the values verified on 2026-06-27. |
| 2 | OTel endpoint routing | Endpoint is now `OTEL_EXPORTER_OTLP_ENDPOINT=http://localhost:4318` (the OTel Collector on the host; `core` runs `network_mode: host` so `localhost` reaches it). P100 services override to `http://192.168.122.1:4318`. Protocol: `http/protobuf`. |
| 3 | Loki version not verified | Verified `3.4.3` via `/loki/api/v1/status/buildinfo`; `schema v13` and `tsdb` index are already in `infra/loki/loki-config.yml:19-27`. The preconditions table records both. |
| 4 | `level` label asymmetry | Resolved structurally by the gateway: all services push OTel, the collector's `transform` processor maps `service.level` → Loki `level` label uniformly. No ruler rule needed. |
| 5 | Observability-drop rule has no replacement | The explicit Promtail drop rule is replaced by an **implicit** "no service ships observability" rule. The `infra-host` pod services don't run an OTel client, so they don't push. No exclusion rule needed. |

### P1 — will cause incidents

| # | Finding | How this revision addresses it |
|---|---|---|
| 6 | `userns_mode: "host"` entangled with #333 | **kept** with a `// TODO: drop userns_mode after #333` comment. The chunk-cache `mkdir -m 777` workaround is still coupled to it. Removed from "what this PR changes" and added to "Out of scope" (it's a follow-up, not this PR). |
| 7 | Rollout order unspecified | New "Order: RTX first, then P100" subsection in the Rollout section: 9 explicit steps with verification gates between them. |
| 8 | No client-side drop counter | New "Loss detection" subsection: `hydra_loki_dropped_entries_total{component, reason}` Prometheus counter on both sides, plus `otelcol_exporter_send_failed_log_records` from the OTel Collector. Prometheus alert `HydraLokiDropsIncreasing` covers all three. |
| 9 | Queue bounds unspecified | New "Queue bounds" subsection: explicit `BackgroundWorkerOptions.QueueSize = 65 536` for the C# side, matching `QueueSize = 65 536` for the Go side, with worst-case memory per process. |
| 10 | Trace_id doesn't cross C#→Go boundary | Resolved structurally by the gateway: both C# and Go use the OTel SDK, which implements W3C trace context propagation natively. No custom `X-Hydra-Trace-Id` middleware needed. |

### P2 — hygiene

| # | Finding | How this revision addresses it |
|---|---|---|
| 11 | `HYDRA_LOG_LOKI_URL` semantics undefined | Resolved: drop the env var and the `Serilog.Sinks.Grafana.Loki` dependency. The OTLP push is the new primary path; no fallback. |
| 12 | `taggingWriter` reuses the brittle regex | Resolved structurally: per-child labeled writers at spawn time. New "Per-child writers" subsection with Go code shape. The regex is gone. |
| 13 | PR size + review strategy unstated | New "Review strategy" subsection in Rollout: squash merge, CI green + 1 reviewer per language group, required cross-links. |
| 14 | Ingestion rate doubling unjustified | Resolved: stay at 32 MB. New "Why ingestion rate stays at 32 MB" subsection with the calculation. The `max_entry_size: 1MiB` (4×) bump stays; it's the one that fixes #324. |
| 15 | Container log-driver revert is operator-side | Resolved: the RUNBOOK entry now has an explicit step for the operator to switch `k8s-file` → `journald` after the cutover. Default stays `k8s-file` (no behavior change) until operators opt in. |
| 16 | Milestone is the deferred M3 | M3 — "Persistence & Real Obs" is the right home: it's labeled "Real Obs" and this work is observability. The active Llama-Engine milestone is about P/D split. The reviewer may have confused the M3 label (which is open + scoped to "Real Obs") with the M3 sub-phase ("Production phase" per `CLAUDE.md`, deferred). The decision stands: this issue stays in M3. |

### Additional changes in this revision (gateway pivot)

- **OTel Collector gateway** added: one container in the `infra-host` pod, exposed on host port 4318. P100 services reach it at `192.168.122.1:4318`. Single push protocol (OTLP) for all services.
- **samber dependencies dropped** on the Go side: `samber/slog-loki/v3` and `samber/slog-multi/v2` are removed. Replaced with the OTel Go log SDK + `otlploghttp` exporter.
- **`level` label asymmetry ruler rule dropped**: no longer needed; the collector's `transform` processor normalizes the OTel path to Loki labels uniformly.
- **`X-Hydra-Trace-Id` middleware dropped**: OTel SDK implements W3C trace context propagation natively; the custom middleware is redundant.
- **OTel Collector Quadlet file added**: `infra/quadlets/infra-otel-collector.container` (modeled on `infra-loki.container`).
- **OTel Collector config added**: `infra/otel-collector/config.yaml` (OTLP receiver, `transform` processor, loki exporter, prometheus exporter).
- **Verification commands expanded from 5 to 10**: includes OTel Collector health check, OTel Collector metrics, and per-component query coverage.
