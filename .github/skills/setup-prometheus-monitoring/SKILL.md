---
name: setup-prometheus-monitoring
description: "Use when: Configuring Prometheus scrape jobs for llama.cpp, LiteLLM, nvidia_gpu_exporter, node_exporter, and custom RPC bandwidth monitoring. Includes metric validation, relabeling strategies, and endpoint discovery for multi-node setups."
---

# Prometheus Monitoring Setup Skill

**Scope:** End-to-end Prometheus configuration for LLM inference observability

## What This Skill Does

Guides setup and troubleshooting of Prometheus scrape configs targeting:
- **llama.cpp** `:8080/metrics` (inference metrics: token/sec, KV cache %, request queue)
- **LiteLLM** `:4001/metrics` (proxy metrics: request count, latency, cost)
- **GPU exporters** (nvidia_gpu_exporter on host RTX 5060 Ti + VM Tesla P100)
- **System exporters** (node_exporter on host + VM)
- **Custom RPC bandwidth** (textfile collector for iftop/nload data)

## Usage Examples

- `/setup-prometheus-monitoring Create a multi-node scrape config with host and VM targets`
- `/setup-prometheus-monitoring Add relabeling to distinguish GPU metrics (host vs P100)`
- `/setup-prometheus-monitoring Validate endpoints are responding before Prometheus startup`
- `/setup-prometheus-monitoring Set up metric federation across host/VM Prometheus instances`

## Workflow

1. **Discover targets** — Identify all metrics endpoints (llama.cpp, LiteLLM, exporters)
2. **Design scrape jobs** — Create scrape_configs with appropriate intervals, timeouts, relabeling
3. **Add service discovery** — Optional: consul/file-based SD for dynamic target management
4. **Validate endpoints** — Run curl tests to confirm metrics format before Prometheus startup
5. **Configure relabeling** — Add labels for node/gpu/service identification
6. **Handle TLS/auth** — If needed, add scrape_configs security settings

## Key Templates Included

- `prometheus.yml` — Base config with all scrape jobs + alerting rules
- `relabel-rules.yml` — Label relabeling patterns for multi-node distinction
- `validation-script.sh` — Curl checks to validate all endpoints
- `alerting-rules.yml` — Alert rules for GPU saturation, queue depth, latency spikes

## Prerequisites

- Prometheus installed
- All target endpoints configured and responding (verify with curl first)
- Understanding of Prometheus YAML syntax

## Related Skills

- `provision-grafana-dashboards` — Consume these metrics into Grafana panels
- `validate-llama-metrics` — Debug missing or incorrect metrics at the source
