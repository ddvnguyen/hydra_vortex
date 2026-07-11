#!/bin/bash
#
# neo4j-wip-query.sh — Neo4j entity checks for Memorize & Evolve hooks
# 
# USAGE:
#   This script outputs STRUCTURED INSTRUCTIONS for the agent (Cline) to execute.
#   The agent reads these instructions and invokes corresponding MCP tool calls directly.
#
# HOOK FLOW:
#   1. Hook triggers (PreCompact, UserPromptSubmit, etc.)
#   2. Hook calls this script with desired action
#   3. Script outputs MCP execution instructions to stderr/stdout
#   4. Agent reads instructions and executes actual <use_mcp_tool> XML blocks
#   5. Agent reports results back to hook via contextModification
#
# IMPORTANT: This script does NOT execute Neo4j operations directly.
# It generates structured instructions that the agent must execute via MCP tools.

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PROJECT_ID_SH="$SCRIPT_DIR/project-id.sh"

# ── ProjectId Resolution ──
PROJECT_ID=""
if [ -f "$PROJECT_ID_SH" ]; then
  PROJECT_ID=$(bash "$PROJECT_ID_SH" --project-id 2>/dev/null) || PROJECT_ID=""
fi
PROJECT_ID="${PROJECT_ID:-unknown}"

# ──────────────────────────────────────────────
# Helper: Output MCP instruction block for agent
# ──────────────────────────────────────────────
# Format that the agent can parse and execute as <use_mcp_tool> blocks.

emit_mcp_instruction() {
  local tool_name="$1"
  shift
  local key="$1"
  shift
  local value="$1"
  
  # Output to stdout for agent consumption
  echo "MCP_INSTRUCTION:server=mcp-neo4j-memory|tool=${tool_name}|key=${key}|value=${value}"
}

emit_mcp_batch() {
  local tool_name="$1"
  shift
  local json_args="$1"
  
  # Output batch instruction for agent to execute multiple MCP calls
  echo "MCP_BATCH:server=mcp-neo4j-memory|tool=${tool_name}|args=${json_args}"
}

# ──────────────────────────────────────────────
# Action: list-all-wip (safety net — all active WIP items)
# ──────────────────────────────────────────────

list_all_wips() {
  echo "=== INSTRUCTION: Read Graph for WIP Safety Check ===" >&2
  echo "Agent should execute: <use_mcp_tool server='mcp-neo4j-memory' tool='read_graph' arguments={}></use_mcp_tool>" >&2
  echo "Then filter entities where type=WorkInProgress and status in (active, pending)" >&2
  echo "" >&2
  echo "MCP_INSTRUCTION:server=mcp-neo4j-memory|tool=read_graph|key=graph_read|value={}" >&2
}

# ──────────────────────────────────────────────
# Action: search-memories (fulltext search with domain filter)
# ──────────────────────────────────────────────

search_memories() {
  local query="$1"
  local domain="${2:-}"
  
  echo "=== INSTRUCTION: Search Neo4j Memories ===" >&2
  echo "Query: '$query'" >&2
  [ -n "$domain" ] && echo "Domain filter: $domain" >&2
  echo "" >&2
  
  if [ -n "$domain" ]; then
    echo "Agent should execute search_memories then filter results by domain" >&2
    echo "MCP_INSTRUCTION:server=mcp-neo4j-memory|tool=search_memories|key=query|value=${query}" >&2
  else
    echo "Agent should execute: <use_mcp_tool server='mcp-neo4j-memory' tool='search_memories' arguments='{\"query\": \"${query}\"}'></use_mcp_tool>" >&2
    echo "MCP_INSTRUCTION:server=mcp-neo4j-memory|tool=search_memories|key=query|value=${query}" >&2
  fi
}

# ──────────────────────────────────────────────
# Action: find-related (evolution — find entities by topic keyword)
# ──────────────────────────────────────────────

find_related() {
  local topic="$1"
  local entity_type="${2:-}"
  local domain_filter="${3:-}"
  
  echo "=== INSTRUCTION: Find Related Entities ===" >&2
  echo "Topic: '$topic'" >&2
  [ -n "$entity_type" ] && echo "Entity type filter: $entity_type" >&2
  [ -n "$domain_filter" ] && echo "Domain filter: $domain_filter" >&2
  echo "" >&2
  
  local search_query="$topic"
  [ -n "$entity_type" ] && search_query="${topic} ${entity_type}"
  
  echo "Agent should execute search_memories with query '$search_query' then filter results" >&2
  echo "MCP_INSTRUCTION:server=mcp-neo4j-memory|tool=search_memories|key=query|value=${search_query}" >&2
}

