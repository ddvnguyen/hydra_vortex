#!/bin/bash
#
# neo4j-memory-init.sh — Foundation entity creation for Memorize & Evolve system
#
# Creates foundational entities in Neo4j if they don't exist:
# - Project entity (root project with hierarchical tasks)
# - MEMO_USER_LOOP SystemState entity (feedback loop toggle)
# - TASK-MemorizeAndEvolveDesign task entity (current design task)
#
# Usage:
#   bash neo4j-memory-init.sh init              Initialize all foundation entities
#   bash neo4j-memory-init.sh check-project     Check if Project entity exists
#   bash neo4j-memory-init.sh check-systemstate Check if SystemState entities exist
#   bash neo4j-memory-init.sh list-all          List all foundation entities
#   bash neo4j-memory-init.sh status            Show overall initialization status
#
# Connects to Neo4j via bolt protocol using cypher-shell (preferred) or Python neo4j driver.

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PROJECT_ID_SH="$SCRIPT_DIR/project-id.sh"

# ── Connection parameters ──
BOLT_URL="${NEO4J_BOLT_URL:-bolt://localhost:7687}"
DB_USER="${NEO4J_USER:-neo4j}"
DB_PASS="${NEO4J_PASSWORD:-password}"

# Resolve dynamic ProjectId from utils/project-id.sh
PROJECT_ID=""
if [ -f "$PROJECT_ID_SH" ]; then
  PROJECT_ID=$(bash "$PROJECT_ID_SH" --project-id 2>/dev/null) || PROJECT_ID=""
fi
PROJECT_ID="${PROJECT_ID:-unknown}"

