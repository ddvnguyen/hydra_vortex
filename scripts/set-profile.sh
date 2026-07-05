#!/usr/bin/env bash
set -euo pipefail

# Hydra Profile Switcher
# Usage: bash scripts/set-profile.sh [moe|dense]
# Switches the .env file and prints the deploy command.

SCRIPT_DIR="$(cd "$(dirname "$0")/.." && pwd)"

case "${1:-}" in
    moe|MoE|MOE)
        ENV_FILE="$SCRIPT_DIR/.env-moe"
        PROFILE="moe"
        ;;
    dense|Dense|DENSE)
        ENV_FILE="$SCRIPT_DIR/.env-dense"
        PROFILE="dense"
        ;;
    *)
        echo "Usage: $0 {moe|dense}"
        echo ""
        echo "  Switches Hydra profile and shows the deploy command."
        echo ""
        echo "  Available profiles:"
        echo "    moe    Qwopus3.6-MoE-A3B-v1-APEX-I-Mini (COMBINED-OT expert split + P/D split)"
        echo "           RTX 5060 Ti head, RTX 3060 peer, P100 decode"
        echo "    dense  Qwopus3.6-Dense-Coder-Compat-MTP (COMBINED-static layer split)"
        echo "           RTX 5060 Ti head + RTX 3060 (0 slots, dedicated peer)"
        exit 1
        ;;
esac

if [ ! -f "$ENV_FILE" ]; then
    echo "ERROR: Profile file not found: $ENV_FILE"
    exit 1
fi

# Copy the profile to .env so podman compose picks it up automatically.
# Warn if operator has customised .env (i.e. it differs from the profile).
if [ -f "$SCRIPT_DIR/.env" ] && ! diff -q "$ENV_FILE" "$SCRIPT_DIR/.env" >/dev/null 2>&1; then
    echo "WARNING: $SCRIPT_DIR/.env has local modifications."
    echo "         The profile will overwrite them. Back up first if needed."
    echo ""
fi
cp "$ENV_FILE" "$SCRIPT_DIR/.env"
echo "Profile set to: $PROFILE ($ENV_FILE → .env)"
echo ""

echo "=== Profile: $PROFILE ==="
grep -v '^#' "$ENV_FILE" | grep -v '^$'
echo ""

echo "=== Deploy ==="
echo "  podman compose -f infra/docker-compose.hydra.yml up -d"
echo ""

echo "=== Monitoring ==="
echo "  Core:       http://localhost:9000/health"
echo "  Core APIs:  http://localhost:9000/metrics"
echo "  Head RTX:   http://localhost:9700/health"
echo "  Head RTX3060: http://localhost:9701/health"
echo "  Grafana:    http://localhost:3000"
echo "  Prometheus: http://localhost:9091"
echo ""
echo "=== Verify ==="
echo "  curl -s http://localhost:9000/health | jq ."
echo ""
echo "=== NIAH Eval ==="
echo "  bash scripts/eval/run-niah.sh -c 5000 -d 50"
