#!/usr/bin/env bash
# hydra-test up — idempotent bring-up of the TEST stack (T2, 6 containers).
# Waits for all 6 containers to be healthy (poll every 5s, timeout 120s).
# On success prints the test-rig URLs.
#
# Usage: bash scripts/hydra-test/up.sh
# Deployed with: podman compose -f infra/docker-compose.infra.yml -f infra/docker-compose.hydra-test.yml up -d
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
INFRA_COMPOSE="$REPO_ROOT/infra/docker-compose.infra.yml"
TEST_COMPOSE="$REPO_ROOT/infra/docker-compose.hydra-test.yml"
INIT_SQL="$REPO_ROOT/infra/sql/init-hydra-test.sql"

if [[ ! -f "$INFRA_COMPOSE" ]]; then
  echo "ERROR: infra compose not found at $INFRA_COMPOSE" >&2
  exit 1
fi
if [[ ! -f "$TEST_COMPOSE" ]]; then
  echo "ERROR: test compose not found at $TEST_COMPOSE" >&2
  exit 1
fi

if [[ -z "${HYDRA_HEAD_AUTH_TOKEN:-}" ]]; then
  if [[ -f "$REPO_ROOT/.hydra-head-token" ]]; then
    export HYDRA_HEAD_AUTH_TOKEN
    HYDRA_HEAD_AUTH_TOKEN="$(cat "$REPO_ROOT/.hydra-head-token")"
    export HYDRA_HEAD_AUTH_TOKEN
  else
    echo "WARN: HYDRA_HEAD_AUTH_TOKEN not set and .hydra-head-token not found; heads may fail health check" >&2
  fi
fi

echo "==> Ensuring store dirs..."
mkdir -p /mnt/llm-ram/hydra-test-store /tmp/hydra-test-l1 2>/dev/null || sudo mkdir -p /mnt/llm-ram/hydra-test-store /tmp/hydra-test-l1 || true
chmod 1777 /mnt/llm-ram 2>/dev/null || true

echo "==> Ensuring hydra_test DB exists (idempotent)..."
if command -v podman >/dev/null 2>&1 && podman ps --format '{{.Names}}' 2>/dev/null | grep -qE '^pg$|postgres'; then
  # Try to init via running pg container; fallback to host psql
  if podman exec pg psql -U hydra -d postgres -c "SELECT 1 FROM pg_database WHERE datname='hydra_test'" 2>/dev/null | grep -q 1; then
    echo "  hydra_test DB already exists"
  else
    echo "  creating hydra_test DB..."
    # Use psql \gexec fallback for CREATE DATABASE idempotency
    podman exec pg psql -U hydra -d postgres -c "SELECT 'CREATE DATABASE hydra_test OWNER hydra' WHERE NOT EXISTS (SELECT 1 FROM pg_database WHERE datname='hydra_test')\\gexec" 2>/dev/null || \
    podman exec pg psql -U hydra -f /dev/stdin < "$INIT_SQL" 2>/dev/null || \
    psql "Host=localhost;Database=postgres;Username=hydra;Password=hydra" -f "$INIT_SQL" 2>/dev/null || \
    echo "  WARN: could not create hydra_test DB (pg not ready?); will retry on next up" >&2
  fi
else
  echo "  pg container not running; DB init will be retried after infra up" >&2
fi

echo "==> Ensuring images exist (idempotent)..."
# Hydra.Core: infra/Dockerfile target core
if podman image exists localhost/hydra-core:latest 2>/dev/null; then
  echo "  localhost/hydra-core:latest already exists — skipping build"
else
  echo "  building localhost/hydra-core:latest ..."
  podman build -f infra/Dockerfile --target core -t localhost/hydra-core:latest . || {
    echo "ERROR: podman build for localhost/hydra-core:latest failed" >&2
    exit 1
  }
fi
# Hydra.Head: infra/hydra-head/Dockerfile.rtx (infra/Dockerfile has no head target — only core)
# The compose references image localhost/hydra-head:rtx built from infra/hydra-head/Dockerfile.rtx
if podman image exists localhost/hydra-head:rtx 2>/dev/null; then
  echo "  localhost/hydra-head:rtx already exists — skipping build"
else
  echo "  building localhost/hydra-head:rtx ..."
  # Head image requires bin/hydra-head; build if missing
  if [[ ! -f "$REPO_ROOT/bin/hydra-head" ]]; then
    echo "  bin/hydra-head missing — building via go..."
    export PATH="$HOME/go-sdk/go/bin:$PATH"
    go build -o "$REPO_ROOT/bin/hydra-head" ./src/head/... 2>&1 | head -n 20 || echo "WARN: go build failed, attempting podman build anyway" >&2
  fi
  podman build -f infra/hydra-head/Dockerfile.rtx -t localhost/hydra-head:rtx . || {
    echo "ERROR: podman build for localhost/hydra-head:rtx failed" >&2
    exit 1
  }
fi

echo "==> Bringing up TEST stack..."
# shellcheck disable=SC2086
podman compose -f "$INFRA_COMPOSE" -f "$TEST_COMPOSE" up -d

echo "==> Waiting for containers to be healthy (poll 5s, timeout 120s)..."
# Container names are derived from compose service names with hydra-test_ prefix or bare names
# With network_mode: host, names are typically hydra-test_<service>_1 or bare service name.
# We poll via podman inspect health where available, else curl the health endpoints.
END=$((SECONDS + 120))
HEALTH_URLS=(
  "http://localhost:19000/v1/models:hydra-core-test-a"
  "http://localhost:19001/v1/models:hydra-core-test-b"
  "http://localhost:19700/health:hydra-head-test-a"
  "http://localhost:19701/health:hydra-head-test-b"
  "http://localhost:18086/health:llama-engine-test-a"
  "http://localhost:18087/health:llama-engine-test-b"
)

all_healthy=false
while (( SECONDS < END )); do
  healthy_count=0
  for entry in "${HEALTH_URLS[@]}"; do
    # entry is "http://localhost:19000/v1/models:hydra-core-test-a"
    url_part="${entry%:*}"
    svc="${entry##*:}"
    if curl -sf --max-time 2 "$url_part" >/dev/null 2>&1; then
      healthy_count=$((healthy_count + 1))
    else
      echo "  waiting: $svc ($url_part) not ready..."
    fi
  done
  if (( healthy_count == ${#HEALTH_URLS[@]} )); then
    all_healthy=true
    break
  fi
  sleep 5
done

if [[ "$all_healthy" != "true" ]]; then
  echo "ERROR: not all services became healthy within 120s" >&2
  echo "Current status:"
  bash "$REPO_ROOT/scripts/hydra-test/status.sh" || true
  exit 1
fi

echo ""
echo "==> Hydra TEST rig is UP"
echo "  Core-A  : http://localhost:19000/v1/chat/completions"
echo "  Core-B  : http://localhost:19001/v1/chat/completions"
echo "  Head-A  : http://localhost:19700/status"
echo "  Head-B  : http://localhost:19701/status"
echo "  Engine-A: http://localhost:18086/health"
echo "  Engine-B: http://localhost:18087/health"
echo "  Metrics-A: http://localhost:19501/metrics"
echo "  Metrics-B: http://localhost:19503/metrics"
