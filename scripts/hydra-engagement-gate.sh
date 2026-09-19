#!/bin/bash
# Engagement gate (E0 acceptance, epic #148): asserts the E0 instrumentation
# is WIRED in a server log, then reports the gate verdict.
#
# Test-pass  = wired (load summary + engagement counter lines present).
# Gate verdict OPEN  (exit 0) = wired AND engaged counter > 0: effect reads allowed.
# Gate verdict CLOSED(exit 1) = wired but counter == 0: NO effect number may be read.
# NOT WIRED  (exit 2) = summary/counter lines missing: the test itself fails.
#
# Usage: hydra-engagement-gate.sh <server-stderr-log>
#        hydra-engagement-gate.sh --self-test   # canned positive/negative fixtures
set -u
selftest() {
  local tmp; tmp=$(mktemp -d)
  cat > "$tmp/open.log" <<'EOF'
[HYDRA pins:cuda] file=/tmp/pins.txt layers=48 total_pins=384 pins_per_layer=min8/max8 dups_removed=0 (bytes at attach)
[HYDRA e0] engaged=14400 (decode-only)
[HYDRA e0-look] il=0 site=1 inv=300 launches=900 hits=1260 lookups=3000 h=0.4200 (decode-only)
EOF
  cat > "$tmp/closed.log" <<'EOF'
[HYDRA pins:cuda] file=/tmp/pins.txt layers=48 total_pins=384 pins_per_layer=min8/max8 dups_removed=2 (bytes at attach)
[HYDRA e0] engaged=0 (decode-only)
EOF
  cat > "$tmp/unwired.log" <<'EOF'
llama_model_loader: loaded meta data with 1 tensors
srv  update_slots: all slots are idle
EOF
  local fail=0
  "$0" "$tmp/open.log" >/dev/null 2>&1;   [ $? -eq 0 ] || { echo "SELFTEST FAIL: open fixture must exit 0"; fail=1; }
  "$0" "$tmp/closed.log" >/dev/null 2>&1; [ $? -eq 1 ] || { echo "SELFTEST FAIL: closed fixture must exit 1"; fail=1; }
  "$0" "$tmp/unwired.log" >/dev/null 2>&1; [ $? -eq 2 ] || { echo "SELFTEST FAIL: unwired fixture must exit 2"; fail=1; }
  rm -rf "$tmp"
  [ $fail -eq 0 ] && echo "SELFTEST PASS (open=0 closed=1 unwired=2)"
  return $fail
}
[ "${1:-}" = "--self-test" ] && { selftest; exit $?; }
[ $# -eq 1 ] || { echo "usage: $0 <server-stderr-log> | --self-test" >&2; exit 2; }
LOG="$1"
[ -f "$LOG" ] || { echo "GATE NOT-WIRED: log not found: $LOG" >&2; exit 2; }
grep -q "^\[HYDRA pins:cuda\]" "$LOG" || { echo "GATE NOT-WIRED: no [HYDRA pins:cuda] load summary" >&2; exit 2; }
ELINE=$(grep -m1 -o "^\[HYDRA e0\] engaged=[0-9]*" "$LOG" || true)
[ -n "$ELINE" ] || { echo "GATE NOT-WIRED: no [HYDRA e0] engagement counter" >&2; exit 2; }
N=${ELINE##*=}
grep -m1 "^\[HYDRA pins:cuda\]" "$LOG"
echo "$ELINE"
if [ "$N" -gt 0 ]; then
  echo "GATE OPEN: engaged=$N, effect reads allowed"
  exit 0
else
  echo "GATE CLOSED: engaged=0, NO effect number may be read"
  exit 1
fi
