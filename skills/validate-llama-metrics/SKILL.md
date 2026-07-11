---
name: validate-llama-metrics
description: "Use when: Debugging missing, incorrect, or stale metrics from llama.cpp /metrics and /slots endpoints. Includes endpoint validation, metric naming verification, common misconfigurations, RPC node issues, and GPU inference diagnostics."
---

# Validate Llama.cpp Metrics Skill

**Scope:** End-to-end validation and debugging of llama.cpp observability

## What This Skill Does

Troubleshoots llama.cpp metrics collection across:
- **Inference metrics** (token/sec, latency, KV cache %)
- **Slot management** (per-slot status, request queue)
- **RPC endpoint connectivity** (P100 VM health check)
- **GPU allocation** (tensor-split verification, layer distribution)

## Usage Examples

- `/validate-llama-metrics Check why llama.cpp /metrics endpoint isn't responding`
- `/validate-llama-metrics Debug missing predicted_tokens_seconds metric`
- `/validate-llama-metrics Verify RPC node connectivity and layer distribution`
- `/validate-llama-metrics Generate a metrics audit report (present vs expected)`
- `/validate-llama-metrics Check if GPU VRAM mismatch is causing slot exhaustion`

## Workflow

1. **Check endpoint health** — curl llama.cpp `:8080/metrics` and `:8080/slots`
2. **Verify flags** — Confirm server started with `--metrics --slots --rpc` 
3. **List expected metrics** — Validate all key metrics are present
4. **Check metric values** — Detect stale/zero values (sign of issues)
5. **Diagnose common issues** — RPC disconnection, tensor-split mismatch, slot exhaustion
6. **Generate report** — Document findings and remediation steps

## Key Diagnostics Included

### Metric Presence Checks
- `llamacpp:predicted_tokens_seconds` — Generation throughput
- `llamacpp:prompt_tokens_seconds` — Prompt processing throughput
- `llamacpp:kv_cache_usage_ratio` — KV cache % utilized
- `llamacpp:requests_processing` — Active inference requests
- `llamacpp:requests_deferred` — Queued requests (slot exhaustion)
- `llamacpp:n_busy_slots_per_decode` — Slot utilization

### Common Issues & Fixes
| Issue | Cause | Fix |
|-------|-------|-----|
| No metrics endpoint | `--metrics` flag missing | Restart llama-server with `--metrics` |
| Zero throughput | No active inference | Load a model, send requests via LiteLLM |
| Stale metrics | RPC node crashed | Check P100 VM SSH connectivity |
| Slot queue growing | GPU VRAM exhausted | Check `predicted_tokens_seconds` drop |
| Tensor-split mismatch | RPC config incorrect | Verify `--tensor-split 0.75,0.25` matches nodes |

## Prerequisites

- llama.cpp server running on `:8080`
- Metrics enabled (`--metrics` flag)
- RPC node accessible (if using tensor-split)
- curl or browser to test endpoints

## Related Skills

- `setup-prometheus-monitoring` — Scrape these metrics safely once validated
- `provision-grafana-dashboards` — Visualize the metrics once collection is working
