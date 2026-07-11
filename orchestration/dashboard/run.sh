#!/usr/bin/env bash
# run.sh — launch the hydra_vortex orchestration dashboard (foreground / tmux).
# Binds to 127.0.0.1:8098 by default. To expose beyond localhost, put it behind
# a reverse proxy with auth; do NOT bind 0.0.0.0 publicly without protection.
set -euo pipefail
cd "$(dirname "$0")/.."   # orchestration/
PORT="${DASHBOARD_PORT:-8098}"
HOST="${DASHBOARD_HOST:-127.0.0.1}"
python3 -m pip install -q -r dashboard/requirements.txt 2>/dev/null || true
cd dashboard
exec python3 -m uvicorn app:app --host "$HOST" --port "$PORT"
