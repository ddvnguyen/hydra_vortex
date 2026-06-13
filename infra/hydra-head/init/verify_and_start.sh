#!/bin/bash
set -e

echo '========================================'
echo 'SYSTEM ENVIRONMENT VERIFICATION'
echo '========================================'
echo -n 'Container OS: ' && cat /etc/os-release | grep PRETTY_NAME | cut -d'=' -f2 | tr -d '"'
echo -n 'Kernel: ' && uname -r
echo -n 'GLIBC: ' && ldd --version 2>/dev/null | head -n 1
echo -n 'CUDA Lib Path: ' && echo "$LD_LIBRARY_PATH"
echo '----------------------------------------'

# GPU detection
echo 'GPU Detection:'
nvidia-smi --query-gpu=name,memory.total,driver_version --format=csv 2>/dev/null || echo '  WARNING: nvidia-smi not available (--device nvidia.com/gpu=all missing?)'
echo '----------------------------------------'

# Verify hydra-head binary
echo -n 'Checking hydra-head binary... '
if /usr/local/bin/hydra-head -h > /dev/null 2>&1; then
  echo 'OK'
else
  echo 'FAILED'
  echo '  Checking linker dependencies:'
  ldd /usr/local/bin/hydra-head 2>/dev/null | head -20 || true
  echo ''
  echo 'CRITICAL: hydra-head binary is not executable. Check build arch and dependencies.'
  exit 1
fi

# Verify config files exist
echo 'Checking config files:'
for f in /opt/hydra/config/global.yaml /opt/hydra/config/node-rtx.yaml; do
  if [ -f "$f" ]; then
    echo "  $f -- OK"
  else
    echo "  $f -- MISSING"
    exit 1
  fi
done

echo '========================================'
echo 'LAUNCHING HYDRA HEAD'
echo '========================================'
echo "Args: $@"
echo ''

exec /usr/local/bin/hydra-head "$@"
