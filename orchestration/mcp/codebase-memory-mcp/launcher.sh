#!/usr/bin/env bash
# Stable launcher for codebase-memory-mcp (hydra_vortex fleet).
# Derives repo root from this script's location so it works on any clone.
set -euo pipefail
SCRIPT_DIR="$(cd "$(dirname "$(readlink -f "${BASH_SOURCE[0]}")")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/../../.." && pwd)"
BIN="$(command -v codebase-memory-mcp 2>/dev/null || echo "$HOME/.local/bin/codebase-memory-mcp")"
export CBM_CACHE_DIR="${CBM_CACHE_DIR:-$HOME/.cache/codebase-memory-mcp}"
export CBM_ALLOWED_ROOT="${CBM_ALLOWED_ROOT:-$REPO_ROOT}"
export CBM_LOG_LEVEL="${CBM_LOG_LEVEL:-info}"
export CBM_WORKERS="${CBM_WORKERS:-}"
exec "$BIN" "$@"
