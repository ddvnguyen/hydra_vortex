#!/usr/bin/env bash
# Creates the GitHub label state machine used by the orchestration system.
# Requires: gh CLI authenticated, run from inside the repo (or set GH_REPO).
set -euo pipefail

mk() { gh label create "$1" --color "$2" --description "$3" --force; }

# Dev-cycle state machine
mk "status:ready"        "0E8A16" "Triaged, waiting for the team lead to pick up"
mk "status:planning"     "FBCA04" "Team lead is writing the technical design"
mk "status:in-progress"  "1D76DB" "Workers assigned, development running"
mk "status:review"       "5319E7" "PR open, agent review in progress"
mk "status:deployed"     "0052CC" "Merged and deployed to staging, testing"
mk "status:monitoring"   "006B75" "In soak; monitoring agent gives verdicts"

# Cross-cutting
mk "draft:needs-review"  "D93F0B" "Produced by tier-3 local model; must be reviewed by t1/t2 before merge"
mk "source:monitoring"   "B60205" "Filed automatically by the monitoring agent"
mk "source:instrumentor" "E4E669" "Filed by the instrumentor probe when a pipeline sweep FAILs"
mk "gate:approved"       "C2E0C6" "User approved the big-change proposal on this issue"

echo "Labels ready."
