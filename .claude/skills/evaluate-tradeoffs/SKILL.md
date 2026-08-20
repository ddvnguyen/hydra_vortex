---
description: Compare multiple architectural or implementation approaches before making decisions.
---

# evaluate-tradeoffs


## Purpose

Evaluate competing approaches systematically.

## Instructions

For each candidate approach evaluate:

| Dimension | Questions |
|---|---|
| Correctness | Does it solve the problem? |
| Simplicity | Is it understandable? |
| Reversibility | Can it be changed later? |
| Performance | Will it scale? |
| Maintainability | Is ownership clear? |
| Risk | What can fail? |

## Requirements

- Generate at least two approaches.
- Explain recommendation clearly.
- Document rejected alternatives.

## Example

Input:
Choose analytics database.

Expected behavior:
- Compare PostgreSQL vs ClickHouse
- Explain scalability tradeoffs
- Recommend based on workload
