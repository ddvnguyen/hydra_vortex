#!/usr/bin/env bash
# Deploy Hydra Head to GPU nodes
# Usage: bash scripts/deploy-hydra-head.sh [rtx|p100|all]

set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$REPO_ROOT"

GREEN='\033[0;32m'; RED='\033[0;31m'; YELLOW='\033[1;33m'; BOLD='\033[1m'; NC='\033[0m'
ok()   { echo -e "  ${GREEN}✓${NC} $*"; }
warn() { echo -e "  ${YELLOW}⚠${NC}  $*"; }
fail() { echo -e "  ${RED}✗${NC} $*"; }
step() { echo -e "\n${BOLD}==> $*${NC}"; }
die()  { fail "$*"; exit 1; }

TARGET="${1:-all}"

# ── Auth Token Management ─────────────────────────────────────────────────────
TOKEN_FILE="$REPO_ROOT/.hydra-head-token"

generate_token() {
  if [ -f "$TOKEN_FILE" ]; then
    ok "Using existing auth token from $TOKEN_FILE"
    return
  fi
  
  step "Generating new auth token"
  # Generate a random 32-byte hex token
  openssl rand -hex 32 > "$TOKEN_FILE"
  chmod 600 "$TOKEN_FILE"
  ok "Generated new auth token: $TOKEN_FILE"
}

get_token() {
  if [ ! -f "$TOKEN_FILE" ]; then
    die "Auth token not found. Run with 'generate' first."
  fi
  cat "$TOKEN_FILE"
}

# ── Build ─────────────────────────────────────────────────────────────────────
step "Building hydra-head"

export PATH=$HOME/go-sdk/go/bin:$PATH
if ! command -v go &>/dev/null; then
  die "Go not found. Install with: mkdir -p ~/go-sdk && cd /tmp && wget https://go.dev/dl/go1.25.0.linux-amd64.tar.gz && tar -C ~/go-sdk -xzf go1.25.0.linux-amd64.tar.gz"
fi

go build -C "$REPO_ROOT/src/head" -o "$REPO_ROOT/bin/hydra-head" .
ok "Built bin/hydra-head"

# Generate auth token
generate_token
AUTH_TOKEN=$(get_token)

# ── Deploy Functions ──────────────────────────────────────────────────────────
deploy_rtx() {
  step "Deploying to RTX (container)"

  if ! command -v podman &>/dev/null; then
    die "podman not found"
  fi

  # Build container image
  podman build -f infra/hydra-head/Dockerfile.rtx -t hydra-head:rtx .
  ok "Built container image hydra-head:rtx"

  # Stop existing container if running
  if podman container exists hydra-head-rtx 2>/dev/null; then
    podman stop hydra-head-rtx
    podman rm hydra-head-rtx
  fi

  # Pre-stop the 3 host sidecar Quadlets — hydra-head now manages these
  # as child processes inside this container, so the host ports (9100,
  # 9835, 9080) must be free for the in-container children to bind.
  export DBUS_SESSION_BUS_ADDRESS="unix:path=/run/user/$(id -u)/bus"
  export XDG_RUNTIME_DIR="/run/user/$(id -u)"
  for svc in infra-node-exporter infra-nvidia-exporter infra-promtail; do
    if systemctl --user is-active --quiet "$svc.service" 2>/dev/null; then
      systemctl --user stop "$svc.service" 2>/dev/null || true
      ok "Stopped host $svc (replaced by hydra-head child)"
    fi
  done
  unset DBUS_SESSION_BUS_ADDRESS XDG_RUNTIME_DIR

  # Persistent volume for promtail positions (so log cursors survive
  # container restarts, replacing the old promtail-positions.volume).
  if ! podman volume exists hydra-head-promtail-positions 2>/dev/null; then
    podman volume create hydra-head-promtail-positions
  fi

  # Mount the host's podman auth file so the in-container hydra-head can
  # pull llama-server from ghcr.io (which now requires auth).
  AUTH_FILE_SRC="/run/user/1000/containers/auth.json"
  AUTH_FILE_MOUNTS=()
  if [ -f "$AUTH_FILE_SRC" ]; then
    AUTH_FILE_MOUNTS=(-v "$AUTH_FILE_SRC:/run/host-ctrs-auth.json:ro")
    ok "Mounting host podman auth for ghcr.io pulls"
  else
    warn "No host auth.json at $AUTH_FILE_SRC — llama-server pull may fail"
  fi

  # Set up a socat-relayed docker socket for promtail. The host's
  # podman.sock is 0660 owned by ddv, but the in-container hydra user
  # (uid 1001) can't read it. socat creates a new socket with 0666
  # permission that anyone in the container can read.
  PROMTAIL_SOCK_DIR="/tmp/hydra-head-promtail-sock"
  mkdir -p "$PROMTAIL_SOCK_DIR"
  rm -f "$PROMTAIL_SOCK_DIR/docker.sock"
  nohup socat -t 300 "UNIX-LISTEN:$PROMTAIL_SOCK_DIR/docker.sock,reuseaddr,fork,unlink-early,mode=666" \
                  "UNIX-CONNECT:/run/user/1000/podman/podman.sock" &>/tmp/hydra-promtail-socat.log &
  SOCAT_PID=$!
  sleep 1
  if [ -S "$PROMTAIL_SOCK_DIR/docker.sock" ]; then
    ok "Promtail docker.sock proxy ready (socat pid=$SOCAT_PID)"
  else
    warn "Promtail docker.sock proxy failed to start; promtail will get EACCES"
  fi

  # Run container with auth token. Volume mounts:
  #   - host auth.json (ro):      ghcr.io creds for in-container pulls
  #   - host /proc /sys / (ro):   node_exporter reads host metrics
  #   - proxied podman socket:    promtail discovers all host containers
  #   - /mnt/containers (ro):     promtail reads each container's CRI log
  #   - promtail positions vol:   persistent cursor for log shipping
  podman run -d \
    --name hydra-head-rtx \
    --network host \
    --device nvidia.com/gpu=all \
    --health-cmd="curl -f http://localhost:9700/health || exit 1" \
    --health-interval=30s \
    --health-timeout=5s \
    --health-start-period=15s \
    --health-retries=3 \
    -e HYDRA_HEAD_AUTH_TOKEN="$AUTH_TOKEN" \
    -e REGISTRY_AUTH_FILE=/run/host-ctrs-auth.json \
    -v /mnt/SSD:/models:ro \
    -v /proc:/host/proc:ro \
    -v /sys:/host/sys:ro \
    -v /:/rootfs:ro \
    -v "$PROMTAIL_SOCK_DIR:/var/run/socks:rw" \
    -v /mnt/containers/:/mnt/containers/:ro \
    -v hydra-head-promtail-positions:/opt/hydra/promtail-positions:rw \
    "${AUTH_FILE_MOUNTS[@]}" \
    hydra-head:rtx

  ok "Started hydra-head-rtx container"

  # Wait for health
  sleep 3
  if curl -sf http://localhost:9700/health &>/dev/null; then
    ok "Hydra Head RTX is healthy"
  else
    warn "Hydra Head RTX not responding yet (may still be starting)"
  fi

  # Verify the 3 sidecar exporters are responding on their host ports.
  # Allow them a few seconds to spawn (they're started after the
  # OCI pull completes which can take ~30s for the llama image).
  for i in 1 2 3 4 5 6 7 8 9 10; do
    sleep 3
    if curl -sf http://localhost:9100/metrics >/dev/null 2>&1 \
       && curl -sf http://localhost:9835/metrics >/dev/null 2>&1 \
       && curl -sf http://localhost:9080/ready  >/dev/null 2>&1; then
      ok "Sidecars up: node_exporter :9100, nvidia_gpu_exporter :9835, promtail :9080"
      break
    fi
  done
}

