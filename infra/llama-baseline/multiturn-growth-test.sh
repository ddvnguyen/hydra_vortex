#!/usr/bin/env bash
# multiturn-growth-test.sh — measures decode throughput as a function of
# context depth across multi-turn conversational sessions.
#
# Unlike concurrent-decode-test.sh (single-shot prompts, ~1K deep), this
# script simulates real coding-agent sessions: each "session" is a growing
# conversation where new context is appended every turn, reaching ~80K
# resident tokens by turn 10.  Per-turn tok/s is recorded so the
# depth-vs-speed curve can be plotted.
#
# Sessions run as true concurrent background jobs; the script verifies
# wall-clock overlap (same approach as concurrent-decode-test.sh).
#
# Prerequisites: an already-running llama-server (e.g. via run-with-params.sh).
#
# Usage:
#   bash multiturn-growth-test.sh <server_port> <n_sessions> <n_turns> \
#                                  <new_tokens_per_turn> <output_tokens_per_turn>
#
# Example (2 concurrent sessions, 10 turns, ~8K new tokens/turn, 750 output):
#   bash multiturn-growth-test.sh 18081 2 10 8000 750
#
# The script uses the /v1/chat/completions endpoint with growing message
# history to leverage prefix caching (--cache-prompt --cache-reuse 64).
# Each turn's prompt is the full conversation so far + new synthetic content,
# so the server should reuse cached KV for the prefix and only process the
# incremental new tokens.
#
# Output: per-turn wall-clock, completion tokens, tok/s for each session,
# aggregate stats, and overlap verification.
set -uo pipefail

PORT="${1:?usage: $0 <server_port> <n_sessions> <n_turns> <new_tokens_per_turn> <output_tokens_per_turn>}"
N_SESSIONS="${2:?usage: $0 ...}"
N_TURNS="${3:?usage: $0 ...}"
NEW_TOKENS="${4:?usage: $0 ...}"
N_PREDICT="${5:?usage: $0 ...}"

# Each word ~1.3 tokens; target ~NEW_TOKENS tokens of synthetic content per turn.
# ~1.5 tokens per word for English prose; pad generously to hit target.
# words_needed ≈ target_tokens / 1.5 = target_tokens * 2 / 3.
WORDS_PER_TURN=$(( NEW_TOKENS * 2 / 3 ))

echo "=== multiturn-growth-test ==="
echo "  port:         $PORT"
echo "  sessions:     $N_SESSIONS"
echo "  turns:        $N_TURNS"
echo "  new tok/turn: ~$NEW_TOKENS (~$WORDS_PER_TURN words)"
echo "  output/turn:  $N_PREDICT tokens"
echo "  target depth: turn $N_TURNS ≈ $(( 7000 + (N_TURNS - 1) * (NEW_TOKENS + N_PREDICT) )) resident tokens"
echo ""

# Session runner: each session is a background subshell.
# The subshell writes per-turn results to $RESULTS_DIR/session_<N>.txt
# and the growing conversation to $RESULTS_DIR/session_<N>_conv.json.
# A Python helper handles JSON construction and API calls (clean escaping).
RESULTS_DIR=$(mktemp -d)
trap 'rm -rf "$RESULTS_DIR"' EXIT