# ── Helper: Run Cypher query and return JSON ──
run_cypher() {
  local cypher_query="$1"
  
  # Try cypher-shell first (Neo4j CLI)
  if command -v cypher-shell &> /dev/null; then
    RESULT=$(echo "$cypher_query" | cypher-shell -a "$BOLT_URL" "$DB_USER" "$DB_PASS" -format plain 2>/dev/null) || RESULT=""
    if [ -n "$RESULT" ]; then
      echo "$RESULT"
      return 0
    fi
  fi
  
  # Try Python neo4j driver as fallback
  if command -v python3 &> /dev/null; then
    local PYTHON_RESULT
    PYTHON_RESULT=$(python3 -c "
import sys, json
try:
    from neo4j import GraphDatabase
    uri = '$BOLT_URL'
    user = '$DB_USER'
    password = '$DB_PASS'
    query = '''$cypher_query'''
    
    def convert_value(v):
        \"\"\"Convert neo4j types to JSON-serializable Python types.\"\"\"
        if v is None:
            return None
        dt_types = ('DateTime', 'Date', 'Time', 'LocalDateTime', 'LocalTime', 'Duration')
        for dtype in dt_types:
            if type(v).__name__ == dtype:
                return str(v)
        if hasattr(type(v), '__module__') and type(v).__module__.startswith('neo4j'):
            return str(v)
        if isinstance(v, (list, tuple)):
            return [convert_value(x) for x in v]
        return v
    
    with GraphDatabase.driver(uri, auth=(user, password)) as driver:
        with driver.session() as session:
            result = session.run(query)
            records = []
            for r in result:
                record_dict = {}
                for key in r.keys():
                    record_dict[key] = convert_value(r.get(key))
                records.append(record_dict)
            print(json.dumps(records))
except Exception as e:
    print('error:' + str(e), file=sys.stderr)
" 2>/dev/null) || PYTHON_RESULT=""
    
    if [ -n "$PYTHON_RESULT" ]; then
      echo "$PYTHON_RESULT"
      return 0
    fi
  fi
  
  # No connection available — return safe default for the operation type
  return 1
}

# ── Helper: Convert cypher-shell output to JSON ──
cypher_to_json() {
  local raw_output="$1"
  
  # Check if the output is already JSON (from Python neo4j driver)
  if echo "$raw_output" | python3 -c "
import json, sys
try:
    data = json.load(sys.stdin)
    if isinstance(data, list):
        print('is_json_list')
    elif isinstance(data, dict):
        print('is_json_dict')
    else:
        print('not_json')
except:
    print('not_json')
" 2>/dev/null | grep -q '^is_json'; then
    echo "$raw_output"
    return
  fi
  
  # Otherwise, parse pipe-separated cypher-shell output
  python3 -c "
import json, sys

raw = '''$raw_output'''
lines = [l.strip() for l in raw.strip().split('\n') if l.strip()]
if not lines:
    print('[]')
    sys.exit(0)

headers = [h.strip() for h in lines[0].split('|')]
records = []
for line in lines[1:]:
    values = [v.strip() for v in line.split('|')]
    if len(values) == len(headers):
        record = {}
        for h, v in zip(headers, values):
            try:
                record[h] = json.loads(v) if v not in ('null', '') else None
            except (json.JSONDecodeError, ValueError):
                record[h] = v if v not in ('null', '') else None
        records.append(record)

print(json.dumps(records))
" 2>/dev/null || echo "[]"
}

# ── Helper: Create or ensure entity exists ──
ensure_entity() {
  local entity_name="$1"
  local entity_type="$2"
  shift 2
  
  # Build MERGE query with all properties
  local merge_props="name: '\$entity_name', type: '\$entity_type'"
  local set_clause="SET n.createdAt = datetime(), n.updatedAt = datetime()"
  
  while [ $# -gt 0 ]; do
    local key="$1"
    shift
    local value="$1"
    shift
    merge_props="$merge_props, $key: '\$${key}'"
    set_clause="$set_clause, n.${key} = '\$${key}'"
  done
  
  local cypher_query="MERGE (n:$entity_type {$merge_props}) $set_clause RETURN n.name AS name, n.type AS type, n.createdAt AS createdAt, n.updatedAt AS updatedAt"
  
  # Escape dollar signs for proper interpolation
  cypher_query=$(echo "$cypher_query" | sed "s/\$entity_name/$entity_name/g")
  
  local result
  result=$(run_cypher "$cypher_query") || { echo '{"created":false,"error":"No connection"}'; return 1; }
  
  python3 -c "
import json, sys
records = json.loads('$result') if '$result' else []
if records:
    print(json.dumps({'created': True, 'entity': records[0]}))
else:
    print('{\"created\": False}')
" 2>/dev/null || echo '{"created":false}'
}

# ── Command: init — Initialize all foundation entities ──
init_all() {
  local project_name="PROJECT-MonitoringOps"
  local domain="GLOBAL"
  
  echo "=== Memorize & Evolve Foundation Initialization ===" >&2
  echo "ProjectId: $PROJECT_ID" >&2
  echo "Neo4j URL: $BOLT_URL" >&2
  echo "" >&2
  
  # 1. Create Project entity if missing
  echo "[1/5] Checking Project entity..." >&2
  local project_result
  project_result=$(ensure_entity "$project_name" "Project" "domain" "$domain" "projectId" "$PROJECT_ID" "description" "LLM Server Monitoring Ops project with graph-based memory system")
  echo "  Result: $project_result" >&2
  
  # 2. Create MEMO_USER_LOOP SystemState entity if missing
  echo "[2/5] Checking MEMO_USER_LOOP SystemState entity..." >&2
  local memo_state_result
  memo_state_result=$(ensure_entity "MEMO_USER_LOOP" "SystemState" 
    "enabled" "true" 
    "description" "User-controlled toggle for Memorize & Evolve feedback capture"
    "updatedAt" "$(date -u +%Y-%m-%dT%H:%M:%SZ)")
  echo "  Result: $memo_state_result" >&2
  
  # 3. Create HAS_TASK relationship if missing
  echo "[3/5] Checking HAS_TASK relationship..." >&2
  local rel_cypher="MATCH (p:Project {name: '\$project_name'}), (t:Task {name: 'TASK-MemorizeAndEvolveDesign'}) WHERE NOT (p)-[:HAS_TASK]->(t) CREATE (p)-[:HAS_TASK]->(t)"
  rel_cypher=$(echo "$rel_cypher" | sed "s/\$project_name/$project_name/g")
  
  local rel_result
  rel_result=$(run_cypher "$rel_cypher" 2>/dev/null) || rel_result="skipped (no connection or already exists)"
  echo "  Result: $rel_result" >&2
  
  # 4. Create PART_OF_PROJECT relationship for existing entities
  echo "[4/5] Ensuring PART_OF_PROJECT relationships..." >&2
  local part_proj_cypher="MATCH (e) WHERE NOT (e)-[:PART_OF_PROJECT]->() AND (e.type IN ['Learning', 'Mistake', 'RuleEvolution', 'UserFeedback']) AND (e.projectId = '\$project_id' OR e.domain = 'GLOBAL') CREATE (e)-[:PART_OF_PROJECT]->(p:Project {name: '\$project_name'})"
  part_proj_cypher=$(echo "$part_proj_cypher" | sed -e "s/\$project_id/$PROJECT_ID/g" -e "s/\$project_name/$project_name/g")
  
  local part_proj_result
  part_proj_result=$(run_cypher "$part_proj_cypher" 2>/dev/null) || part_proj_result="skipped (no connection or no entities to update)"
  echo "  Result: $part_proj_result" >&2
  
  # 5. Verify all foundation entities exist
  echo "[5/5] Verification..." >&2
  local verify_cypher="MATCH (n) WHERE n.type IN ['Project', 'SystemState'] AND n.name IN ['PROJECT-MonitoringOps', 'MEMO_USER_LOOP'] RETURN n.name AS name, n.type AS type, n.enabled AS enabled, n.observations AS observations"
  
  local verify_result
  verify_result=$(run_cypher "$verify_cypher") || { echo "  WARNING: Could not connect to Neo4j for verification"; return 1; }
  
  python3 -c "
import json, sys
records = json.loads('$verify_result') if '$verify_result' else []
print(f'  Foundation entities found: {len(records)}')
for r in records:
    print(f'  • {r.get(\"name\", \"unknown\")} ({r.get(\"type\", \"unknown\")})', end='')
    if r.get('enabled') is not None:
        print(f' - enabled={r.get(\"enabled\")}', end='')
    print()
" 2>/dev/null || echo "  Could not parse verification results" >&2
  
  echo "" >&2
  echo "=== Foundation initialization complete ===" >&2
}

# ── Command: check-project — Check Project entity existence ──
check_project() {
  local project_name="PROJECT-MonitoringOps"
  
  local cypher_query="MATCH (n) WHERE n.name = '\$project_name' AND n.type = 'Project' RETURN n.name AS name, n.type AS type, n.domain AS domain, n.projectId AS projectId"
  cypher_query=$(echo "$cypher_query" | sed "s/\$project_name/$project_name/g")
  
  local result
  result=$(run_cypher "$cypher_query") || { echo '{"exists":false}'; return; }
  
  python3 -c "
import json, sys
records = json.loads('$result') if '$result' else []
if records:
    print(json.dumps({'exists': True, 'entity': records[0]}))
else:
    print('{\"exists\": False}')
" 2>/dev/null || echo '{"exists":false}'
}

# ── Command: check-systemstate — Check SystemState entities existence ──
check_systemstate() {
  local cypher_query="MATCH (n) WHERE n.type = 'SystemState' RETURN n.name AS name, n.enabled AS enabled, n.description AS description, n.updatedAt AS updatedAt"
  
  local result
  result=$(run_cypher "$cypher_query") || { echo '[]'; return; }
  
  cypher_to_json "$result" "name,enabled,description,updatedAt"
}

# ── Command: list-all — List all foundation entities ──
list_all() {
  local cypher_query="MATCH (n) WHERE n.type IN ['Project', 'SystemState', 'Task'] AND n.name IN ['PROJECT-MonitoringOps', 'MEMO_USER_LOOP', 'TASK-MemorizeAndEvolveDesign'] RETURN n.name AS name, n.type AS type, n.status AS status ORDER BY n.name"
  
  local result
  result=$(run_cypher "$cypher_query") || { echo '[]'; return; }
  
  cypher_to_json "$result" "name,type,status"
}

# ── Command: status — Show overall initialization status ──
status() {
  echo "=== Memorize & Evolve Initialization Status ===" >&2
  echo "ProjectId: $PROJECT_ID" >&2
  echo "Neo4j URL: $BOLT_URL" >&2
  echo "" >&2
  
  # Check Project
  local project_result
  project_result=$(check_project 2>/dev/null) || project_result='{"exists":false}'
  if python3 -c "import json; d=json.loads('$project_result'); exit(0 if d.get('exists') else 1)" 2>/dev/null; then
    echo "✓ Project entity: EXISTS" >&2
  else
    echo "✗ Project entity: MISSING (run 'bash $0 init')" >&2
  fi
  
  # Check MEMO_USER_LOOP
  local memo_result
  memo_result=$(check_systemstate 2>/dev/null) || memo_result="[]"
  if python3 -c "import json; d=json.loads('$memo_result'); exit(0 if len(d)>0 else 1)" 2>/dev/null; then
    echo "✓ MEMO_USER_LOOP SystemState: EXISTS" >&2
  else
    echo "✗ MEMO_USER_LOOP SystemState: MISSING (run 'bash $0 init')" >&2
  fi
  
  # Check relationships
  local rel_cypher="MATCH ()-[r:HAS_TASK]->() RETURN count(r) AS count"
  local rel_result
  rel_result=$(run_cypher "$rel_cypher" 2>/dev/null) || rel_result=""
  
  local has_rel=false
  if [ -n "$rel_result" ]; then
    has_rel=true
  fi
  
  if $has_rel; then
    echo "✓ HAS_TASK relationship: EXISTS" >&2
  else
    echo "✗ HAS_TASK relationship: MISSING (run 'bash $0 init')" >&2
  fi
  
  echo "" >&2
}

# ── Main ──
if [ $# -lt 1 ]; then
  echo "Usage: bash $0 <command>" >&2
  echo "" >&2
  echo "Commands:" >&2
  echo "  init                    Initialize all foundation entities" >&2
  echo "  check-project           Check if Project entity exists" >&2
  echo "  check-systemstate       Check if SystemState entities exist" >&2
  echo "  list-all                List all foundation entities" >&2
  echo "  status                  Show overall initialization status" >&2
  exit 1
fi

COMMAND="$1"
shift

case "$COMMAND" in
  init)
    init_all
    ;;
  
  check-project)
    check_project
    ;;
  
  check-systemstate)
    check_systemstate
    ;;
  
  list-all)
    list_all
    ;;
  
  status)
    status
    ;;
  
  *)
    echo "Unknown command: $COMMAND" >&2
    echo "Use --help for usage information" >&2
    exit 1
    ;;
esac