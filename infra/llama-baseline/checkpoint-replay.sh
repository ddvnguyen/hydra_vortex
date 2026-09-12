#!/usr/bin/env bash
# checkpoint-replay.sh — re-issue a deep turn against a saved transcript.
#
# Loads a transcript produced by multiturn-growth-test.sh --checkpoint-dir and
# replays it instead of regrowing context. The headline use is a max_tokens
# sweep: one prefill of the deep prefix, then N cheap calls that reuse the
# server's prefix KV cache — collapsing N full context-growth runs into one.
#
# This is the generalised form of the Round-5 manual workaround documented in
# docs/arms/chat-template-sharp-quality-eval.md §8-9. See
# docs/arm-testing-fast-iteration.md for the workflow.
#
# Usage:
#   bash checkpoint-replay.sh <server_port> <transcript.json> [options]
#
# Common examples:
#   # Re-issue the last saved turn with a bigger budget:
#   bash checkpoint-replay.sh 18081 /tmp/ckpt/session1.transcript.json --max-tokens 1500
#
#   # Sweep budgets on the last turn, reusing one prefix across the whole sweep:
#   bash checkpoint-replay.sh 18081 /tmp/ckpt/session1.transcript.json \
#        --sweep-max-tokens 1100,1200,1300,1500,2000
#
#   # New final prompt (e.g. a checkpoint-verification question) on the deep prefix:
#   bash checkpoint-replay.sh 18081 /tmp/ckpt/session1.transcript.json \
#        --turn-prompt-file /tmp/verify.txt --max-tokens 1500
#
#   # Replay the last 3 saved turns sequentially:
#   bash checkpoint-replay.sh 18081 /tmp/ckpt/session1.transcript.json --replay-last 3
#
# Prerequisites: an already-running llama-server (same params as the growth run
# if you want prefix-cache hits) + python3.
#
# See `bash checkpoint-replay.sh --help` for all options.
set -uo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
exec python3 "$SCRIPT_DIR/lib/replay_transcript.py" "$@"
