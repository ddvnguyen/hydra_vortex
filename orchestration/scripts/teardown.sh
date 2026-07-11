#!/usr/bin/env bash
# Kill switch: removes all orchestration schedules. Running agents keep running;
# stop those separately with `paseo ls` + `paseo stop <id>`.
set -euo pipefail

for name in lead-heartbeat issue-triage monitor; do
  if paseo schedule delete "$name" >/dev/null 2>&1; then
    echo "✗ removed schedule: $name"
  else
    echo "  (schedule not found: $name)"
  fi
done

# Also remove any pending quota-resume one-shots created by quota-resume.sh
paseo schedule ls 2>/dev/null | grep -o 'quota-resume-[A-Za-z0-9_-]*' | sort -u | while read -r name; do
  paseo schedule delete "$name" >/dev/null 2>&1 && echo "✗ removed schedule: $name"
done || true

# Sweep any leftover instrumentor canaries
paseo ls 2>/dev/null | grep -o 'canary-[0-9]*' | sort -u | while read -r c; do
  paseo stop "$c" >/dev/null 2>&1 && echo "✗ stopped canary: $c"
done || true
git worktree remove --force canary-scratch 2>/dev/null || true

echo
echo "Schedules removed. Running agents: paseo ls"
echo "Reminder: the Instrumentor runs on system cron — remove its line with: crontab -e"
