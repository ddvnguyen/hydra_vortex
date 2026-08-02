#!/usr/bin/env bash
# scripts/ci/wait-for-head.sh <label> <status-url> [timeout-seconds] [container-name]
#
# Block until a hydra-head /status endpoint answers, or fail.
#
# [container-name] is optional and local-only (the RTX/RTX3060 deploy paths
# run on this host; P100 doesn't have a local container to inspect). When
# given, a timeout dumps `podman inspect` state and recent logs for that
# container straight into the CI log, so a wedged/zombie container (state
# says running, host process is dead — see reap_zombie_container in
# deploy-hydra-head.sh) is diagnosable from the Actions tab instead of
# requiring someone to SSH in and reconstruct it by hand.
#
# Replaces `sleep 8; curl ... || echo "WARNING: not responding"`, which was
# wrong twice over: 8 seconds is far short of what a head needs (OCI pull plus
# a 60-120s model load), and a WARNING on failure meant the deploy step went
# green while the node was still down. The subsequent "Verify deployed digests"
# step then reported all three heads unresponsive and failed the job with a
# misleading message.
#
# Default budget is 480s (8 min), which covers the documented worst case:
# ~2s image pull + up to 120s model load, with generous headroom for a cold
# tmpfs or a slow VM disk on the P100 (node-p100.yaml raises health.max_fails
# for exactly this reason).
set -euo pipefail

if [[ $# -lt 2 ]]; then
    echo "usage: $0 <label> <status-url> [timeout-seconds]" >&2
    exit 2
fi

LABEL="$1"
URL="$2"
TIMEOUT="${3:-480}"
CONTAINER="${4:-}"
INTERVAL=5

deadline=$(( SECONDS + TIMEOUT ))
attempt=0

while (( SECONDS < deadline )); do
    attempt=$(( attempt + 1 ))
    if curl -sf --max-time 5 "$URL" >/dev/null 2>&1; then
        echo "OK: $LABEL responding after ${SECONDS}s (${attempt} attempt(s))"
        exit 0
    fi
    # Progress roughly every 30s so a stuck deploy is visible in the log
    # without drowning it in one line per poll.
    if (( attempt % 6 == 0 )); then
        echo "  ... waiting for $LABEL (${SECONDS}s / ${TIMEOUT}s)"
    fi
    sleep "$INTERVAL"
done

echo "FAIL: $LABEL ($URL) did not respond within ${TIMEOUT}s" >&2
echo "  The container/service may still be pulling its engine image or loading" >&2
echo "  the model. Check: podman logs <head-container>, or on the P100:" >&2
echo "  systemctl --user status hydra-head" >&2

if [[ -n "$CONTAINER" ]] && command -v podman >/dev/null 2>&1 && podman container exists "$CONTAINER" 2>/dev/null; then
    echo "" >&2
    echo "-- podman state for $CONTAINER --" >&2
    podman inspect "$CONTAINER" --format 'Running={{.State.Running}} Pid={{.State.Pid}} Health={{.State.Health.Status}} FailingStreak={{.State.Health.FailingStreak}}' >&2 2>&1
    pid=$(podman inspect "$CONTAINER" --format '{{.State.Pid}}' 2>/dev/null || echo 0)
    if [[ "$pid" != "0" ]] && ! kill -0 "$pid" 2>/dev/null; then
        echo "  ^ DESYNCED: podman reports Running, but PID $pid does not exist on the host." >&2
        echo "  This is a podman/conmon state-desync zombie, not a slow start — the container" >&2
        echo "  will never recover on its own. reap_zombie_container() in deploy-hydra-head.sh" >&2
        echo "  should catch this on the next deploy; if it's still here, force-remove it:" >&2
        echo "  podman rm -f $CONTAINER" >&2
    fi
    echo "-- last 30 log lines from $CONTAINER --" >&2
    podman logs --tail 30 "$CONTAINER" >&2 2>&1
fi

exit 1
