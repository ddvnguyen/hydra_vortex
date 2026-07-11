---
name: memorize-and-evolve
description: "Neo4j knowledge graph operations for persistent learning, work-in-progress tracking, and memory lifecycle management. Use when: storing insights from sessions, tracking WIP across tasks, or managing the memory lifecycle."
---

# Memorize & Evolve — Agent Skill Guide

---

## What This Skill Does

This skill provides structured guidance for using the **Neo4j knowledge graph** as persistent memory. It helps you:
- **Store insights** that persist across sessions (not just in-context)
- **Track work-in-progress** with proper lifecycle management
- **Evolve learnings** when new evidence emerges
- **Avoid repeating mistakes** through Mistake entity tracking

---

## Current Implementation Status

### What Works Now ✅
| Component | Description |
|-----------|-------------|
| Neo4j MCP Server | Full CRUD for entities and relationships |
| ProjectId System | Dynamic resolution via `utils/project-id.sh` |
| Foundation Entities | PROJECT-MonitoringOps, TASK-MemorizeAndEvolveDesign, MEMO_USER_LOOP |
| Entity Schema | Learning, Decision, Mistake, Task, WorkInProgress, **PlanTask**, EntityTypeDefinition types |
| Plan Tracking | 6 planned implementation tasks tracked in Neo4j (implementation_plan.md steps) |

### What's Coming in Phase 2 ⏳
- Automated Project context loading on session start
- Pre-Compact reflection hook for saving observations before context loss
- Enhanced WIP→Task conversion automation
- Confidence evolution model for Learning entities

---

## Neo4j MCP Tools Available

All operations go through the **mcp-neo4j-memory** MCP server:

```
read_graph()                    — Read entire knowledge graph
create_entities([...])          — Create new entity nodes
add_observations([...])         — Append facts to existing entities  
create_relations([...])         — Add relationship edges between entities
delete_entities([...])          — Remove entities and their relationships
delete_observations([...])      — Remove specific observations from entities
delete_relations([...])         — Remove specific relationships

search_memories("query")        — Fulltext search across entities (recommended)
find_memories_by_name(["names"]) — Find entities by exact name match
```

---

## Entity Types You'll Use

### 1. Learning — Facts, patterns, insights
```json
{
  "name": "LLM-TensorZero-Throughput-Metric",
  "type": "Learning", 
  "observations": [
    "TensorZero /metrics endpoint exposes throughput in tokens/sec",
    "Metric name format: tensorzero_inference_output_tokens_total"
  ],
  "confidence": 0.75,
  "domain": "monitoring",
  "projectId": "p_01j8vq3k5m2n7r4t6w9z"
}
```

### 2. Decision — Architectural or technical decisions
```json
{
  "name": "GLOBAL-ProjectID",
  "type": "Decision",
  "observations": ["Used p_01j8vq3k5m2n7r4t6w9z as sortable project identifier"],
  "reason": "ULID-like format provides lexicographic ordering capability"
}
```

### 3. Mistake — Bug patterns, things to avoid
```json
{
  "name": "Mistake-Disable-PG-Without-Vars",
  "type": "Mistake",
  "observations": [
    "symptom: Postgres connection errors in container logs",
    "rootCause: Disabling PG in config but environment variables still set",
    "fix: Either disable both or provide valid DB credentials"
  ]
}
```

### 4. Task — Completed or active tasks
```json
{
  "name": "TASK-MemorizeAndEvolveDesign",
  "type": "Task", 
  "status": "in_progress",
  "observations": ["Phase 1 foundation complete"],
  "startedAt": "2026-05-17T14:30:00+07:00"
}
```

### 5. WorkInProgress — Active work tracking
```json
{
  "name": "TASK-Phase-X-ComponentName",
  "type": "WorkInProgress",
  "status": "active",
  "phase": "X",
  "observations": ["Design phase started"],
  "taskId": "TASK-MemorizeAndEvolveDesign"
}
```

### 6. SystemState — User-controllable flags
```json
{
  "name": "MEMO_USER_LOOP",
  "type": "SystemState",
  "enabled": true,
  "description": "Controls whether user prompts are captured for memory"
}
```

### 7. PlanTask — Planned/design tasks (pre-implementation)
```json
{
  "name": "PLAN-MemorizeEvolvePhase1-Impl",
  "type": "PlanTask",
  "status": "planning",
  "priority": "P0",
  "observations": [
    "Description: Enhance neo4j-wip-query.sh with --update-task-status command",
    "Source: implementation_plan.md Step 1",
    "Dependencies: None",
    "Expected outcome: neo4j-wip-query.sh can update task status via Cypher queries"
  ],
  "blockedBy": ["PLAN-MemorizeEvolvePhase0-Impl"]
}
```

### 8. EntityTypeDefinition — Schema documentation for entity types
```json
{
  "name": "ENTITY-TYPE-PlanTask",
  "type": "EntityTypeDefinition",
  "observations": [
    "PlanTask: Entity type for tracking planned/design tasks before implementation starts",
    "Properties: name, status (planning/in_progress/ready_for_impl/complete), priority (P0/P1/P2)",
    "Relations: PART_OF → Project, BLOCKED_BY → PlanTask",
    "Purpose: Bridge between planning phase and actual Task entity creation"
  ]
}
```

---

## Workflow: Storing New Learning

### Step 1: Check if related knowledge exists
```
use_mcp_tool: search_memories("LLM monitoring metrics")
```

