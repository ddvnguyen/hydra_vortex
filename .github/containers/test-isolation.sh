#!/bin/bash
set -e

RUNTIME=${1:-podman}

echo "=== Container Isolation Tests ==="

if ! command -v "$RUNTIME" >/dev/null 2>&1; then
  echo "Error: runtime '$RUNTIME' is not installed"
  exit 1
fi

# Wait for the container to be ready
echo "Waiting for llm-prometheus container..."
$RUNTIME ps --format '{{.Names}}' | grep -q '^llm-prometheus$'

HOST_IP=$(hostname -I | awk '{print $1}')

function check() {
  local name=$1
  local cmd=$2
  local success_msg=$3
  local fail_msg=$4

  echo -n "$name: "
  if eval "$cmd" >/dev/null 2>&1; then
    echo "$fail_msg"
    return 1
  fi
  echo "$success_msg"
}

check "Namespace isolation" "$RUNTIME exec llm-prometheus ps aux | grep -c '$$'" "PASS" "FAIL: Host PIDs visible"
check "Read-only config" "$RUNTIME exec llm-prometheus touch /etc/prometheus/prometheus.yml" "PASS" "FAIL: Config is writable"
check "Network isolation" "$RUNTIME exec llm-prometheus ping -c1 $HOST_IP" "PASS" "FAIL: Can reach host IP"

CAPS=$($RUNTIME inspect llm-prometheus --format '{{json .HostConfig.CapAdd}}' | grep -c 'NET_BIND_SERVICE' || true)
if [ "$CAPS" -eq 0 ]; then
  echo "Capabilities check: FAIL (NET_BIND_SERVICE missing or invalid)"
  exit 1
fi

echo "Capabilities check: PASS"

echo "=== All isolation tests completed ==="
