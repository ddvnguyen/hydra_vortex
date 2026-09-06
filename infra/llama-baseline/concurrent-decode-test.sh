#!/usr/bin/env bash
# concurrent-decode-test.sh — fires N requests as true background jobs against
# an already-running llama-server, verifies their wall-clock windows actually
# overlap (real concurrent decode, not sequential turn-taking), and reports
# per-slot + aggregate tok/s.
#
# run-with-params.sh's built-in curl loop is sequential and can't validate
# concurrency or measure per-slot decode speed under simultaneous load.
#
# Usage: bash concurrent-decode-test.sh <server_port> <n_slots> <n_predict> <prompt_path>
set -uo pipefail

PORT="${1:?usage: $0 <server_port> <n_slots> <n_predict> <prompt_path>}"
N_SLOTS="${2:?usage: $0 <server_port> <n_slots> <n_predict> <prompt_path>}"
N_PREDICT="${3:?usage: $0 <server_port> <n_slots> <n_predict> <prompt_path>}"
PROMPT_PATH="${4:?usage: $0 <server_port> <n_slots> <n_predict> <prompt_path>}"

if [[ ! -f "$PROMPT_PATH" ]]; then
  echo "ERROR: prompt file not found: $PROMPT_PATH" >&2
  exit 1
fi

TMPDIR=$(mktemp -d)
trap 'rm -rf "$TMPDIR"' EXIT

PROMPT_JSON=$(python3 -c "import json,sys; print(json.dumps(open(sys.argv[1]).read()))" "$PROMPT_PATH")

echo "=== launching $N_SLOTS concurrent requests, n_predict=$N_PREDICT, port=$PORT ==="

PIDS=()
for i in $(seq 1 "$N_SLOTS"); do
  (
    START=$(date +%s.%N)
    RESP=$(curl -s --max-time 180 "http://127.0.0.1:${PORT}/v1/chat/completions" \
      -H "Content-Type: application/json" \
      -d "{\"messages\":[{\"role\":\"user\",\"content\":${PROMPT_JSON}}],\"n_predict\":${N_PREDICT},\"temperature\":0}")
    END=$(date +%s.%N)
    echo "$START $END $RESP" > "$TMPDIR/slot_${i}.out"
  ) &
  PIDS+=($!)
done

for pid in "${PIDS[@]}"; do
  wait "$pid"
done

echo "=== all requests returned, analyzing overlap + speed ==="

python3 - "$TMPDIR" "$N_SLOTS" "$N_PREDICT" <<'PYEOF'
import json, sys, glob

tmpdir, n_slots, n_predict = sys.argv[1], int(sys.argv[2]), int(sys.argv[3])

windows = []
speeds = []
for i in range(1, n_slots + 1):
    path = f"{tmpdir}/slot_{i}.out"
    try:
        with open(path) as f:
            line = f.read()
    except FileNotFoundError:
        print(f"slot {i}: FAILED (no output file)")
        continue
    start_s, end_s, rest = line.split(" ", 2)
    start, end = float(start_s), float(end_s)
    windows.append((start, end))
    wall = end - start
    try:
        resp = json.loads(rest)
        usage = resp.get("usage", {})
        completion_tokens = usage.get("completion_tokens", n_predict)
    except Exception:
        completion_tokens = n_predict
    tok_s = completion_tokens / wall if wall > 0 else 0.0
    speeds.append(tok_s)
    print(f"slot {i}: wall={wall:.2f}s completion_tokens={completion_tokens} tok/s={tok_s:.2f}")

if len(windows) >= 2:
    overlap_start = max(w[0] for w in windows)
    overlap_end = min(w[1] for w in windows)
    overlapped = overlap_end > overlap_start
    print(f"\nconcurrency check: {'PASS (windows overlap)' if overlapped else 'FAIL (sequential, no overlap)'}")
    if overlapped:
        print(f"  shared overlap window: {overlap_end - overlap_start:.2f}s")

if speeds:
    mean_speed = sum(speeds) / len(speeds)
    print(f"\nper-slot tok/s: {', '.join(f'{s:.1f}' for s in speeds)}")
    print(f"mean tok/s/slot: {mean_speed:.2f}")
    print(f"aggregate tok/s: {sum(speeds):.2f}")
PYEOF
