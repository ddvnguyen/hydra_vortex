# Coding Agent Rules

> Extracted from CLAUDE.md to keep the handoff file short. Referenced from
> `CLAUDE.md` `## Coding Agent Rules`. Written generically for coding agents
> across frameworks; on Claude Code the named tools map to: `question` →
> `AskUserQuestion`, `todowrite` → `TaskCreate`/`TaskUpdate`, `task` → `Agent`.

## 1. Ask for decisions via `question` tool
When there are multiple options, solutions, or design choices — always use the `question` tool with structured selections to get a clear decision from the user before proceeding.

**Example:**
```
question(questions=[{
  "header": "Storage backend",
  "question": "Which storage backend should we use for KV cache?",
  "options": [
    {"label": "Redis", "description": "In-memory, fast but volatile"},
    {"label": "tmpfs", "description": "Local RAM disk, simplest setup"},
    {"label": "S3", "description": "Persistent, slower but durable"}
  ]
}])
```

## 2. Track tasks with `todowrite` always
Always use `todowrite` to track work, even for seemingly simple tasks. Keeps progress visible and ensures nothing is skipped.

**Pattern:**
```
todowrite(todos=[
  {content: "Implement Store RPC server", status: "in_progress", priority: "high"},
  {content: "Add integration tests",      status: "pending",     priority: "medium"},
  {content: "Update docs",                status: "pending",     priority: "low"}
])
```
Update status as work progresses — exactly one `in_progress` at a time. Mark `completed` only after verification (test pass, lint clean, etc.).

## 3. Use sub-agents aggressively (2-3 in parallel)

Always launch parallel sub-agents via the `task` tool when work can be decomposed.
This is **not optional** for multi-file or multi-domain tasks.

**When to use:**
- Research / exploration — e.g., search codebase for patterns across services (Hydra.Core C#,
  llama-server C++) simultaneously
- Multi-file changes — e.g., one agent implements the Store change, another the Agent change,
  a third updates tests
- Decomposition — break a large feature into 2-3 parallel scouting agents, then implement
  based on their findings
- Anything that would take you >30s to do serially

**How to use:**
```
task(description="Explore Store codebase", prompt="Find all ...",
      subagent_type="explore")
task(description="Check Coordinator tests", prompt="Read all ...",
      subagent_type="explore")
```

- Use `explore` for quick codebase searches, `general` for complex multi-step work.
- Launch them in a single message (parallel tool calls).
- Each agent returns its findings in one message — consolidate and proceed.

**Don't use sub-agents for:** trivial single-file edits, reading a file you already know
the path of, running a single command.

## 4. End with a final result block
After completing work, output a clear summary block prefixed with `---` or a code-free section that highlights what was done, changed, or needs attention. Make the result stand out so the user can quickly understand the outcome.

**Example:**
```
---

**Summary:**
- Implemented `SlotService` in `src/core/Hydra.Store/Services/SlotService.cs`
- Added `GET /slots/{id}/state` endpoint to llama.cpp fork
- Fixed: n_tokens guard in Coordinator (must be > n_past)
- Pending: integration test for cross-GPU migration
```
