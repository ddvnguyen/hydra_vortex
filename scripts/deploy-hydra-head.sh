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

# ── Build ─────────────────────────────────────────────────────────────────────
step "Building hydra-head"

export PATH=$HOME/go-sdk/go/bin:$PATH
if ! command -v go &>/dev/null; then
  die "Go not found. Install with: mkdir -p ~/go-sdk && cd /tmp && wget https://go.dev/dl/go1.25.0.linux-amd64.tar.gz && tar -C ~/go-sdk -xzf go1.25.0.linux-amd64.tar.gz"
fi

go build -C "$REPO_ROOT/src/head" -o "$REPO_ROOT/bin/hydra-head" .
ok "Built bin/hydra-head"

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
  
  # Run container
  podman run -d \
    --name hydra-head-rtx \
    --network host \
    --device nvidia.com/gpu=all \
    -v /mnt/WorkDisk/Workplace/hydra_vortex/src/llama-cpp/build_sm120:/llama:ro \
    -v /mnt/SSD:/models:ro \
    hydra-head:rtx
  
  ok "Started hydra-head-rtx container"
  
  # Wait for health
  sleep 3
  if curl -sf http://localhost:9700/health &>/dev/null; then
    ok "Hydra Head RTX is healthy"
  else
    warn "Hydra Head RTX not responding yet (may still be starting)"
  fi
}

deploy_p100() {
  step "Deploying to P100 (VM)"
  
  if ! ssh -o ConnectTimeout=5 -o BatchMode=yes hydra-p100 true 2>/dev/null; then
    die "Cannot reach hydra-p100 via SSH (check ~/.ssh/config)"
  fi
  
  # Create directories
  ssh hydra-p100 "mkdir -p /opt/hydra/bin /opt/hydra/config"
  
  # Copy binary
  rsync -avz bin/hydra-head hydra-p100:/opt/hydra/bin/hydra-head
  ok "Copied hydra-head binary"
  
  # Copy config files
  rsync -avz infra/hydra-head/config/global.yaml hydra-p100:/opt/hydra/config/global.yaml
  rsync -avz infra/hydra-head/config/node-p100.yaml hydra-p100:/opt/hydra/config/node-p100.yaml
  ok "Copied config files"
  
  # Copy systemd service
  scp infra/hydra-head/hydra-head.service hydra-p100:/tmp/hydra-head.service
  ssh hydra-p100 "
    mkdir -p ~/.config/systemd/user
    cp /tmp/hydra-head.service ~/.config/systemd/user/hydra-head.service
    systemctl --user daemon-reload
  "
  ok "Installed systemd service"
  
  # Stop existing service
  ssh hydra-p100 "systemctl --user stop hydra-head 2>/dev/null || true"
  
  # Start service
  ssh hydra-p100 "systemctl --user enable --now hydra-head"
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
echo "  Test: curl http://localhost:9700/status | jq"
