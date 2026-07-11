---
name: provision-grafana-dashboards
description: "Use when: Creating multi-panel Grafana dashboards for LLM inference observability. Covers: LLM throughput visualization (token/sec, latency), GPU hardware monitoring (VRAM, utilization, temperature), RPC network diagnostics. Generates Grafana JSON, provisioning configs, and dashboard templates."
---

# Grafana Dashboard Provisioning Skill

**Scope:** End-to-end Grafana dashboard design and provisioning for LLM monitoring

## What This Skill Does

Generates production-ready Grafana dashboards targeting three observability areas:

1. **LLM Inference Dashboard**
   - Token throughput (prompt/sec, generation/sec)
   - Request latency (p50, p95, p99)
   - Active requests, queue depth, error rate

2. **GPU Hardware Dashboard**
   - VRAM utilization (host RTX 5060 Ti + VM Tesla P100)
   - GPU utilization %, power draw, temperature
   - Memory bandwidth, thermal throttling alerts

3. **RPC Network Diagnostics Dashboard**
   - Network throughput (VM ↔ host PCIe/network)
   - Packet loss, latency between RPC server and host
   - Bandwidth saturation detection

## Usage Examples

- `/provision-grafana-dashboards Create an inference dashboard showing token/sec with alert thresholds`
- `/provision-grafana-dashboards Generate GPU comparison panels (RTX vs P100 side-by-side)`
- `/provision-grafana-dashboards Build RPC bottleneck diagnostics dashboard`
- `/provision-grafana-dashboards Export all dashboards as JSON and provisioning config`

## Workflow

1. **Define panels** — Specify metrics, PromQL queries, visualization type
2. **Design layout** — Organize panels into logical rows/sections
3. **Add thresholds** — Set alert colors, value ranges, warning zones
4. **Generate JSON** — Export Grafana dashboard JSON
5. **Create provisioning** — Generate `dashboard.yml` for auto-import
6. **Document metrics** — Include legend and metric definitions

## Key Templates Included

- `llm-inference-dashboard.json` — Throughput, latency, request lifecycle
- `gpu-hardware-dashboard.json` — Dual GPU comparison (host + VM)
- `rpc-network-dashboard.json` — Network bottleneck detection
- `dashboard-provisioning.yml` — Auto-load dashboards on Grafana startup
- `shared-library.json` — Reusable panels for inheritance

## Prerequisites

- Grafana installed (:3000)
- Prometheus data source configured
- Metrics already being scraped (see `setup-prometheus-monitoring`)

## Related Skills

- `setup-prometheus-monitoring` — Source metric collection
- `validate-llama-metrics` — Ensure underlying metrics are correct
