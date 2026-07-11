#!/bin/bash
#
# task-status-sync.sh — Sync IMPLEMENTATION_STATUS.md to Neo4j entities
# 
# This utility reads feature statuses from IMPLEMENTATION_STATUS.md and creates/updates
# corresponding Neo4j entities (Task, PlanTask, etc.) with their current status.
#
# IMPORTANT: This script outputs MCP instructions for the agent to execute.
# The agent reads these instructions and invokes actual <use_mcp_tool> XML blocks.
#

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PROJECT_ROOT="$(cd "$SCRIPT_DIR/../.." && pwd)"
STATUS_FILE="${1:-$PROJECT_ROOT/memorize-and-evolve/IMPLEMENTATION_STATUS.md}"

if [ ! -f "$STATUS_FILE" ]; then
  echo "ERROR: Status file not found: $STATUS_FILE" >&2
  exit 1
fi

echo "=== Task Status Sync ===" >&2
echo "Source: $STATUS_FILE" >&2
echo "" >&2

# ──────────────────────────────────────────────
# Parse IMPLEMENTATION_STATUS.md table rows
# ──────────────────────────────────────────────
# Format expected: | Feature Name | Status | Notes |
# Status values: ✅ Done, ⚠️ Partial, ❌ Not started

entity_count=0
pending_operations=""

while IFS='|' read -r feature status emoji details; do
  # Skip header/footer lines
  [[ "$feature" =~ ^[[:space:]]*-+ ]] && continue
  [[ -z "$feature" ]] && continue
  
  # Extract entity name from feature (e.g., "TASK-Prometheus-EndpointDiscovery" -> "TASK-Prometheus")
  entity_name=$(echo "$feature" | grep -oP '^[A-Z]+-[A-Za-z0-9_-]+' || echo "")
  
  if [ -z "$entity_name" ]; then
    continue
  fi
  
  # Determine entity type based on prefix
  entity_type="StatusEntry"
  [[ "$entity_name" == PLAN* ]] && entity_type="PlanTask"
  [[ "$entity_name" == TASK* ]] && entity_type="Task"
  
  # Clean up status value
  case "$status" in
    *✅*) clean_status="done" ;;
    *⚠️*) clean_status="partial" ;;
    *❌*) clean_status="not_started" ;;
    *) clean_status="$status" ;;
  esac
  
  echo "Entity $entity_count: '$entity_name' (type=$entity_type, status=$clean_status)" >&2
  echo "  Details: ${details:-N/A}" >&2
  
  # Output MCP instruction for this entity
  echo "" >&2
  echo "MCP_INSTRUCTION:server=mcp-neo4j-memory|tool=add_observations|key=sync_entity_$entity_count|value={\"entities\":[{\"entityName\":\"${entity_name}\",\"observations\":[\"feature: ${feature}\", \"status: ${clean_status}\", \"source: task-status-sync.sh\", \"timestamp: $(date -Iseconds 2>/dev/null || echo 'now')\"]}]}" >&2
  
  entity_count=$((entity_count + 1))
done < <(tail -n +5 "$STATUS_FILE" | grep '|' || true)

echo "" >&2
echo "=== Sync Complete ===" >&2
echo "Total entities processed: $entity_count" >&2
echo "" >&2
echo "=== AGENT INSTRUCTIONS ===" >&2
echo "Execute each MCP_INSTRUCTION above by converting to <use_mcp_tool> XML blocks." >&2
echo "Example:" >&2
echo "  MCP_INSTRUCTION:server=mcp-neo4j-memory|tool=add_observations|key=xyz|value={...}" >&2
echo "  becomes:" >&2
echo "  <use_mcp_tool server='mcp-neo4j-memory' tool='add_observations' arguments='{...}'></use_mcp_tool>" >&2