### Step 2: Add to existing or create new
```
If found → add_observations([{entityName, observations}])
If not → create_entities([{name, type, observations}])
```

### Step 3: Link related entities (optional)
```
create_relations([{source: "Learning-A", target: "Learning-B", relationType: "RELATED_TO"}])
```

---

## Workflow: Tracking WIP Across Sessions

### Start tracking
```
1. create_entities([{name: "TASK-Phase-X-Component", type: "WorkInProgress", status: "active"}])
2. add_observations([{entityName, observations: ["Started work"] }])
```

### Update progress
```
add_observations([{entityName: "TASK-Phase-X-Component", observations: ["Completed design"] }])
```

### Complete work
```
1. update-task-status --name TASK-Phase-X-Component --status completed  (via neo4j-wip-query.sh)
OR add_observations([{entityName, observations: ["status=completed"] }])
```

---

## Workflow: Tracking Planned Tasks (PlanTask)

Use this workflow when planning work that hasn't started yet. PlanTasks bridge the gap between planning and implementation.

### Register a planned task
```
1. Create PlanTask entity with status "planning":
   create_entities([{
     name: "PLAN-{FeatureName}-{Phase}",
     type: "PlanTask",
     status: "planning",
     priority: "P0/P1/P2",
     observations: ["Description", "Source: file.md Step X"]
   }])

2. Link to project:
   create_relations([{
     source: "PLAN-{FeatureName}-{Phase}",
     target: "PROJECT-MonitoringOps",
     relationType: "PART_OF"
   }])

3. Create BLOCKED_BY relations for dependencies:
   create_relations([{
     source: "PLAN-LaterTask",
     target: "PLAN-EarlierTask",
     relationType: "BLOCKED_BY"
   }])
```

### Promote to implementation (when ready)
```
1. Update status via add_observations:
   add_observations([{entityName: "PLAN-X", observations: ["status=in_progress"] }])

2. Create actual Task entity for tracking implementation:
   create_entities([{name: "TASK-FeatureName", type: "Task", status: "in_progress"}])

3. Link PlanTask to Task:
   create_relations([{
     source: "PLAN-X",
     target: "TASK-FeatureName",
     relationType: "IMPLEMENTS"
   }])
```

### Query planned tasks
```
search_memories("PlanTask planning") — Find all planned tasks
find_memories_by_name(["PLAN-X"])    — Get specific plan details
```

---

## Workflow: Learning From Mistakes

When you encounter a bug or error pattern:

1. **Capture the mistake**
   ```
   create_entities([{name: "Mistake-ShortDescription", type: "Mistake", 
     observations: ["symptom: what happened", "rootCause: why it happened", "fix: how to resolve"] }])
   ```

2. **Link to related learning** (if applicable)
   ```
   create_relations([{source: "Mistake-X", target: "Learning-Y", relationType: "REFUTES"}])
   ```

3. **Notify on task completion** — Mistake entities are automatically surfaced during PostToolUse/TaskComplete hooks

---

## Entity Naming Conventions

| Type | Format | Example |
|------|--------|---------|
| Project | `PROJECT-{ProjectName}` | `PROJECT-MonitoringOps` |
| Task | `TASK-{Description}` | `TASK-MemorizeAndEvolveDesign` |
| WIP | `TASK-{Phase}-{Component}` | `TASK-Phase-X-Gateway` |
| Learning | `{Domain}-{Topic}-{Aspect}` | `Learning-TensorZero-Throughput` |
| Decision | `GLOBAL-{Topic}` or `PROJECT-{ProjectId}-{Topic}` | `GLOBAL-ProjectID` |
| Mistake | `Mistake-{ActionThatFailed}` | `Mistake-Disable-PG-Without-Vars` |
| SystemState | `{FEATURE_NAME}` | `MEMO_USER_LOOP` |

---

## Available Utility Scripts

### Entity Operations (via MCP)
```
search_memories("query")           — Fulltext search
find_memories_by_name(["names"])   — Exact name lookup  
read_graph()                       — Read entire graph
```

### Shell Utilities (in utils/)
```bash
bash utils/project-id.sh --project-id         # Get current ProjectId
bash utils/neo4j-wip-query.sh check-exists --name "Entity"  # Check existence
bash utils/neo4j-wip-query.sh search --query "text"         # Search entities
bash utils/neo4j-system-state.sh status       # Check SystemState flag
```

---

## Important Notes

1. **Always use search_memories first** — Don't blindly create_entities; check if similar knowledge exists
2. **Include projectId in entities** — Ensures project-scoped queries work correctly
3. **Mistakes should have fix instructions** — A Mistake without a fix is just noise
4. **Confidence evolves naturally** — Start at 0.5, increment on confirmation, decrement on contradiction
5. **WIP needs taskId linkage** — Connects work items to their parent task for orphan detection

---

## Quick Reference: When You Should Use This Skill

| Scenario | Action |
|----------|--------|
| Just completed an important task | Create Task entity + Learning entities from insights |
| Encountered a bug worth remembering | Create Mistake entity with symptom/rootCause/fix |
| Made an architectural decision | Create Decision entity with reasoning |
| Starting work on a new component | Create WorkInProgress entity for tracking |
| User mentions something worth noting | Create Learning entity (if MEMO_USER_LOOP enabled) |
| Need to check past learnings | search_memories("topic") |