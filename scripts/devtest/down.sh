#!/usr/bin/env bash
# devtest down — idempotent teardown of the dev-test lane.
# NEVER touches prod containers/ports.
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
COMPOSE="$REPO_ROOT/docker-compose.hydra-devtest.yml"

MODE="all"
for arg in "$@"; do
  case "$arg" in
    --baseline-only) MODE="baseline" ;;
    --hydra-only)    MODE="hydra" ;;
    --all)           MODE="all" ;;
    --help|-h)       echo "Usage: $0 [--hydra-only|--baseline-only|--all]"; exit 0 ;;
    *) echo "WARN: unknown arg $arg (ignoring)" >&2 ;;
  esac
done

echo "==> Tearing down devtest lane: mode=$MODE (prod untouched)..."

# Idempotent: compose down for requested profile
if [[ -f "$COMPOSE" ]]; then
  case "$MODE" in
    hydra)    podman compose -f "$COMPOSE" --profile hydra down 2>&1 || true ;;
    baseline) podman compose -f "$COMPOSE" --profile baseline down 2>&1 || true ;;
    all)      podman compose -f "$COMPOSE" --profile hydra --profile baseline down 2>&1 || true ;;
  esac
else
  echo "WARN: compose $COMPOSE not found" >&2
fi

# Remove any orphan devtest containers by exact name (never glob prod names)
for name in hydra-core-devtest hydra-head-devtest hydra-engine-devtest llama-baseline; do
  # Only act if profile matches mode
  if [[ "$MODE" == "hydra" && "$name" == "llama-baseline" ]]; then continue; fi
  if [[ "$MODE" == "baseline" && "$name" != "llama-baseline" ]]; then continue; fi
  if podman ps -a --format '{{.Names}}' 2>/dev/null | grep -qx "$name"; then
    echo "  removing orphan: $name"
    podman rm -f "$name" 2>/dev/null || true
  fi
done

echo "==> Verifying devtest containers are down (prod untouched)..."
remaining="$(podman ps --format '{{.Names}}' 2>/dev/null | grep -E 'hydra-core-devtest|hydra-head-devtest|hydra-engine-devtest|llama-baseline' || true)"
if [[ -n "$remaining" ]]; then
  echo "WARN: devtest containers still running:" >&2
  echo "$remaining" >&2
else
  echo "==> devtest DOWN ($MODE)"
fi

# Guard: verify prod ports are still reachable if they were before (non-fatal)
for port in 9000 9500 8080 8086; do
  if curl -sf --max-time 1 "http://localhost:${port}/health" >/dev/null 2>&1 || curl -sf --max-time 1 "http://localhost:${port}/v1/models" >/dev/null 2>&1; then
    echo "  prod :$port still up (guard pass)"
  else
    # prod may legitimately be down on CI runners without hardware — don't fail
    echo "  prod :$port not responding (expected on hardware-absent runners)"
  fi
done
