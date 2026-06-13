#!/usr/bin/env bash
# Post-process podlet-generated Quadlet files to add features
# that podlet cannot express (Notify=healthy, CDI, [Install], etc.).
#
# Usage:
#   bash scripts/patch-quadlets.sh          # patch files in infra/quadlets/
#   bash scripts/patch-quadlets.sh /tmp/out  # patch files in custom dir

set -euo pipefail

QUADLET_DIR="${1:-$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)/infra/quadlets}"

patch_file() {
  local file="$1"
  local name; name=$(basename "$file" .container)
  echo "  Patching $name..."

  # --- 1. Add ContainerName (if not present) ---
  if ! grep -q "^ContainerName=" "$file"; then
    sed -i "/^\[Container\]/a ContainerName=$name" "$file"
  fi

  # --- 2. Add [Install] section (if not present) ---
  if ! grep -q "^\[Install\]" "$file"; then
    echo -e "\n[Install]\nWantedBy=default.target" >> "$file"
  fi

  # --- 3. Add Notify=healthy if HealthCmd present (if not already set) ---
  if grep -q "^HealthCmd" "$file" && ! grep -q "^Notify=healthy" "$file"; then
    sed -i "/^HealthTimeout=.*/a Notify=healthy" "$file"
  fi

  # --- 4. Service-specific patches ---
  case "$name" in
    llama-rtx)
      # CDI GPU, security, IPC, resource limits
      if ! grep -q "^AddDevice=nvidia.com/gpu=all" "$file"; then
        sed -i "/^\[Container\]/a AddDevice=nvidia.com/gpu=all\nAddCapability=SYS_ADMIN\nSecurityLabel=disable\nIPC=host\nCPUSet=4-11\nMemory=28G\nMemorySwap=36G" "$file"
      fi
      ;;
  esac
}

echo "==> Patching Quadlet files in $QUADLET_DIR"
for f in "$QUADLET_DIR"/*.container; do
  [ -f "$f" ] && patch_file "$f"
done
echo "==> Done"
