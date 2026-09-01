#!/bin/bash
# Protect the GitHub Actions self-hosted runner work directory + build cache.
#
# WHY: the runner stores its repo checkouts + build cache under
#   /mnt/containers/actions-runner-work/
# Deleting it (accident, cleanup script, or agent rm -rf) forces a FULL
# re-checkout of every repo + submodule (llama-cpp fork ~1.5GB) on the next
# CI run — minutes of lost time. `podman system prune` is SAFE (it only
# touches container storage, never this dir), but anything doing `rm -rf`
# against it is forbidden.
#
# Usage:
#   scripts/protect-runner-work.sh install   # create sentinel + verify
#   scripts/protect-runner-work.sh verify    # check integrity (exit 1 on tamper)
#   scripts/protect-runner-work.sh check     # same as verify, quiet
#
# This is a CONVENTION + SENTINEL guard (the runner user == repo user, so
# ACLs cannot stop a determined rm). The real protection is:
#   1. A marker file that cleanup logic can test before deleting.
#   2. A verification script agents/cleanup-jobs call before acting.
#   3. The documented rule in AGENTS.md / CLAUDE.md.

set -euo pipefail

WORK_ROOT="/mnt/containers/actions-runner-work"
SENTINEL="${WORK_ROOT}/.PROTECTED-DO-NOT-DELETE"
MARKER_CONTENT="Hydra runner build cache + repo checkouts. Deleting this dir
forces full re-checkout of all repos incl. the ~1.5GB llama-cpp fork.
Never rm -rf this path. podman system prune is safe (does not touch it).
See scripts/protect-runner-work.sh and AGENTS.md."

# Paths that MUST survive (repo checkouts + their build caches).
CRITICAL_PATHS=(
  "${WORK_ROOT}/hydra_vortex"
  "${WORK_ROOT}/llama.cpp"
)

fail() { echo "ERROR: $*" >&2; exit 1; }

cmd_install() {
  if [ ! -d "${WORK_ROOT}" ]; then
    fail "work root ${WORK_ROOT} does not exist"
  fi
  # 1) Sentinel file (read-only, checksummed). Re-install removes first
  #    (the file is 444 — read-only even for the owner).
  rm -f "${SENTINEL}"
  echo "${MARKER_CONTENT}" > "${SENTINEL}"
  chmod 444 "${SENTINEL}"
  # 2) Verify the critical checkouts exist. The runner nests checkouts
  #    (hydra_vortex/hydra_vortex/hydra_vortex), so search for a real .git.
  for p in "${CRITICAL_PATHS[@]}"; do
    if [ -d "${p}" ] && find "${p}" -maxdepth 3 -name ".git" -type d 2>/dev/null | grep -q .; then
      echo "ok: ${p} (git checkout present)"
    elif [ -d "${p}" ]; then
      echo "warn: ${p} exists but has no .git (fresh/partial checkout)"
    else
      echo "warn: ${p} missing (will be created on next CI run)"
    fi
  done
  echo
  echo "Sentinel installed: ${SENTINEL}"
  echo "Verify with: scripts/protect-runner-work.sh verify"
}

cmd_verify() {
  local quiet="${1:-}"
  [ -f "${SENTINEL}" ] || fail "sentinel ${SENTINEL} missing — protection OFF"
  local got
  got="$(cat "${SENTINEL}" 2>/dev/null)"
  if [ "${got}" != "${MARKER_CONTENT}" ]; then
    fail "sentinel ${SENTINEL} tampered (content mismatch)"
  fi
  # Sentinel must be read-only.
  if [ -w "${SENTINEL}" ]; then
    fail "sentinel ${SENTINEL} is writable — protection degraded"
  fi
  [ -z "${quiet}" ] && echo "PROTECTION OK: sentinel present + read-only"
  exit 0
}

case "${1:-}" in
  install) cmd_install ;;
  verify)  cmd_verify ;;
  check)   cmd_verify quiet ;;
  *)
    echo "usage: $0 {install|verify|check}"
    exit 2
    ;;
esac
