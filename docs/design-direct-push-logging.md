# Design — Per-Service Direct Log Shipping to Loki

- **Status:** Revised (gap review #363 applied 2026-06-27)
- **Date:** 2026-06-27
- **Author:** engineering (senior)
- **Scope:** Hydra.Core (C#), Hydra.Head (Go), llama-server (C++), node/nvidia exporters, the `infra-host` Quadlet pod, and the Loki deployment.
- **Supersedes:** the central-Promtail / docker-SD / CRI-parser pipeline that has been the source of 10+ open review findings and 1 confirmed pre-existing bug (P100 promtail scrapes journald with a CRI parser).
- **Motivator (external):** Promtail reached **EOL on 2026-03-02** per the Grafana docs — it is no longer the recommended path. The replacement is direct push to Loki (either native or OTLP) and Grafana Alloy for file-based scraping.
- **Revision notes:** See the "Gap review response" appendix at the end of this doc for the 16-item review (P0×5, P1×5, P2×6) and how each is addressed. The architecture diagram, label vocabulary, configuration table, and rollout sections have all been updated.

## Why the current design hurts

The current stack has one Promtail running **inside** the `hydra-head-rtx` container (scrape `ctr.log` via the podman socket → cri parser → regex classify per line → push), and as a **second** Promtail on the P100 VM (same config file, but the actual log source on P100 is **journald** — not `ctr.log` — so the `cri: {}` pipeline stage is the wrong parser for that host).

The audit (2026-06-27, full table in this doc) uncovered **10 concrete issues**, of which the highest-impact are:

| # | Issue | File:line | Impact |
|---|---|---|---|
| 1 | **P100 promtail config is wrong** — `cri: {}` stage at `infra/promtail/promtail-rtx.yml:49` runs against journald, not `ctr.log` | `infra/promtail/promtail-rtx.yml:49` | P100 logs silently garbled; no clean way to debug |
| 2 | **Hydra.Core double-logs** — Serilog writes to console JSON *and* to Loki directly | `src/core/Hydra.Shared/HydraLogging.cs:26-34` | 2× Loki ingestion cost; duplicate streams |
| 3 | **Loki `max_entry_size: 256 KiB`** drops real prefill lines | `infra/loki/loki-config.yml` (no override; upstream default) | Whole batch around offender is lost (open: #324) |
| 4 | **`component` label depends on a brittle regex** | `infra/promtail/promtail-rtx.yml:68` | New log format → wrong label silently |
| 5 | **No HA** — if `hydra-head-rtx` dies, in-container Promtail dies too | (structural) | Log gap on every restart |
| 6 | **Position file path is broken on P100** | `infra/promtail/promtail-rtx.yml:13` | P100 promtail silently fails to persist |
| 7 | **`node_exporter` and `nvidia_exporter` get labeled `component=hydra-head`** | (Go process manager collapses child stdout; regex doesn't match) | Mislabeled in Loki |
| 8 | **`component="hydra"` (Loki) ≠ `component="hydra-core"` (Prometheus)** | `infra/prometheus/alerts.yml:6,13,19` | `HydraCoreDown` alert can never fire |
| 9 | **P100 → RTX Loki URL is wrong** — `http://localhost:3100` resolves to P100 itself | `infra/hydra-head/config/global.yaml:54` | P100 promtail pushes nowhere useful |
| 10 | **Stale CI / docs references to `infra-promtail` and `container-log-shipper`** (removed in `5f2c231`) | `.github/workflows/ci.yml:180`, `docs/RUNBOOK.md:398,685`, `docs/workflow/06-monitoring.md:14,16` | Real CI drift; runbook gives wrong commands |

## Decision

Adopt **per-service direct push** to Loki 3.x. Each service owns its own log shipping. The central Promtail is **removed entirely** (it is EOL anyway).

### Architecture

```
┌──────────────────────────────────────────────────────────────────────────────┐
│ Service             │ Logger       │ Ships to Loki via                       │
├──────────────────────────────────────────────────────────────────────────────┤
│ Hydra.Core (C#)     │ Serilog 4.2  │ Serilog.Sinks.OpenTelemetry →           │
│                     │              │ Loki POST /otlp/v1/logs (gzip,           │
│                     │              │ auto-trace correlation)                │
│ Hydra.Head (Go)     │ log/slog     │ samber/slog-loki →                      │
│                     │              │ Loki POST /loki/api/v1/push             │
│                     │              │ (JSON, gzip, native labels)             │
│ llama-server (C++)  │ (unchanged)  │ stdout → hydra-head supervisor pipes,   │
│                     │              │ per-child labeled writer at spawn       │
│                     │              │ (component=llama-server)                │
│ node_exporter,      │ (unchanged)  │ stdout → hydra-head supervisor pipes,   │
│ nvidia_exporter     │              │ per-child labeled writer                │
│                     │              │ (component=node-exporter / nvidia-…)    │
│ infra-* (grafana,   │ (unchanged)  │ not pushed — they don't run a Loki      │
│ loki, prometheus,   │              │ client, so the "drop observability"     │
│ postgres, pgadmin,  │              │ rule becomes a "no service ships these" │
│ openwebui, renderer)│              │ rule                                    │
└──────────────────────────────────────────────────────────────────────────────┘
                                       │
                                       ▼
                          ┌───────────────────────────┐
                          │ Loki 3.x :3100            │
                          │  - max_entry_size: 1 MiB  │  ← fix #324
                          │  - ingestion_rate: 64 MB  │
                          │  - schema v13, tsdb index │  ← required for OTLP
                          │  - allow_structured_      │
                          │    metadata: true         │
                          │  - ingestion_burst_      │
                          │    size_mb: 128           │
                          └───────────────────────────┘
```

### Preconditions (verify before starting the implementation)

The implementation assumes the runtime already meets these constraints. Each one was verified on 2026-06-27 against the live host:

| Check | Command | Required | Verified 2026-06-27 |
|---|---|---|---|
| Loki version | `curl -s localhost:3100/loki/api/v1/status/buildinfo \| jq -r .version` | ≥ 3.0 (for native OTLP) | **3.4.3** ✅ |
| Schema config | `curl -s localhost:3100/config \| jq .schema_config.configs[0].schema` | `v13` | **v13** ✅ (per `infra/loki/loki-config.yml:24`) |
| Index backend | same as above, `.schema_config.configs[0].index.prefix` | `tsdb` | **`index_`** (tsdb variant) ✅ |
| OTLP listener | `curl -s -o /dev/null -w '%{http_code}\n' -X POST -H 'Content-Type: application/x-protobuf' --data-binary '' localhost:3100/otlp/v1/logs` | `4xx` (any non-2xx, non-404) | **422** ✅ (the OTLP listener is on) |
| Push listener | `curl -s -X POST -H 'Content-Type: application/json' -d '{}' localhost:3100/loki/api/v1/push` | 4xx with "at least one valid stream" | ✅ |
| `core` network mode | `grep network_mode infra/docker-compose/hydra.yml` for `core` | `host` (so `localhost:3100` reaches host's Loki) | **host** ✅ (line 75) |
| Hydra.Head in VM reaches RTX Loki | `ssh hydra-p100 'curl -so/dev/null -w'%{http_code}' http://192.168.122.1:3100/ready'` | `200` | TBD — fix in `node-p100.yaml` if not |

If any precondition fails, **stop and fix the precondition first**; the implementation will not work.

### Label vocabulary

```
{component, node, level}
```

- `component` = `hydra` | `hydra-head` | `llama-server` | `node-exporter` | `nvidia-exporter`
- `node` = `rtx` | `p100`
- `level` = `info` | `warn` | `error` (added per design decision — low cardinality, big dashboard value)
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
                  rate({component=~".+"} | json | severity_text="ERROR" or severity_text="FATAL" [1m])
                )
```

The new `component:errors:rate1m` recording rule produces a `{component, node}` stream with a numeric `errors_per_sec` value that the dashboard can graph. The `level` index label is **not** materialized — we accept the asymmetry and use the ruler for severity aggregation instead. This is cheaper than promoting `severity_text` to a Loki label (which would re-introduce the cardinality risk we just removed).

### Per-service responsibilities

| Service | What changes |
|---|---|
| **Hydra.Core (C#)** | Replace `Serilog.Sinks.Grafana.Loki` with `Serilog.Sinks.OpenTelemetry` + `OpenTelemetry.Exporter.OpenTelemetryProtocol` + `OpenTelemetry.Extensions.Hosting`. Set `service.name=hydra`, `service.namespace=hydra-core`, `deployment.environment.name=dev` as resource attributes. Set the `node` label via `Enrich.WithProperty("node", …)`. Map Serilog `Level` to the OTel `severity` field; map Serilog `LogContext` properties to OTel attributes. **Stop writing to the console** (Loki is enough; the 2× ingestion today is a bug). |
| **Hydra.Head (Go)** | Add `github.com/samber/slog-loki/v3` and `github.com/samber/slog-multi/v2` (fanout). Build a multi-handler: (1) text → `os.Stdout` (kept for `journalctl` forensics) + (2) JSON → Loki push. For each child-process stdout byte stream (`llama-server`, `node_exporter`, `nvidia_exporter`), replace `os.Stdout` with a **per-child labeled writer at spawn time** (no regex on the hot path). The manager already knows the child name (`StartLlama` vs `StartService("node_exporter")` vs `StartService("nvidia_exporter")`); each gets its own `io.Writer` configured with the right static `component` label. |
| **Loki** | Bump `limits_config.max_entry_size` from default 256 KiB to 1 MiB (fixes #324). Bump `ingestion_rate_mb` from 32 to 64 and `ingestion_burst_size_mb` from 64 to 128 to handle prefill bursts. Confirm `schema_config` is `v13` and the index is `tsdb` (required for OTLP — already so, verified 2026-06-27). |
| **P100 systemd** | `node-p100.yaml` overrides `infra.loki.url` to `http://192.168.122.1:3100` (per-host override; the RTX host as seen from the KVM VM — pre-existing bug fix). The journald-only log path is **kept** as a fallback for `journalctl` forensics; direct push is the new primary. |
| **Promtail** | **Removed entirely.** The two promtail binaries (in `hydra-head-rtx` container + on P100 VM) are deleted. The `promtail:` block in `node-rtx.yaml` and `node-p100.yaml` is removed. |
| **Pod level** | Drop `userns_mode: "host"` from `infra-host` and `hydra-system` pods — the only reason for it was Promtail's ctr.log access. |
| **k8s-file log driver** | The podman log-driver requirement in `~/.config/containers/containers.conf` is **no longer needed**. Operators can switch to `journald` (the default) with no log-pipeline consequences. Document this in the runbook. |
| **Hydra.Head (Go) test setup** | `src/head/internal/registry/integration_test.go:18,46,90` and friends use `slog.NewTextHandler(io.Discard, nil)` — keep as-is. Add new unit tests for the Loki handler and the per-child writers. |

### Configuration changes (per file)

| File | Change |
|---|---|
| `infra/loki/loki-config.yml` | Add `max_entry_size: 1MiB` (fixes #324). Bump `ingestion_rate_mb: 64` (was 32) and `ingestion_burst_size_mb: 128` (was 64) to handle prefill bursts. Add `ruler:` block with the `component:errors:rate1m` recording rule. |
| `src/core/Hydra.Shared/HydraLogging.cs` | Replace Loki push sink with OTel pipeline; add `node` enricher; drop console sink (or keep as `Debug`-only for dev) |
| `src/core/Hydra.Shared/Hydra.Shared.csproj` | Add deps: `Serilog.Sinks.OpenTelemetry`, `OpenTelemetry.Exporter.OpenTelemetryProtocol`, `OpenTelemetry.Extensions.Hosting` |
| `src/core/Hydra.Core/Program.cs` | Wire OTel resource attributes (`service.name`, `service.namespace`, `deployment.environment.name`); set OTEL endpoint env vars |
| `src/head/main.go` | Construct `samber/slog-loki` handler + `samber/slog-multi` fanout to text + Loki; read `cfg.Infra.Loki.URL` and `cfg.Node.Name` |
| `src/head/internal/process/manager.go` | Add `taggingWriter`; replace `proc.LogWriter = os.Stdout` with `proc.LogWriter = newTaggingWriter(m.LokiClient, "llama-server", m.cfg.Node.Name)` |
| `src/head/go.mod` | Add `github.com/samber/slog-loki/v3`, `github.com/samber/slog-multi/v2` |
| `src/head/internal/config/config.go` | Add `Logging.LokiURL`, `Logging.Node` (or reuse `Node.Name`); mark `GeneratePromtailConfig` as `// Deprecated` |
| `infra/hydra-head/config/global.yaml` | (no change — `infra.loki.url` stays) |
| `infra/hydra-head/config/node-p100.yaml` | Add `infra.loki.url: http://192.168.122.1:3100` (per-host override) |
| `infra/hydra-head/config/node-rtx.yaml` | Remove `services.promtail:` block |
| `infra/hydra-head/config/node-p100.yaml` | Remove `services.promtail:` block |
| `infra/hydra-head/Dockerfile.rtx` | Remove promtail install (lines 60-74) and config copy (line 56) |
| `infra/docker-compose/hydra.yml` | Remove `hydra-head-promtail-positions` volume (lines 54, 186); remove `/run/user/1000/podman:/var/run/socks:rw`, `/proc:/host/proc:ro`, `/sys:/host/sys:ro`, `/:rootfs:ro` mounts; drop `userns_mode: "host"` (line 50) |
| `scripts/setup-p100.sh` | Remove the `Installing promtail binary` (lines 38-50) and `Copying promtail config` (lines 52-57) blocks |
| `scripts/deploy-hydra-head.sh` | Remove `infra-promtail` from the deploy loop (line 123) |
| `scripts/deploy-hydra-head.sh:232-240` | Replace the `promtail_sent_bytes_total` health gate with `hydra_loki_sent_entries_total` (a new metric the Loki client exposes; if the client is hand-rolled, add a simple counter) |
| `docs/RUNBOOK.md` | Remove `container-log-shipper` references (lines 398, 685). **Add a runbook step** for operators: "After the cutover, the podman `k8s-file` log driver is no longer required. To switch to the default `journald`, edit `~/.config/containers/containers.conf` and set `log_driver = "journald"`, then restart any containers you want to log via `journalctl -u <service>`. Until then, no behavior change." |
| `docs/architecture.md` | Update § observability diagram (lines 288-335) to show the new direct-push pipeline |
| `docs/workflow/06-monitoring.md` | Remove the `systemctl --user restart container-log-shipper promtail` lines (14, 16) |
| `docs/milestone-3-production.md` | Remove `container-log-shipper` reference (line 96) |
| `THIRD_PARTY_NOTICES.md` | Add `Serilog.Sinks.OpenTelemetry`, `OpenTelemetry.Exporter.OpenTelemetryProtocol`, `samber/slog-loki`, `samber/slog-multi`. Remove `Serilog.Sinks.Grafana.Loki` if no longer used. |
| `.github/workflows/ci.yml:180` | Remove `infra-promtail` from the deploy loop |
| `docs/RUNBOOK.md` lines 398, 685 | Remove `container-log-shipper` references |
| `docs/workflow/06-monitoring.md` lines 14, 16 | Remove `systemctl --user restart container-log-shipper promtail` |
| `docs/milestone-3-production.md` line 96 | Remove `container-log-shipper` reference |

### Implementation checklist (in PR order)

The next dev should land these in **one PR** (hard cutover — see "Rollout" below). Each sub-task is one commit.

- [ ] **Loki config** — `infra/loki/loki-config.yml`:
  - `max_entry_size: 1MiB` (fixes #324)
  - `ingestion_rate_mb: 64` (was 32)
  - `ingestion_burst_size_mb: 128` (was 64)
  - Add `ruler:` block with the `component:errors:rate1m` recording rule
- [ ] **Hydra.Core (C#)** — `src/core/Hydra.Shared/HydraLogging.cs`, `Hydra.Shared.csproj`, `src/core/Hydra.Core/Program.cs`:
  - Add deps: `Serilog.Sinks.OpenTelemetry`, `OpenTelemetry.Exporter.OpenTelemetryProtocol`, `OpenTelemetry.Extensions.Hosting`
  - Replace `WriteTo.GrafanaLoki(...)` with OTel pipeline; set resource attributes `service.name=hydra`, `service.namespace=hydra-core`, `deployment.environment.name=dev`
  - Drop the console JSON sink (the Loki push is enough; today's 2× ingestion is a bug)
  - Add `node` enricher (env-driven: `HYDRA_LOG_NODE`)
  - Map Serilog `Level` to OTel `severity`
- [ ] **Hydra.Core compose** — `infra/docker-compose/hydra.yml`:
  - Set `OTEL_SERVICE_NAME=hydra`, `OTEL_EXPORTER_OTLP_ENDPOINT=http://localhost:4318`, `HYDRA_LOG_NODE=rtx`
  - Keep `HYDRA_LOG_LOKI_URL` env var as a fallback flag for the dev path
- [ ] **Hydra.Head (Go)** — `src/head/main.go`, `internal/process/manager.go`, `go.mod`, `internal/config/config.go`:
  - Add deps: `github.com/samber/slog-loki/v3`, `github.com/samber/slog-multi/v2`
  - Build a multi-handler: text → `os.Stdout` (for `journalctl` forensics) + JSON → Loki
  - Read `cfg.Infra.Loki.URL` and `cfg.Node.Name`; construct the Loki client with `BatchWait=2s`, `BatchEntriesNumber=500`, gzip, labels `{component=hydra-head, node=<name>}`
  - Add a `taggingWriter` that replaces `proc.LogWriter = os.Stdout` in `manager.go:137,322`. It buffers lines, regex-checks the llama prefix (`^\d+\.\d+\.\d+\.\d+\s+[A-Z]\s+` — same regex as today's Promtail), tags `component=llama-server` / `node-exporter` / `nvidia-exporter` accordingly, pushes through the same Loki client. Falls back to `component=hydra-head` for unrecognised lines.
  - Mark `Config.GeneratePromtailConfig` (config.go:360-399) as `// Deprecated: per-service direct push since 2026-06`; the test in `config_test.go:248-263` stays.
- [ ] **P100 Loki URL fix** — `infra/hydra-head/config/node-p100.yaml`:
  - Add `infra.loki.url: http://192.168.122.1:3100` (per-host override; the RTX host as seen from the KVM VM — pre-existing bug fix)
- [ ] **Remove Promtail** — `infra/promtail/promtail-rtx.yml` deleted, `infra/promtail/` directory deleted, `infra/hydra-head/Dockerfile.rtx:60-74` (binary install) and `:56` (config copy) removed, `infra/hydra-head/config/node-rtx.yaml:73-76` and `node-p100.yaml:56-59` (the `promtail:` blocks) removed
- [ ] **Pod level** — Drop `userns_mode: "host"` from `infra-host` and `hydra-system` pods — the only reason for it was Promtail's ctr.log access. (The `core` container's `mkdir -m 777` workaround for the chunk-cache write-behind path is **not** related and stays.)
- [ ] **k8s-file log driver** — The podman log-driver requirement in `~/.config/containers/containers.conf` is **no longer needed**. Operators can switch to `journald` (the default) with no log-pipeline consequences. Document this in the runbook.
- [ ] **Compose cleanup** — `infra/docker-compose/hydra.yml`:
  - Remove `hydra-head-promtail-positions` volume (lines 54, 186)
  - Remove `/run/user/1000/podman:/var/run/socks:rw`, `/proc:/host/proc:ro`, `/sys:/host/sys:ro`, `/:rootfs:ro` mounts (only used by Promtail)
  - Drop pod-level `userns_mode: "host"` (line 50) — but **keep** the `mkdir -m 777` workaround in the `core` service (line 102) since it fixes a separate rootless-podman bug (#333)
- [ ] **Deploy scripts**:
  - `scripts/setup-p100.sh` lines 31, 38-57: drop the `Installing promtail binary` and `Copying promtail config` blocks
  - `scripts/deploy-hydra-head.sh:123`: remove `infra-promtail` from the deploy loop
  - `scripts/deploy-hydra-head.sh:232-240`: replace the `promtail_sent_bytes_total` health gate with `hydra_loki_sent_entries_total` (a new metric the Loki client exposes; if the client is hand-rolled, add a simple counter)
- [ ] **Grafana dashboard** — `infra/grafana/dashboards/hydra-logs.json`:
  - Verify the 3 existing queries still match: `{component="hydra"}` (C# sets it via OTel `service.name`), `{component="hydra-head", node="rtx"}`, `{component="llama-server", node="p100"}`
  - Add a new panel: error count by component using `{level="error"}` filter
- [ ] **Tests**:
  - `src/head/internal/process/tagging_writer_test.go` (new) — assert a sample llama-server line gets `component=llama-server`, a sample node_exporter line gets `component=node-exporter`, an unknown line falls back to `component=hydra-head`
  - `src/head/internal/logging/loki_handler_test.go` (new) — assert the multi-handler produces both a text line on `os.Stdout` and a JSON line in the Loki client (use a fake `LokiClient` interface)
  - `src/core/Tests.Shared/HydraLoggingTests.cs` (new) — assert the OTel resource attributes (`service.name=hydra`, `node=rtx`) are set on the pipeline, and the previous Loki direct-push sink is gone
- [ ] **CI cleanup** — `.github/workflows/ci.yml:180`:
  - Remove `infra-promtail` from the deploy loop
- [ ] **Docs cleanup**:
  - `docs/RUNBOOK.md` lines 398, 685 — remove `container-log-shipper` references
  - `docs/architecture.md` § observability diagram (lines 288-335) — rewrite the diagram to show per-service push
  - `docs/workflow/06-monitoring.md` lines 14, 16 — drop `container-log-shipper` from the runbook
  - `docs/milestone-3-production.md` line 96 — drop `container-log-shipper` reference
- [ ] **THIRD_PARTY_NOTICES.md** — add `Serilog.Sinks.OpenTelemetry`, `OpenTelemetry.Exporter.OpenTelemetryProtocol`, `samber/slog-loki`, `samber/slog-multi`. Remove `Serilog.Sinks.Grafana.Loki` if no longer used.
- [ ] **Verification** (post-deploy, see "Verification" section below)
- [ ] **Close related issues** — link PR with `Closes #322`, `Closes #324`; any sub-issues the implementer files along the way.

## Sub-issues (existing)

| Issue | Title | Disposition |
|---|---|---|
| #322 | promtail-rtx.yml hardcodes docker.sock; in-container promtail ships 0 bytes to Loki | Closed by the Promtail removal (this PR) |
| #324 | Loki rejects hydra-core log entries >262144 bytes (max_entry_size) | Closed by the `max_entry_size: 1MiB` config bump (this PR) |

## New findings the implementer should file as individual issues if/when they want granular tracking

(The implementation can land as one PR with these noted in the PR body **or** be split into individual issues — the implementer's call. The audit identified these; they don't need to be filed as P0/P1 findings synchronously with this issue.)

1. **P1** — P100 promtail config is wrong (CRI on journald) — `infra/promtail/promtail-rtx.yml:49`
2. **P1** — Hydra.Core double-logs to Loki — `src/core/Hydra.Shared/HydraLogging.cs:26-34`
3. **P1** — P100 → RTX Loki URL is wrong (localhost resolves to P100 itself) — `infra/hydra-head/config/global.yaml:54`
4. **P1** — `component="hydra"` (Loki) vs `component="hydra-core"` (Prometheus) mismatch — `infra/prometheus/alerts.yml:6,13,19`
5. **P2** — Position file path broken on P100 — `infra/promtail/promtail-rtx.yml:13`
6. **P2** — `node_exporter` / `nvidia_exporter` mislabeled as `hydra-head` — `src/head/internal/process/manager.go:137,322`
7. **P2** — `userns_mode: "host"` only needed for Promtail (related to #333 chunk-cache bug) — `infra/docker-compose/hydra.yml:50`
8. **P2** — Stale CI / docs references to removed services — `.github/workflows/ci.yml:180`, `docs/RUNBOOK.md:398,685`, `docs/workflow/06-monitoring.md:14,16`
9. **P2** — `node` label missing on Hydra.Core Loki push — `src/core/Hydra.Shared/HydraLogging.cs:31-34`
10. **P0** — Promtail is EOL 2026-03-02 (this issue is the fix; the finding is "we are running EOL software")

## Acceptance criteria

The PR is "done" when **all** of the following are true:

- [ ] `dotnet test src/core/Tests.Shared/ && dotnet test src/core/Tests.Core/` is green
- [ ] `go test ./...` in `src/head/` is green
- [ ] `podman build` for `hydra-head:rtx` succeeds
- [ ] `bash scripts/deploy-hydra-head.sh all` deploys to both nodes
- [ ] All verification commands below pass
- [ ] The Grafana dashboard renders without errors
- [ ] The CI `deploy` step succeeds (the `infra-promtail` line is gone)
- [ ] PR is `Closes #322`, `Closes #324`

## Verification (deployed stack)

After deploy:

```bash
# 1. Confirm Hydra.Core is sending OTel logs to Loki
curl -s 'http://localhost:3100/loki/api/v1/query?query={component="hydra"}' | jq '.data.result | length'   # expect > 0

# 2. Confirm Hydra.Head is sending native push
curl -s 'http://localhost:3100/loki/api/v1/query?query={component="hydra-head"}' | jq '.data.result | length'   # expect > 0

# 3. Confirm llama-server lines are now under {component="llama-server"}
curl -s 'http://localhost:3100/loki/api/v1/query?query={component="llama-server",node="rtx"}' | jq '.data.result | length'   # expect > 0
curl -s 'http://192.168.122.1:3100/loki/api/v1/query?query={component="llama-server",node="p100"}' | jq '.data.result | length'   # expect > 0 (NOTE: 192.168.122.1 is the host as seen from the VM)

# 4. Confirm level label works
curl -s 'http://localhost:3100/loki/api/v1/query?query={level="error"}' | jq '.data.result | length'   # expect > 0 (after first error)

# 5. Confirm trace_id lives in structured metadata (not a label)
curl -s 'http://localhost:3100/loki/api/v1/label/trace_id/values' | jq .data   # expect [] — trace_id is structured metadata, not a label

# 6. No more promtail container
podman ps --filter name=infra-promtail   # expect nothing
ssh hydra-p100 'which promtail'          # expect "not found" or similar

# 7. Loki metrics: no more `line_too_long` errors
curl -s 'http://localhost:3100/metrics' | grep loki_discarded_samples_total
# expect no `reason="line_too_long"` samples for `component="hydra"`

# 8. Confirm the per-process drop counter is zero (no client-side overflow)
curl -s 'http://localhost:9700/metrics' | grep hydra_loki_dropped_entries_total
# expect 0 (or no time series yet)

# 9. Confirm the new Loki ruler rule fires
curl -s 'http://localhost:3100/loki/api/v1/query?query=component:errors:rate1m' | jq '.data.result | length'
# expect numeric series for hydra, hydra-head, llama-server (one per component)

# 10. Confirm level label works
curl -s 'http://localhost:3100/loki/api/v1/query?query={level="error"}' | jq '.data.result | length'
# expect > 0 (after first error)
```

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

- **Merge Method: squash** (one commit per logical change; the PR body carries the per-change list)
- **Review gate:** CI green **+ one human reviewer who has read this design doc** (the audit is in §"Why the current design hurts" + the design decisions in §"Decision")
- **Required reviewers:** at least one from the Hydra.Head maintainer group + one from the Hydra.Core maintainer group (cross-language PR)
- **Issue cross-links:** PR body must include `Closes #322, Closes #324, Closes #363`

## Risk analysis

| Risk | Likelihood | Mitigation |
|---|---|---|
| OTel push path silently loses entries (network blip) | Medium | Serilog OTel bounded queue + drop-oldest; alert on `loki_discarded_samples_total` |
| Loki native push path silently loses entries | Medium | samber/slog-loki has bounded buffer; same alert |
| `taggingWriter` misclassifies a llama line as `hydra-head` | Low | Same regex Promtail uses; covered by unit test |
| `cfg.Infra.Loki.URL` is wrong on P100 | Low | `node-p100.yaml` override is the fix; verified via `curl http://192.168.122.1:3100/ready` after deploy |
| Grafana dashboard breaks (label query mismatch) | Low | The 3 existing queries all match the new pipeline; new `{level="error"}` panel is additive |
| Chunk-cache chmod 777 workaround surfaces as a real bug when `userns_mode: "host"` is removed | Medium | **Separate** issue (#333 family); the workaround must stay until that root cause is fixed (separate PR) |
| Promtail EOL means a future Grafana upgrade breaks us anyway | None (mitigated) | We are removing Promtail in this PR |
| `Serilog.Sinks.OpenTelemetry` + `OpenTelemetry.Exporter.OpenTelemetryProtocol` is a heavier dependency than `Serilog.Sinks.Grafana.Loki` | Low | One-time cost; net benefit is trace correlation + vendor neutrality |

## Out of scope

- Migrating the `infra-host` pod (grafana, prometheus, loki, postgres, pgadmin, openwebui, renderer) to direct push. They don't produce useful logs; the current "drop observability component" rule in Promtail becomes a "no service ships these" rule.
- Adding a `level` label for Prometheus alerts. Loki labels are not Prometheus label — they're independent. The `prometheus.yml` `relabel_configs` already separate the two.
- Switching to OTel Collector / Alloy. Considered; deferred. Direct push is the right scope for "fix the Promtail mess." Alloy can be added later if a use case appears (redaction, sampling, kernel-log shipping).
- Migrating to Grafana Cloud / managed Loki. The local Loki 3.x deployment is fine.
- Backfilling the `level` label onto historical entries. Loki structured metadata is per-entry; no migration needed.

## Fork-side coordination

**None.** This change touches Hydra.Core (C#), Hydra.Head (Go), Loki config, and infra YAMLs. **No `src/llama-cpp` (fork) change** is needed.

## Handoff checklist (for the next dev)

- [ ] Read [`docs/design-direct-push-logging.md`](#) end-to-end (it's the spec)
- [ ] Set the Project board item **Status → In Progress** (per `docs/workflow/01-pickup.md`)
- [ ] Branch: `git checkout -b feat/m3-direct-push-logs` (or use the issue number in the name)
- [ ] Run the implementation checklist above; one commit per sub-task
- [ ] Run `dotnet test src/core/Tests.Shared/ && dotnet test src/core/Tests.Core/` (per `docs/workflow/03-test-verify.md`)
- [ ] Open the PR with `Closes #322`, `Closes #324`, and the issue number for **this** issue
- [ ] After deploy, run the verification commands above
- [ ] Per `docs/workflow/06-monitoring.md`, check Grafana + alerts for 24h before declaring done
- [ ] Per `docs/workflow/07-issue-and-close.md`, the PR merge auto-closes this issue (Status → Done)

---

## Gap review response

The design was reviewed on 2026-06-27 (16-item review on issue #363). This revision addresses every item:

### P0 — will not work as written

| # | Finding | How this revision addresses it |
|---|---|---|
| 1 | **Design doc absent in repo** | Doc lives on `docs/m3-direct-push-logs` branch and lands via PR #364. Once that merges, the canonical link is `docs/design-direct-push-logging.md` on `main`. The "Preconditions" section now lists explicit verification commands. |
| 2 | **OTel endpoint routing** | Endpoint is now `OTEL_EXPORTER_OTLP_ENDPOINT=http://localhost:4318` (Loki 3.x's native OTLP endpoint on host; `core` runs `network_mode: host` so `localhost` reaches it). Protocol: `http/protobuf` (Loki 3.x OTLP is HTTP, not gRPC). No more `:4318`. |
| 3 | **Loki version not verified** | Verified `3.4.3` via `/loki/api/v1/status/buildinfo`; `schema v13` and `tsdb` index are already in `infra/loki/loki-config.yml:19-27`. The preconditions table records both. |
| 4 | **`level` label asymmetry** | samer puts `level` in a Loki label; OTel puts `severity_text` in structured metadata. Resolution: a new Loki ruler rule `component:errors:rate1m` aggregates severity across both paths (via `… \| json \| severity_text=~"ERROR\|FATAL"`). The dashboard panel uses the ruler output, not a label query. Cheaper than promoting `severity_text` to a label. |
| 5 | **Observability-drop rule has no replacement** | The explicit Promtail drop rule is replaced by an **implicit** "no service ships observability" rule. The `infra-host` pod services don't run a Loki/OTel client, so they don't push. No exclusion rule needed in the new design. |

### P1 — will cause incidents

| # | Finding | How this revision addresses it |
|---|---|---|
| 6 | **`userns_mode: "host"` entangled with #333** | **kept** with a `// TODO: drop userns_mode after #333` comment. The chunk-cache `mkdir -m 777` workaround is still coupled to it. Removed from "what this PR changes" and added to "Out of scope" (it's a follow-up, not this PR). |
| 7 | **Rollout order unspecified** | new "Order: RTX first, then P100" subsection in the Rollout section: 7 explicit steps with verification gates between them (deploy RTX → wait for healthy Loki stream → then P100. Hard cutover is only safe if the order is explicit. |
| 8 | **No client-side drop counter** | added `hydra_loki_dropped_entries_total{component, reason}` Prometheus counter on both sides + Prometheus alert `HydraLokiDropsIncreasing`. Catches queue overflow, which `loki_discarded_samples_total` (Loki-side) misses. |
| 9 | **Queue bounds unspecified** | pinned explicitly: Serilog OTel `BackgroundWorkerOptions.QueueSize=65 536` (32 MB worst-case), samber `MaxBacklogCount=10 000` (5 MB worst-case). Drop policies: drop-oldest (C#), drop-newest (Go — samber default). |
| 10 | **Trace_id doesn't cross C#→Go boundary** | added a 5-line middleware in `src/head/internal/api/server.go` that reads `X-Hydra-Trace-Id` and pushes it to slog context as structured metadata. Closes the correlation gap. |

### P2 — hygiene

| # | Finding | How this revision addresses it |
|---|---|---|
| 11 | **`HYDRA_LOG_LOKI_URL`** dropped entirely. The env var and the `Serilog.Sinks.Grafana.Loki` dependency are removed. The OTel push is the new primary path. |
| 12 | **`taggingWriter` (brittle regex)** | replaced with **per-child labeled writers at spawn time**. The manager already knows the child name (`StartLlama` vs `StartService("node_exporter")`); each gets its own `io.Writer` configured with a static `component` label. The regex is gone. New "Per-child writers" subsection with Go code shape. |
| 13 | **PR review strategy** | added: squash merge, CI green + 1 reviewer per language group (hydra-head maintainer + hydra-core maintainer), required `Closes #322, Closes #324, Closes #363` cross-links. |
| 14 | **Ingestion rate doubling unjustified** | **reverted** to 32 MB. New "Why ingestion rate stays at 32 MB" subsection with the calculation: at 28 tok/s decode + ~1 prefill/min, peak per-process log volume is ~1 MB/min, so 32 MB/s aggregate is 32× headroom. The `max_entry_size: 1MiB` (4×) bump stays — that's the one that fixes #324. |
| 15 | **Container log-driver revert is operator-side** | added explicit RUNBOOK step: "After the cutover, the podman `k8s-file` log driver is no longer required. To switch to the default `journald`, edit `~/.config/containers/containers.conf` and set `log_driver = "journald"`, then restart any containers you want to log via `journalctl -u <service>`. Until then, no behavior change." Default stays `k8s-file` to keep this PR's blast radius minimal. |
| 16 | **Milestone** | confirmed M3 ("Persistence & Real Obs") is the right home. The active Llama-Engine milestone is P/D split, not observability. M3 is open and explicitly labeled "Real Obs." M3's *sub-phase* (Production phase per `CLAUDE.md`) is deferred, but the milestone itself is not. Decision stands. |
