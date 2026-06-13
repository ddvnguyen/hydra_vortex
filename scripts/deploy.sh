#!/usr/bin/env bash
# Deploy everything — version bump, build, and deploy all services.
# Can also deploy individual parts via sub-commands.
#
# Usage:
#   bash scripts/deploy.sh           # bump patch + deploy all
#   bash scripts/deploy.sh patch     # bump patch + deploy all
#   bash scripts/deploy.sh minor     # bump minor + deploy all
#   bash scripts/deploy.sh major     # bump major + deploy all
#   bash scripts/deploy.sh 1.2.3     # set specific version + deploy all
#
#   # Individual parts (no version bump, no implicit build):
#   bash scripts/deploy.sh infra        # deploy infra only
#   bash scripts/deploy.sh hydra        # build + deploy hydra core only
#   bash scripts/deploy.sh hydra-head   # build + deploy hydra-head to nodes

set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
SUB="${1:-}"

# ── Sub-commands: deploy individual parts ────────────────────────────────────
case "$SUB" in
  infra)
    exec bash "$REPO_ROOT/scripts/deploy-infra.sh"
    ;;
  hydra)
    exec bash "$REPO_ROOT/scripts/deploy-hydra.sh"
    ;;
  hydra-head|head)
    shift
    exec bash "$REPO_ROOT/scripts/deploy-hydra-head.sh" all
    ;;
esac

# ── Full deploy: version bump + deploy everything ────────────────────────────
cd "$REPO_ROOT"

CURRENT=$(cat VERSION)
echo "Current version: $CURRENT"

IFS='.' read -r MAJ MIN PATCH <<< "$CURRENT"

case "${SUB:-patch}" in
  major) MAJ=$((MAJ + 1)); MIN=0; PATCH=0 ;;
  minor) MIN=$((MIN + 1)); PATCH=0 ;;
  patch) PATCH=$((PATCH + 1)) ;;
  *)
    if [[ "$SUB" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]]; then
      NEW="$SUB"
    else
      echo "Usage: $0 [patch|minor|major|<semver>|infra|hydra|hydra-head]" >&2
      exit 1
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

cd "$REPO_ROOT"

# Deploy individual parts
bash "$REPO_ROOT/scripts/deploy-hydra.sh"
bash "$REPO_ROOT/scripts/deploy-hydra-head.sh" all

REV=$(git rev-parse --short HEAD)
echo "[$(date -u +%Y-%m-%dT%H:%M:%SZ)] $NEW ($REV) deployed" >> deploy.log
echo ""
echo -e "\033[1mDeployed v$NEW ($REV)\033[0m"
