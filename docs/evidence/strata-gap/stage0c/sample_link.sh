#!/usr/bin/env bash
# t0006 1 Hz arm sampler: PCIe gen/width/util/VRAM on one GPU + load1 + engine
# RSS (optional pid file, re-read every loop so it works before/after spawn).
# The arm script starts this BEFORE engine launch and stops it AFTER the bench,
# so one TSV covers load + bench. Row: ts gen width util mem_mib load1 rss_kb
# usage: sample_link.sh OUT.tsv GPUIDX [PIDFILE]
set -u
OUT=$1; GPU=$2; PIDFILE=${3:-}
trap 'exit 0' TERM INT
printf 'ts\tgen\twidth\tutil\tmem_mib\tload1\trss_kb\n' > "$OUT"
while :; do
  smi=$(nvidia-smi --query-gpu=pcie.link.gen.current,pcie.link.width.current,utilization.gpu,memory.used \
        --format=csv,noheader,nounits -i "$GPU" 2>/dev/null | sed 's/, */,/g')
  IFS=, read -r gen width util mem <<EOF
$smi
EOF
  load1=$(cut -d' ' -f1 /proc/loadavg)
  rss=""
  if [ -n "$PIDFILE" ] && [ -s "$PIDFILE" ]; then
    pid=$(cat "$PIDFILE" 2>/dev/null || true)
    if [ -n "$pid" ] && [ -r "/proc/$pid/status" ]; then
      rss=$(awk '/^VmRSS:/{print $2}' "/proc/$pid/status" 2>/dev/null || true)
    fi
  fi
  printf '%s\t%s\t%s\t%s\t%s\t%s\t%s\n' \
    "$(date -Iseconds)" "${gen:-}" "${width:-}" "${util:-}" "${mem:-}" "$load1" "${rss:-}" >> "$OUT"
  sleep 1
done