# ──────────────────────────────────────────────
# Action: check-exists (check if entity exists by name)
# ──────────────────────────────────────────────

check_exists() {
  local name="$1"
  
  echo "=== INSTRUCTION: Check Entity Existence ===" >&2
  echo "Entity name: '$name'" >&2
  echo "" >&2
  echo "Agent should execute one of:" >&2
  echo "  Option A: find_memories_by_name with exact name" >&2
  echo "  Option B: search_memories then filter by exact name match" >&2
  echo "" >&2
  echo "MCP_INSTRUCTION:server=mcp-neo4j-memory|tool=find_memories_by_name|key=names|value=[\"${name}\"]" >&2
}

# ──────────────────────────────────────────────
# Action: check-collision (check for exact name collision)
# ──────────────────────────────────────────────

check_collision() {
  local name="$1"
  
  echo "=== INSTRUCTION: Check Entity Name Collision ===" >&2
  echo "Entity name: '$name'" >&2
  echo "" >&2
  echo "Agent should execute find_memories_by_name to check for exact collision" >&2
  echo "MCP_INSTRUCTION:server=mcp-neo4j-memory|tool=find_memories_by_name|key=names|value=[\"${name}\"]" >&2
}

# ──────────────────────────────────────────────
# Action: check-references (enforcement — entity has incoming relationships?)
# ──────────────────────────────────────────────

check_references() {
  local name="$1"
  
  echo "=== INSTRUCTION: Check Entity References ===" >&2
  echo "Entity name: '$name'" >&2
  echo "" >&2
  echo "Agent should execute read_graph, then check all relations where source=entity or target=entity" >&2
  echo "MCP_INSTRUCTION:server=mcp-neo4j-memory|tool=read_graph|key=graph_read|value={}" >&2
}

# ──────────────────────────────────────────────
# Action: create-entity (create new entity in Neo4j)
# ──────────────────────────────────────────────

create_entity() {
  local name="$1"
  shift
  local type=""
  local observations=""
  
  while [ $# -gt 0 ]; do
    case "$1" in
      --type) shift; type="$1" ;;
      --observations) shift; observations="$1" ;;
    esac
    shift
  done
  
  if [ -z "$type" ]; then
    echo '{"error":"Missing required parameter: --type"}' >&2
    return 1
  fi
  
  echo "=== INSTRUCTION: Create Entity ===" >&2
  echo "Name: '$name', Type: '$type'" >&2
  [ -n "$observations" ] && echo "Observations: $observations" >&2
  echo "" >&2
  
  # Format observations as JSON array
  local obs_json="\"${observations}\""
  
  echo "Agent should execute:" >&2
  echo "<use_mcp_tool server='mcp-neo4j-memory' tool='create_entities' arguments='{\"entities\": [{\"name\": \"${name}\", \"type\": \"${type}\", \"observations\": ${obs_json}}]}'></use_mcp_tool>" >&2
  echo "MCP_INSTRUCTION:server=mcp-neo4j-memory|tool=create_entities|key=entity|value={\"entities\":[{\"name\":\"${name}\",\"type\":\"${type}\",\"observations\":[${obs_json}]}]}" >&2
}

# ──────────────────────────────────────────────
# Action: add-observation (add observation to existing entity)
# ──────────────────────────────────────────────

