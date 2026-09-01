#!/usr/bin/env bash
# hydra-test down — idempotent teardown of the TEST stack.
# Verifies no orphan engine pids left.
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
INFRA_COMPOSE="$REPO_ROOT/infra/docker-compose.infra.yml"
TEST_COMPOSE="$REPO_ROOT/infra/docker-compose.hydra-test.yml"

echo "==> Tearing down TEST stack..."
if [[ -f "$INFRA_COMPOSE" && -f "$TEST_COMPOSE" ]]; then
  podman compose -f "$INFRA_COMPOSE" -f "$TEST_COMPOSE" down 2>&1 || \
  podman compose -f "$TEST_COMPOSE" down 2>&1 || true
elif [[ -f "$TEST_COMPOSE" ]]; then
  podman compose -f "$TEST_COMPOSE" down 2>&1 || true
else
  echo "WARN: compose files not found; trying bare podman rm" >&2
fi

# Also remove any orphan containers by label
for name in hydra-core-test-a hydra-core-test-b hydra-head-test-a hydra-head-test-b llama-engine-test-a llama-engine-test-b; do
  if podman ps -a --format '{{.Names}}' 2>/dev/null | grep -q "^${name}$"; then
    echo "  removing orphan: $name"
    podman rm -f "$name" 2>/dev/null || true
  fi
done

echo "==> Verifying no orphan engine pids..."
orphans="$(pgrep -f "llama-engine.*1808[67]" 2>/dev/null || true)"
if [[ -n "$orphans" ]]; then
  echo "WARN: orphan llama-engine pids still running: $orphans" >&2
  echo "  (engine should be containerized; host pids indicate a leak)" >&2
else
  echo "  no orphan engine pids"
fi

# Also check via podman
remaining="$(podman ps --format '{{.Names}}' 2>/dev/null | grep -E 'hydra-core-test|hydra-head-test|llama-engine-test' || true)"
if [[ -n "$remaining" ]]; then
  echo "WARN: containers still running:" >&2
  echo "$remaining" >&2
else
  echo "==> TEST stack is DOWN"
fi
