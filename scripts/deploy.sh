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

sed -i "s/<Version>[0-9.]*<\/Version>/<Version>$NEW<\/Version>/" src/Directory.Build.props
sed -i "s/<AssemblyVersion>[0-9.]*<\/AssemblyVersion>/<AssemblyVersion>$NEW.0<\/AssemblyVersion>/" src/Directory.Build.props
sed -i "s/<FileVersion>[0-9.]*<\/FileVersion>/<FileVersion>$NEW.0<\/FileVersion>/" src/Directory.Build.props
sed -i "s/<InformationalVersion>[0-9.]*<\/InformationalVersion>/<InformationalVersion>$NEW<\/InformationalVersion>/" src/Directory.Build.props

git add VERSION src/Directory.Build.props
git commit -m "v$NEW"
git tag -a "v$NEW" -m "Hydra v$NEW"

echo "Building images..."
cd infra

# Build and start Hydra stack
podman-compose build 2>&1 | tail -3
podman-compose up -d 2>&1 | tail -5

# Build and start llama-cpp server
echo "Starting llama-cpp server..."
LLAMA_DIR="llama-rtx-node"
if podman ps --format '{{.Names}}' | grep -q '^llama-cpp$'; then
    echo "llama-cpp already running"
else
    podman-compose -f "$LLAMA_DIR/docker-compose.yml" up -d 2>&1 | tail -3
fi

# Connect llama-cpp to hydra_default network so agents can reach it
HYDRA_NET="hydra_default"
if podman network exists "$HYDRA_NET"; then
    podman network connect "$HYDRA_NET" llama-cpp 2>/dev/null && echo "llama-cpp joined $HYDRA_NET" || echo "llama-cpp already on $HYDRA_NET"
fi

cd "$(git rev-parse --show-toplevel)"

REV=$(git rev-parse --short HEAD)
echo "[$(date -u +%Y-%m-%dT%H:%M:%SZ)] $NEW ($REV) deployed" >> deploy.log
echo "Deployed v$NEW ($REV)"
