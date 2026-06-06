#!/bin/bash
set -e

echo '========================================'
echo '⚙️ SYSTEM ENVIRONMENT VERIFICATION'
echo '========================================'
echo -n 'Container OS: ' && cat /etc/os-release | grep PRETTY_NAME | cut -d'=' -f2
echo -n 'GLIBC Version: ' && ldd --version | head -n 1
echo -n 'CUDA Library Path: ' && echo $LD_LIBRARY_PATH
echo -n 'CUDA Runtime Ver: ' && (grep '"version"' /usr/local/cuda/version.json 2>/dev/null | head -n 1 | cut -d'"' -f4 || echo 'N/A')
echo -n 'NVIDIA Driver Ver: ' && (nvidia-smi --query-gpu=driver_version --format=csv,noheader 2>/dev/null | head -n 1 || echo 'N/A')
echo '----------------------------------------'
echo 'Checking llama-server binary compatibility...'

export PATH="/llama/bin:${PATH}"
export LD_LIBRARY_PATH="/llama/bin:${LD_LIBRARY_PATH}"
# Test-run the server help flag to verify dependencies match up
if llama-server -h > /dev/null 2>&1; then
  echo '✅ Success: llama-server executable is healthy and compatible.'
  echo '========================================'
  echo '🚀 Launching Model Server...'
  
  # Execute the server process, replacing bash as PID 1
  echo '✅ Success: llama-server executable is healthy and compatible.'
  echo '========================================'
  echo '🚀 Launching Model Server with custom Compose parameters...'
  eval "exec $@"
else
  echo '❌ CRITICAL ERROR: llama-server failed to execute!'
  echo 'Checking specific linker errors below:'
  ldd ./bin/llama-server
  exit 1
fi