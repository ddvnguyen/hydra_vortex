#!/bin/sh
set -e

echo "[sidecar] Shutting down managed containers (reverse order)..."

for c in \
  llama-cpp \
  hydra_promtail_1 \
  hydra_grafana_1 \
  hydra_prometheus_1 \
  hydra_coordinator_1 \
  hydra_agent-p100_1 \
  hydra_agent-rtx_1 \
  hydra_nvidia-exporter_1 \
  hydra_node-exporter_1 \
  hydra_loki_1 \
  hydra_store_1
do
  if podman stop -t 5 "$c" >/dev/null 2>&1; then
    echo "[sidecar] Stopped $c"
  else
    echo "[sidecar] Skipped $c (not running or not found)"
  fi
  sleep 2
done

echo "[sidecar] Shutdown complete"
