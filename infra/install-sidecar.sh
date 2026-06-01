#!/bin/bash
set -euo pipefail

SELF_DIR="$(cd "$(dirname "$0")" && pwd)"
SIDECAR_DIR="$SELF_DIR/sidecar"
SERVICE_NAME="hydra-sidecar.service"
USER_SYSTEMD_DIR="$HOME/.config/systemd/user"

echo "=== Hydra Sidecar Installer ==="
echo ""

# ── Step 1: Rebuild hydra images ──
echo "[1/4] Rebuilding hydra compose images..."
podman-compose -f "$SELF_DIR/docker-compose.yml" build
echo ""

# ── Step 2: Pre-create containers (no mount, no start) ──
echo "[2/4] Creating containers (no-start) ..."
podman-compose -f "$SELF_DIR/docker-compose.yml" down 2>/dev/null || true
podman-compose -f "$SELF_DIR/docker-compose.yml" up -d --no-start
echo ""

# ── Step 3: Build sidecar image ──
echo "[3/4] Building sidecar image..."
podman build -t hydra-sidecar "$SIDECAR_DIR"
echo ""

# ── Step 4: Install systemd user unit ──
echo "[4/4] Installing systemd user unit..."
mkdir -p "$USER_SYSTEMD_DIR"
cp "$SELF_DIR/$SERVICE_NAME" "$USER_SYSTEMD_DIR/"
systemctl --user daemon-reload
systemctl --user enable "$SERVICE_NAME"
echo ""

echo "=== Install complete ==="
echo ""
echo "Commands:"
echo "  systemctl --user start  hydra-sidecar   # Start stack"
echo "  systemctl --user stop   hydra-sidecar   # Stop stack"
echo "  systemctl --user status hydra-sidecar   # Check status"
echo "  systemctl --user enable hydra-sidecar   # Auto-start on boot"
echo ""
echo "Or just run: systemctl --user start hydra-sidecar"
