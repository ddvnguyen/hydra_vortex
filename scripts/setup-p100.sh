#!/usr/bin/env bash
# One-time setup for P100 VM: systemd services for llama-server and promtail log shipping.
# Run from the repo root: bash scripts/setup-p100.sh
set -euo pipefail

VM="hydra-p100"
REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"

echo "==> Creating directories in VM"
ssh "$VM" "sudo mkdir -p /var/log/hydra /etc/promtail /var/lib/promtail && sudo chown vm1:vm1 /var/log/hydra"

echo "==> Installing llama-p100 systemd service"
scp "$REPO_ROOT/infra/systemd/llama-p100.service" "$VM":/tmp/
ssh "$VM" "sudo cp /tmp/llama-p100.service /etc/systemd/system/ && sudo systemctl daemon-reload && sudo systemctl enable llama-p100"
echo "    NOTE: llama-p100 service enabled but NOT started (stop the tmux session first)"

echo "==> Installing promtail binary (v3.4.3)"
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
scp "$REPO_ROOT/infra/promtail/promtail-p100.yml" "$VM":/tmp/
scp "$REPO_ROOT/infra/systemd/promtail-p100.service" "$VM":/tmp/
ssh "$VM" "
  sudo cp /tmp/promtail-p100.yml /etc/promtail/config.yml
  sudo cp /tmp/promtail-p100.service /etc/systemd/system/
  sudo systemctl daemon-reload && sudo systemctl enable --now promtail-p100
"

echo ""
echo "==> Setup complete. Next steps:"
echo "    1. Stop the tmux llama session in the VM:"
echo "       ssh hydra-p100 'tmux kill-session -t llama-session'"
echo "    2. Start the systemd service:"
echo "       ssh hydra-p100 'sudo systemctl start llama-p100'"
echo "    3. Verify:"
echo "       ssh hydra-p100 'systemctl status llama-p100 promtail-p100'"
echo "       curl http://192.168.122.21:8086/health"
