#!/usr/bin/env bash
# Deploy the observability / infra stack (node-exporter, nvidia-exporter,
# loki, promtail, prometheus, grafana, pgadmin).
#
# Usage:
#   bash scripts/deploy-infra.sh

set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
QUADLET_DIR="${XDG_CONFIG_HOME:-$HOME/.config}/containers/systemd"

GREEN='\033[0;32m'; RED='\033[0;31m'; YELLOW='\033[1;33m'; BOLD='\033[1m'; NC='\033[0m'
ok()   { echo -e "  ${GREEN}✓${NC} $*"; }
step() { echo -e "\n${BOLD}==> $*${NC}"; }

SERVICES="infra-node-exporter infra-nvidia-exporter infra-loki infra-promtail infra-prometheus infra-grafana infra-pgadmin"

step "Installing Quadlet files"
mkdir -p "$QUADLET_DIR"
cp "$REPO_ROOT/infra/quadlets"/infra-*.container "$QUADLET_DIR"
cp "$REPO_ROOT/infra/quadlets"/infra-host.pod "$QUADLET_DIR"
cp "$REPO_ROOT/infra/quadlets"/*.volume "$QUADLET_DIR"
cp "$REPO_ROOT/infra/quadlets"/hydra-coordinator.env "$QUADLET_DIR" 2>/dev/null || true

step "Deploying infra services"
systemctl --user daemon-reload
systemctl --user stop infra-host-pod.service 2>/dev/null || true
sleep 1
systemctl --user start infra-node-exporter.service
systemctl --user start infra-nvidia-exporter.service
systemctl --user start infra-loki.service
systemctl --user start infra-promtail.service
systemctl --user start infra-prometheus.service
systemctl --user start infra-grafana.service
systemctl --user start infra-pgadmin.service

step "Verifying infra services"
sleep 10
for url in "http://localhost:9091" "http://localhost:3000" "http://localhost:3100" "http://localhost:8888"; do
  if curl -sf "$url" --connect-timeout 5 &>/dev/null; then
    ok "$url"
  else
    echo "  ${YELLOW}⚠${NC} $url not responding"
  fi
done

echo -e "\n${GREEN}${BOLD}Infra stack deployed.${NC}"
