#!/usr/bin/env bash
# One-time setup for the P100 VM: installs llama-server as a user systemd service
# and sets up promtail for log shipping.
#
# Run from the repo root: bash scripts/setup-p100.sh
#
# No sudo required on the VM — everything installs in user scope (~/.config/systemd/user/).
# After this runs, use: bash scripts/start-env.sh   (day-to-day startup)
set -euo pipefail

VM="hydra-p100"
REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"

echo "==> Checking SSH access to $VM"
ssh -o ConnectTimeout=10 -o BatchMode=yes "$VM" true || {
  echo "ERROR: Cannot SSH to $VM. Check ~/.ssh/config has a 'hydra-p100' entry."
  exit 1
}

echo "==> Creating required directories on VM"
ssh "$VM" "
  mkdir -p \$HOME/.config/systemd/user
  mkdir -p /mnt/kv_slots 2>/dev/null || true
"

echo "==> Deploying P100 llama-server binary"
rsync -az --checksum --progress \
  "$REPO_ROOT/src/llama-cpp/build_sm60/bin/llama-server" \
  "$VM:/opt/software/llama-cpp-hydra-sm60/hydra-sm60/bin/llama-server"

echo "==> Installing llama-p100 user systemd service"
scp "$REPO_ROOT/infra/systemd/llama-p100-user.service" "$VM:/tmp/llama-p100.service"
ssh "$VM" "
  cp /tmp/llama-p100.service \$HOME/.config/systemd/user/llama-p100.service
  systemctl --user daemon-reload
  systemctl --user enable llama-p100
  echo 'Service enabled (not started yet)'
"

echo "==> Installing promtail for log shipping"
ssh "$VM" "
  set -euo pipefail
  if command -v promtail &>/dev/null; then
    echo 'promtail already installed:' \$(promtail --version 2>&1 | head -1)
  else
    curl -sLO https://github.com/grafana/loki/releases/download/v3.4.3/promtail-linux-amd64.zip
    unzip -o promtail-linux-amd64.zip && chmod +x promtail-linux-amd64
    sudo mv promtail-linux-amd64 /usr/local/bin/promtail
    rm -f promtail-linux-amd64.zip
    echo 'promtail installed'
  fi
"

echo "==> Deploying promtail config and service"
scp "$REPO_ROOT/infra/promtail/promtail-p100.yml" "$VM:/tmp/"
scp "$REPO_ROOT/infra/systemd/promtail-p100.service" "$VM:/tmp/"
ssh "$VM" "
  sudo cp /tmp/promtail-p100.yml /etc/promtail/config.yml
  sudo cp /tmp/promtail-p100.service /etc/systemd/system/
  sudo systemctl daemon-reload && sudo systemctl enable --now promtail-p100
"

echo ""
echo "==> Setup complete. To start llama-server P100:"
echo "    bash scripts/start-env.sh"
echo ""
echo "    Or manually:"
echo "    ssh $VM 'systemctl --user start llama-p100'"
echo "    curl http://192.168.122.21:8086/health   # ready after ~90s model load"
