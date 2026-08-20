---
description: Implement code incrementally with testing and explicit error handling, following Clean Code and SOLID. Use after design is approved.
---

# implement


## Purpose

Safely implement code changes incrementally, producing code that is correct, readable, and change-tolerant.

## Instructions

1. Follow implementation plan step-by-step.
2. Add tests alongside code.
3. Use explicit naming.
4. Handle all error paths.
5. Validate after each step.
6. Never bypass failing tests.

## Clean Code standards

- Small functions, one level of abstraction per function.
- No side effects hidden behind an innocent-looking name.
- No dead code, no commented-out code, no magic numbers.
- A function does one thing; a class has one reason to change.

## SOLID

- **S**ingle Responsibility — one reason to change per class/module.
- **O**pen/Closed — extend behavior without editing stable code.
- **L**iskov Substitution — subtypes must be usable wherever the base type is expected.
- **I**nterface Segregation — no client forced to depend on methods it doesn't use.
- **D**ependency Inversion — depend on abstractions, not concretions.

## Validation Checklist

- [ ] Tests added
- [ ] Errors handled
- [ ] No silent failures
- [ ] Naming is clear
- [ ] Functions/classes are single-purpose
- [ ] Dependencies point at abstractions, not concrete details
- [ ] Diff reviewed

## Example

Input:
Implement Redis cache layer.

Expected behavior:
- Cache abstraction added (interface, not concrete client, injected into consumers)
- Retry handling added
- Integration tests added
