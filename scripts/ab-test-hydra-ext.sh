#!/usr/bin/env bash
# WS4 live-rig A/B test for the server-context extension seam (epic #610).
#
# The seam adds a runtime toggle (HYDRA_EXT_MODE=legacy|seam) to llama-server.
# Both paths are compiled into the SAME binary; the env var picks which one
# drives Hydra behavior. This script runs the SAME live-rig scenario twice —
# once per mode — and diffs the responses, proving the refactor is
# behavior-identical.
#
# Requires:
#   - the live GPU rig up (hydra-core :9000, llama RTX :8080, llama P100 :8086,
#     store :9500, agents :9601/:9602)
#   - a llama-server image built from epic/610 (with the seam)
#   - the scenario script you want to A/B (default: Tests.LiveRig tier)
#
# Usage:
#   bash scripts/ab-test-hydra-ext.sh [--scenario "<cmd>"] [--mode legacy] [--mode seam]
#
# Examples:
#   bash scripts/ab-test-hydra-ext.sh                              # run Tier-2 LiveRig in both modes
#   bash scripts/ab-test-hydra-ext.sh --scenario "curl -s :8080/v1/chat/completions ..."
#
# Output: <TMP>/ab-<mode>/ with per-mode responses; final diff verdict.
set -euo pipefail

NODES="infra/hydra-head/config/node-rtx.yaml infra/hydra-head/config/node-rtx3060.yaml infra/hydra-head/config/node-p100.yaml"
OUT_DIR="${AB_OUT_DIR:-/tmp/hydra-ab}"
SCENARIO="${AB_SCENARIO:-dotnet test src/core/Tests.LiveRig/ -c Release --no-build --nologo}"

usage() { sed -n '2,25p' "$0" | grep '^#' | tr -d '#'; }

# Set HYDRA_EXT_MODE in the llama env block of every node config.
# The head passes Llama.Env through to the llama-server process (manager.go).
# Scoped to the `llama:`..`services:` range so a stray `env:` elsewhere is untouched.
set_mode() {
    local mode="$1"
    for cfg in $NODES; do
        [ -f "$cfg" ] || { echo "WARN: $cfg not found, skipping" >&2; continue; }
        local start end
        start=$(grep -n "^llama:" "$cfg" | head -1 | cut -d: -f1)
        end=$(grep -n "^services:" "$cfg" | head -1 | cut -d: -f1)
        [ -n "$start" ] || { echo "WARN: no llama: block in $cfg" >&2; continue; }
        [ -n "$end" ]   || end=$(wc -l < "$cfg")
        if awk "NR>=$start && NR<=$end" "$cfg" | grep -q 'HYDRA_EXT_MODE'; then
            sed -i "${start},${end}s/^\(\s*HYDRA_EXT_MODE:\).*/\1 $mode/" "$cfg"
        else
            # insert under the first `  env:` inside the llama block
            local envline
            envline=$(awk "NR>=$start && NR<=$end && /^  env:/{print NR; exit}" "$cfg")
            [ -n "$envline" ] || { echo "WARN: no env: in llama block of $cfg" >&2; continue; }
            sed -i "${envline}a\\    HYDRA_EXT_MODE: $mode" "$cfg"
        fi
        echo "  $cfg: HYDRA_EXT_MODE=$mode"
    done
}

# Remove HYDRA_EXT_MODE from all node configs (restore after A/B).
restore() {
    for cfg in $NODES; do
        [ -f "$cfg" ] || continue
        sed -i '/^    HYDRA_EXT_MODE:/d' "$cfg"
        echo "  $cfg: HYDRA_EXT_MODE removed"
    done
}

run_mode() {
    local mode="$1"
    local dir="$OUT_DIR/ab-$mode"
    mkdir -p "$dir"

    echo "==> [1/3] configure $mode"
    set_mode "$mode"

    echo "==> [2/3] deploy llama-server in $mode"
    bash scripts/deploy-hydra-head.sh rtx+rtx3060+p100 || bash scripts/deploy-hydra.sh hydra

    echo "==> [3/3] run scenario"
    # shellcheck disable=SC2086
    ( cd /mnt/WorkDisk/Workplace/hydra_vortex && eval "$SCENARIO" ) > "$dir/scenario.log" 2>&1 \
        || echo "WARN: scenario exited non-zero (see $dir/scenario.log)" >&2
}

verdict() {
    echo
    echo "=== A/B verdict (epic #610 WS4) ==="
    if diff -r "$OUT_DIR/ab-legacy" "$OUT_DIR/ab-seam" > "$OUT_DIR/ab.diff" 2>&1; then
        echo "IDENTICAL — legacy and seam behave the same."
        exit 0
    else
        echo "DIFFERENCES FOUND:"
        cat "$OUT_DIR/ab.diff"
        exit 1
    fi
}

main() {
    local legacy=0 seam=0 verdict=0 do_restore=0
    while [ $# -gt 0 ]; do
        case "$1" in
            --scenario) shift; SCENARIO="$1";;
            --mode) shift; case "$1" in legacy) legacy=1;; seam) seam=1;; *) echo "unknown mode $1" >&2; exit 2;; esac;;
            --verdict) verdict=1;;
            --restore) do_restore=1;;
            --out) shift; OUT_DIR="$1";;
            -h|--help) usage; exit 0;;
            *) echo "unknown arg $1" >&2; usage; exit 2;;
        esac
        shift
    done

    if [ "$verdict" = "1" ]; then verdict; return; fi
    if [ "$do_restore" = "1" ]; then restore; return; fi
    [ "$legacy" = "1" ] && [ "$seam" = "1" ] && { echo "pick one --mode" >&2; exit 2; }
    [ "$legacy" = "0" ] && [ "$seam" = "0" ] && { echo "pick --mode legacy OR --mode seam per run" >&2; exit 2; }

    # One invocation = one mode (the deploy is exclusive). Run both, then:
    #   bash scripts/ab-test-hydra-ext.sh --mode legacy
    #   bash scripts/ab-test-hydra-ext.sh --mode seam
    #   bash scripts/ab-test-hydra-ext.sh --verdict
    #   bash scripts/ab-test-hydra-ext.sh --restore
    if [ "$legacy" = "1" ]; then run_mode legacy; else run_mode seam; fi
}

main "$@"
