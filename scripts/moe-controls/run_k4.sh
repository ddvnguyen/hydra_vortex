#!/usr/bin/env bash
# Pre-registered order (s134): pass 1 ascending layers-on-GPU, pass 2 reversed; k10 then k4 inside each arm. 4 reps/boot (rep 1 cold, unused).
set -u
OUT=/tmp/opencode/controls/k4timing
TOK=$(/tmp/opencode/rig-lock.sh acquire k4timing $$ 7200) || { echo ACQUIRE_FAIL; exit 1; }
echo "HOLDING $TOK $(date -u +%FT%TZ)"
trap '/tmp/opencode/rig-lock.sh release "$TOK"; true' EXIT
export ALLOW_DEGRADED_LINK=1 MIN_TOKENS=150
cd /mnt/WorkDisk/workspace/worktree/1q3ry0vb/baseline-flash-next/scripts/moe-controls
python3 arm_k4_timing.py $OUT/pass1 ncm4:k10,ncm4:k4,ncm2:k10,ncm2:k4,allhost:k10,allhost:k4 4
python3 arm_k4_timing.py $OUT/pass2 allhost:k4,allhost:k10,ncm2:k4,ncm2:k10,ncm4:k4,ncm4:k10 4
echo "CAMPAIGN_DONE $(date -u +%FT%TZ)"
