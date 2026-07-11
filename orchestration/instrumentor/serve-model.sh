#!/usr/bin/env bash
# Serves MiniCPM5-1B-Agentic-Tooluse for the Instrumentor on port 8090.
# Q4_K_M is 688 MB — fits anywhere; pin it to whichever GPU has headroom,
# or run it fully on CPU (a 1B model is fast enough on CPU for this job).
set -euo pipefail

PORT="${INSTR_PORT:-8090}"
NGL="${INSTR_NGL:-99}"        # 0 = CPU only
DEV="${INSTR_DEVICE:-}"       # e.g. CUDA0 / CUDA1 to pin a GPU; empty = default

ARGS=(
  -hf ewinregirgojr/MiniCPM5-1B-Agentic-Tooluse-GGUF:Q4_K_M
  --port "$PORT"
  --ctx-size 8192
  --temp 0
  -ngl "$NGL"
  --alias minicpm5-1b-instrumentor
)
[ -n "$DEV" ] && ARGS+=(--device "$DEV")

# Notes from the model card:
#  - the exact chat template matters; llama-server uses the GGUF's built-in one
#  - natural EOS is weak (15%) — the driver stop-strings on </function>,
#    which is why --temp 0 and small max_tokens are set driver-side too.
exec llama-server "${ARGS[@]}"
