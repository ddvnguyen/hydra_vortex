Do not put any source code in this folder.
This belong to Cline Agent. We should put source code in ./memorize-and-evolve
Ensure load file by PATH absolutet config
Only Hook files allow

# Cline Hooks - Memorize & Evolve Documentation

This document describes all hooks that integrate with the Memorize & Evolve system — a memory lifecycle management workflow using Neo4j knowledge graphs.

## Overview

Hooks are Bash scripts triggered by specific Cline events. They use jq to parse JSON input from stdin and output JSON to stdout. The Memorize & Evolve edition adds `contextModification` fields that signal the AI agent what memory operations it should perform based on the event type.

### Common Patterns

- **Input**: JSON via stdin with event-specific payload
- **Output**: JSON with these keys:
  - `cancel`: boolean — whether to cancel the operation (always `false` for Memorize & Evolve hooks)
  - `contextModification`: string — human-readable instructions injected into the agent's context
  - `errorMessage`: string — error message if any
- **Escape strategy**: Use `jq -n --arg ctx "$CONTEXT_MOD"` for proper JSON escaping of multi-line strings (avoids double-escaping bugs that occurred with `sed` pre-escaping)
- **Dynamic ProjectId**: All hooks resolve ProjectId dynamically from `utils/project-id.sh` — no hardcoded values
- **Neo4j integration**: Hooks call `utils/neo4j-wip-query.sh` for graph queries (orphan detection, WIP listing, etc.)
- **Fallback**: If jq is unavailable, hooks fall back to manual JSON escaping

---

## Hook: TaskStart

**File**: `.clinerules/hooks/TaskStart`

**Trigger**: When a new task begins in Cline.

**Purpose**: Initializes memory context for the task. Checks for orphaned WIP items from previous sessions and loads relevant project memories. **Auto-archives orphaned WIP items if MEMO_ORPHAN_AUTO_ARCHIVE=true.**

**Input fields**:
- `taskStart.taskMetadata.taskId` — Current task ID
- `taskStart.taskMetadata.initialTask` — Task description
- `timestamp` — Millisecond Unix timestamp

**Memory signals emitted**:
1. **CHECK_ORPHANED_WIP** — Check for WIP items from previous sessions whose taskId doesn't match the current one
2. **LOAD_PROJECT_MEMORY_CONTEXT** — Load project-level memories relevant to this task

**Environment variables**:
- `MEMO_ORPHAN_AUTO_ARCHIVE` — Set to `true` to automatically archive orphaned WIP items with status=abandoned

**Output example**:
```json
{
  "cancel": false,
  "contextModification": "Task started at 2026-05-17T16:00:00Z (TaskId: test-task-123)\n\n[Memorize&Evol] Memory Initialization:\n- Check orphaned WIP items from previous sessions — ORPHAN CHECK COMPLETED\n[Memorize&Evol] Orphaned WIP items found (2):\n- WIP-TensorZero-Migration\n- WIP-Prometheus-Sync\n\n⚠ AUTO-ARCHIVE ENABLED: These orphaned WIP items will be archived with status=abandoned.\nAction: review above orphans — if any are still needed, cancel archive by setting MEMO_ORPHAN_AUTO_ARCHIVE=false\n- Load project memory context: LOAD_PROJECT_MEMORY_CONTEXT",
  "errorMessage": ""
}
```

---

## Hook: TaskResume

**File**: `.clinerules/hooks/TaskResume`

**Trigger**: When a task is resumed after being interrupted (e.g., session disconnect, timeout).

**Purpose**: Initializes memory context when resuming an interrupted task. Since the conversation history may be deleted on resume, this hook ensures the agent has the necessary memory context to continue without losing state. **Auto-archives orphaned WIP items if MEMO_ORPHAN_AUTO_ARCHIVE=true.**

**Input fields**:
- `taskResume.taskMetadata.taskId` — Current task ID (same as before interruption)
- `taskResume.previousState.messageCount` — Number of messages in previous session
- `taskResume.previousState.conversationHistoryDeleted` — Whether conversation was deleted on resume
- `timestamp` — Millisecond Unix timestamp

**Memory signals emitted**:
1. **CHECK_ORPHANED_WIP** — Same as TaskStart, check for WIP items from interrupted sessions
2. **LOAD_PROJECT_MEMORY_CONTEXT** — Load project memories to restore context lost during interruption

**Environment variables**:
- `MEMO_ORPHAN_AUTO_ARCHIVE` — Set to `true` to automatically archive orphaned WIP items with status=abandoned

