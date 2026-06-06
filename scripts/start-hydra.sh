#!/usr/bin/env bash
# Start the Hydra application services via Quadlet systemd services.
# Safe to re-run — checks state before starting anything.
#
# Usage:
#   bash scripts/start-hydra.sh [--skip-p100]
#
# Requirements:
#   - podman (Quadlet reads from ~/.config/containers/systemd/)
#   - Pre-built images: hydra-store, hydra-agent, hydra-coordinator, llama-rtx
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

command -v systemctl &>/dev/null && ok "systemctl" || die "systemctl not found"
command -v podman    &>/dev/null && ok "podman"    || die "podman not installed"
command -v python3   &>/dev/null && ok "python3"   || die "python3 not installed"
command -v curl      &>/dev/null && ok "curl"      || die "curl not installed"

QUADLET_DIR="${XDG_CONFIG_HOME:-$HOME/.config}/containers/systemd"

# Ensure Quadlet files are installed
step "Installing Quadlet files"
mkdir -p "$QUADLET_DIR"
cp "$REPO_ROOT/infra/quadlets"/hydra-*.container "$QUADLET_DIR" 2>/dev/null || true
cp "$REPO_ROOT/infra/quadlets"/llama-rtx.container "$QUADLET_DIR" 2>/dev/null || true
cp "$REPO_ROOT/infra/quadlets"/pg-data.volume "$QUADLET_DIR" 2>/dev/null || true
systemctl --user daemon-reload

# ── 2. Hydra core stack ───────────────────────────────────────────────────────
step "Hydra core (Store + Agents + Coordinator)"

HYDRA_SERVICES="hydra-postgres hydra-store hydra-agent-rtx hydra-agent-p100 hydra-coordinator"

# Start in dependency order
echo "  Starting hydra-postgres..."
systemctl --user start hydra-postgres.service 2>/dev/null && ok "hydra-postgres" || warn "hydra-postgres"

echo "  Starting hydra-store..."
systemctl --user start hydra-store.service 2>/dev/null && ok "hydra-store" || warn "hydra-store"

echo "  Starting agents..."
systemctl --user start hydra-agent-rtx.service 2>/dev/null && ok "hydra-agent-rtx" || warn "hydra-agent-rtx"
systemctl --user start hydra-agent-p100.service 2>/dev/null && ok "hydra-agent-p100" || warn "hydra-agent-p100"

echo "  Starting hydra-coordinator..."
systemctl --user start hydra-coordinator.service 2>/dev/null && ok "hydra-coordinator" || warn "hydra-coordinator"

# ── 3. llama-server RTX ───────────────────────────────────────────────────────
step "llama-server RTX (:8080)"

if curl -sf http://localhost:8080/health &>/dev/null; then
  ok "Already running"
else
  echo "  Starting llama-rtx..."
  systemctl --user start llama-rtx.service 2>/dev/null && ok "llama-rtx" || warn "llama-rtx start issued"
fi

# ── 4. llama-server P100 ──────────────────────────────────────────────────────
if [[ "$SKIP_P100" == false ]]; then
  step "llama-server P100 (:8086 on hydra-p100)"

  if curl -sf http://192.168.122.21:8086/health --connect-timeout 5 &>/dev/null; then
    ok "Already running"
  else
    if ! ssh -o ConnectTimeout=5 -o BatchMode=yes hydra-p100 true 2>/dev/null; then
      warn "Cannot reach hydra-p100 via SSH — skipping P100 (check ~/.ssh/config)"
      SKIP_P100=true
    else
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
        systemctl --user enable --now llama-p100
      "
      ok "Started (model loading ~90s, check with: curl http://192.168.122.21:8086/health)"
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

check_http "Store debug      :9501"    "http://localhost:9501/debug"
check_http "Agent RTX debug  :9611"    "http://localhost:9611/debug"
check_http "Agent P100 debug :9622"    "http://localhost:9622/debug"
check_http "llama-server RTX :8080"    "http://localhost:8080/health"

if [[ "$SKIP_P100" == false ]]; then
  printf "  %-35s" "llama-server P100 :8086"
  if curl -sf http://192.168.122.21:8086/health --connect-timeout 8 &>/dev/null; then
    ok "ok"
  else
    warn "still loading (retry in ~60s)"
  fi
fi

printf "  %-35s" "Coordinator :9000"
COORD=$(curl -sf http://localhost:9000/health --connect-timeout 5 \
  | python3 -c "import sys,json; d=json.load(sys.stdin); print(d['status'])" 2>/dev/null || echo "unreachable")
case "$COORD" in
  healthy)  ok "healthy" ;;
  degraded) warn "degraded — one or more nodes unhealthy (P100 may still be loading)" ;;
  *)        fail "$COORD" ;;
esac

echo ""
echo -e "${GREEN}${BOLD}Hydra services ready.${NC}"
echo "  Coordinator : http://localhost:9000/health"
echo ""
echo "  Run tests   : pytest tests/system/test_m1_system.py tests/system/test_m2_system.py -v"
echo "  Full tests  : pytest tests/system/ -m system -v --timeout=300 \\"
echo "                  --ignore=tests/system/test_large_prompt_system.py \\"
echo "                  --ignore=tests/system/test_stress_system.py"
