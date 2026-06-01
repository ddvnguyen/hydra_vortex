#!/bin/sh
set -e

DOCKER="docker -H unix:///var/run/docker.sock"

echo "[sidecar] Waiting for podman socket..."
for i in $(seq 30); do
  [ -S /var/run/docker.sock ] && break
  sleep 1
done

echo "[sidecar] Starting managed containers sequentially..."

for c in \
  hydra_store_1 \
  hydra_loki_1 \
  hydra_node-exporter_1 \
  hydra_nvidia-exporter_1 \
  hydra_agent-rtx_1 \
  hydra_agent-p100_1 \
  hydra_coordinator_1 \
  hydra_prometheus_1 \
  hydra_grafana_1 \
  hydra_promtail_1 \
  llama-cpp
do
  if $DOCKER start "$c" >/dev/null 2>&1; then
    echo "[sidecar] Started $c"
  else
    echo "[sidecar] Skipped $c (not found or already running)"
  fi
  sleep 4
done

echo "[sidecar] Ensuring llama-cpp is on hydra_default network..."
$DOCKER network connect hydra_default llama-cpp 2>/dev/null || true

echo "[sidecar] Verifying..."
sleep 5
$DOCKER ps --format 'table {{.Names}}\t{{.Status}}'
echo "[sidecar] Startup complete"
