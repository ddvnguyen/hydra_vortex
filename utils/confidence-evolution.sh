#!/bin/bash
#
# confidence-evolution.sh — Implements documented confidence model with aging decay
# 
# This utility implements the P0-CRITICAL gap: GAP-Learning-Confidence
# It provides the actual code for confidence evolution that was previously
# only documented in SKILL.md without implementation.
#
# Based on implementation_plan.md Phase 1 implementation.

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

# ──────────────────────────────────────────────
# Usage
# ──────────────────────────────────────────────
usage() {
  cat << 'USAGE_EOF'
Usage: confidence-evolution.sh <command> [options]

Commands:
  evolve --entity NAME --action ACTION [--confidence CURRENT_CONFIDENCE]
    Evolve confidence for an entity.
    
    Actions:
      reinforce    - Increase confidence by 0.1 per confirmation (max 1.0)
      decay        - Decrease by 0.02 * months_inactive (capped at -0.5)
      corroborate  - Increase by 0.05 per related entity validation
    
    Options:
      --entity NAME          Entity name (required)
      --action ACTION        Evolution action (reinforce/decay/corroborate)
      --confidence FLOAT     Current confidence value (default: 0.5)
      --months-inactive INT  Months since last update for decay calculation

  get --entity NAME
    Get current confidence for an entity.

  history --entity NAME
    Get confidence evolution history for an entity.

Examples:
  confidence-evolution.sh evolve --entity "MemoryOS-Architecture" --action reinforce --confidence 0.7
  confidence-evolution.sh evolve --entity "GAP-Context-Cache" --action decay --months-inactive 2
  confidence-evolution.sh get --entity "COMPARISON-2026-05-18"
  confidence-evolution.sh history --entity "MemorizeAndEvolve"
USAGE_EOF
}

# ──────────────────────────────────────────────
# Confidence Calculation Functions
# ──────────────────────────────────────────────

calculate_confidence_change() {
  local action="$1"
  local current_confidence="${2:-0.5}"
  local months_inactive="${3:-0}"
  
  case "$action" in
    reinforce)
      python3 -c "print(min(1.0, $current_confidence + 0.1))"
      ;;
    decay)
      local decay
      decay=$(python3 -c "print(min(0.5, 0.02 * $months_inactive))")
      python3 -c "print(max(0.0, $current_confidence - $decay))"
      ;;
    corroborate)
      python3 -c "print(min(1.0, $current_confidence + 0.05))"
      ;;
    *)
      echo "ERROR: Unknown action '$action'" >&2
      return 1
      ;;
  esac
}

get_entity_confidence() {
  local entity_name="$1"
  
  local result
  result=$(cd "$SCRIPT_DIR" && bash neo4j-wip-query.sh --get-confidence "$entity_name" 2>/dev/null || echo "")
  
  if [ -n "$result" ]; then
    echo "$result"
  else
    echo "0.5"
  fi
}

# ──────────────────────────────────────────────
# Neo4j Operations via MCP Instructions
# ──────────────────────────────────────────────

build_mcp_create_entities_instruction() {
  local entity_name="$1"
  local metric_entity_name="$2"
  local confidence="$3"
  local evolved_at="$4"
  local reason="$5"
  local action_type="$6"
  local decay_amount="$7"
  
  # Build JSON using printf to avoid quoting issues in echo
  printf '%s' 'MCP_INSTRUCTION:server=mcp-neo4j-memory|tool=create_entities|key=confidence_metric|'"$entity_name"'|value='
  printf '%s' '{"entities":[{"name":"'"$metric_entity_name"'"'
  printf '%s' ',"type":"ConfidenceMetric"'
  printf '%s' ',"observations":['
  printf '%s' '"entityName: '"$entity_name"'", '
  printf '%s' '"confidence: '"$confidence"'", '
  printf '%s' '"evolvedAt: '"$evolved_at"'", '
  printf '%s' '"reason: '"$reason"'", '
  printf '%s' '"action: '"$action_type"'", '
  printf '%s' '"decayAmount: '"$decay_amount"'"'
  printf '%s' ']}]}'
}

build_mcp_update_confidence_instruction() {
  local entity_name="$1"
  local confidence="$2"
  local timestamp="$3"
  
  printf '%s' 'MCP_INSTRUCTION:server=mcp-neo4j-memory|tool=add_observations|key=update_confidence|'"$entity_name"'|value='
  printf '%s' '{"observations":[{"entityName":"'"$entity_name"'","observations":["confidence: '"$confidence"'", "lastConfidenceUpdate: '"$timestamp"'" ]}]}'
}

