#!/bin/sh
set -e

echo "[sidecar] Stopping compose services..."

cd /mnt/WorkDisk/Workplace/hydra_vortex/infra
podman-compose down 2>&1

echo "[sidecar] Stopping llama-cpp..."
podman stop -t 5 llama-cpp 2>/dev/null || true

echo "[sidecar] Shutdown complete"
