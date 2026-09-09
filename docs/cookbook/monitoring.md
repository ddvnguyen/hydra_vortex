# monitoring — start Grafana / Prometheus / Loki / OTel, read the data

Start the observability stack and find the data. Distilled from
`docs/monitoring-observability.md`, `scripts/start-*.sh`, and
`DevelopmentRunBook.md`.

## Start

```bash
# Everything (quadlets + infra + hydra services) — idempotent:
bash scripts/start-env.sh

# Observability only:
bash scripts/start-infra.sh

# Manual (no quadlets):
cd infra && podman-compose -f docker-compose.infra.yml up -d
```

The stack runs as Quadlet user-systemd services (files in `infra/quadlets/`,
installed to `~/.config/containers/systemd/` by `start-env.sh`).

### OTel Collector (log pipeline)

```bash
systemctl --user is-active infra-otel-collector
curl -so/dev/null -w '%{http_code}\n' http://localhost:13133/   # 200 = healthy
systemctl --user restart infra-otel-collector                    # if needed
```

All services push logs via OTLP/HTTP to the collector on `localhost:4318`
(P100 VM uses `http://192.168.122.1:4318`), which fans out to Loki.

## Where things are

| What | URL |
|---|---|
| Grafana | http://localhost:3000 (anonymous admin) |
| Prometheus | http://localhost:9091 |
| Loki | http://localhost:3100 |
| Hydra.Core API metrics | http://localhost:9000/metrics |
| Hydra.Core Store metrics | http://localhost:9501/metrics |
| llama RTX 5060 Ti | http://localhost:8080/metrics |
| llama RTX 3060 | http://localhost:8081/metrics |
| node exporter | http://localhost:9100/metrics |
| GPU (DCGM) exporter | http://localhost:9835/metrics |
| Hydra Head status | http://localhost:9700/status · :9701/status · http://192.168.122.21:9700/status |
| Grafana image renderer | http://localhost:28081/ (was :8081 until PR #440) |

Test-lane equivalents are +10000 (19501/19503 core metrics, 18086/18087
engines) — see `test-lane.md`.

## Dashboards (Hydra dashboard panels)

1. Service Metrics — request rate, sessions, store ops, bytes, cache hit rate
2. KV Save/Restore Performance — save/restore p50/p95
3. Host & GPU — utilization, memory, temperature, power, CPU, RAM
4. llama-server — tokens/s, requests processing, KV cache usage
5. Service Health — up/down table, llama health per node, worker slot status
6. Logs — all service logs with `$trace_id` filter

## Reading logs

Container logs: `k8s-file` → `ctr.log` (CRI format) → docker service
discovery (`docker_sd_configs`) → relabel (component/node/job) → cri parser →
Loki.

**Prerequisite:** podman's log driver must be `k8s-file` in
`~/.config/containers/containers.conf`:

```ini
[containers]
log_driver = "k8s-file"
```

Existing containers must be recreated after changing it. With the default
`journald` driver there are no file-backed logs for the pipeline to scrape —
symptom: Loki shows zero hydra logs.

In Grafana Explore (Loki datasource), filter by `$trace_id` to correlate a
request across core → head → engine. P100 logs arrive via the OTel collector
from the VM (`node-test-*.yaml` / `node-p100.yaml` set
`OTEL_EXPORTER_OTLP_LOGS_ENDPOINT`).

## Alerts

Prometheus alerting rules: `infra/prometheus/alerts.yml` — service down, high
latency, GPU memory/temp, migration issues.

## Troubleshooting

| Symptom | Check |
|---|---|
| No logs in Grafana | OTel Collector active? `curl -s http://localhost:13133/`. Log driver `k8s-file`? |
| GPU panels empty | `curl -s localhost:9835/metrics \| head` — nvidia_exporter owned by the head (in-container since the host Quadlets were removed) |
| Port conflict on :9100/:9835/:9080 | Stray host exporters — `ss -tlnp \| grep -E ':(9080\|9100\|9835) '`; in-container hydra-head owns these now |
| Renderer panels broken | `curl -s localhost:28081/ \| head -1` |

## Related

- Live-request monitoring commands → `DevelopmentRunBook.md` (Monitoring Live Requests)
- Post-deploy check → `docs/workflow/06-monitoring.md`
