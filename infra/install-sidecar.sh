#!/bin/bash
set -euo pipefail

SELF_DIR="$(cd "$(dirname "$0")" && pwd)"
SIDECAR_DIR="$SELF_DIR/sidecar"
SERVICE_NAME="hydra-sidecar.service"
USER_SYSTEMD_DIR="$HOME/.config/systemd/user"

echo "=== Hydra Sidecar Installer ==="
echo ""

# ── Step 1: Build sidecar image ──
echo "[1/3] Building sidecar image..."
podman build -t hydra-sidecar "$SIDECAR_DIR"
echo ""

# ── Step 2: Install systemd user unit ──
echo "[2/3] Installing systemd user unit..."
mkdir -p "$USER_SYSTEMD_DIR"
cp "$SELF_DIR/$SERVICE_NAME" "$USER_SYSTEMD_DIR/"
systemctl --user daemon-reload
systemctl --user enable "$SERVICE_NAME"
echo ""

# ── Step 3: Rebuild compose images (or skip if cached okay) ──
echo "[3/3] Rebuilding hydra compose images..."
podman-compose -f "$SELF_DIR/docker-compose.hydra.yml" build
echo ""

echo "=== Install complete ==="
echo ""
echo "The first start will auto-create containers via ExecStartPre:"
echo "  systemctl --user start hydra-sidecar"
echo ""
echo "Other commands:"
echo "  systemctl --user stop   hydra-sidecar   # Stop + remove stack"
echo "  systemctl --user status hydra-sidecar   # Check status"