add_observation() {
  local name="$1"
  shift
  local observations=""
  
  while [ $# -gt 0 ]; do
    case "$1" in
      --observations) shift; observations="$1" ;;
    esac
    shift
  done
  
  if [ -z "$name" ] || [ -z "$observations" ]; then
    echo '{"error":"Missing required parameters: --name, --observations"}' >&2
    return 1
  fi
  
  echo "=== INSTRUCTION: Add Observation to Entity ===" >&2
  echo "Entity: '$name'" >&2
  echo "Observation(s): $observations" >&2
  echo "" >&2
  
  # Split observations by pipe if multiple
  local obs_array=""
  local IFS='|'
  for obs in $observations; do
    [ -n "$obs_array" ] && obs_array="${obs_array}, "
    obs_array="${obs_array}\"${obs}\""
  done
  
  echo "Agent should execute:" >&2
  echo "<use_mcp_tool server='mcp-neo4j-memory' tool='add_observations' arguments='{\"entities\": [{\"entityName\": \"${name}\", \"observations\": [${obs_array}]}]}'></use_mcp_tool>" >&2
  echo "MCP_INSTRUCTION:server=mcp-neo4j-memory|tool=add_observations|key=observation|value={\"entities\":[{\"entityName\":\"${name}\",\"observations\":[${obs_array}]}]}" >&2
}

# ──────────────────────────────────────────────
# Action: create-relation (create relationship between entities)
# ──────────────────────────────────────────────

create_relation() {
  local source="$1"
  shift
  local target=""
  local relation_type=""
  
  while [ $# -gt 0 ]; do
    case "$1" in
      --target) shift; target="$1" ;;
      --type) shift; relation_type="$1" ;;
    esac
    shift
  done
  
  if [ -z "$source" ] || [ -z "$target" ] || [ -z "$relation_type" ]; then
    echo '{"error":"Missing required parameters: --source, --target, --type"}' >&2
    return 1
  fi
  
  echo "=== INSTRUCTION: Create Relation ===" >&2
  echo "Source: '$source', Target: '$target', Type: '$relation_type'" >&2
  echo "" >&2
  
  echo "Agent should execute:" >&2
  echo "<use_mcp_tool server='mcp-neo4j-memory' tool='create_relations' arguments='{\"relations\": [{\"source\": \"${source}\", \"target\": \"${target}\", \"relationType\": \"${relation_type}\"}]}'></use_mcp_tool>" >&2
  echo "MCP_INSTRUCTION:server=mcp-neo4j-memory|tool=create_relations|key=relation|value={\"relations\":[{\"source\":\"${source}\",\"target\":\"${target}\",\"relationType\":\"${relation_type}\"}]}" >&2
}

# ──────────────────────────────────────────────
# Action: update-task-status (update Task entity status)
# ──────────────────────────────────────────────

update_task_status() {
  local entity_name="$1"
  shift
  local new_status=""
  local completed_at=""
  
  while [ $# -gt 0 ]; do
    case "$1" in
      --status)
        new_status="$2"
        shift 2
        ;;
      --completed-at)
        completed_at="$2"
        shift 2
        ;;
      *)
        shift
        ;;
    esac
  done
  
  if [ -z "$entity_name" ]; then
    echo '{"error":"Missing required parameter: --name"}' >&2
    return 1
  fi
  
  if [ -z "$new_status" ]; then
    echo '{"error":"Missing required parameter: --status"}' >&2
    return 1
  fi
  
  echo "=== INSTRUCTION: Update Task Status ===" >&2
  echo "Entity: '$entity_name'" >&2
  echo "New status: '$new_status'" >&2
  [ -n "$completed_at" ] && echo "Completed at: $completed_at" >&2
  echo "" >&2
  
  local obs_array="\"Status updated to: ${new_status}\""
  if [ -n "$completed_at" ]; then
    obs_array="${obs_array}, \"Completed at: ${completed_at}\""
  fi
  obs_array="${obs_array}, \"source: neo4j-wip-query.sh update-task-status\""
  
  echo "Agent should execute:" >&2
  echo "<use_mcp_tool server='mcp-neo4j-memory' tool='add_observations' arguments='{\"entities\": [{\"entityName\": \"${entity_name}\", \"observations\": [${obs_array}]}]}'></use_mcp_tool>" >&2
  
  # Additional status update if terminal state
  if [[ "$new_status" == "completed" || "$new_status" == "failed" || "$new_status" == "abandoned" ]]; then
    echo "" >&2
    echo "Additional observation for terminal status:" >&2
    local obs2="\"status: ${new_status}\""
    echo "<use_mcp_tool server='mcp-neo4j-memory' tool='add_observations' arguments='{\"entities\": [{\"entityName\": \"${entity_name}\", \"observations\": [${obs2}]}]}'></use_mcp_tool>" >&2
  fi
  
  echo "MCP_INSTRUCTION:server=mcp-neo4j-memory|tool=add_observations|key=task_status|value={\"entities\":[{\"entityName\":\"${entity_name}\",\"observations\":[${obs_array}]}]}" >&2
}

