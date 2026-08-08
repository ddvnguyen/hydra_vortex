---
description: Analyze and clarify requirements before implementation. Use when starting new tasks, features, refactors, or debugging work.
---

# understand-problem


## Purpose

Ensure the task is fully understood before implementation begins.

## Instructions

1. Restate the task goal clearly.
2. Extract explicit requirements.
3. Infer implicit constraints:
   - performance
   - security
   - compatibility
   - scalability
4. Identify edge cases:
   - null inputs
   - empty inputs
   - invalid inputs
   - concurrency
   - failure states
5. Define acceptance criteria.
6. If ambiguity exists:
   - ask clarifying questions
   - halt implementation

## Output Format

### Goal
...

### Requirements
- ...

### Constraints
- ...

### Edge Cases
- ...

### Acceptance Criteria
- ...

## Example

Input:
Build file upload API.

Expected behavior:
- Clarify auth requirements
- Clarify max upload size
- Clarify storage backend
