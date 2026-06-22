#!/usr/bin/env bash
# Start the hydra-system pod: hydra-core + hydra-head-rtx in the same pod
# so they share a network namespace and pod-level health check works.
# P100 stays in its own VM and connects over the network.
#
# Idempotent: stops+removes existing standalone containers and re-creates
# them in the pod. Run after `bash scripts/deploy-hydra-head.sh rtx` has
# already built hydra-head:rtx.
#
# Requires:
#   - /home/.../infra/hydra-core/config:/etc/hydra/config mounted (config)
#   - /mnt/SSD/hydra-backup mounted into core (so backups hit real disk)
#   - /mnt/containers/auth.json (or ~/.config/containers/auth.json) populated
#     with a write:packages GHCR token
#   - src/llama-cpp/build_sm120/bin/ has the current llama-server

set -euo pipefail

REPO_ROOT="$(cd "$(dirname "$0")/.." && pwd)"
POD_NAME="hydra-system"

# 1. Tear down standalone
for c in hydra-core_core_1 hydra-head-rtx; do
  if podman ps -a --format '{{.Names}}' | grep -qx "$c"; then
    podman stop "$c" 2>/dev/null || podman kill "$c" 2>/dev/null || true
    podman rm "$c" 2>/dev/null || true
  fi
done

# 2. Create pod (host network so :9000 / :9700 / :8080 are accessible)
if podman pod exists "$POD_NAME"; then
  podman pod rm "$POD_NAME" 2>/dev/null || true
fi
podman pod create --name "$POD_NAME" --network host

# 3. Worktree path (where container reads workers.json from)
WORKTREE=/home/ddv/.local/share/opencode/worktree/8115ac4c1f142397ad1a1c64b687457fc241b866/worker-engine-llama-cpp
[ -d "$WORKTREE/infra/hydra-core/config" ] || WORKTREE="$REPO_ROOT"

# 4. hydra-core
podman run -d \
  --pod "$POD_NAME" \
  --name hydra-core \
  --tmpfs /mnt/llm-ram:size=30G \
  -v "$WORKTREE/infra/hydra-core/config:/etc/hydra/config:ro" \
  -v /mnt/SSD/hydra-backup:/mnt/SSD/hydra-backup:rw \
  -e HYDRA_STORE_HOST=0.0.0.0 \
  -e HYDRA_STORE_PORT=9500 \
  -e HYDRA_STORE_DIR=/mnt/llm-ram/store \
  -e HYDRA_STORE_DEBUG_PORT=9501 \
  -e "HYDRA_STORE_PG_CONN=Host=localhost;Database=hydra_store;Username=hydra;Password=hydra" \
  -e HYDRA_STORE_BACKUP_DIR=/mnt/SSD/hydra-backup \
  -e HYDRA_STORE_RESTORE_TOP_N=10 \
  -e HYDRA_COORD_PORT=9000 \
  -e HYDRA_COORD_MIX_PRECISION_ENABLED=true \
  -e HYDRA_COORD_ALLOW_CROSS_MODEL_KV_REUSE=true \
  -e HYDRA_COORD_ATOMIC_THRESHOLD=2048 \
  -e HYDRA_COORD_WARM_THRESHOLD=5120 \
  -e HYDRA_COORD_CONFIG_FILE=/etc/hydra/config/workers.json \
  -e HYDRA_COORD_ENABLE_CHUNKS=true \
  -e HYDRA_STORE_CHUNK_SIZE=8192 \
  -e HYDRA_LOG_LOKI_URL=http://localhost:3100 \
  --label component=hydra \
  --health-cmd "/bin/bash -c 'exec 3<>/dev/tcp/localhost/9501'" \
  --health-interval 10s --health-timeout 5s --health-retries 3 \
  --health-start-period 20s \
  --restart always \
  localhost/hydra-core_core:latest

# 5. hydra-head-rtx (auth token for GHCR pulls)
TOKEN="$(cat "$REPO_ROOT/.hydra-head-token" 2>/dev/null || echo placeholder)"
podman run -d \
  --pod "$POD_NAME" \
  --name hydra-head-rtx \
  --device nvidia.com/gpu=all \
  -e HYDRA_HEAD_AUTH_TOKEN="$TOKEN" \
  -e REGISTRY_AUTH_FILE=/run/host-ctrs-auth.json \
  -v /run/user/1000/containers/auth.json:/run/host-ctrs-auth.json:ro \
  -v /mnt/SSD:/models:ro \
  -v /proc:/host/proc:ro \
  -v /sys:/host/sys:ro \
  -v /:/rootfs:ro \
  -v /run/user/1000/podman:/var/run/socks:rw \
  -v /mnt/containers/:/mnt/containers/:ro \
  -v hydra-head-promtail-positions:/opt/hydra/promtail-positions:rw \
  -v "$REPO_ROOT/src/llama-cpp/build_sm120/bin/:/llama/bin/:ro" \
  -v "$REPO_ROOT/infra/hydra-head/config:/opt/hydra/config:ro" \
  --health-cmd "curl -f http://localhost:9700/health || exit 1" \
  --health-interval 30s --health-timeout 5s --health-retries 3 \
  --health-start-period 15s \
  --restart always \
  hydra-head:rtx

echo "==> pod hydra-system up. tail with: podman pod logs -f hydra-system"
