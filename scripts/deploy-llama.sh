#!/usr/bin/env bash
# Build and deploy llama-server nodes (RTX + P100).
#
# Usage:
#   bash scripts/deploy-llama.sh [--skip-p100]

set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
SKIP_P100=false
for arg in "$@"; do [[ "$arg" == "--skip-p100" ]] && SKIP_P100=true; done

GREEN='\033[0;32m'; RED='\033[0;31m'; YELLOW='\033[1;33m'; BOLD='\033[1m'; NC='\033[0m'
ok()   { echo -e "  ${GREEN}✓${NC} $*"; }
warn() { echo -e "  ${YELLOW}⚠${NC}  $*"; }
step() { echo -e "\n${BOLD}==> $*${NC}"; }
die()  { echo -e "  ${RED}✗${NC} $*"; exit 1; }

# ── RTX ──────────────────────────────────────────────────────────────────────
step "llama-server RTX (:8080)"
if curl -sf http://localhost:8080/health --connect-timeout 5 &>/dev/null; then
  ok "Already running — building and restarting anyway"
fi

echo "  Building image..."
podman build -f "$REPO_ROOT/infra/llama-rtx-node/Dockerfile" \
  -t localhost/llama-rtx:latest \
  "$REPO_ROOT/infra/llama-rtx-node" 2>&1 | tail -1
ok "Image built"

QUADLET_DIR="${XDG_CONFIG_HOME:-$HOME/.config}/containers/systemd"
mkdir -p "$QUADLET_DIR"
cp "$REPO_ROOT/infra/quadlets"/llama-rtx.container "$QUADLET_DIR"
systemctl --user daemon-reload
systemctl --user restart llama-rtx.service 2>/dev/null || systemctl --user start llama-rtx.service
ok "llama-rtx deployed"

# ── P100 ──────────────────────────────────────────────────────────────────────
if [[ "$SKIP_P100" == false ]]; then
  step "llama-server P100 (:8086 on hydra-p100)"

  if curl -sf http://192.168.122.21:8086/health --connect-timeout 5 &>/dev/null; then
    ok "Already running"
  fi

  if ! ssh -o ConnectTimeout=5 -o BatchMode=yes hydra-p100 true 2>/dev/null; then
    die "Cannot reach hydra-p100 via SSH"
  fi

  echo "  Deploying binary..."
  rsync -az --checksum \
    "$REPO_ROOT/src/llama-cpp/build_sm60/bin/llama-server" \
    hydra-p100:/opt/software/llama-cpp-hydra-sm60/hydra-sm60/bin/llama-server

  echo "  Installing user systemd service..."
  scp "$REPO_ROOT/infra/systemd/llama-p100-user.service" \
      hydra-p100:/tmp/llama-p100.service
  ssh hydra-p100 "
    mkdir -p ~/.config/systemd/user
    cp /tmp/llama-p100.service ~/.config/systemd/user/llama-p100.service
    systemctl --user daemon-reload
    systemctl --user restart llama-p100
  "
  ok "llama-p100 deployed (model loading ~90s)"
fi

echo -e "\n${GREEN}${BOLD}llama nodes deployed.${NC}"
echo "  RTX: http://localhost:8080/health"
echo "  P100: http://192.168.122.21:8086/health"
