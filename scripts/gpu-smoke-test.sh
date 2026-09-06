#!/usr/bin/env bash
# GPU smoke test: verifies every configured GPU node is healthy.
#
# Host GPUs (RTX 5060 Ti, RTX 3060): builds a small CUDA test binary and runs
#   (1) a correctness check (vector-add, compared against expected output)
#   (2) a short sustained full-power GEMM soak (drives the card to its power
#       limit, not just to "busy" — a light/idle-power kernel can pass while
#       the card still faults under real load)
# then scans the kernel log for Xid errors emitted during the test window.
#
# P100 (KVM VM, no local CUDA dispatch path): HTTP health check only.
#
# Exit code is non-zero if any GPU fails.
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
BUILD_DIR="${SMOKE_BUILD_DIR:-/tmp/gpu-smoke-test}"
# Default 90s matches shortest window where gpu-burn's corruption was actually caught on the faulty 3060
# (escalating 1520->22718 errors within 90s, vs 15s default that missed it and reported false PASS). Keep overridable via SMOKE_FULL_SECONDS.
FULL_SECONDS="${SMOKE_FULL_SECONDS:-90}"
GEMM_N="${SMOKE_GEMM_N:-8192}"
GENCODES="${SMOKE_GENCODES:--gencode arch=compute_86,code=sm_86 -gencode arch=compute_120,code=sm_120}"
P100_HEALTH_URL="${P100_HEALTH_URL:-http://192.168.122.21:8086/health}"
SKIP_P100="${SKIP_P100:-0}"

NVCC="${NVCC:-$(command -v nvcc || true)}"
if [ -z "$NVCC" ]; then
  for c in /opt/software/cuda/13.2/bin/nvcc /opt/software/cuda/*/bin/nvcc; do
    [ -x "$c" ] && NVCC="$c" && break
  done
fi
if [ -z "$NVCC" ]; then
  echo "ERROR: nvcc not found (set NVCC=/path/to/nvcc)" >&2
  exit 2
fi

mkdir -p "$BUILD_DIR"
BIN="$BUILD_DIR/kernels"
if [ ! -x "$BIN" ] || [ "$SCRIPT_DIR/gpu-smoke-test/kernels.cu" -nt "$BIN" ]; then
  echo "Building smoke-test kernels ($NVCC)..."
  # shellcheck disable=SC2086
  "$NVCC" -O2 $GENCODES -o "$BIN" "$SCRIPT_DIR/gpu-smoke-test/kernels.cu"
fi

FAIL=0
declare -a SUMMARY=()

check_xid() {
  local bus_id="$1" since="$2" until_ts="$3"
  if ! command -v journalctl >/dev/null 2>&1; then
    echo "    (journalctl unavailable, skipping Xid check)"
    return 0
  fi
  local hits slot="${bus_id#PCI:}"
  # NVRM logs Xid with the full domain, e.g. "Xid (PCI:0000:02:00.0): 13".
  # ([0-9a-f]{4}:)? optionally absorbs the "0000:" domain prefix so both the
  # fully-qualified and domain-less "PCI:02:00.0" forms match.
  hits=$(journalctl -k --since "$since" --until "$until_ts" 2>/dev/null | grep -iE "Xid \(PCI:([0-9a-f]{4}:)?${slot}" || true)
  if [ -n "$hits" ]; then
    echo "    Xid errors found in kernel log during test window:"
    echo "$hits" | sed 's/^/      /'
    return 1
  fi
  return 0
}

echo "=== Host GPU smoke test ==="
while IFS=, read -r idx name bus_id; do
  idx="$(echo "$idx" | xargs)"
  name="$(echo "$name" | xargs)"
  bus_id="$(echo "$bus_id" | xargs | tr 'A-Z' 'a-z')"
  # nvidia-smi reports e.g. 00000000:02:00.0; Xid log lines use PCI:0000:02:00
  short_bus="PCI:${bus_id#00000000:}"
  short_bus="${short_bus%.*}"

  echo "--- GPU $idx: $name ($bus_id) ---"
  gpu_ok=1

  start_ts="$(date '+%Y-%m-%d %H:%M:%S')"
  if ! CUDA_DEVICE_ORDER=PCI_BUS_ID "$BIN" light "$idx"; then
    echo "    LIGHT test FAILED"
    gpu_ok=0
  fi
  if ! CUDA_DEVICE_ORDER=PCI_BUS_ID "$BIN" full "$idx" "$GEMM_N" "$FULL_SECONDS"; then
    echo "    FULL-POWER test FAILED"
    gpu_ok=0
  fi
  end_ts="$(date '+%Y-%m-%d %H:%M:%S')"

  if ! check_xid "$short_bus" "$start_ts" "$end_ts"; then
    gpu_ok=0
  fi

  if [ "$gpu_ok" = "1" ]; then
    SUMMARY+=("PASS  GPU $idx  $name")
  else
    SUMMARY+=("FAIL  GPU $idx  $name")
    FAIL=1
  fi
done < <(nvidia-smi --query-gpu=index,name,pci.bus_id --format=csv,noheader)

if [ "$SKIP_P100" != "1" ]; then
  echo "--- P100 (VM, HTTP health check) ---"
  if curl -sf --max-time 5 "$P100_HEALTH_URL" >/dev/null 2>&1; then
    SUMMARY+=("PASS  P100  $P100_HEALTH_URL")
  else
    SUMMARY+=("FAIL  P100  $P100_HEALTH_URL (unreachable)")
    FAIL=1
  fi
fi

echo
echo "=== Summary ==="
printf '%s\n' "${SUMMARY[@]}"

exit "$FAIL"