# ──────────────────────────────────────────────
# Action: create-milestone (create TaskMilestone entity)
# ──────────────────────────────────────────────

create_milestone() {
  local milestone_name="$1"
  shift
  local phase=""
  local task_name=""
  local status=""
  local description=""
  
  while [ $# -gt 0 ]; do
    case "$1" in
      --phase) shift; phase="$1" ;;
      --task-name) shift; task_name="$1" ;;
      --status) shift; status="$1" ;;
      --description) shift; description="$1" ;;
      *) shift ;;
    esac
  done
  
  if [ -z "$milestone_name" ] || [ -z "$phase" ] || [ -z "$task_name" ] || [ -z "$status" ]; then
    echo '{"error":"Missing required parameters: --name, --phase, --task-name, --status"}' >&2
    return 1
  fi
  
  echo "=== INSTRUCTION: Create TaskMilestone ===" >&2
  echo "Milestone: '$milestone_name'" >&2
  echo "Phase: '$phase', Task: '$task_name', Status: '$status'" >&2
  [ -n "$description" ] && echo "Description: $description" >&2
  echo "" >&2
  
  local desc_obs="\"Phase ${phase} milestone\""
  [ -n "$description" ] && desc_obs="\"${description}\""
  
  echo "Agent should execute TWO operations:" >&2
  echo "" >&2
  echo "Step 1: Create entity" >&2
  echo "<use_mcp_tool server='mcp-neo4j-memory' tool='create_entities' arguments='{\"entities\": [{\"name\": \"${milestone_name}\", \"type\": \"TaskMilestone\", \"observations\": [\"status: ${status}\", \"phase: ${phase}\", ${desc_obs}, \"source: neo4j-wip-query.sh create-milestone\"]}]}'></use_mcp_tool>" >&2
  echo "" >&2
  echo "Step 2: Create relation" >&2
  echo "<use_mcp_tool server='mcp-neo4j-memory' tool='create_relations' arguments='{\"relations\": [{\"source\": \"${milestone_name}\", \"target\": \"${task_name}\", \"relationType\": \"LINKED_TO\"}]}'></use_mcp_tool>" >&2
}

# ──────────────────────────────────────────────
# Action: add-confidence (increment confidence on Learning entity)
# ──────────────────────────────────────────────

add_confidence() {
  local entity_name="$1"
  shift
  local increment=0.1
  
  while [ $# -gt 0 ]; do
    case "$1" in
      --increment) shift; increment="$1" ;;
      *) shift ;;
    esac
  done
  
  if [ -z "$entity_name" ]; then
    echo '{"error":"Missing required parameter: --name"}' >&2
    return 1
  fi
  
  echo "=== INSTRUCTION: Add Confidence Evolution ===" >&2
  echo "Entity: '$entity_name'" >&2
  echo "Increment: +$increment" >&2
  echo "" >&2
  
  echo "Agent should execute:" >&2
  echo "<use_mcp_tool server='mcp-neo4j-memory' tool='add_observations' arguments='{\"entities\": [{\"entityName\": \"${entity_name}\", \"observations\": [\"confidence_evolution: +${increment} (evolved by neo4j-wip-query.sh)\", \"timestamp: datetime.now().isoformat()\"]}]}'></use_mcp_tool>" >&2
  echo "MCP_INSTRUCTION:server=mcp-neo4j-memory|tool=add_observations|key=confidence|value={\"entities\":[{\"entityName\":\"${entity_name}\",\"observations\":[\"confidence_evolution: +${increment} (evolved by neo4j-wip-query.sh)\", \"timestamp: datetime.now().isoformat()\"]}]}" >&2
}

# ──────────────────────────────────────────────
# Action: load-project-context (load Project entity with child tasks)
# ──────────────────────────────────────────────

