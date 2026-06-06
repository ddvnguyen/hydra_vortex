#!/usr/bin/env bash
# Start the Hydra observability / infra stack via Quadlet systemd services.
# Safe to re-run — checks state before starting anything.
#
# Usage:
#   bash scripts/start-infra.sh
#
# Requirements:
#   - podman (Quadlet reads from ~/.config/containers/systemd/)
#   - Quadlet files installed (see scripts/install-quadlets.sh)

set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"

GREEN='\033[0;32m'; RED='\033[0;31m'; YELLOW='\033[1;33m'; BOLD='\033[1m'; NC='\033[0m'
ok()   { echo -e "  ${GREEN}✓${NC} $*"; }
warn() { echo -e "  ${YELLOW}⚠${NC}  $*"; }
fail() { echo -e "  ${RED}✗${NC} $*"; }
step() { echo -e "\n${BOLD}==> $*${NC}"; }
die()  { fail "$*"; exit 1; }

# ── 1. Prerequisites ──────────────────────────────────────────────────────────
step "Checking prerequisites"
command -v systemctl &>/dev/null && ok "systemctl" || die "systemctl not found"
command -v curl      &>/dev/null && ok "curl"      || die "curl not installed"

# Ensure Quadlet files are installed
QUADLET_DIR="${XDG_CONFIG_HOME:-$HOME/.config}/containers/systemd"
if ! ls "$QUADLET_DIR"/infra-*.container &>/dev/null 2>&1; then
  warn "infra Quadlet files not found in $QUADLET_DIR — installing..."
  mkdir -p "$QUADLET_DIR"
  cp "$REPO_ROOT/infra/quadlets"/infra-*.container "$QUADLET_DIR"
  cp "$REPO_ROOT/infra/quadlets"/infra-host.pod "$QUADLET_DIR"
  cp "$REPO_ROOT/infra/quadlets"/*.volume "$QUADLET_DIR"
  cp "$REPO_ROOT/infra/quadlets"/hydra-coordinator.env "$QUADLET_DIR" 2>/dev/null || true
  systemctl --user daemon-reload
fi

# ── 2. Infra / observability stack ───────────────────────────────────────────
step "Infra stack (Loki + Promtail + Prometheus + Grafana)"

SERVICES="infra-node-exporter infra-nvidia-exporter infra-loki infra-promtail infra-prometheus infra-grafana infra-pgadmin"
ALL_ACTIVE=true
for s in $SERVICES; do
  if ! systemctl --user is-active --quiet "$s.service" 2>/dev/null; then
    ALL_ACTIVE=false
    break
  fi
done

if $ALL_ACTIVE; then
  ok "Infra stack already running"
else
  echo "  Starting services..."
  systemctl --user daemon-reload 2>/dev/null || true
  for s in $SERVICES; do
    systemctl --user start "$s.service" 2>/dev/null && ok "$s" || warn "$s failed to start"
  done
fi

# ── 3. Health summary ────────────────────────────────────────────────────────
step "Infra service health"

check_http() {
  local label="$1" url="$2" timeout="${3:-5}"
  printf "  %-35s" "$label"
  if curl -sf "$url" --connect-timeout "$timeout" &>/dev/null; then
    ok "ok"
  else
    warn "not responding"
  fi
}

check_http "Grafana          :3000"    "http://localhost:3000"
check_http "Prometheus       :9091"    "http://localhost:9091"
check_http "Loki             :3100"    "http://localhost:3100/ready"
check_http "pgAdmin          :8888"    "http://localhost:8888/misc/ping"

systemctl --user is-active --quiet infra-promtail.service && ok "promtail active" || warn "promtail inactive"

echo ""
echo -e "${GREEN}${BOLD}Infra stack ready.${NC}"
echo "  Grafana     : http://localhost:3000"
echo "  Prometheus  : http://localhost:9091"
echo "  Loki        : http://localhost:3100"
echo "  pgAdmin     : http://localhost:8888"
