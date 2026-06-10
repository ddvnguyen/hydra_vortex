# GitHub Workflow Guide for Coding Agents

Complete walkthrough of the **feature → issue → implement → review → merge → deploy → monitoring** cycle.

---

## Overview

All work flows through GitHub Issues. Reviews create issues. Fixes branch from issues. PRs link back to issues. Monitoring problems create issues. This is the single canonical source of truth.

```
Code Review          Fix Work           Merge              Monitoring
────────────────────────────────────────────────────────────────────
mN-review.md
    ↓
sync_reviews_to_github.py
    ↓
GitHub Issues #2–#25
    ↓
gh issue develop #N → fix/MN-Psev-seq branch
    ↓
[implement fix, mark resolved]
    ↓
gh pr create → Closes #N
    ↓
Merge to main
    ↓
ci.yml deploy
    ↓
monitor.yml (Prometheus alerts) → auto-create issues
[optional: alert fires, create issue]
```

---

## Before Starting a Fix

### 1. Check the review status
```bash
gh issue list --label review-finding --state open
```
Shows all 24 open findings across M0/M1/M2. Pick one with a number (`#N`).

### 2. Read the full finding
Open the issue in GitHub or fetch it:
```bash
gh issue view 16
```
You'll see the full code block, the problem description, and the recommended fix.

### 3. Optional: read the review file
For context, you can also read the milestone review file:
```bash
cat reviews/m2-review.md | grep -A 30 "^\[M2-P0-001\]"
```
(The issue body should have everything you need, but the full markdown has surrounding findings.)

---

## The Fix Workflow

### Step 1: Create a branch from the issue
```bash
gh issue develop 16 --name fix/M2-P0-001
```

This:
- Creates a branch named `fix/M2-P0-001` (or uses your custom name)
- Checks out that branch locally
- Links the branch to GitHub issue #16

### Step 2: Implement the fix

Edit the files listed in the issue (e.g., `src/core/Hydra.Core/StateHandler.cs:190–191`).

Follow the fix recommendation in the issue body.

Run the test suite to verify:
```bash
# Tier 1 (no hardware needed)
pytest tests/system -v

# Or specific unit tests
dotnet test src/core/Tests.Core/ -c Release -v normal
```

### Step 3: Mark the finding as resolved

Edit the review file for your milestone. Find the finding block and change `**Status:** open` to `**Status:** resolved`:

**Before:**
```markdown
### [M2-P0-001] Partial-cache restore sends incomplete data
**File:** `src/core/Hydra.Core/StateHandler.cs:190–191`
**Status:** open
**Issue:** #16
**Assigned:** —
```

**After:**
```markdown
### [M2-P0-001] Partial-cache restore sends incomplete data
**File:** `src/core/Hydra.Core/StateHandler.cs:190–191`
**Status:** resolved
**Issue:** #16
**Assigned:** —
**PR:** #27
```

Also update `reviews/INDEX.md` — decrement the count for your severity level.

### Step 4: Commit your work
```bash
git add src/core/Hydra.Core/StateHandler.cs reviews/m2-review.md reviews/INDEX.md
git commit -m "fix: [M2-P0-001] reconstruct full KV state from cached chunks

Previously only missing chunks were sent to llama, corrupting the
restore on partial-cache hits. Now LocalChunkCache stores chunk data
on disk so the agent can reassemble the full state.

Closes #16"
```

### Step 5: Create a pull request
```bash
gh pr create \
  --title "fix: [M2-P0-001] reconstruct full KV state from cached chunks" \
  --body "Closes #16

Implements the fix from the review finding. Tests pass locally."
```

This will:
- Create the PR on GitHub
- Link it to issue #16 (via `Closes #16`)
- Auto-close the issue when the PR is merged

**Or, if you already committed, use the shorter form:**
```bash
gh pr create --title "fix: [M2-P0-001] reconstruct full KV state from cached chunks" --body "Closes #16"
```

### Step 6: Merge the PR
Once CI passes and review is complete:
```bash
gh pr merge --squash
```

GitHub will automatically close issue #16 when you merge.

---

## Finding Severity & Priority

Fix findings **top-down by severity:**

| Severity | Count | Priority | Issue Labels |
|----------|-------|----------|--------------|
| **P0** (Critical) | 2 | 🔴 First | `p0-critical` |
| **P1** (High) | 9 | 🟡 Second | `p1-high` |
| **P2** (Low) | 13 | ⚪ Third | `p2-low` |

All findings: `gh issue list --label review-finding --state open`

By severity:
```bash
gh issue list --label p0-critical --state open    # 2 issues
gh issue list --label p1-high --state open        # 9 issues
gh issue list --label p2-low --state open         # 13 issues
```

---

## Monitoring Issues (Auto-created)

Two types of issues are **auto-created by CI/monitoring** — do NOT close without investigating:

### CI Failures (`ci-failure` label)
Created by `ci.yml` when a job fails:
- `build-test` failure → issue "CI: build-test failure on [branch]"
- `integration` failure → issue "CI: integration failure on [branch]"
- `system-full` failure → issue "CI: System test full-stack failure on [branch]"

**Action:** Check the run link in the issue body. Fix the root cause and push a new commit.

The issue auto-closes when the job passes on the same branch.

### Prometheus Alerts (`monitoring` label)
Created by `monitor.yml` (every 30 min) when a critical alert fires:
- Alert: StoreDown, CoreDown, LlamaServerDown, etc.

**Action:** Investigate the alert via Grafana (link in issue body). The issue auto-closes when the alert stops firing.

---

## Scripts & Automation

You don't need to run these manually — they're automated — but good to understand:

### `sync_reviews_to_github.py`
Runs when `reviews/*.md` is pushed (via `reviews.yml` workflow).
- Parses all open findings
- Skips findings with `**Status:** resolved`
- Skips findings that already have `**Issue:** #N`
- Creates GitHub issues for new findings
- Writes `**Issue:** #N` back into the review file

**Manual run:**
```bash
python scripts/sync_reviews_to_github.py
```

### `create_monitoring_issues.py`
Runs on a 30-minute schedule (via `monitor.yml`).
- Queries Prometheus for firing critical alerts
- Creates issues for new alerts
- Closes issues when alerts resolve

**Manual run:**
```bash
python scripts/create_monitoring_issues.py
```

### `setup_github_labels.sh`
One-time setup (already done). Creates all labels:
- `p0-critical`, `p1-high`, `p2-low`
- `milestone-m0`, `milestone-m1`, `milestone-m2`, `milestone-m3`
- `review-finding`, `monitoring`, `ci-failure`, `auto-created`

---

## Example: Fix M1-P1-001

**Scenario:** You're assigned to fix issue #8 (`[M1-P1-001] All new sessions registered with slot_id=0`).

```bash
# 1. View the issue
gh issue view 8

# 2. Start a branch from the issue
gh issue develop 8 --name fix/M1-P1-001

# 3. Edit the core routing (from the issue body: src/core/Hydra.Core/Router.cs:94–96)
vim src/core/Hydra.Core/Router.cs
# Change:
#   session_table.register(sess_id, decision.node_name, decision.slot_id or 0)
# To:
#   if decision.slot_id is None:
#     raise ValueError(f"slot_id must be assigned, not None")
#   session_table.register(sess_id, decision.node_name, decision.slot_id)

# 4. Run tests
pytest tests/system -v   # system tests, no hardware needed

# 5. Update review file
vim reviews/m1-review.md
# Change [M1-P1-001] block: **Status:** open → **Status:** resolved

# 6. Update INDEX.md
vim reviews/INDEX.md
# Decrement M1 P1 count from 4 to 3

# 7. Commit
git add src/core/Hydra.Core/Router.cs reviews/m1-review.md reviews/INDEX.md
git commit -m "fix: [M1-P1-001] validate slot_id before session register

All new sessions were registered with default slot_id=0, causing
collisions when multiple sessions landed on the same node. Now
validate that the router decision includes a valid slot assignment.

Closes #8"

# 8. Create PR
gh pr create --title "fix: [M1-P1-001] validate slot_id before session register" --body "Closes #8"

# 9. Wait for CI (green), then merge
gh pr merge --squash
# GitHub auto-closes #8 when merged
```

---

## Troubleshooting

### "gh: command not found"
Install GitHub CLI: https://cli.github.com/

### "gh issue develop: no branch name given"
Provide a branch name:
```bash
gh issue develop 16 --name fix/M2-P0-001
```

### "Issue #N already closed"
If the issue is already closed (e.g., someone else merged a fix), you can still create a new PR:
```bash
gh pr create --title "fix: [MN-Psev-seq] ..." --body "Addresses #N"
```

### "My branch isn't linked to the issue"
If you created a branch manually, you can still link it:
```bash
gh issue develop 16 --resume
```

Or just use the issue number in your PR body:
```bash
gh pr create --body "Closes #16"
```

### "How do I see which issues are assigned to me?"
```bash
gh issue list --assignee @me --state open
```

### "I fixed multiple findings in one PR"
Update the PR body to close all related issues:
```bash
gh pr create --body "Closes #16, #17"
```

---

## Questions?

- **Where are the reviews?** `reviews/INDEX.md` (status summary) and `reviews/m*.md` (full details)
- **Where are the open issues?** `gh issue list --label review-finding --state open`
- **How do I check a monitoring alert?** `gh issue list --label monitoring --state open`
- **How do I report a CI failure?** Create an issue with label `ci-failure` and link the run

Last updated: 2026-05-29 (after GitHub workflow setup)