**Output example**:
```json
{
  "cancel": false,
  "contextModification": "Task resumed at 2026-05-17T16:00:00Z (TaskId: test-task-123, previous messages: 42, conversation deleted: true)\n\n[Memorize&Evol] Memory Initialization on Task Resume:\n- Check orphaned WIP items from interrupted session — ORPHAN CHECK COMPLETED\n[Memorize&Evol] Orphaned WIP items found (1):\n- WIP-TensorZero-Migration\n\n⚠ AUTO-ARCHIVE ENABLED: These orphaned WIP items will be archived with status=abandoned.\nAction: review above orphans — if any are still needed, cancel archive by setting MEMO_ORPHAN_AUTO_ARCHIVE=false\n- Load project memory context: LOAD_PROJECT_MEMORY_CONTEXT",
  "errorMessage": ""
}
```

---

## Hook: TaskCancel

**File**: `.clinerules/hooks/TaskCancel`

**Trigger**: When a task is cancelled (manually or by the agent).

**Purpose**: Archives orphaned WIP items and preserves completed learnings. Prevents memory bloat by marking abandoned entities for the RETIRE cycle. **Auto-archives orphaned WIP items if MEMO_ORPHAN_AUTO_ARCHIVE=true.**

**Input fields**:
- `taskCancel.taskMetadata.taskId` — The cancelled task ID
- `timestamp` — Millisecond Unix timestamp

**Memory signals emitted**:
1. **ARCHIVE_ORPHANED_WIP** — Search for and mark WIP items from this session as "abandoned" using `add_observations(status=abandoned)`
2. **PRESERVE_LEARNINGS** — Ensure Learning entities from the session are preserved for future evolution

**Environment variables**:
- `MEMO_ORPHAN_AUTO_ARCHIVE` — Set to `true` to automatically archive orphaned WIP items with status=abandoned

**Agent action guidance**:
```
[Memorize&Evol] Task cancelled (TaskId: test-task-123)
[Memorize&Evol] Orphaned WIP items found (1):
- WIP-TensorZero-Migration

⚠ AUTO-ARCHIVE ENABLED: These orphaned WIP items will be archived with status=abandoned.
Action: review above orphans — if any are still needed, cancel archive by setting MEMO_ORPHAN_AUTO_ARCHIVE=false

- Preserve Learning entities from this session for future evolution
```

---

## Hook: TaskComplete

**File**: `.clinerules/hooks/TaskComplete`

**Trigger**: When a task completes successfully.

**Purpose**: Consolidates memories and triggers the memory evolution cycle. Converts WIP items to completed Task entities and runs confidence checks on related Learning entities.

**Input fields**:
- `taskComplete.taskMetadata.taskId` — The completed task ID
- `taskComplete.taskMetadata.result` — Result description from the agent
- `timestamp` — Millisecond Unix timestamp

**Memory signals emitted**:
1. **CONVERT_WIP_TO_TASK** — Search for WIP items from this session and mark them as "completed"
2. **CONSOLIDATE_MEMORY** — Signal to run confidence checks on related Learning entities

**Agent action guidance**:
```
[Memorize&Evol] Task completed (result: success)
- Agent should convert WIP items to Task entities
- Archive: search_memories(WIP) → add_observations(status=completed) for each
- Memory evolution: run confidence checks on related Learning entities
- Signal: CONSOLIDATE_MEMORY test-task-123
```

---

## Hook: PostToolUse

**File**: `.clinerules/hooks/PostToolUse`

**Trigger**: After any Neo4j MCP tool is used.

**Purpose**: Triggers memory evolution based on the specific operation performed. Provides real-time feedback to the agent about what to do with the graph state changes.

**Input fields**:
- `postToolUse.toolName` — The MCP tool name (e.g., `create_entities`, `add_observations`)
- `postToolUse.parameters` — Parameters passed to the tool
- `postToolUse.result` — Tool result string
- `postToolUse.success` — Whether the operation succeeded
- `postToolUse.executionTimeMs` — Execution duration in milliseconds

**Memory signals by tool type**:

| Tool | Signal | Agent Action |
|------|--------|--------------|
| `create_entities` | New entity created | Update project manifest log |
| `add_observations` | Observations added | Check if this triggers confidence evolution of related entities |
| `search_memories` | Memory search complete | Evaluate if results indicate entity evolution needed |
| `create_relations` | New relation created | Verify relationship integrity in graph |

**Output examples**:

For `create_entities`:
```json
{
  "cancel": false,
  "contextModification": "[Memorize&Evol] New entity created (entity: MyEntity) — agent should update project manifest log.",
  "errorMessage": ""
}
```

For `add_observations`:
```json
{
  "cancel": false,
  "contextModification": "[Memorize&Evol] Observations added (entity: Test-Entity-TestTopic) — agent should check if this triggers confidence evolution of related entities.",
  "errorMessage": ""
}
```

---

## Hook: PreToolUse

