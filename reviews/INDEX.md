# Review Index

All reviews for the Hydra project. Read this file first to understand current health.

## Status Overview

| Milestone | File | Date | Reviewer | Status | Open P0 | Open P1 | Open P2 |
|-----------|------|------|----------|--------|---------|---------|---------|
| M0 | [m0-review.md](m0-review.md) | 2026-05-28 | claude-sonnet-4-6 | open | 0 | 0 | 2 |
| M1 | [m1-review.md](m1-review.md) | 2026-05-28 | claude-sonnet-4-6 | open | 0 | 1 | 3 |
| M2 | [m2-review.md](m2-review.md) | 2026-05-29 | claude-sonnet-4-6 | open | 1 | 2 | 5 |

**Total open:** 1 P0, 3 P1, 10 P2

## Finding IDs

Finding IDs use the format `[M{n}-P{severity}-{seq}]`. Severity: P0=correctness/data loss,
P1=significant behavioral bug, P2=minor/performance/maintainability.

When fixing a finding: change `Status: open` → `resolved` in the finding block, then update
the counts in this index table.

When a milestone is fully resolved: change its row `status` to `resolved`.

## Process

```
implement milestone
    ↓
review agent reads code → writes reviews/mN-review.md → updates INDEX.md
    ↓
fix agent reads review → picks findings top-down by severity → fixes + marks resolved
    ↓
all P0/P1 resolved → mark milestone review status: resolved
    ↓
next milestone
```