load_project_context() {
  local entity_name="$1"
  
  if [ -z "$entity_name" ]; then
    echo '{"error":"Missing required parameter: --entity-name"}' >&2
    return 1
  fi
  
  echo "=== INSTRUCTION: Load Project Context ===" >&2
  echo "Project: '$entity_name'" >&2
  echo "" >&2
  echo "Agent should execute read_graph, then:" >&2
  echo "1. Find entity where name='$entity_name' and type='Project'" >&2
  echo "2. For each relation with relationType='HAS_TASK', find target task entity" >&2
  echo "3. Return project + childTasks summary" >&2
  echo "" >&2
  echo "MCP_INSTRUCTION:server=mcp-neo4j-memory|tool=read_graph|key=project_context|value={}" >&2
}

# ──────────────────────────────────────────────
# Action: sync-from-status (sync IMPLEMENTATION_STATUS.md to Neo4j)
# ──────────────────────────────────────────────

sync_from_status() {
  local status_file="${1:-memorize-and-evolve/IMPLEMENTATION_STATUS.md}"
  
  echo "=== INSTRUCTION: Sync Implementation Status ===" >&2
  echo "Source file: $status_file" >&2
  echo "" >&2
  
  if [ ! -f "$status_file" ]; then
    echo "ERROR: Status file not found: $status_file" >&2
    return 1
  fi
  
  # Parse and output instructions for each feature line
  local entity_count=0
  while IFS='|' read -r feature status emoji details; do
    # Extract entity name from feature (e.g., "TASK-Prometheus-EndpointDiscovery" -> "TASK-Prometheus")
    local entity_name
    entity_name=$(echo "$feature" | grep -oP '^[A-Z]+-[A-Za-z0-9_-]+' || echo "")
    
    if [ -n "$entity_name" ]; then
      # Determine entity type
      local entity_type="StatusEntry"
      [[ "$entity_name" == PLAN* ]] && entity_type="PlanTask"
      [[ "$entity_name" == TASK* ]] && entity_type="Task"
      
      echo "Entity $entity_count: Create/Update '$entity_name' (type=$entity_type)" >&2
      echo "  Status: $status ($details)" >&2
      echo "MCP_INSTRUCTION:server=mcp-neo4j-memory|tool=add_observations|key=sync_status|value={\"entities\":[{\"entityName\":\"${entity_name}\",\"observations\":[\"feature: ${feature}\", \"status: ${status}\"]}]}" >&2
      entity_count=$((entity_count + 1))
    fi
  done < <(tail -n +5 "$status_file" | grep '|' || true)
  
  echo "" >&2
  echo "Total entities to sync: $entity_count" >&2
}

# ──────────────────────────────────────────────
# Main: Parse arguments
# ──────────────────────────────────────────────