**File**: `.clinerules/hooks/PreToolUse`

**Trigger**: Before any MCP tool is used.

**Purpose**: Provides a pre-execution checkpoint for Neo4j operations. Validates entity naming conventions, checks for collisions before create, and verifies references before delete.

**Pre-tool validations**:

| Tool | Validation | Action |
|------|-----------|--------|
| `search_memories` | Auto-inject projectId filter | Ensure search results are project-scoped |
| `add_observations` | Validate entity exists | Check entity naming follows convention (DOMAIN-{Category}-{Topic}) |
| `delete_entities` | Verify no incoming relationships | Prevent orphan deletion — check references first |
| `create_entities` | Check for name collision | If entity exists, use add_observations instead |

**Output example**:
```json
{
  "cancel": false,
  "contextModification": "[Memorize&Evol] Pre-injection: Check search_memories for existing entity before creating. If exists, use add_observations instead.",
  "errorMessage": ""
}
```

---

## Hook: UserPromptSubmit

**File**: `.clinerules/hooks/UserPromptSubmit`

**Trigger**: When the user submits a prompt message.

**Purpose**: Detects memory-relevant queries in user prompts and injects context about available Neo4j graph operations. Helps the agent remember its memory capabilities when users ask about past work, configurations, or decisions.

**Memory-relevant keywords detected**:
- Temporal: "past", "previous", "last session", "before", "earlier"
- Learning: "what did we learn", "remember", "recalled", "memorize"
- Configuration: "configuration", "config", "setting", "parameter"
- Monitoring: "monitoring", "metrics", "dashboard", "alert"
- Architecture: "architecture", "design", "decision", "rationale"
- Bugs: "bug", "error", "issue", "problem", "fix"
- Progress: "progress", "status", "work", "wip", "task"

**Output example (keyword matched)**:
```json
{
  "cancel": false,
  "contextModification": "[Memorize&Evol] Memory-relevant query detected (keyword: 'monitoring').\n\nAvailable Neo4j graph operations:\n- search_memories(\"query\") — Find entities by name/type/observations\n- find_memories_by_name([\"names\"]) — Find specific entities by exact names\n- read_graph() — Read the entire knowledge graph\n- create_entities([...]) — Add new entity nodes to the graph\n- add_observations([{entityName, observations}]) — Append facts to existing entities\n- create_relations([...]) — Add relationship edges between entities\n\nTip: Graph traversal via search_memories is often more effective than searching for exact terms. Use semantic patterns like \"(?i).*monitoring.*\" for flexible matching.",
  "errorMessage": ""
}
```

---

## Hook: PreCompact

**File**: `.clinerules/hooks/PreCompact`

**Trigger**: Before the conversation is compacted/summarized.

**Purpose**: Acts as a safety net — warns about pending Neo4j operations and provides a checkpoint context so the agent can resume memory operations after compaction. Checks for active WIP items in Neo4j before allowing compaction to proceed.

**Input fields**:
- `preCompact.contextSize` — Current context size
- `preCompact.compactionStrategy` — Strategy being used (e.g., "summarize", "truncate")
- `preCompact.tokensIn` — Tokens consumed in this turn
- `preCompact.tokensOut` — Tokens generated in this turn

**Output example (WIP items active)**:
```json
{
  "cancel": false,
  "contextModification": "[Memorize&Evol] Pre-Compact Safety Net: 2 active WIP item(s) detected in Neo4j\n\nActive WIP items before compaction:\n- WIP-TensorZero-Migration\n- WIP-Prometheus-Sync\n\n⚠ WARNING: Compaction will delete conversation context. Before continuing:\n  1. Verify all pending Neo4j operations are completed (create_entities, add_observations, etc.)\n  2. If any WIP item needs further work, consider completing it before compaction\n  3. After compaction, the agent should verify WIP status via search_memories\n\nCompaction details: strategy=summarize, tokensIn=5000, tokensOut=3000",
  "errorMessage": ""
}
```

**Output example (no WIP items active)**:
```json
{
  "cancel": false,
  "contextModification": "[Memorize&Evol] Pre-Compact Safety Net: No active WIP items in Neo4j. Safe to proceed with compaction.\n\nCompaction details: strategy=summarize, contextSize=50000, tokensIn=5000, tokensOut=3000",
  "errorMessage": ""
}
```

---

## Utility Scripts

### `utils/project-id.sh`

Resolves ProjectId dynamically from AGENT.MD or generates one if not present.

**Usage**:
```bash
bash utils/project-id.sh --project-id    # Print current ProjectId
bash utils/project-id.sh --generate       # Generate new ProjectId and save to AGENT.MD
```

### `utils/neo4j-wip-query.sh`

Queries Neo4j for entity existence, references, collisions, and orphaned WIP items.

