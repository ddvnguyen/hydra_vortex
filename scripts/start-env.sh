#!/usr/bin/env bash
# Start the full Hydra environment on the self-hosted development machine.
# Safe to re-run — checks state before starting anything.
#
# Usage:
#   bash scripts/start-env.sh [--skip-p100]
#
# Requirements:
#   - podman + podman-compose
#   - SSH access to hydra-p100 (192.168.122.21) via ~/.ssh/vm_agent_01
#   - Pre-built llama binaries:
#       RTX : src/llama-cpp/build_sm120/bin/llama-server
#       P100: src/llama-cpp/build_sm60/bin/llama-server  (skipped if missing)
#
# First-time P100 setup is handled automatically (user systemd, no sudo needed).

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

command -v podman        &>/dev/null && ok "podman"        || die "podman not installed"
command -v podman-compose &>/dev/null && ok "podman-compose" || die "podman-compose not installed"
command -v python3       &>/dev/null && ok "python3"       || die "python3 not installed"
command -v curl          &>/dev/null && ok "curl"          || die "curl not installed"

if [[ -x "$REPO_ROOT/src/llama-cpp/build_sm120/bin/llama-server" ]]; then
  ok "llama-server RTX binary (build_sm120)"
else
  die "RTX binary missing: src/llama-cpp/build_sm120/bin/llama-server
     Build it:
       cd src/llama-cpp
       cmake -B build_sm120 -G Ninja \\
         -DCMAKE_CUDA_ARCHITECTURES=120 \\
         -DGGML_CUDA=ON -DGGML_CUDA_FORCE_CUBLAS=ON -DGGML_NATIVE=ON
       cmake --build build_sm120 --target llama-server -j4"
fi

if [[ "$SKIP_P100" == false ]]; then
  if [[ -x "$REPO_ROOT/src/llama-cpp/build_sm60/bin/llama-server" ]]; then
    ok "llama-server P100 binary (build_sm60)"
  else
    warn "P100 binary missing: src/llama-cpp/build_sm60/bin/llama-server — skipping P100"
    SKIP_P100=true
  fi

  if [[ "$SKIP_P100" == false ]]; then
    if ssh -o ConnectTimeout=5 -o BatchMode=yes hydra-p100 true 2>/dev/null; then
      ok "SSH to hydra-p100"
    else
      warn "Cannot reach hydra-p100 via SSH — skipping P100 (check ~/.ssh/config)"
      SKIP_P100=true
    fi
  fi
fi

# ── 2. Hydra infra stack ──────────────────────────────────────────────────────
step "Hydra infra stack (Store + Agents + Coordinator + Observability)"

cd "$REPO_ROOT/infra"
RUNNING=$(podman-compose ps 2>/dev/null | grep -c " Up " || true)
if [[ "$RUNNING" -ge 8 ]]; then
  ok "Infra stack already running ($RUNNING containers up)"
else
  echo "  Starting containers..."
  podman-compose up -d 2>&1 | tail -3
  ok "Infra stack started"
fi
cd "$REPO_ROOT"

# ── 2b. Host log shipper + promtail ──────────────────────────────────────────
step "Host log shipping (container-log-shipper + promtail)"

export XDG_RUNTIME_DIR="/run/user/$(id -u)"
export DBUS_SESSION_BUS_ADDRESS="unix:path=$XDG_RUNTIME_DIR/bus"
if systemctl --user is-active container-log-shipper &>/dev/null && \
   systemctl --user is-active promtail &>/dev/null; then
  ok "Both services already running"
else
  systemctl --user restart container-log-shipper promtail 2>/dev/null && \
    ok "Started" || warn "Could not start (services may not be installed)"
fi

# ── 3. llama-server RTX ──────────────────────────────────────────────────────
step "llama-server RTX (:8080)"

if curl -sf http://localhost:8080/health &>/dev/null; then
  ok "Already running"
else
  echo "  Starting llama-cpp container..."
  cd "$REPO_ROOT/infra/llama-rtx-node"
  podman-compose up -d 2>&1 | tail -3
  cd "$REPO_ROOT"
  ok "Started"
fi

# Always ensure llama-cpp is on hydra_default (container recreation drops this)
HYDRA_NET="hydra_default"
if podman network exists "$HYDRA_NET" 2>/dev/null; then
  podman network connect "$HYDRA_NET" llama-cpp 2>/dev/null \
    && ok "llama-cpp joined $HYDRA_NET" \
    || ok "llama-cpp already on $HYDRA_NET"
fi

# ── 4. llama-server P100 ─────────────────────────────────────────────────────
if [[ "$SKIP_P100" == false ]]; then
  step "llama-server P100 (:8086 on hydra-p100)"

  if curl -sf http://192.168.122.21:8086/health --connect-timeout 5 &>/dev/null; then
    ok "Already running"
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

check_http "Grafana          :3000"    "http://localhost:3000"
check_http "Prometheus       :9091"    "http://localhost:9091"

# Check host log services
printf "  %-35s" "log-shipper systemd"
systemctl --user is-active container-log-shipper &>/dev/null && ok "active" || warn "inactive"
printf "  %-35s" "promtail systemd"
systemctl --user is-active promtail &>/dev/null && ok "active" || warn "inactive"

echo ""
echo -e "${GREEN}${BOLD}Done.${NC}"
echo "  Coordinator : http://localhost:9000/health"
echo "  Grafana     : http://localhost:3000"
echo "  Prometheus  : http://localhost:9091"
echo ""
echo "  Run tests   : pytest tests/system/test_m1_system.py tests/system/test_m2_system.py -v"
echo "  Full tests  : pytest tests/system/ -m system -v --timeout=300 \\"
echo "                  --ignore=tests/system/test_large_prompt_system.py \\"
echo "                  --ignore=tests/system/test_stress_system.py"
