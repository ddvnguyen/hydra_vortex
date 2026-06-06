#!/usr/bin/env bash
set -euo pipefail

cd "$(git rev-parse --show-toplevel)"

CURRENT=$(cat VERSION)
echo "Current version: $CURRENT"

IFS='.' read -r MAJ MIN PATCH <<< "$CURRENT"

case "${1:-patch}" in
  major) MAJ=$((MAJ + 1)); MIN=0; PATCH=0 ;;
  minor) MIN=$((MIN + 1)); PATCH=0 ;;
  patch) PATCH=$((PATCH + 1)) ;;
  *)
    if [[ "$1" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]]; then
      NEW="$1"
    else
      echo "Usage: $0 [patch|minor|major|<semver>]" >&2; exit 1
    fi
    ;;
esac

NEW="${NEW:-$MAJ.$MIN.$PATCH}"
echo "New version:     $NEW"

echo "$NEW" > VERSION

sed -i "s/<Version>[0-9.]*<\/Version>/<Version>$NEW<\/Version>/" src/core/Directory.Build.props
sed -i "s/<AssemblyVersion>[0-9.]*<\/AssemblyVersion>/<AssemblyVersion>$NEW.0<\/AssemblyVersion>/" src/core/Directory.Build.props
sed -i "s/<FileVersion>[0-9.]*<\/FileVersion>/<FileVersion>$NEW.0<\/FileVersion>/" src/core/Directory.Build.props
sed -i "s/<InformationalVersion>[0-9.]*<\/InformationalVersion>/<InformationalVersion>$NEW<\/InformationalVersion>/" src/core/Directory.Build.props

git add VERSION src/core/Directory.Build.props
git commit -m "v$NEW"
git tag -a "v$NEW" -m "Hydra v$NEW"

echo "Building images..."

# Build hydra core images
podman build --target store -f infra/Dockerfile -t localhost/hydra-store:latest .
podman build --target agent -f infra/Dockerfile -t localhost/hydra-agent:latest .
podman build --target coordinator -f infra/Dockerfile -t localhost/hydra-coordinator:latest .

# Build llama-server RTX image
podman build -f infra/llama-rtx-node/Dockerfile -t localhost/llama-rtx:latest infra/llama-rtx-node

echo "Installing Quadlet files..."
QUADLET_DIR="${XDG_CONFIG_HOME:-$HOME/.config}/containers/systemd"
mkdir -p "$QUADLET_DIR"
cp infra/quadlets/*.container "$QUADLET_DIR"
cp infra/quadlets/*.volume "$QUADLET_DIR"
systemctl --user daemon-reload

echo "Deploying hydra core services..."
systemctl --user stop hydra-postgres hydra-store hydra-agent-rtx hydra-agent-p100 hydra-coordinator 2>/dev/null || true
systemctl --user start hydra-postgres.service
systemctl --user start hydra-store.service
systemctl --user start hydra-agent-rtx.service hydra-agent-p100.service
systemctl --user start hydra-coordinator.service

echo "Deploying llama-server RTX..."
systemctl --user stop llama-rtx 2>/dev/null || true
systemctl --user start llama-rtx.service

REV=$(git rev-parse --short HEAD)
echo "[$(date -u +%Y-%m-%dT%H:%M:%SZ)] $NEW ($REV) deployed" >> deploy.log
echo "Deployed v$NEW ($REV)"