deploy_p100() {
  step "Deploying to P100 (VM)"
  
  if ! ssh -o ConnectTimeout=5 -o BatchMode=yes hydra-p100 true 2>/dev/null; then
    die "Cannot reach hydra-p100 via SSH (check ~/.ssh/config)"
  fi
  
  # Create directories
  ssh hydra-p100 "sudo mkdir -p /opt/hydra/bin /opt/hydra/config /etc/hydra-head"
  
  # Copy binary
  rsync -avz bin/hydra-head hydra-p100:/opt/hydra/bin/hydra-head
  ok "Copied hydra-head binary"
  
  # Copy config files
  rsync -avz infra/hydra-head/config/global.yaml hydra-p100:/opt/hydra/config/global.yaml
  rsync -avz infra/hydra-head/config/node-p100.yaml hydra-p100:/opt/hydra/config/node-p100.yaml
  ok "Copied config files"
  
  # Create environment file with auth token
  ssh hydra-p100 "echo 'HYDRA_HEAD_AUTH_TOKEN=$AUTH_TOKEN' | sudo tee /etc/hydra-head/env > /dev/null"
  ssh hydra-p100 "sudo chmod 600 /etc/hydra-head/env"
  ok "Created auth token environment file"
  
  # Copy systemd service
  scp infra/hydra-head/hydra-head.service hydra-p100:/tmp/hydra-head.service
  ssh hydra-p100 "
    sudo cp /tmp/hydra-head.service /etc/systemd/system/hydra-head.service
    sudo systemctl daemon-reload
  "
  ok "Installed systemd service"
  
  # Stop existing service
  ssh hydra-p100 "sudo systemctl stop hydra-head 2>/dev/null || true"
  
  # Start service
  ssh hydra-p100 "sudo systemctl enable --now hydra-head"
  ok "Started hydra-head service"
  
  # Wait for health
  sleep 3
  if ssh hydra-p100 "curl -sf http://localhost:9700/health" &>/dev/null; then
    ok "Hydra Head P100 is healthy"
  else
    warn "Hydra Head P100 not responding yet (may still be starting)"
  fi
}

# ── Deploy ────────────────────────────────────────────────────────────────────
case "$TARGET" in
  rtx)
    deploy_rtx
    ;;
  p100)
    deploy_p100
    ;;
  all)
    deploy_rtx
    deploy_p100
    ;;
  *)
    die "Unknown target: $TARGET (expected: rtx, p100, all)"
    ;;
esac

step "Deployment complete"
echo -e "${GREEN}${BOLD}Hydra Head deployed successfully.${NC}"
echo ""
echo "  RTX API:  http://localhost:9700/status"
echo "  P100 API: http://192.168.122.21:9700/status"
echo ""
echo "  Auth token: $TOKEN_FILE"
echo "  Test: curl -H 'Authorization: Bearer \$(cat $TOKEN_FILE)' http://localhost:9700/status | jq"
