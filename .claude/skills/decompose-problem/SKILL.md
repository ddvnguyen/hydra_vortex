---
description: Break a large or unclear problem into smaller, independently solvable sub-problems. Use when a task or system is too complex to reason about as a whole.
---

# decompose-problem

## Purpose

Split complex problems into ordered, verifiable sub-problems.

## Instructions

1. State the problem as a single sentence. If you can't, return to `understand-problem` first.
2. Label its dimension: data, logic, concurrency, integration, or performance.
3. Split into sub-problems that are independently solvable, small enough to hold in working memory, and individually verifiable.
4. Order sub-problems by dependency.
5. Apply `understand-problem` to each sub-problem before solving it.
6. After all sub-problems are solved, verify the composed result actually solves the original problem — parts can be correct while the whole is wrong.

## Decomposition Checklist

- [ ] Problem stated in one sentence
- [ ] Dimension labeled
- [ ] Sub-problems independently verifiable
- [ ] Dependency order established
- [ ] Composed solution re-verified against the original problem

## Example

Input:
Migrate monolith auth to a microservice.

Expected behavior:
- Split into token issuance, session migration, and permission mapping
- Order: token issuance before session migration
- Re-verify the combined flow end-to-end
