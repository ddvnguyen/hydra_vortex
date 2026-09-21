#!/usr/bin/env bash
set -u
TOK=$(/tmp/opencode/rig-lock.sh acquire controls123-build $$ 3600) || { echo ACQUIRE_FAIL; exit 1; }
trap '/tmp/opencode/rig-lock.sh release "$TOK"' EXIT
cd /mnt/WorkDisk/workspace/worktree/1q3ry0vb/baseline-flash-next/src/llama-cpp
export PATH=/opt/software/cuda/13.2.1/bin:$PATH
cmake --build build-demand --target llama-server -j6 2>&1 | tail -25
echo "BUILD_EXIT ${PIPESTATUS[0]} $(date -u +%T)"
