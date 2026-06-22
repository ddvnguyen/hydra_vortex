#!/usr/bin/env bash
# Deploy Hydra Head to GPU nodes
# Usage: bash scripts/deploy-hydra-head.sh [rtx|p100|all]
#
# RTX path (since #322 / PR #328):
#   - Build Go binary + container image
#   - Deploy via `podman compose -f infra/docker-compose.hydra.yml up -d`
#     which brings up `core` + `head-rtx` as a single pod with
#     userns=host (so the in-container promtail can read /mnt/containers/
#     ctr.log directly, no socat proxy needed).
#   - The compose file is the source of truth for mount paths,
#     env vars, health checks, and resource limits.
#
# P100 path (still uses systemd, not in compose):
#   - rsync binary + configs to hydra-p100
#   - install / enable systemd service
#   - hydra-head is rebuilt with the configurable health-check
#     values (PR #328) so the slow-VM-disk model load (3-5 min)
#     doesn't trigger the kill loop any more.

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
    die "Auth token not found at $TOKEN_FILE. Run: openssl rand -hex 32 > $TOKEN_FILE"
  fi
  cat "$TOKEN_FILE"
}

# ── Go Build ─────────────────────────────────────────────────────────────────
build_go() {
  step "Building hydra-head (Go)"

  export PATH=$HOME/go-sdk/go/bin:$PATH
  if ! command -v go &>/dev/null; then
    die "Go not found. Install with: mkdir -p ~/go-sdk && cd /tmp && wget https://go.dev/dl/go1.25.0.linux-amd64.tar.gz && tar -C ~/go-sdk -xzf go1.25.0.linux-amd64.tar.gz — see docs/build-environment.md"
  fi

  go build -C "$REPO_ROOT/src/head" -o "$REPO_ROOT/bin/hydra-head" .
  ok "Built bin/hydra-head ($(stat -c '%s' bin/hydra-head) bytes)"
}

# ── Container Image Build ────────────────────────────────────────────────────
build_rtx_image() {
  step "Building hydra-head:rtx image"

  if ! command -v podman &>/dev/null; then
    die "podman not found"
  fi

  podman build -f infra/hydra-head/Dockerfile.rtx -t hydra-head:rtx .
  ok "Built container image hydra-head:rtx"
}

# ── Pre-deploy Cleanup ───────────────────────────────────────────────────────
# Stop the 3 host sidecar Quadlets (node-exporter, nvidia-exporter,
# promtail) — the in-container hydra-head now manages them as children,
# so the host ports (9100, 9835, 9080) must be free for it to bind.
stop_host_sidecars() {
  if ! command -v systemctl &>/dev/null; then return; fi
  export DBUS_SESSION_BUS_ADDRESS="unix:path=/run/user/$(id -u)/bus"
  export XDG_RUNTIME_DIR="/run/user/$(id -u)"
  for svc in infra-node-exporter infra-nvidia-exporter infra-promtail; do
    if systemctl --user is-active --quiet "$svc.service" 2>/dev/null; then
      systemctl --user stop "$svc.service" 2>/dev/null || true
      ok "Stopped host $svc (replaced by hydra-head child)"
    fi
  done
  unset DBUS_SESSION_BUS_ADDRESS XDG_RUNTIME_DIR
}

# ── Auth File Sanity Check ──────────────────────────────────────────────────
# The in-container hydra-head needs to read the host's podman auth.json
# to pull llama-server from ghcr.io. With userns=host the container
# user (uid 1000) IS host user (uid 1000), so the file just needs to
# be at the standard path and be readable. If it's 600, the chmod below
# is a no-op; if it's 644, no change. We do this defensively because
# the persistent copy at ~/.config/containers/auth.json is what the
# user actually maintains; the /run/user/1000/... copy is a tmpfs
# shadow of it.
check_auth_file() {
  local auth_file="$HOME/.config/containers/auth.json"
  local xdg_auth="/run/user/$(id -u)/containers/auth.json"
  for f in "$auth_file" "$xdg_auth"; do
    if [ -f "$f" ]; then
      local mode
      mode=$(stat -c '%a' "$f")
      if [ "$mode" = "600" ]; then
        chmod 644 "$f" && ok "chmod 644 $f (was 600; in-container uid 1000 needs to read it)"
      fi
    fi
  done
}

