# decisions — lightweight Architecture Decision Records

This directory holds one-file-per-decision ADRs. Each file is named `NNN-slug.md` with
a zero-padded 3-digit sequence number and a short kebab-case slug.

## Required sections per file

| Section | Purpose |
|---|---|
| `## Problem` | What is broken, blocked, or under consideration |
| `## Decision` | What was decided and why |
| `## Alternatives considered` | **Mandatory.** At least one alternative you evaluated and why it was rejected. If there is genuinely nothing else to consider, state that explicitly. |
| `## Consequences` | Positive outcomes and costs / follow-ups |
| `Ref: #NNN` | The GitHub issue this decision relates to. Include the line at the bottom of the file. |

Non-trivial PRs that make an architectural or process choice worth remembering should
include a decision note in the same PR. GitHub issues and PRs remain the source of truth
for *what shipped*; these files are the record of *why*.

## Lifecycle

Decisions are appended, not moved between folders. When a decision is superseded, note
it in the original file and link to the replacement.

---

*Pattern adapted from `deepseek-ai/deepseek-harness` `.agents/notes/`.*
