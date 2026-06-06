#!/usr/bin/env bash
# Start the full Hydra environment — convenience wrapper around
# start-infra.sh + start-hydra.sh.
#
# Usage:
#   bash scripts/start-env.sh [--skip-p100]

set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"

echo "==> Installing/refreshing Quadlet files..."
mkdir -p "${XDG_CONFIG_HOME:-$HOME/.config}/containers/systemd/"
cp "$REPO_ROOT/infra/quadlets"/*.container "$REPO_ROOT/infra/quadlets"/*.volume "$REPO_ROOT/infra/quadlets"/hydra-coordinator.env \
  "${XDG_CONFIG_HOME:-$HOME/.config}/containers/systemd/" 2>/dev/null || true
systemctl --user daemon-reload

echo ""
echo "==> Starting infra (observability) stack..."
bash "$REPO_ROOT/scripts/start-infra.sh"

echo ""
echo "==> Starting Hydra application services..."
bash "$REPO_ROOT/scripts/start-hydra.sh" "$@"