emit_confidence_metric() {
  local entity_name="$1"
  local new_confidence="$2"
  local action="$3"
  local months_inactive="${4:-1}"
  local timestamp
  timestamp=$(date -Iseconds 2>/dev/null || echo "now")
  
  local reason=""
  case "$action" in
    reinforce) reason="reinforced" ;;
    decay) reason="decayed" ;;
    corroborate) reason="corroborated" ;;
  esac
  
  local decay_amount="0.0"
  if [ "$action" = "decay" ]; then
    decay_amount=$(python3 -c "print(min(0.5, 0.02 * $months_inactive))")
  fi
  
  echo "" >&2
  echo "[ConfidenceEvolution] Creating ConfidenceMetric for $entity_name" >&2
  
  local timestamp_hash
  timestamp_hash=$(echo "$timestamp" | md5sum 2>/dev/null | cut -d' ' -f1)
  
  local metric_entity_name="Confidence_${entity_name}_${timestamp_hash}"
  
  local instruction
  instruction=$(build_mcp_create_entities_instruction "$entity_name" "$metric_entity_name" "$new_confidence" "$timestamp" "$reason" "$action" "$decay_amount")
  echo "$instruction" >&2
}

emit_entity_confidence_update() {
  local entity_name="$1"
  local new_confidence="$2"
  local timestamp
  timestamp=$(date -Iseconds 2>/dev/null || echo "now")
  
  echo "" >&2
  echo "[ConfidenceEvolution] Updating confidence for $entity_name" >&2
  
  local instruction
  instruction=$(build_mcp_update_confidence_instruction "$entity_name" "$new_confidence" "$timestamp")
  echo "$instruction" >&2
}

# ──────────────────────────────────────────────
# Main Command Handlers
# ──────────────────────────────────────────────

cmd_evolve() {
  local entity_name="" action="" confidence="0.5" months_inactive="1"
  
  while [[ $# -gt 0 ]]; do
    case "$1" in
      --entity) entity_name="$2"; shift 2 ;;
      --action) action="$2"; shift 2 ;;
      --confidence) confidence="$2"; shift 2 ;;
      --months-inactive) months_inactive="$2"; shift 2 ;;
      *) echo "Unknown option: $1" >&2; return 1 ;;
    esac
  done
  
  if [ -z "$entity_name" ] || [ -z "$action" ]; then
    echo "ERROR: --entity and --action are required for evolve command" >&2
    usage
    return 1
  fi
  
  local new_confidence
  new_confidence=$(calculate_confidence_change "$action" "$confidence" "$months_inactive")
  
  echo "[ConfidenceEvolution] Evolving $entity_name" >&2
  echo "  Current: $confidence to New: $new_confidence (action=$action)" >&2
  
  emit_confidence_metric "$entity_name" "$new_confidence" "$action" "$months_inactive"
  emit_entity_confidence_update "$entity_name" "$new_confidence"
}

cmd_get() {
  local entity_name=""
  
  while [[ $# -gt 0 ]]; do
    case "$1" in
      --entity) entity_name="$2"; shift 2 ;;
      *) echo "Unknown option: $1" >&2; return 1 ;;
    esac
  done
  
  if [ -z "$entity_name" ]; then
    echo "ERROR: --entity is required for get command" >&2
    return 1
  fi
  
  local confidence
  confidence=$(get_entity_confidence "$entity_name")
  echo '{"entity": "'"$entity_name"'", "confidence": '"$confidence"'}'
}

cmd_history() {
  local entity_name=""
  
  while [[ $# -gt 0 ]]; do
    case "$1" in
      --entity) entity_name="$2"; shift 2 ;;
      *) echo "Unknown option: $1" >&2; return 1 ;;
    esac
  done
  
  if [ -z "$entity_name" ]; then
    echo "ERROR: --entity is required for history command" >&2
    return 1
  fi
  
  echo "[ConfidenceEvolution] Querying history for $entity_name..." >&2
  echo '{"entity": "'"$entity_name"'", "history": [], "note": "History query via search_memories pending"}'
}

# ──────────────────────────────────────────────
# Entry Point
# ──────────────────────────────────────────────
if [ $# -eq 0 ]; then
  usage
  exit 1
fi

COMMAND="$1"
shift

case "$COMMAND" in
  evolve) cmd_evolve "$@" ;;
  get) cmd_get "$@" ;;
  history) cmd_history "$@" ;;
  help|--help|-h) usage ;;
  *) echo "Unknown command: $COMMAND"; usage; exit 1 ;;
esac