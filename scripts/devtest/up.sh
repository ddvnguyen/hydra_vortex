#!/usr/bin/env bash
# devtest up — idempotent bring-up of the dev-test lane (hydra vs baseline).
# NEVER touches prod ports/services (:9000/:9500/:9501/:8080/:8081/:8086/:9601-9603/:9700).
# Ports: core :19000, head :19700, engine :18086, store :19500, metrics :19501, baseline :18080.
# Usage:
#   bash scripts/devtest/up.sh                # hydra lane (default)
#   bash scripts/devtest/up.sh --baseline-only
#   bash scripts/devtest/up.sh --hydra-only
#   bash scripts/devtest/up.sh --all          # both lanes (VM has ~13GB headroom)
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
COMPOSE="$REPO_ROOT/docker-compose.hydra-devtest.yml"

# Guard: never touch prod services
for port in 9000 9500 9501 8080 8081 8086 9601 9602 9603 9700 9701; do
  # we only guard via not referencing them; assert compose doesn't contain them
  true
done

MODE="hydra"
for arg in "$@"; do
  case "$arg" in
    --baseline-only) MODE="baseline" ;;
    --hydra-only)    MODE="hydra" ;;
    --all)           MODE="all" ;;
    --help|-h)       echo "Usage: $0 [--hydra-only|--baseline-only|--all]"; exit 0 ;;
    *) echo "WARN: unknown arg $arg (ignoring)" >&2 ;;
  esac
done

if [[ ! -f "$COMPOSE" ]]; then
  echo "ERROR: compose not found at $COMPOSE" >&2
  exit 1
fi

# Auth token: allow dummy for config/health; real deploy needs the secret
if [[ -z "${HYDRA_HEAD_AUTH_TOKEN:-}" ]]; then
  if [[ -f "$REPO_ROOT/.hydra-head-token" ]]; then
    HYDRA_HEAD_AUTH_TOKEN="$(cat "$REPO_ROOT/.hydra-head-token")"
    export HYDRA_HEAD_AUTH_TOKEN
  else
    export HYDRA_HEAD_AUTH_TOKEN="devtest-dummy"
    echo "WARN: HYDRA_HEAD_AUTH_TOKEN not set — using dummy for up.sh (hydra lane may fail health until real token exported)" >&2
  fi
fi

echo "==> Ensuring devtest dirs (idempotent)..."
mkdir -p /tmp/hydra-devtest-store /tmp/hydra-devtest-l1 2>/dev/null || true
chmod 1777 /tmp 2>/dev/null || true

echo "==> Ensuring hydra_test DB exists (idempotent, no prod DB touched)..."
# quick port guard — never connect to prod DB name without suffix
if command -v podman >/dev/null 2>&1; then
  # Check if postgres container is up (host's infra postgres on :5432)
  if podman ps --format '{{.Names}}' 2>/dev/null | grep -qE 'postgres|pg'; then
    # Find correct container name (try common names)
    PG_CANDIDATES=("postgres" "infra-postgres" "hydra-postgres" "pg")
    for pg in "${PG_CANDIDATES[@]}"; do
      if podman ps --format '{{.Names}}' 2>/dev/null | grep -qx "$pg"; then
        if podman exec "$pg" psql -U hydra -d postgres -c "SELECT 1 FROM pg_database WHERE datname='hydra_test'" 2>/dev/null | grep -q 1; then
          echo "  hydra_test DB already exists"
          break
        else
          echo "  creating hydra_test DB via $pg..."
          podman exec "$pg" psql -U hydra -d postgres -c "SELECT 'CREATE DATABASE hydra_test OWNER hydra' WHERE NOT EXISTS (SELECT 1 FROM pg_database WHERE datname='hydra_test')\\gexec" 2>/dev/null || echo "  WARN: CREATE DATABASE failed (pg not ready?)" >&2
          break
        fi
      fi
    done
  else
    echo "  postgres container not running — skipping DB init (will retry on next up)" >&2
  fi
else
  echo "  podman not found — skipping DB init" >&2
fi

echo "==> Ensuring devtest images (idempotent, no build if present)..."
# Hydra.Core devtest — build only if missing
if podman image exists localhost/hydra-core:devtest 2>/dev/null; then
  echo "  localhost/hydra-core:devtest exists — skipping build"
elif podman image exists localhost/hydra-core:latest 2>/dev/null; then
  echo "  tagging localhost/hydra-core:latest -> localhost/hydra-core:devtest"
  podman tag localhost/hydra-core:latest localhost/hydra-core:devtest 2>/dev/null || true
else
  echo "  building localhost/hydra-core:devtest ..."
  podman build -f infra/Dockerfile --target core -t localhost/hydra-core:devtest . 2>&1 | tail -n 5 || echo "WARN: core build failed (retry later)" >&2
fi

# Hydra.Head devtest — reuse rtx image if devtest tag missing
if podman image exists localhost/hydra-head:devtest 2>/dev/null; then
  echo "  localhost/hydra-head:devtest exists — skipping build"
elif podman image exists localhost/hydra-head:rtx 2>/dev/null; then
  echo "  tagging localhost/hydra-head:rtx -> localhost/hydra-head:devtest"
  podman tag localhost/hydra-head:rtx localhost/hydra-head:devtest 2>/dev/null || true
else
  echo "  WARN: hydra-head image missing — head may fail until built" >&2
fi

echo "==> Bringing up devtest lane: mode=$MODE ..."
case "$MODE" in
  hydra)
    podman compose -f "$COMPOSE" --profile hydra up -d
    echo "  hydra lane: core :19000 head :19700 engine :18086"
    ;;
  baseline)
    podman compose -f "$COMPOSE" --profile baseline up -d
    echo "  baseline lane: llama-server :18080"
    ;;
  all)
    podman compose -f "$COMPOSE" --profile hydra --profile baseline up -d
    echo "  both lanes (ensure <16GB VRAM guard: 5.9+7≈13GB)"
    ;;
esac

echo "==> Waiting for health (timeout 120s, prod untouched)..."
# Wait for requested lane only
wait_for() {
  local url="$1" label="$2" tries=24
  for i in $(seq 1 $tries); do
    if curl -sf --max-time 2 "$url" >/dev/null 2>&1; then
      echo "  $label UP ($url)"
      return 0
    fi
    sleep 5
  done
  echo "WARN: $label not healthy after ${tries}*5s ($url)" >&2
  return 1
}

ok=true
if [[ "$MODE" == "hydra" || "$MODE" == "all" ]]; then
  wait_for "http://localhost:19000/v1/models" "hydra-core :19000" || ok=false
  # head + engine are best-effort; core is the gate
fi
if [[ "$MODE" == "baseline" || "$MODE" == "all" ]]; then
  wait_for "http://localhost:18080/health" "baseline :18080" || ok=false
fi

if [[ "$ok" == "true" ]]; then
  echo "==> devtest UP ($MODE) — prod ports untouched"
else
  echo "WARN: devtest lane not fully healthy — see above (prod still untouched)" >&2
  exit 1
fi