PIDS=()
for sid in $(seq 1 "$N_SESSIONS"); do
  (
    python3 - "$PORT" "$sid" "$N_TURNS" "$WORDS_PER_TURN" "$N_PREDICT" "$RESULTS_DIR" <<'PYEOF'
import sys, json, time, urllib.request

port, sid, n_turns, words_per_turn, n_predict, results_dir = (
    sys.argv[1], int(sys.argv[2]), int(sys.argv[3]),
    int(sys.argv[4]), int(sys.argv[5]), sys.argv[6]
)

def gen_content(turn, words):
    """Generate ~words tokens of synthetic content for a turn.
    Varies content by turn number to avoid highly-repetitive text that
    inflates MTP acceptance artificially (per arm098 caveat)."""
    # Mix several paragraph templates with numeric variation
    templates = [
        "The implementation refactored the core module for turn {t} \
processing, introducing a new abstraction layer that handles buffered \
I/O operations with configurable retry semantics and exponential \
backoff strategies for transient network failures.",
        "Performance analysis of the distributed cache revealed that \
turn {t} latency improved by approximately {pct} percent after \
switching to a lock-free concurrent hash map with epoch-based \
reclamation for the hot path, reducing tail latency at p99.",
        "The code review for turn {t} identified several areas where \
memory allocation patterns could be optimized: arena-based allocation \
for short-lived objects, pool reuse for connection handlers, and \
prefetch-friendly layout for the main data structures in the query \
planner's critical section.",
        "Documentation update for turn {t} covers the new streaming \
interface, including backpressure handling, graceful degradation \
under load, and the circuit-breaker pattern applied to upstream \
service calls with configurable timeout and retry budgets.",
        "Test coverage expansion for turn {t} added integration tests \
for the authentication middleware, including token refresh flows, \
session invalidation across distributed nodes, and rate limiting \
with sliding window counters backed by the replicated store.",
        "Infrastructure changes for turn {t} migrated the deployment \
pipeline to a blue-green strategy with canary analysis, reducing \
rollback time from minutes to seconds while maintaining zero-downtime \
guarantees for the primary API endpoints under production traffic.",
        "The debugging session for turn {t} traced a race condition in \
the event bus dispatcher where concurrent publish operations could \
lose messages under high throughput, fixed by introducing a per-topic \
sequence number with compare-and-swap validation on the commit path.",
        "Database schema evolution for turn {t} added a materialized \
view for the analytics dashboard, pre-aggregating hourly metrics \
with incremental refresh, reducing query latency from 2.3 seconds to \
47 milliseconds for the most common dashboard access patterns.",
    ]
    paragraphs = []
    for i in range(words // 30 + 2):
        t = templates[(turn * 3 + i) % len(templates)]
        pct = 15 + (turn * 7 + i * 13) % 40
        paragraphs.append(t.format(t=turn, pct=pct))
    text = " ".join(paragraphs)
    # Trim to approximate word count
    word_list = text.split()
    return " ".join(word_list[:words])

def build_messages(conv_path, new_user_content):
    """Build messages array: load existing conversation, append new user msg."""
    messages = [{"role": "system", "content":
        "You are a helpful coding assistant. Respond concisely with technical "
        "details. Generate realistic code snippets and explanations."}]
    try:
        with open(conv_path) as f:
            messages.extend(json.load(f))
    except (FileNotFoundError, json.JSONDecodeError):
        pass
    messages.append({"role": "user", "content": new_user_content})
    return messages

def send_request(port, messages, n_predict):
    """Send chat completion request, return (response_dict, wall_seconds)."""
    payload = json.dumps({
        "messages": messages,
        "max_tokens": n_predict,
        "temperature": 0
    }).encode("utf-8")
    req = urllib.request.Request(
        f"http://127.0.0.1:{port}/v1/chat/completions",
        data=payload,
        headers={"Content-Type": "application/json"}
    )
    t0 = time.time()
    with urllib.request.urlopen(req, timeout=300) as resp:
        data = json.loads(resp.read().decode())
    wall = time.time() - t0
    return data, wall

conv_path = f"{results_dir}/session_{sid}_conv.json"
results_path = f"{results_dir}/session_{sid}.txt"
all_turns = []

# Record session start timestamp for overlap verification
with open(f"{results_dir}/session_{sid}_start", "w") as f:
    f.write(f"{sid} {time.time():.6f}\n")

# Write header
with open(results_path, "w") as f:
    f.write(f"session {sid} | {n_turns} turns | {words_per_turn} words/turn new | {n_predict} output tokens\n")
    f.write("-" * 70 + "\n")

for turn in range(1, n_turns + 1):
    content = gen_content(turn, words_per_turn)
    messages = build_messages(conv_path, content)
    total_msgs = len(messages)

    try:
        data, wall = send_request(port, messages, n_predict)
        usage = data.get("usage", {})
        prompt_tok = usage.get("prompt_tokens", 0)
        comp_tok = usage.get("completion_tokens", 0)
        tok_s = comp_tok / wall if wall > 0 else 0.0

        # Extract assistant response and append to conversation history
        assistant_msg = data["choices"][0]["message"]["content"]
        try:
            with open(conv_path) as f:
                conv = json.load(f)
        except (FileNotFoundError, json.JSONDecodeError):
            conv = []
        conv.append({"role": "user", "content": content})
        conv.append({"role": "assistant", "content": assistant_msg})
        with open(conv_path, "w") as f:
            json.dump(conv, f)

        line = (f"  turn {turn:2d}/{n_turns}  "
                f"wall={wall:6.2f}s  prompt_tok={prompt_tok:6d}  "
                f"comp_tok={comp_tok:4d}  tok/s={tok_s:6.2f}  "
                f"msgs={total_msgs}")
        all_turns.append({"turn": turn, "wall": wall, "prompt_tok": prompt_tok,
                          "comp_tok": comp_tok, "tok_s": tok_s})
    except Exception as e:
        line = f"  turn {turn:2d}/{n_turns}  FAILED: {e}"
        all_turns.append({"turn": turn, "wall": 0, "prompt_tok": 0,
                          "comp_tok": 0, "tok_s": 0})

    with open(results_path, "a") as f:
        f.write(line + "\n")

# Summary
if all_turns:
    valid = [t for t in all_turns if t["tok_s"] > 0]
    if valid:
        t1 = valid[0]["tok_s"]
        tlast = valid[-1]["tok_s"]
        avg = sum(t["tok_s"] for t in valid) / len(valid)
        first_pt = valid[0]["prompt_tok"]
        last_pt = valid[-1]["prompt_tok"]
        ptk_ratio = last_pt / first_pt if first_pt > 0 else 0
        summary = (
            f"\n  summary: turn1={t1:.2f} turn{valid[-1]['turn']}={tlast:.2f} "
            f"mean={avg:.2f} tok/s  "
            f"prompt_tok: {first_pt} -> {last_pt} ({ptk_ratio:.1f}x)"
        )
    else:
        summary = "\n  summary: all turns failed"
    with open(results_path, "a") as f:
        f.write(summary + "\n")
    print(summary, flush=True)
else:
    print("  no turns completed", flush=True)

# Record session end timestamp for overlap verification
with open(f"{results_dir}/session_{sid}_end", "w") as f:
    f.write(f"{sid} {time.time():.6f}\n")
PYEOF
  ) &
  PIDS+=($!)
done

echo "Launched ${#PIDS[@]} sessions, waiting for completion..."
for pid in "${PIDS[@]}"; do
  wait "$pid"
done

echo ""
echo "=== all sessions complete, results ==="
echo ""

# Print per-session results
for sid in $(seq 1 "$N_SESSIONS"); do
  rfile="$RESULTS_DIR/session_${sid}.txt"
  if [[ -f "$rfile" ]]; then
    cat "$rfile"
    echo ""
  fi
done

# Overlap verification
echo "=== overlap check ==="
python3 - "$RESULTS_DIR" "$N_SESSIONS" <<'PYEOF'
import sys

results_dir = sys.argv[1]
n_sessions = int(sys.argv[2])

windows = []
for sid in range(1, n_sessions + 1):
    try:
        with open(f"{results_dir}/session_{sid}_start") as f:
            parts = f.read().split()
            start = float(parts[1])
        with open(f"{results_dir}/session_{sid}_end") as f:
            parts = f.read().split()
            end = float(parts[1])
        windows.append((start, end, sid))
    except Exception:
        pass

if len(windows) >= 2:
    # Check pairwise overlap
    any_overlap = False
    for i in range(len(windows)):
        for j in range(i + 1, len(windows)):
            s1, e1, _ = windows[i]
            s2, e2, _ = windows[j]
            overlap_start = max(s1, s2)
            overlap_end = min(e1, e2)
            if overlap_end > overlap_start:
                any_overlap = True
                print(f"  sessions {windows[i][2]} & {windows[j][2]}: "
                      f"overlap {overlap_end - overlap_start:.1f}s")
    if any_overlap:
        print(f"\nconcurrency check: PASS (sessions overlap)")
    else:
        print(f"\nconcurrency check: FAIL (sessions sequential)")
else:
    print("concurrency check: N/A (single session)")
PYEOF
