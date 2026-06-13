#!/usr/bin/env bash
# Start the Hydra application services.
# Safe to re-run — checks state before starting anything.
#
# Usage:
#   bash scripts/start-hydra.sh [--skip-p100]
#
# Requirements:
#   - podman + docker-compose for the core container
#   - Pre-built images: localhost/hydra-core:latest, localhost/hydra-head:rtx
#   - SSH access to hydra-p100 (192.168.122.21) via ~/.ssh/vm_agent_01

set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
SKIP_P100=false
for arg in "$@"; do [[ "$arg" == "--skip-p100" ]] && SKIP_P100=true; done

GREEN='\033[0;32m'; RED='\033[0;31m'; YELLOW='\033[1;33m'; BOLD='\033[1m'; NC='\033[0m'
ok()   { echo -e "  ${GREEN}✓${NC} $*"; }
warn() { echo -e "  ${YELLOW}⚠${NC}  $*"; }
fail() { echo -e "  ${RED}✗${NC} $*"; }
step() { echo -e "\n${BOLD}==> $*${NC}"; }
die()  { fail "$*"; exit 1; }

# ── 1. Prerequisites ──────────────────────────────────────────────────────────
step "Checking prerequisites"

command -v podman    &>/dev/null && ok "podman"    || die "podman not installed"
command -v curl      &>/dev/null && ok "curl"      || die "curl not installed"
command -v go        &>/dev/null && ok "go"        || warn "go not found (hydra-head build needs it)"

# ── 2. Hydra core stack ───────────────────────────────────────────────────────
step "Hydra Core (Store + Coordinator — single container)"

if curl -sf http://localhost:9000/health &>/dev/null; then
  ok "Already running"
else
  echo "  Starting via docker-compose..."
  cd "$REPO_ROOT/infra"
  podman compose -f docker-compose.hydra.yml up -d
  cd "$REPO_ROOT"
  ok "hydra-core started"
fi

# ── 3. Hydra Head RTX (container) ─────────────────────────────────────────────
step "Hydra Head RTX (:9700)"

if curl -sf http://localhost:9700/status &>/dev/null; then
  ok "Already running"
else
  echo "  Deploying via scripts/deploy-hydra-head.sh..."
  bash "$REPO_ROOT/scripts/deploy-hydra-head.sh" rtx
  ok "hydra-head-rtx started"
fi

# ── 4. Hydra Head P100 (VM systemd) ───────────────────────────────────────────
if [[ "$SKIP_P100" == false ]]; then
  step "Hydra Head P100 (:9700 on hydra-p100)"

  if curl -sf http://192.168.122.21:9700/status --connect-timeout 5 &>/dev/null; then
    ok "Already running"
  else
    if ! ssh -o ConnectTimeout=5 -o BatchMode=yes hydra-p100 true 2>/dev/null; then
      warn "Cannot reach hydra-p100 via SSH — skipping P100 (check ~/.ssh/config)"
      SKIP_P100=true
    else
      echo "  Deploying via scripts/deploy-hydra-head.sh..."
      bash "$REPO_ROOT/scripts/deploy-hydra-head.sh" p100
      ok "hydra-head-p100 started (model loading ~90s)"
    fi
  fi
fi

# ── 5. Health summary ─────────────────────────────────────────────────────────
step "Service health"

check_http() {
  local label="$1" url="$2" timeout="${3:-5}"
  printf "  %-35s" "$label"
  if curl -sf "$url" --connect-timeout "$timeout" &>/dev/null; then
    ok "ok"
  else
    warn "not responding"
  fi
}

check_http "Hydra.Core (coord)  :9000"    "http://localhost:9000/health"
check_http "Hydra.Head (RTX)    :9700"    "http://localhost:9700/status"
check_http "llama-server RTX   :8080"     "http://localhost:8080/health"

if [[ "$SKIP_P100" == false ]]; then
  check_http "Hydra.Head (P100)   :9700"   "http://192.168.122.21:9700/status"
  printf "  %-35s" "llama-server P100 :8086"
  if curl -sf http://192.168.122.21:8086/health --connect-timeout 8 &>/dev/null; then
    ok "ok"
  else
    warn "still loading (retry in ~60s)"
  fi
fi

echo ""
echo -e "${GREEN}${BOLD}Hydra services ready.${NC}"
echo "  API   : http://localhost:9000/health"
echo "  Test  : curl -X POST http://localhost:9000/v1/chat/completions \\"
echo "            -H 'Content-Type: application/json' \\"
echo "            -d '{\"model\":\"balanced\",\"messages\":[{\"role\":\"user\",\"content\":\"Hello\"}]}'"
