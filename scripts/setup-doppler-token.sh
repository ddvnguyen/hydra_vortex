#!/usr/bin/env bash
# Configure Doppler with a non-expiring service token (no keyring required)
# Usage: bash scripts/setup-doppler-token.sh dp.st.dev.XXXX
set -euo pipefail

TOKEN="${1:-}"
if [ -z "$TOKEN" ]; then
    echo "Usage: bash scripts/setup-doppler-token.sh <service-token>"
    echo ""
    echo "Generate a service token at https://dashboard.doppler.com"
    echo "  Project: hydra-vortex → Config: dev → Access → Generate Service Token"
    echo "  DO NOT set expiration (leave blank for permanent token)"
    exit 1
fi

echo "Configuring Doppler service token for /mnt/WorkDisk/Workplace/hydra_vortex..."
echo "$TOKEN" | doppler configure set token --scope /mnt/WorkDisk/Workplace/hydra_vortex

echo ""
echo "Verifying..."
doppler secrets get GITHUB_APP_PRIVATE_KEY -p hydra-vortex -c dev --plain 2>&1 | head -c 40
echo "..."
echo ""
echo "Configuration complete. Doppler should now work without keyring."
