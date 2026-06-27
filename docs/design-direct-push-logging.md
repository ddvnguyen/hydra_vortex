# Design — Per-Service Direct Log Shipping to Loki

- **Status:** Draft (awaiting review)
- **Date:** 2026-06-27
- **Author:** engineering (senior)
- **Scope:** Hydra.Core (C#), Hydra.Head (Go), llama-server (C++), node/nvidia exporters, the `infra-host` Quadlet pod, and the Loki deployment.
- **Supersedes:** the central-Promtail / docker-SD / CRI-parser pipeline that has been the source of 10+ open review findings and 1 confirmed pre-existing bug (P100 promtail scrapes journald with a CRI parser).
- **Motivator (external):** Promtail reached **EOL on 2026-03-02** per the Grafana docs — it is no longer the recommended path. The replacement is direct push to Loki (either native or OTLP) and Grafana Alloy for file-based scraping.

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
│                     │              │ Loki POST /otlp/v1/logs (OTLP/HTTP,     │
│                     │              │ gzip; auto-trace correlation)          │
│ Hydra.Head (Go)     │ log/slog     │ samber/slog-loki →                      │
│                     │              │ Loki POST /loki/api/v1/push             │
│                     │              │ (JSON, gzip, native labels)            │
│ llama-server (C++)  │ (unchanged)  │ stdout → hydra-head supervisor pipes,  │
│                     │              │ regex-classifies, ships with           │
│                     │              │ component=llama-server (via head's     │
│                     │              │ Loki client)                           │
│ node_exporter,      │ (unchanged)  │ stdout → hydra-head supervisor pipes,  │
│ nvidia_exporter     │              │ ships with component=node-exporter /  │
│                     │              │ component=nvidia-exporter              │
│ infra-* (grafana,   │ (unchanged)  │ ctr.log; the observability stack       │
│ loki, prometheus,   │              │ continues to be excluded from logs    │
│ postgres, pgadmin,  │              │ (not useful, not scraped)               │
│ openwebui,          │              │                                        │
│ renderer)           │              │                                        │
└──────────────────────────────────────────────────────────────────────────────┘
                                       │
                                       ▼
                          ┌───────────────────────────┐
                          │ Loki 3.x :3100            │
                          │  - max_entry_size: 1 MiB  │  ← fixes #324
                          │  - ingestion_rate: 64 MB  │  ← fixes burst drops
                          │  - structured_metadata:   │
                          │    on (default in 3.0+)   │
                          │  - schema v13, tsdb index │  ← required for OTLP
                          └───────────────────────────┘
```

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

### Per-service responsibilities

| Service | What changes |
|---|---|
| **Hydra.Core (C#)** | Replace `Serilog.Sinks.Grafana.Loki` with `Serilog.Sinks.OpenTelemetry` + `OpenTelemetry.Exporter.OpenTelemetryProtocol` + `OpenTelemetry.Extensions.Hosting`. Set `service.name=hydra`, `service.namespace=hydra-core`, `deployment.environment.name=dev` as resource attributes. Set the `node` label via `Enrich.WithProperty("node", …)`. Map Serilog `Level` to the OTel `severity` field; map Serilog `LogContext` properties to OTel attributes. Stop writing to the console (Loki is enough; the 2× ingestion today is a bug). |
| **Hydra.Head (Go)** | Add `github.com/samber/slog-loki/v3` and `github.com/samber/slog-multi/v2` (fanout). Build a multi-handler: (1) text → `os.Stdout` (kept for journald / `journalctl -u hydra-head`), (2) JSON → Loki push with `BatchWait=2s`, `BatchEntriesNumber=500`, gzip, labels `{component=hydra-head, node=<config.node.name>}`. For each child-process stdout byte stream (`llama-server`, `node_exporter`, `nvidia_exporter`), replace `os.Stdout` with a `taggingWriter` that: buffers lines, regex-checks the llama prefix, constructs the right `component` label, pushes through the same Loki client. |
| **Loki** | Bump `limits_config.max_entry_size` from default 256 KiB to 1 MiB (fixes #324). Bump `ingestion_rate_mb` from 32 to 64 and `ingestion_burst_size_mb` from 64 to 128 to handle prefill bursts. Confirm `schema_config` is `v13` and the index is `tsdb` (required for OTLP — already so, verified). |
| **P100 systemd** | `node-p100.yaml` overrides `infra.loki.url` to `http://192.168.122.1:3100` (the RTX host as seen from the KVM VM — pre-existing bug fix). The journald-only log path is **kept** as a fallback for `journalctl` forensics; direct push is the new primary. |
| **Promtail** | **Removed entirely.** The two promtail binaries (in `hydra-head-rtx` container + on P100 VM) are deleted. The `promtail:` block in `node-rtx.yaml` and `node-p100.yaml` is removed (the `Config.GeneratePromtailConfig` function in `src/head/internal/config/config.go:360-399` becomes dead code, kept for now behind a `// Deprecated: ...` comment). |
| **Pod level** | Drop `userns_mode: "host"` from `infra-host` and `hydra-system` pods — the only reason for it was Promtail's ctr.log access. (The `core` container's `mkdir -m 777` workaround for the chunk-cache write-behind path is **not** related and stays.) |
| **k8s-file log driver** | The podman log-driver requirement in `~/.config/containers/containers.conf` is **no longer needed**. Operators can switch to `journald` (the default) with no log-pipeline consequences. Document this in the runbook. |
| **Hydra.Head (Go) test setup** | `src/head/internal/registry/integration_test.go:18,46,90` and friends use `slog.NewTextHandler(io.Discard, nil)` — keep as-is. Add new unit tests for the Loki handler and the tagging writer. |

