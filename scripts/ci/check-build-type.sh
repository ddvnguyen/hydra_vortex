#!/usr/bin/env bash
# scripts/ci/check-build-type.sh
# Refuse to deploy a static llama-engine build.
#
# Background: a -DBUILD_SHARED_LIBS=OFF build of any llama.cpp commit
# (including f518cff16) hangs in the post-init phase on RTX sm_120.
# The same source built -DBUILD_SHARED_LIBS=ON works. The two have
# different md5s of the launcher but the build_info() output looked
# identical, so the difference was invisible in CI.
#
# After ddvnguyen/hydra_vortex#349, llama-server --version reports
# [shared] or [static] so we can fail fast. This script is invoked
# from scripts/deploy-hydra-head.sh (smoke test before deploy).
#
# Note: pre-#349 builds of the same source report no [shared] token
# at all (not "[static]"). This script treats both as failures, which
# is the intended fail-fast — re-build with the new fork.

set -euo pipefail

if [[ $# -ne 1 ]]; then
    echo "usage: $0 <path-to-llama-engine-or-llama-server>" >&2
    exit 2
fi

BIN="$1"
if [[ ! -x "$BIN" ]]; then
    echo "FATAL: '$BIN' is not executable" >&2
    exit 2
fi

VERSION_OUT="$("$BIN" --version 2>&1 || true)"

if ! grep -q '\[shared\]' <<<"$VERSION_OUT"; then
    cat >&2 <<INNER_EOF
FATAL: '$BIN' is not a shared-lib build.

  output: $VERSION_OUT

A static (BUILD_SHARED_LIBS=OFF) build hangs in the post-init phase
on RTX sm_120. Re-build with -DBUILD_SHARED_LIBS=ON. See ddvnguyen/hydra_vortex#346.
INNER_EOF
    exit 1
fi

echo "OK: '$BIN' reports $(grep -oE '\[shared\]' <<<"$VERSION_OUT" | head -1)"
