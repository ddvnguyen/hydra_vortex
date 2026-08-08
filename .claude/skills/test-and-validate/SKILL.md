---
description: Validate implementations with tests, edge cases, and verification steps before deployment or completion.
---

# test-and-validate


## Purpose

Verify correctness and stability before completion.

## Instructions

1. Run full test suite.
2. Verify acceptance criteria.
3. Test:
   - empty input
   - boundary values
   - invalid input
4. Check performance-sensitive paths.
5. Review final diff critically.

## Automated Validation Checklist

- [ ] Unit tests pass
- [ ] Integration tests pass
- [ ] Edge cases tested
- [ ] Error handling verified
- [ ] No skipped tests
- [ ] Acceptance criteria verified

## Example

Input:
Validate payment processing changes.

Expected behavior:
- Invalid card handling tested
- Timeout handling tested
- Performance acceptable