**Usage**:
```bash
# Check if entity exists in Neo4j
bash neo4j-wip-query.sh check-exists --name "ENTITY_NAME"

# Check for incoming relationships
bash neo4j-wip-query.sh check-references --name "ENTITY_NAME"

# Check for entity name collision (exact match)
bash neo4j-wip-query.sh check-collision --name "ENTITY_NAME"

# Search entities by name/observations
bash neo4j-wip-query.sh search --query "search text" [--domain DOMAIN]

# List orphaned WIP items from other tasks
bash neo4j-wip-query.sh list-orphans --task-id "TASK_ID"
```

**Environment variables**:
- `NEO4J_BOLT_URL` — Bolt connection URL (default: bolt://localhost:7687)
- `NEO4J_USER` — Username (default: neo4j)
- `NEO4J_PASSWORD` — Password (default: password)

---

## Memory Lifecycle Summary

The hooks collectively implement this memory lifecycle:

```
TaskStart/Resume ──→ CHECK_ORPHANED_WIP → AUTO_ARCHIVE (if enabled) → LOAD_MEMORY_CONTEXT
       │                                              │
       │                              [Agent works with
       │                               knowledge graph]
       │                                              │
PostToolUse ──→ Real-time evolution signals ────────→│
       │                                              │
TaskComplete ──→ CONVERT_WIP_TO_TASK → CONSOLIDATE_MEMORY
       │                                              │
TaskCancel   ──→ ARCHIVE_ORPHANED_WIP → PRESERVE_LEARNINGS
```

### Key Entity Types

| Entity | Purpose | Lifecycle State Transitions |
|--------|---------|----------------------------|
| **WIP** | Work in progress — temporary entity under creation | `pending` → `completed` / `abandoned` |
| **Task** | Completed work item with result | `active` → `completed` |
| **Learning** | Verified knowledge from completed tasks | `raw` → `verified` (confidence evolution) → `archived` |

### Key Operations

1. **Search**: `search_memories(query)` — Find entities by name/type/observations
2. **Create**: `create_entities([...])` — Add new entity nodes to the graph
3. **Observe**: `add_observations([{entityName, observations}])` — Append facts to existing entities (triggers confidence evolution)
4. **Relate**: `create_relations([...])` — Add relationship edges between entities
5. **Delete**: `delete_entities([...])`, `delete_observations([...])`, `delete_relations([...])` — Remove entities/observations/relations

---

## Common Pitfalls & Fixes

### Bug 1: Double-escaping newlines in contextModification

**Cause**: Using `sed ':a;N;$!ba;s/\n/\\n/g'` to convert newlines before passing to `jq --arg ctx`. The sed outputs literal `\n` (two characters), then jq re-encodes them as `\\n` (double backslash-n) in JSON.

**Fix**: Pass raw multi-line string directly to `jq -n --arg ctx "$CONTEXT_MOD"`. jq handles all escaping automatically.

### Bug 2: Wrong jq path for observations entityName

**Cause**: Using `.postToolUse.observations[0].entityName` instead of `.postToolUse.parameters.observations[0].entityName`. The parameters are nested inside a `parameters` object.

**Fix**: Use the correct nested path: `.postToolUse.parameters.observations[0].entityName`

### Bug 3: Missing null/empty checks in jq paths

**Cause**: Accessing `[0]` on arrays that might be empty or `null` causes jq errors.

**Mitigation**: Always use `// "unknown"` fallback pattern: e.g., `.postToolUse.parameters.entities[0].name // "unknown"`

### Bug 4: Hardcoded ProjectId in hooks

**Cause**: Using a hardcoded ProjectId string (e.g., `"p_01j8vq3k5m2n7r4t6w9z"`) instead of resolving it dynamically from AGENT.MD. This causes cross-project contamination if the same Neo4j database is shared across projects.

**Fix**: All hooks now use `utils/project-id.sh` to resolve ProjectId dynamically:
```bash
PROJECT_ID=""
if [ -f "$SCRIPT_DIR/../utils/project-id.sh" ]; then
  PROJECT_ID=$(bash "$SCRIPT_DIR/../utils/project-id.sh" --project-id 2>/dev/null) || PROJECT_ID="unknown"
fi
PROJECT_ID="${PROJECT_ID:-unknown}"
```

### Bug 5: Incorrect script path resolution in hooks

**Cause**: Using relative paths like `$SCRIPT_DIR/../../utils/neo4j-wip-query.sh` which may not resolve correctly depending on where the hook is executed from.

**Fix**: All hooks now use `$SCRIPT_DIR/../utils/neo4j-wip-query.sh` since hooks are in `.clinerules/hooks/` and utils are in `.clinerules/utils/`.