#!/usr/bin/env bash
# Regenerate Quadlet files from docker-compose using podlet,
# then post-process with patch-quadlets.sh to add Notify=healthy,
# CDI GPU, [Install], ContainerName, and other Quadlet-only features.
#
# Preserves hand-crafted files that podlet can't generate
# (llama-rtx.container, nvidia-exporter edge cases).
#
# Usage:
#   bash scripts/regenerate-quadlets.sh
#
# Requirements:
#   - podlet (run as container: ghcr.io/containers/podlet)

set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
QUADLET_DIR="$REPO_ROOT/infra/quadlets"
TMPDIR=$(mktemp -d)
trap 'rm -rf "$TMPDIR"' EXIT

PODLET="podman run --rm -v $REPO_ROOT:/workspace:ro -w /workspace ghcr.io/containers/podlet"

echo "==> Regenerating Quadlet files from docker-compose..."

# Backup hand-crafted files
SAVE_LIST=""
for f in llama-rtx.container; do
  if [ -f "$QUADLET_DIR/$f" ]; then
    cp "$QUADLET_DIR/$f" "$TMPDIR/saved-$f"
    SAVE_LIST="$SAVE_LIST $f"
  fi
done

# Generate with podlet, write to temp directory
echo "--- hydra-core ---"
$PODLET compose -f "$TMPDIR" infra/docker-compose.hydra.yml 2>&1 || true
echo ""
echo "--- infra ---"
$PODLET compose -f "$TMPDIR" infra/docker-compose.infra.yml 2>&1 || true

# Remove .build files (CI builds images separately)
rm -f "$TMPDIR"/*.build

# Copy generated files to quadlets dir
mkdir -p "$QUADLET_DIR"
cp "$TMPDIR"/*.container "$QUADLET_DIR" 2>/dev/null || true
cp "$TMPDIR"/*.volume "$QUADLET_DIR" 2>/dev/null || true
cp "$TMPDIR"/*.env "$QUADLET_DIR" 2>/dev/null || true

# Restore hand-crafted files
for f in $SAVE_LIST; do
  cp "$TMPDIR/saved-$f" "$QUADLET_DIR/$f"
  echo "  Preserved $f"
done

# Post-process with patches
bash "$REPO_ROOT/scripts/patch-quadlets.sh" "$QUADLET_DIR"

echo ""
echo "============================================"
echo "Regeneration complete."
echo "Review changes with: git diff infra/quadlets/"
echo "============================================"
