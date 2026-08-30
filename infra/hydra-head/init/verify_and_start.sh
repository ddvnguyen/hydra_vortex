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

# Verify config files exist (config is mounted at runtime from the host —
# nothing is baked into the image at build time). The node config path
# comes from the command line (-node ...); verify it exists rather than
# hardcoding a name, since the 3060 container runs node-rtx3060.yaml.
echo 'Checking config files:'
for f in /opt/hydra/config/global.yaml; do
  if [ -f "$f" ]; then
    echo "  $f -- OK"
  else
    echo "  $f -- MISSING"
    exit 1
  fi
done
NODE_CONFIG=""
# Parse args WITHOUT consuming $@ — the exec below must receive the
# FULL original argument list (including -global etc.). Indexing into
# "$@" preserves it for the exec.
i=1
while [ "$i" -le "$#" ]; do
  if [ "${!i}" = "-node" ] && [ "$i" -lt "$#" ]; then
    j=$((i+1))
    NODE_CONFIG="${!j}"
    break
  fi
  i=$((i+1))
done
if [ -n "$NODE_CONFIG" ]; then
  if [ -f "$NODE_CONFIG" ]; then
    echo "  $NODE_CONFIG -- OK"
  else
    echo "  $NODE_CONFIG -- MISSING"
    exit 1
  fi
fi

echo '========================================'
echo 'LAUNCHING HYDRA HEAD'
echo '========================================'

# Deploy-time engine pin (#470): the workflow sets
# HYDRA_LLAMA_IMAGE_SOURCE / HYDRA_LLAMA_IMAGE_DIGEST (compose
# environment — NOT the command list, where podman-compose's
# ${VAR:--} substitution corrupts the YAML list structure). Append
# them as -llama-image-source/-llama-image-digest flags; empty/unset
# → fall back to the node config file.
if [ -n "${HYDRA_LLAMA_IMAGE_SOURCE:-}" ]; then
  set -- "$@" -llama-image-source "$HYDRA_LLAMA_IMAGE_SOURCE"
fi
if [ -n "${HYDRA_LLAMA_IMAGE_DIGEST:-}" ]; then
  set -- "$@" -llama-image-digest "$HYDRA_LLAMA_IMAGE_DIGEST"
fi

echo "Args: $@"
echo ''

exec /usr/local/bin/hydra-head "$@"
