---
description: Produce a retrospective or blameless postmortem after finishing a task or handling an incident. Use after completion or after any unexpected failure.
---

# reflect-and-improve

## Purpose

Capture learnings and concrete follow-ups after work completes or an incident occurs.

## Instructions

1. Write a brief retrospective: what went well, what slowed the work down, what to change next time.
2. For incidents or production failures, produce a blameless postmortem: timeline, root cause (system or process, never a person), follow-up actions with owners.
3. Identify at least one backlog improvement (tooling, tests, docs, process).
4. Document any non-obvious decisions or learnings in a short note.

## Checklist

- [ ] Retrospective or postmortem written
- [ ] Root cause is system/process-based, not person-based
- [ ] At least one actionable follow-up identified
- [ ] Non-obvious decisions documented

## Example

Input:
A deploy caused a 12-minute outage.

Expected behavior:
- Blameless timeline reconstructed
- Root cause: missing cache warm-up step
- Follow-up: add warm-up to deploy checklist, owner assigned
