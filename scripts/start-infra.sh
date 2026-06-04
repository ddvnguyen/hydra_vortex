#!/usr/bin/env bash
# Start the Hydra observability / infra stack.
# Safe to re-run — checks state before starting anything.
#
# Usage:
#   bash scripts/start-infra.sh
#
# Requirements:
#   - podman + podman-compose

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

command -v podman         &>/dev/null && ok "podman"         || die "podman not installed"
command -v podman-compose &>/dev/null && ok "podman-compose" || die "podman-compose not installed"
command -v curl           &>/dev/null && ok "curl"           || die "curl not installed"

# ── 2. Infra / observability stack ───────────────────────────────────────────
step "Infra stack (Loki + Promtail + Prometheus + Grafana)"

cd "$REPO_ROOT/infra"
INFRA_UP=$(podman-compose -f docker-compose.infra.yml ps 2>/dev/null | grep -c " Up " || true)
if [[ "$INFRA_UP" -ge 4 ]]; then
  ok "Infra stack already running ($INFRA_UP containers up)"
else
  echo "  Starting containers..."
  podman-compose -f docker-compose.infra.yml up -d 2>&1 | tail -3
  ok "Infra stack started"
fi
cd "$REPO_ROOT"

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

printf "  %-35s" "promtail container"
podman ps --filter name=promtail --format '{{.Status}}' 2>/dev/null | grep -q Up && ok "active" || warn "inactive"

echo ""
echo -e "${GREEN}${BOLD}Infra stack ready.${NC}"
echo "  Grafana     : http://localhost:3000"
echo "  Prometheus  : http://localhost:9091"
