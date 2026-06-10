#!/usr/bin/env bash
# Build and deploy Hydra.Core (single binary: Store + Coordinator + Agent).
# Optionally bump version before deploying.
#
# Usage:
#   bash scripts/deploy-hydra.sh             # build + deploy (no version bump)
#   bash scripts/deploy-hydra.sh patch       # bump patch + build + deploy
#   bash scripts/deploy-hydra.sh minor       # bump minor + build + deploy
#   bash scripts/deploy-hydra.sh major       # bump major + build + deploy
#   bash scripts/deploy-hydra.sh 1.2.3       # set version + build + deploy

set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
QUADLET_DIR="${XDG_CONFIG_HOME:-$HOME/.config}/containers/systemd"

GREEN='\033[0;32m'; RED='\033[0;31m'; YELLOW='\033[1;33m'; BOLD='\033[1m'; NC='\033[0m'
ok()   { echo -e "  ${GREEN}✓${NC} $*"; }
step() { echo -e "\n${BOLD}==> $*${NC}"; }
die()  { echo -e "  ${RED}✗${NC} $*"; exit 1; }

# ── Version bump (optional) ──────────────────────────────────────────────────
if [ $# -gt 0 ]; then
  cd "$REPO_ROOT"
  CURRENT=$(cat VERSION)
  echo "Current version: $CURRENT"

  IFS='.' read -r MAJ MIN PATCH <<< "$CURRENT"

  case "${1}" in
    major) MAJ=$((MAJ + 1)); MIN=0; PATCH=0 ;;
    minor) MIN=$((MIN + 1)); PATCH=0 ;;
    patch) PATCH=$((PATCH + 1)) ;;
    *)
      if [[ "$1" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]]; then
        NEW="$1"
      else
        die "Usage: $0 [patch|minor|major|<semver>]"
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
  echo ""
fi

step "Building image"
podman build --target core -f "$REPO_ROOT/infra/Dockerfile" -t localhost/hydra-core:latest "$REPO_ROOT"
ok "Image built"

step "Installing Quadlet files"
mkdir -p "$QUADLET_DIR"
cp "$REPO_ROOT/infra/quadlets"/hydra-*.container "$QUADLET_DIR"
cp "$REPO_ROOT/infra/quadlets"/hydra-core.pod "$QUADLET_DIR"
cp "$REPO_ROOT/infra/quadlets"/hydra-coordinator.env "$QUADLET_DIR"
cp "$REPO_ROOT/infra/quadlets"/pg-data.volume "$QUADLET_DIR"

step "Deploying Hydra.Core"
systemctl --user daemon-reload
systemctl --user stop hydra-core-pod.service 2>/dev/null || true
sleep 1

systemctl --user start hydra-postgres.service
echo "  Waiting for postgres to be healthy..."
systemctl --user start hydra-core.service
ok "Hydra.Core deployed"

step "Verifying services"
sleep 15
printf "  %-35s" "Hydra.Core debug :9501"
if curl -sf http://localhost:9501/metrics --connect-timeout 5 &>/dev/null; then ok "ok"; else echo "  ${YELLOW}⚠${NC} not responding"; fi
printf "  %-35s" "Hydra.Core API :9000"
COORD=$(curl -sf http://localhost:9000/health --connect-timeout 5 \
  | python3 -c "import sys,json; d=json.load(sys.stdin); print(d['status'])" 2>/dev/null || echo "unreachable")
if [ "$COORD" = "healthy" ] || [ "$COORD" = "degraded" ]; then ok "$COORD"; else echo "  ${YELLOW}⚠${NC} $COORD"; fi

echo -e "\n${GREEN}${BOLD}Hydra.Core deployed.${NC}"