### Why OTel OTLP for Hydra.Core, native push for Hydra.Head

This is the only asymmetry in the design. The reason is **trace correlation**:

- **Hydra.Core** is where the business logic lives (Coordinator, Store, state migration, KV save/restore). Every domain event in C# has a `trace_id` (`HydraLogging.cs:39-42`; `TraceScope` is called from `StateHandler.cs:55,93,163,336` and others). OTel's log SDK auto-correlates these with traces. Direct OTel push to Loki's `/otlp/v1/logs` is the cleanest path.
- **Hydra.Head** is a supervisor — most of its log lines are system events (process started, restart with backoff, OCI pull success). No `trace_id` propagation. A clean `slog` handler that fans out to text + Loki is a much smaller code change than introducing the OTel Go SDK and its resource-attribute boilerplate.

Both endpoints land in the same Loki 3.x. The label set is normalized to `{component, node, level}` at the **client side** (C# sets `component=hydra` via OTel resource attribute `service.name=hydra`; Go sets it explicitly in the stream).

This is a deliberate trade-off. If a future requirement demands full OTel end-to-end (e.g. Grafana Tempo cross-stack correlation), the Go side can be migrated to `otlploghttp` later in a one-service change.

### Configuration changes (per file)

| File | Change |
|---|---|
| `infra/loki/loki-config.yml` | Add `max_entry_size: 1MiB`, bump `ingestion_rate_mb: 64`, `ingestion_burst_size_mb: 128` |
| `src/core/Hydra.Shared/HydraLogging.cs` | Replace Loki push sink with OTel pipeline; add `node` enricher; drop console sink (or keep as `Debug`-only for dev) |
| `src/core/Hydra.Shared/Hydra.Shared.csproj` | Add `Serilog.Sinks.OpenTelemetry`, `OpenTelemetry.Exporter.OpenTelemetryProtocol`, `OpenTelemetry.Extensions.Hosting` |
| `src/core/Hydra.Core/Program.cs` | Wire OTel resource attributes (`service.name`, `service.namespace`, `deployment.environment.name`) |
| `src/head/main.go` | Construct `samber/slog-loki` handler + `samber/slog-multi` fanout to text + Loki; read `cfg.Infra.Loki.URL` and `cfg.Node.Name` |
| `src/head/internal/process/manager.go` | Add `taggingWriter`; replace `proc.logWriter = os.Stdout` with `proc.logWriter = newTaggingWriter(m.lokiClient, m.cfg.Node.Name)` |
| `src/head/go.mod` | Add `github.com/samber/slog-loki/v3`, `github.com/samber/slog-multi/v2` |
| `src/head/internal/config/config.go` | Add `Logging.LokiURL`, `Logging.Node` (or reuse `Node.Name`); mark `GeneratePromtailConfig` as `// Deprecated` |
| `infra/hydra-head/config/global.yaml` | (no change — `infra.loki.url` stays) |
| `infra/hydra-head/config/node-p100.yaml` | Add `infra.loki.url: http://192.168.122.1:3100` (per-host override) |
| `infra/hydra-head/config/node-rtx.yaml` | Remove `services.promtail:` block |
| `infra/hydra-head/config/node-p100.yaml` | Remove `services.promtail:` block |
| `infra/hydra-head/Dockerfile.rtx` | Remove promtail install (lines 60-74) and config copy (line 56) |
| `infra/docker-compose.hydra.yml` | Remove `hydra-head-promtail-positions` volume (lines 54, 186); remove `/run/user/1000/podman:/var/run/socks:rw`, `/proc:/host/proc:ro`, `/sys:/host/sys:ro`, `/:/rootfs:ro` mounts; drop `userns_mode: "host"` (line 50) |
| `infra/docker-compose.hydra.yml` | `core` service: change `HYDRA_LOG_LOKI_URL` to `http://loki:3100` (or keep `localhost:3100` — `core` runs `network_mode: host`); set `OTEL_SERVICE_NAME=hydra`, `OTEL_EXPORTER_OTLP_ENDPOINT=http://localhost:4318` |
| `infra/promtail/promtail-rtx.yml` | **File deleted** (no longer used) |
| `infra/promtail/` | **Directory deleted** (only contained `promtail-rtx.yml`) |
| `scripts/setup-p100.sh` | Remove the `Installing promtail binary` (lines 38-50) and `Copying promtail config` (lines 52-57) blocks |
| `scripts/deploy-hydra-head.sh` | Remove the `promtail_sent_bytes_total` health gate (lines 232-240); remove `infra-promtail` from `stop_host_sidecars()` (line 123 loop) |
| `infra/grafana/dashboards/hydra-logs.json` | Add `{level="error"}` filter panel; verify the three existing queries (`{component="hydra"}`, `{component="llama-server", node="rtx"}`, `{component="llama-server", node="p100"}`) still match — they will, because the C# side sets `component=hydra` and the Go side sets `component=llama-server` |
| `.github/workflows/ci.yml` | Remove `infra-promtail` from the deploy loop (line 180) |
| `docs/RUNBOOK.md` | Remove `container-log-shipper` references (lines 398, 685) |
| `docs/architecture.md` | Update § observability diagram (lines 288-335) to show the new direct-push pipeline |
| `docs/workflow/06-monitoring.md` | Remove the `systemctl --user restart container-log-shipper promtail` lines (14, 16) |
| `docs/milestone-3-production.md` | Remove `container-log-shipper` reference (line 96) |
| `THIRD_PARTY_NOTICES.md` | Add `Serilog.Sinks.OpenTelemetry`, `OpenTelemetry.Exporter.OpenTelemetryProtocol`, `samber/slog-loki`, `samber/slog-multi` (the existing Loki sink can be removed if no longer used) |

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

Hard cutover in one PR. Rationale (from the user):

- The new pipeline does not require any new infrastructure (Loki is already up).
- The two promtail instances are still running in production — if the cutover fails, the **rollback is `git revert` and `bash scripts/deploy-hydra-head.sh all`** to redeploy the old hydra-head image. The OCI-cached promtail binary remains in the old image; no data loss on rollback.
- A parallel-run window would double Loki ingestion cost for one release and create a "which pipeline is the truth" question.
- The risk is bounded: the new direct-push clients fail-soft (Serilog Loki sink: drop on queue overflow; samber/slog-loki: bounded buffer with retries). No service crashes if Loki is unreachable.

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

# 4. Confirm level label works
curl -s 'http://localhost:3100/loki/api/v1/query?query={level="error"}' | jq .data.result | head

# 5. Confirm no {component="observability"} stream (sanity)
curl -s 'http://localhost:3100/loki/api/v1/query?query={component="observability"}' | jq .data.result

# 6. Verify no entries from the old Promtail pipeline are still being created
#    (only relevant if rollback is not used)
#    Look for: {component="hydra"} entries dropping to 0
```

## Risk analysis

| Risk | Likelihood | Mitigation |
|---|---|---|
| OTel push path silently loses entries (network blip) | Medium | Serilog OTel sink has bounded queue + drop-oldest; `loki_discarded_samples_total` alerts |
| Loki native push path silently loses entries | Medium | `samber/slog-loki` has bounded buffer; same alert |
| `taggingWriter` misclassifies a llama line as `hydra-head` | Low | Regex is the same one Promtail uses; covered by unit test |
| `cfg.Infra.Loki.URL` is wrong on P100 | Low | `node-p100.yaml` override is the fix; verified via `curl http://192.168.122.1:3100/ready` after deploy |
| Grafana dashboard breaks (label query mismatch) | Low | The 3 existing queries all match the new pipeline; new `{level="error"}` panel is additive |
| Chunk-cache chmod 777 workaround surfaces as a real bug when `userns_mode: "host"` is removed | Medium | **Separate** issue: this is not a log-pipeline fix; the workaround must stay until the underlying rootless-podman mapping bug is fixed (separate PR) |
| Promtail EOL means a future Grafana upgrade breaks us anyway | None (mitigated) | We are removing Promtail in this PR |
| `Serilog.Sinks.OpenTelemetry` + `OpenTelemetry.Exporter.OpenTelemetryProtocol` is a heavier dependency than `Serilog.Sinks.Grafana.Loki` | Low | One-time cost; net benefit is trace correlation + vendor neutrality |

## Out of scope

- Migrating the `infra-host` pod (grafana, prometheus, loki, postgres, pgadmin, openwebui, renderer) to direct push. They don't produce useful logs; the current "drop observability component" rule in Promtail becomes a "no service ships these" rule. Simpler.
- Adding a `level` label for Prometheus alerts. Loki labels are not Prometheus labels — they're independent. The `prometheus.yml` `relabel_configs` already separate the two.
- Switching to OTel Collector / Alloy. Considered; deferred. Direct push is the right scope for "fix the Promtail mess." Alloy can be added later if a use case appears (redaction, sampling, kernel-log shipping).
- Migrating to Grafana Cloud / managed Loki. The local Loki 3.x deployment is fine.
- Backfilling the `level` label onto historical entries. Loki structured metadata is per-entry; no migration needed.
