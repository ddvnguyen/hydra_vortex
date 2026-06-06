#!/usr/bin/env bash
# Build and deploy the Hydra core stack (postgres, store, agents, coordinator).
#
# Usage:
#   bash scripts/deploy-hydra.sh

set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
QUADLET_DIR="${XDG_CONFIG_HOME:-$HOME/.config}/containers/systemd"

GREEN='\033[0;32m'; RED='\033[0;31m'; YELLOW='\033[1;33m'; BOLD='\033[1m'; NC='\033[0m'
ok()   { echo -e "  ${GREEN}✓${NC} $*"; }
step() { echo -e "\n${BOLD}==> $*${NC}"; }
die()  { echo -e "  ${RED}✗${NC} $*"; exit 1; }

step "Building images"
echo "  store..."
podman build --target store -f "$REPO_ROOT/infra/Dockerfile" -t localhost/hydra-store:latest "$REPO_ROOT" 2>&1 | tail -1
echo "  agent..."
podman build --target agent -f "$REPO_ROOT/infra/Dockerfile" -t localhost/hydra-agent:latest "$REPO_ROOT" 2>&1 | tail -1
echo "  coordinator..."
podman build --target coordinator -f "$REPO_ROOT/infra/Dockerfile" -t localhost/hydra-coordinator:latest "$REPO_ROOT" 2>&1 | tail -1
ok "Images built"

step "Installing Quadlet files"
mkdir -p "$QUADLET_DIR"
cp "$REPO_ROOT/infra/quadlets"/hydra-*.container "$QUADLET_DIR"
cp "$REPO_ROOT/infra/quadlets"/hydra-coordinator.env "$QUADLET_DIR"
cp "$REPO_ROOT/infra/quadlets"/pg-data.volume "$QUADLET_DIR"

step "Deploying hydra core"
systemctl --user daemon-reload
systemctl --user stop hydra-postgres hydra-store hydra-agent-rtx hydra-agent-p100 hydra-coordinator 2>/dev/null || true
systemctl --user start hydra-postgres.service
echo "  Waiting for postgres to be healthy..."
systemctl --user start hydra-store.service
systemctl --user start hydra-agent-rtx.service hydra-agent-p100.service
systemctl --user start hydra-coordinator.service
ok "Hydra core deployed"

step "Verifying services"
sleep 15
printf "  %-35s" "Store :9501"
if curl -sf http://localhost:9501/debug --connect-timeout 5 &>/dev/null; then ok "ok"; else echo "  ${YELLOW}⚠${NC} not responding"; fi
printf "  %-35s" "Coordinator :9000"
COORD=$(curl -sf http://localhost:9000/health --connect-timeout 5 \
  | python3 -c "import sys,json; d=json.load(sys.stdin); print(d['status'])" 2>/dev/null || echo "unreachable")
if [ "$COORD" = "healthy" ] || [ "$COORD" = "degraded" ]; then ok "$COORD"; else echo "  ${YELLOW}⚠${NC} $COORD"; fi

echo -e "\n${GREEN}${BOLD}Hydra core deployed.${NC}"