# ── Deploy: RTX via compose ──────────────────────────────────────────────────
deploy_rtx() {
  step "Deploying to RTX (compose)"

  # Build prerequisites
  build_go
  generate_token
  AUTH_TOKEN=$(get_token)
  build_rtx_image
  stop_host_sidecars
  check_auth_file

  # Ensure the promtail positions volume exists (compose declares it but
  # `podman compose up` won't create volumes in --build=skip mode).
  if ! podman volume exists hydra-head-promtail-positions 2>/dev/null; then
    podman volume create hydra-head-promtail-positions
  fi

  # Drop any pre-compose standalone container (we used to run a single
  # hydra-head-rtx container via `podman run`; the compose brings it
  # up under the pod_hydra-system name).
  if podman container exists hydra-head-rtx 2>/dev/null; then
    podman stop hydra-head-rtx 2>/dev/null || true
    podman rm hydra-head-rtx 2>/dev/null || true
  fi
  # Also drop the old manually-created 'hydra-system' pod (from before
  # the compose existed). It's safe to remove — compose will recreate.
  for old_pod in hydra-system pod_hydra-system; do
    if podman pod exists "$old_pod" 2>/dev/null; then
      timeout 10 podman pod rm -f "$old_pod" 2>/dev/null || true
    fi
  done

  # Export the token so podman-compose picks it up via ${HYDRA_HEAD_AUTH_TOKEN:?}
  export HYDRA_HEAD_AUTH_TOKEN
  export HYDRA_HEAD_AUTH_TOKEN

  # Deploy. Use `up` (not `up -d`) so we see errors; it returns
  # immediately when containers are detached.
  if ! podman compose -f infra/docker-compose.hydra.yml up -d 2>&1 | tail -10; then
    die "podman compose up failed — check the output above. Common causes: HYDRA_HEAD_AUTH_TOKEN not exported, image not built, or userns conflict."
  fi
  ok "Compose up: core + head-rtx in pod hydra-system"

  # Wait for both healthchecks to pass
  step "Waiting for health"
  for i in 1 2 3 4 5 6 7 8 9 10 11 12 13 14 15; do
    sleep 3
    if curl -sf http://localhost:9000/health >/dev/null 2>&1 \
       && curl -sf http://localhost:9700/health >/dev/null 2>&1; then
      ok "Both core (:9000) and head-rtx (:9700) are healthy"
      break
    fi
    if [ "$i" = "15" ]; then
      warn "Health not fully green after 45s — check `podman ps` and `podman logs <ctr>`"
    fi
  done

  # Verify the 3 in-container sidecar exporters are responding
  for i in 1 2 3 4 5; do
    sleep 3
    if curl -sf http://localhost:9100/metrics >/dev/null 2>&1 \
       && curl -sf http://localhost:9835/metrics >/dev/null 2>&1 \
       && curl -sf http://localhost:9080/ready  >/dev/null 2>&1; then
      ok "Sidecars up: node_exporter :9100, nvidia_gpu_exporter :9835, promtail :9080"
      break
    fi
  done

  # Verify promtail is actually shipping (not just running but stuck)
  sleep 5
  local sent_bytes
  sent_bytes=$(curl -sf http://localhost:9080/metrics 2>/dev/null | awk '/^promtail_sent_bytes_total/ {print $2}')
  if [ -n "$sent_bytes" ] && [ "$sent_bytes" != "0" ]; then
    ok "promtail shipping: $sent_bytes bytes sent to Loki"
  else
    warn "promtail_sent_bytes_total = ${sent_bytes:-N/A} — promtail may not be shipping yet, check /var/log/hydra/promtail.log"
  fi
}

# ── Deploy: P100 via systemd (not in compose) ────────────────────────────────
deploy_p100() {
  step "Deploying to P100 (VM, systemd)"

  if ! ssh -o ConnectTimeout=5 -o BatchMode=yes hydra-p100 true 2>/dev/null; then
    die "Cannot reach hydra-p100 via SSH (check ~/.ssh/config)"
  fi

  # Create directories
  ssh hydra-p100 "sudo mkdir -p /opt/hydra/bin /opt/hydra/config /etc/hydra-head"

  # Copy binary
  rsync -avz bin/hydra-head hydra-p100:/opt/hydra/bin/hydra-head
  ok "Copied hydra-head binary"

  # Copy config files (incl. the new health: section from PR #328 —
  # node-p100.yaml overrides max_fails: 30 so the slow-VM-disk
  # model load doesn't get killed).
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
    warn "Hydra Head P100 not responding yet (may still be starting; model load = 3-5 min on P100 VM disk)"
  fi
}

# ── Main ──────────────────────────────────────────────────────────────────────
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
echo "  RTX Core API:  http://localhost:9000/health"
echo "  RTX Head API:  http://localhost:9700/status"
echo "  P100 Head API: http://192.168.122.21:9700/status"
echo ""
echo "  Auth token: $TOKEN_FILE"
echo "  Test: curl -H 'Authorization: Bearer \$(cat $TOKEN_FILE)' http://localhost:9700/status | jq"
