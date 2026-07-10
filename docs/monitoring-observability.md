# Monitoring & Observability

> Extracted from CLAUDE.md to keep the handoff file short. Referenced from
> `CLAUDE.md` `## Monitoring & Observability` and `docs/workflow/06-monitoring.md`.

Prometheus + Loki + Grafana + Promtail run as Quadlet systemd user services
(files in `infra/quadlets/`); Hydra services (Hydra.Core) also run via
podman compose. Grafana at :3000, Prometheus at :9091, Loki at :3100.

## Start everything
```bash
# Install Quadlet files and start all services
bash scripts/start-env.sh

# Or start individual stacks:
bash scripts/start-infra.sh           # infra observability only
bash scripts/start-hydra.sh           # hydra core + hydra-head on RTX
bash scripts/deploy-hydra-head.sh all # deploy hydra-head to both nodes
```

## Key dashboards/metrics endpoints
- Grafana: http://localhost:3000 (anonymous admin)
- Prometheus: http://localhost:9091
- Core metrics: http://localhost:9501/metrics
- Core API metrics: http://localhost:9000/metrics
- llama RTX 5060 Ti metrics: http://localhost:8080/metrics
- llama RTX 3060 metrics: http://localhost:8081/metrics
- Node exporter: http://localhost:9100/metrics
- GPU exporter: http://localhost:9835/metrics
- Hydra Head API: http://localhost:9700/status (5060 Ti),
  http://localhost:9701/status (RTX 3060), http://192.168.122.21:9700/status (P100)

## Logs
Container logs shipped via containerized Promtail → Loki using Docker service
discovery (`docker_sd_configs`). Promtail discovers all containers from the
podman socket and reads k8s-file (CRI-format) logs directly from
`/mnt/containers/overlay-containers/<id>/userdata/ctr.log`.

View in Grafana Explore (Loki datasource) or the Logs panel in the Hydra dashboard.
Filter by `$trace_id` template variable to correlate logs across services.

**Log pipeline:** `k8s-file` → `ctr.log` (CRI) → `docker_sd_configs` →
`relabel_configs` (component/node/container/job) → `cri` parser → Loki.

**P100 logs:** Promtail reads journald for `hydra-head.service` unit, then regex-splits
llama-server lines from hydra-head lines via log pattern detection (see Log Separation
in `docs/hydra-head.md`).

**Prerequisite:** Podman's log driver must be `k8s-file` (set in
`~/.config/containers/containers.conf`) — journald has no file-backed logs for
Promtail to scrape.

## Alerts
Prometheus alerting rules in `infra/prometheus/alerts.yml` — covers service down, high latency, GPU memory/temp, migration issues.

## Dashboard panels
1. Service Metrics: request rate, sessions, store ops, bytes, cache hit rate, migrations
2. KV Save/Restore Performance: save/restore p50/p95 duration
3. Host & GPU: utilization, memory, temperature, power, CPU, RAM
4. llama-server: tokens/s, requests processing, KV cache usage
5. Service Health: up/down table, llama health per node, worker slot status
6. Logs: all service logs with trace_id filter
