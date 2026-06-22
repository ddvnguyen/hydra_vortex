#!/usr/bin/env bash
# One-time setup for the P100 VM: installs hydra-head which manages all 4 services
# (llama-server, node-exporter, nvidia-gpu-exporter, promtail) as a user systemd service.
#
# Run from the repo root: bash scripts/setup-p100.sh
#
# No sudo required on the VM — everything installs in user scope (~/.config/systemd/user/).
# After this runs, use: bash scripts/deploy-hydra-head.sh p100   (day-to-day startup)
#
# Note (2026-06-22): previously this script also installed host-side
# systemd services for the 3 exporters + promtail, then disabled
# them at the end (the in-container hydra-head owns them as
# subprocesses). All four host-side systemd services are now
# REMOVED entirely — the binaries alone are installed, and
# hydra-head spawns them as children. This script reflects that
# (no service files installed, no disable step at the end).
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
  mkdir -p \$HOME/hydra/bin \$HOME/hydra/config \$HOME/.config/promtail
  mkdir -p /mnt/kv_slots 2>/dev/null || true
"

echo "==> Enabling user session lingering (survive SSH logout)"
ssh "$VM" "loginctl enable-linger" || true

echo "==> Installing promtail binary (hydra-head spawns as child)"
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

echo "==> Copying promtail config to in-container path"
ssh "$VM" "
  mkdir -p \$HOME/.config/promtail
"
scp "$REPO_ROOT/infra/promtail/promtail-rtx.yml" "$VM:/tmp/promtail-config.yml"
ssh "$VM" "cp /tmp/promtail-config.yml \$HOME/.config/promtail/config.yml"

echo "==> Installing node_exporter binary (hydra-head spawns as child)"
ssh "$VM" "
  if [ -f ~/.local/bin/node_exporter ]; then
    echo 'node_exporter already installed'
  else
    curl -sLO https://github.com/prometheus/node_exporter/releases/download/v1.8.2/node_exporter-1.8.2.linux-amd64.tar.gz
    tar xf node_exporter-1.8.2.linux-amd64.tar.gz
    mv node_exporter-1.8.2.linux-amd64/node_exporter ~/.local/bin/
    rm -rf node_exporter-1.8.2*
    echo 'node_exporter installed'
  fi
"

echo "==> Installing nvidia_gpu_exporter binary (hydra-head spawns as child)"
ssh "$VM" "
  if [ -f ~/.local/bin/nvidia_gpu_exporter ]; then
    echo 'nvidia_gpu_exporter already installed'
  else
    curl -sLO https://github.com/utkuozdemir/nvidia_gpu_exporter/releases/download/v1.2.1/nvidia_gpu_exporter_1.2.1_linux_x86_64.tar.gz
    tar xf nvidia_gpu_exporter_1.2.1_linux_x86_64.tar.gz
    mv nvidia_gpu_exporter ~/.local/bin/
    rm -rf nvidia_gpu_exporter_1.2.1* LICENSE README.md 2>/dev/null || true
    echo 'nvidia_gpu_exporter installed'
  fi
"

echo ""
echo "==> Setup complete. To deploy hydra-head to P100:"
echo "    bash scripts/deploy-hydra-head.sh p100"
echo ""
echo "    Or manually:"
echo "    ssh $VM 'systemctl --user start hydra-head'"
echo "    curl http://192.168.122.21:9700/status   # hydra-head API"
echo ""
echo "    The 4 child services (llama / node_exporter / nvidia_gpu_exporter"
echo "    / promtail) are managed by hydra-head itself — no separate systemd"
echo "    services to start or stop."
