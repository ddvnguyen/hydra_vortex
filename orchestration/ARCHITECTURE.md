# Architecture — hydra_vortex

> HUMAN-ONLY FILE. Agents read this before planning and must never edit it.
> Changes to anything described here fall under the big-change gate in
> LEAD_CHARTER.md and require explicit user approval.

## System overview

This repo is **infrastructure-as-code** for a local LLM inference + observability
stack. There is no application source tree (no C#/Go/Python "app"); the units of
work are **config files, compose files, exporters, and dashboards**. The
autonomic loop (issue → plan → implement → PR → validate → deploy → monitor)
operates on these artifacts.

```
 CLIENTS / AGENTS (OpenAI-compat)
        │
        ▼
 Bifrost gateway :8088  (LB, OTel→Langfuse, /metrics)
        │
        ▼
 llama.cpp server :8080  (RTX 5060 Ti, --tensor-split 0.75,0.25)
        │ RPC
        ▼
 RPC node :50052  (Tesla P100)
        │
 OTel   ▼
 Langfuse :3000  (Postgres :5432 / ClickHouse :8123 / Redis :6379 / MinIO :9000)

 INFRA PLANE (Docker/Podman on host)
   Prometheus :9090  ← scrapes llama.cpp, Bifrost, GPU exporters, node_exporter,
                        RPC-bandwidth textfile, AND hydra_orchestration :8098
   Grafana    :3001  ← dashboards for inference, GPU, RPC, + hydra orchestration
```

The orchestration layer (this `orchestration/` folder) is driven by Paseo
schedules + GitHub issues, and is itself observable via the dashboard at `:8098`
(which exports Prometheus metrics consumed by the stack above).

## Module boundaries

| Area | Path | Language / kind | Validation command | Owner notes |
|------|------|-----------------|--------------------|-------------|
| Gateway | `monitoring-bifrost-langfuse/bifrost/config.json` | JSON | `curl :8088/metrics`; `curl -XPOST :8088/v1/chat/completions` | Bifrost provider + OTel plugin |
| Inference | `llama.cpp` launch (systemd/host) | binary + flags | `curl :8080/health`; `curl :8080/metrics \| grep llamacpp` | `--tensor-split`, `--metrics --slots` |
| Tracing | `monitoring-bifrost-langfuse/` (Langfuse compose) | YAML/compose | Langfuse UI :3000 shows traces | OTel auth via `LANGFUSE_AUTH` |
| Infra metrics | `monitoring/prometheus/prometheus.yml` | YAML | `promtool check config prometheus.yml` | scrape jobs + `alert_rules/*.yml` |
| Dashboards | `monitoring/grafana/provisioning/` | JSON/YAML | `promtool check rules`; Grafana loads on boot | datasources + dashboards |
| Exporters | systemd: `nvidia_gpu_exporter` :9835, `node_exporter` :9100, rpc-bandwidth textfile | systemd + bash | `curl :9835/metrics`, `curl :9100/metrics` | host + VM |
| Orchestration | `orchestration/` | Markdown + bash + Python | `bash -n scripts/*.sh`; `python3 -m py_compile dashboard/*.py` | Paseo schedules + dashboard :8098 |
| Instrumentor | `orchestration/instrumentor/` | Python | `python3 instrumentor/instrumentor.py` (one sweep) | MiniCPM5 canary probe (pi provider) |

## Hard rules (violations = stop and ask the user)

- **Secrets:** `.env`, `*.env`, `*.env.*` are git-ignored and NEVER committed.
  Config references secrets only via env (`.env.example` / compose `env_file`).
  The dashboard service reads `orchestration/state/` + shells `gh`/`paseo` only;
  it never reads `.env`.
- **Deploy targets:** agents may change configs that affect the running stack on
  **this host (staging)**. There is no production. Anything that would touch a
  remote/production target is human-only.
- **Public contracts:** Bifrost's OpenAI-compat API surface and Prometheus metric
  names are effectively public contracts for downstream tools — renaming metrics
  or changing API paths needs user approval (breaks Grafana/dashboards).
- **Schema/migrations:** none here (no app DB the agents own). Langfuse/Postgres
  are managed by compose; do not hand-edit their schemas.
- **CI/CD:** there is no CI yet. "red CI" in the monitoring charter refers to
  `gh run list` if/when workflows exist; until then, validation = the commands
  in the module table above.

## Preferred patterns

- Compose/YAML is the unit of change; validate before deploy (`docker compose
  config` / `podman-compose config`).
- Metric names: keep the existing `llamacpp:*`, `bifrost:*`, `nvidia_smi:*`,
  `rpc_network_*` conventions; new orchestration metrics use the `hydra_*`
  prefix (see `orchestration/dashboard/metrics.py`).
- One concern per PR; label with the correct `status:*` state-machine label.
- State that must survive agent restarts lives in GitHub issues/labels and
  `orchestration/state/` — never only in agent context.

## Environments

| Env | URL / target | Deploy command | Who may deploy |
|-----|--------------|----------------|----------------|
| staging | this host (Ubuntu + P100 VM) | `podman-compose up -d` in the relevant stack dir; restart systemd exporters | agents (autonomic) |
| production | none | n/a | human only (n/a) |

## Observability of the orchestration itself

- Live dashboard: `http://<host>:8098/` (agents, schedules, issue board,
  Instrumentor verdict, state checkpoints), auto-refreshing.
- Prometheus: `hydra_*` metrics scraped from `host-gateway:8098`
  (`monitoring/prometheus/prometheus.yml` job `hydra_orchestration`).
- Grafana: `monitoring/grafana/provisioning/dashboards/hydra-orchestration.json`.
- Instrumentor canary: `orchestration/state/instrumentor-report.md` +
  `instrumentor-history.log`; FAIL → issue labeled `source:instrumentor`.