case "${1:-help}" in
  # ── WIP Management ──
  orphaned-wips)
    shift
    PROJECT_ID=""
    TASKID=""
    while [ $# -gt 0 ]; do
      case "$1" in
        --project-id) shift; PROJECT_ID="$1" ;;
        --taskid) shift; TASKID="$1" ;;
      esac
      shift
    done
    echo "=== Orphaned WIP Items ===" >&2
    list_all_wips
    ;;
  active-wips)
    shift
    TASKID=""
    while [ $# -gt 0 ]; do
      case "$1" in
        --taskid) shift; TASKID="$1" ;;
      esac
      shift
    done
    echo "=== Active WIP Items ===" >&2
    list_all_wips
    ;;
  list-all-wip)
    shift
    echo "=== All Active WIP Items (Safety Net) ===" >&2
    list_all_wips
    ;;
  
  # ── Search & Query ──
  search)
    shift
    QUERY=""
    DOMAIN=""
    while [ $# -gt 0 ]; do
      case "$1" in
        --query) shift; QUERY="$1" ;;
        --domain) shift; DOMAIN="$1" ;;
      esac
      shift
    done
    if [ -z "$QUERY" ]; then
      echo "Usage: $0 search --query <query> [--domain <DOMAIN>]" >&2
      exit 1
    fi
    echo "=== Memory Search ===" >&2
    search_memories "$QUERY" "$DOMAIN"
    ;;
  list-orphans)
    shift
    TASKID=""
    while [ $# -gt 0 ]; do
      case "$1" in
        --task-id) shift; TASKID="$1" ;;
      esac
      shift
    done
    if [ -z "$TASKID" ]; then
      echo "Usage: $0 list-orphans --task-id <TASK_ID>" >&2
      exit 1
    fi
    echo "=== Orphaned WIP Items (by TaskId) ===" >&2
    list_all_wips
    ;;
  
  # ── Entity Checks ──
  check-exists)
    shift
    NAME=""
    while [ $# -gt 0 ]; do
      case "$1" in
        --name) shift; NAME="$1" ;;
      esac
      shift
    done
    if [ -z "$NAME" ]; then
      echo "Usage: $0 check-exists --name <ENTITY_NAME>" >&2
      exit 1
    fi
    echo "=== Entity Existence Check ===" >&2
    check_exists "$NAME"
    ;;
  check-collision)
    shift
    NAME=""
    while [ $# -gt 0 ]; do
      case "$1" in
        --name) shift; NAME="$1" ;;
      esac
      shift
    done
    if [ -z "$NAME" ]; then
      echo "Usage: $0 check-collision --name <ENTITY_NAME>" >&2
      exit 1
    fi
    echo "=== Entity Collision Check ===" >&2
    check_collision "$NAME"
    ;;
  check-references)
    shift
    NAME=""
    while [ $# -gt 0 ]; do
      case "$1" in
        --name) shift; NAME="$1" ;;
      esac
      shift
    done
    if [ -z "$NAME" ]; then
      echo "Usage: $0 check-references --name <ENTITY_NAME>" >&2
      exit 1
    fi
    echo "=== Entity Reference Check ===" >&2
    check_references "$NAME"
    ;;
  find-related)
    shift
    QUERY=""
    ENTITY_TYPE=""
    DOMAIN=""
    while [ $# -gt 0 ]; do
      case "$1" in
        --query) shift; QUERY="$1" ;;
        --type) shift; ENTITY_TYPE="$1" ;;
        --domain) shift; DOMAIN="$1" ;;
      esac
      shift
    done
    if [ -z "$QUERY" ]; then
      echo "Usage: $0 find-related --query <text> [--type TYPE] [--domain DOMAIN]" >&2
      exit 1
    fi
    echo "=== Find Related ===" >&2
    find_related "$QUERY" "$ENTITY_TYPE" "$DOMAIN"
    ;;
  
  # ── Entity Operations ──
  create-entity)
    shift
    NAME=""
    while [ $# -gt 0 ]; do
      case "$1" in
        --name) shift; NAME="$1" ;;
      esac
      shift
    done
    if [ -z "$NAME" ]; then
      echo "Usage: $0 create-entity --name <ENTITY_NAME> --type <TYPE> [--observations OBS]" >&2
      exit 1
    fi
    shift
    echo "=== Create Entity ===" >&2
    create_entity "$NAME" "$@"
    ;;
  add-observation)
    shift
    NAME=""
    while [ $# -gt 0 ]; do
      case "$1" in
        --name) shift; NAME="$1" ;;
      esac
      shift
    done
    if [ -z "$NAME" ]; then
      echo "Usage: $0 add-observation --name <ENTITY_NAME> --observations OBS" >&2
      exit 1
    fi
    shift
    echo "=== Add Observation ===" >&2
    add_observation "$NAME" "$@"
    ;;
  create-relation)
    shift
    SOURCE=""
    while [ $# -gt 0 ]; do
      case "$1" in
        --source) shift; SOURCE="$1" ;;
      esac
      shift
    done
    if [ -z "$SOURCE" ]; then
      echo "Usage: $0 create-relation --source <SRC> --target <TGT> --type <REL_TYPE>" >&2
      exit 1
    fi
    shift
    echo "=== Create Relation ===" >&2
    create_relation "$SOURCE" "$@"
    ;;
  
  # ── Task Status ──
  get-task-status)
    shift
    NAME=""
    while [ $# -gt 0 ]; do
      case "$1" in
        --name) shift; NAME="$1" ;;
      esac
      shift
    done
    if [ -z "$NAME" ]; then
      echo "Usage: $0 get-task-status --name <ENTITY_NAME>" >&2
      exit 1
    fi
    echo "=== Get Task Status ===" >&2
    search_memories "$NAME"
    ;;
  update-task-status)
    shift
    NAME=""
    while [ $# -gt 0 ]; do
      case "$1" in
        --name) shift; NAME="$1" ;;
      esac
      shift
    done
    if [ -z "$NAME" ]; then
      echo "Usage: $0 update-task-status --name <ENTITY_NAME> --status <completed|failed|abandoned>" >&2
      exit 1
    fi
    shift
    echo "=== Update Task Status ===" >&2
    update_task_status "$NAME" "$@"
    ;;
  
  # ── Milestone Management ──
  create-milestone)
    shift
    NAME=""
    while [ $# -gt 0 ]; do
      case "$1" in
        --name) shift; NAME="$1" ;;
      esac
      shift
    done
    if [ -z "$NAME" ]; then
      echo "Usage: $0 create-milestone --name <PHASE-X-TaskSlug> --phase <X> --task-name <TaskName> --status <completed>" >&2
      exit 1
    fi
    shift
    echo "=== Create Milestone ===" >&2
    create_milestone "$NAME" "$@"
    ;;
  
  # ── Confidence Evolution ──
  add-confidence)
    shift
    NAME=""
    INCREMENT="0.1"
    while [ $# -gt 0 ]; do
      case "$1" in
        --name) shift; NAME="$1" ;;
        --increment) shift; INCREMENT="$1" ;;
      esac
      shift
    done
    if [ -z "$NAME" ]; then
      echo "Usage: $0 add-confidence --name <ENTITY_NAME> [--increment 0.1]" >&2
      exit 1
    fi
    echo "=== Add Confidence ===" >&2
    add_confidence "$NAME" --increment "$INCREMENT"
    ;;
  
  # ── Project Context ──
  load-project-context)
    shift
    NAME=""
    while [ $# -gt 0 ]; do
      case "$1" in
        --entity-name) shift; NAME="$1" ;;
      esac
      shift
    done
    if [ -z "$NAME" ]; then
      echo "Usage: $0 load-project-context --entity-name <ENTITY_NAME>" >&2
      exit 1
    fi
    echo "=== Load Project Context ===" >&2
    load_project_context "$NAME"
    ;;
  
  # ── Status Sync (NEW) ──
  sync-from-status)
    shift
    STATUS_FILE="memorize-and-evolve/IMPLEMENTATION_STATUS.md"
    while [ $# -gt 0 ]; do
      case "$1" in
        --status-file) shift; STATUS_FILE="$1" ;;
      esac
      shift
    done
    echo "=== Sync Status to Neo4j ===" >&2
    sync_from_status "$STATUS_FILE"
    ;;
  
  # ── Help ──
  help|--help|-h)
    echo "Usage: $0 <action> [params...]"
    echo ""
    echo "NOTE: This script outputs INSTRUCTIONS for the agent (Cline) to execute."
    echo "      The agent reads these instructions and invokes actual MCP tool calls."
    echo ""
    echo "WIP Management:"
    echo "  orphaned-wips [--project-id <pid>] [--taskid <tid>]"
    echo "  active-wips --taskid <tid>"
    echo "  list-all-wip"
    echo ""
    echo "Search & Query:"
    echo "  search --query <q> [--domain <DOMAIN>]"
    echo "  find-related --query <text> [--type TYPE] [--domain DOMAIN]"
    echo ""
    echo "Entity Checks:"
    echo "  check-exists --name <ENTITY_NAME>"
    echo "  check-collision --name <ENTITY_NAME>"
    echo "  check-references --name <ENTITY_NAME>"
    echo ""
    echo "Entity Operations:"
    echo "  create-entity --name X --type Y [--observations Z]"
    echo "  add-observation --name X --observations Y"
    echo "  create-relation --source X --target Y --type Z"
    echo ""
    echo "Task Status:"
    echo "  update-task-status --name X --status Y"
    echo ""
    echo "Milestone Management:"
    echo "  create-milestone --name X --phase Y --task-name Z --status S"
    echo ""
    echo "Confidence Evolution:"
    echo "  add-confidence --name <ENTITY_NAME> [--increment N.N]"
    echo ""
    echo "Project Context:"
    echo "  load-project-context --entity-name <PROJECT-Name>"
    echo ""
    echo "Status Sync (NEW):"
    echo "  sync-from-status [--status-file PATH]"
    ;;
  *)
    echo "Unknown action: $1" >&2
    echo "Run '$0 help' for usage information." >&2
    exit 1
    ;;
esac

# ──────────────────────────────────────────────
# Exit with success — instructions have been output
# ──────────────────────────────────────────────
exit 0