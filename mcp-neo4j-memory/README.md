# MCP-Neo4j-Memory - Knowledge Graph Memory Server

## Overview

This guide documents the setup, usage, and maintenance of the Neo4j MCP-Memory server - a knowledge graph memory stored in a local Neo4j database instance. The server enables storing and retrieving entities and relationships from your personal knowledge graph across sessions and conversations.

## Architecture

```
┌─────────────────┐     STDIO      ┌──────────────────┐
│  MCP Client     │ ◄────────────► │ mcp-neo4j-memory │
│  (Cline, VSCode)│                │                  │
└─────────────────┘                └────────┬─────────┘
                                            │ bolt://localhost:7687
                                            ▼
                                   ┌──────────────────┐
                                   │   Neo4j Docker   │
                                   │    Container     │
                                   │  (port 7687)     │
                                   └──────────────────┘
```

## Quick Start

### 1. Start Neo4j Database

The Neo4j database runs as a podman container with auto-restart:

```bash
# Start the container
podman start mcp-neo4j

# Verify it's running
podman ps -a --filter name=mcp-neo4j
```

### 2. Access via MCP Client

The MCP server is configured in Cline at `cline_mcp_settings.json`:

```json
{
  "mcpServers": {
    "mcp-neo4j-memory": {
      "autoApprove": [],
      "timeout": 60,
      "type": "stdio",
      "command": "/home/ddv/anaconda3/envs/neo4j-mcp/bin/mcp-neo4j-memory",
      "args": [
        "--db-url", "bolt://localhost:7687",
        "--username", "neo4j",
        "--password", "password"
      ]
    }
  }
}
```

### 3. Test Connectivity

```bash
printf '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2024-11-05","capabilities":{},"clientInfo":{"name":"test","version":"1.0.0"}}}\n{"jsonrpc":"2.0","id":2,"method":"tools/list"}\n' | /home/ddv/anaconda3/envs/neo4j-mcp/bin/mcp-neo4j-memory --db-url bolt://localhost:7687 --username neo4j --password password
```

## Available Tools

The MCP-Neo4j-Memory server provides **9 tools** for knowledge graph operations:

### Read-Only Tools

#### 1. read_graph
Read the entire knowledge graph with all entities and relationships.

**No parameters required.**

```json
// Request
{"id":1,"method":"read_graph","params":{}}

// Response
{
  "entities": [
    {"name": "John Smith", "type": "person", "observations": ["Works at Neo4j"]},
    {"name": "Neo4j Inc", "type": "company", "observations": ["Graph database company"]}
  ],
  "relations": [
    {"source": "John Smith", "target": "Neo4j Inc", "relationType": "WORKS_AT"}
  ]
}
```

#### 2. search_memories
Search for entities using fulltext search across entity names, types, and observations. Supports partial matches and multiple search terms.

**Parameters:**
- `query` (required): Fulltext search query string

```json
{"id":1,"method":"search_memories","params":{"query":"engineer software"}}
```

#### 3. find_memories_by_name
Find specific entities by their exact names, along with all relationships and connected entities.

**Parameters:**
- `names` (required): Array of exact entity names to retrieve

```json
{"id":1,"method":"find_memories_by_name","params":{"names":["Alice Johnson","Microsoft","Seattle"]}}
```

### Write Tools

#### 4. create_entities
Create multiple new entities with associated observations. If an entity already exists, observations are merged.

**Parameters:**
- `entities` (required): Array of Entity objects

Entity types: `person`, `company`, `location`, `concept`, `event`, or any custom uppercase type.

```json
{
  "id":1,
  "method":"create_entities",
  "params":{
    "entities":[
      {
        "name": "Alice Johnson",
        "type": "person",
        "observations": ["Software engineer", "Lives in Seattle"]
      },
      {
        "name": "Microsoft",
        "type": "company",
        "observations": ["Technology company", "Headquartered in Redmond, WA"]
      }
    ]
  }
}
```

#### 5. create_relations
Create directed relationships between existing entities. Both source and target must exist.

**Parameters:**
- `relations` (required): Array of Relation objects

Relation types: uppercase with underscores (e.g., `WORKS_AT`, `LIVES_IN`, `MANAGES`).

```json
{
  "id":1,
  "method":"create_relations",
  "params":{
    "relations":[
      {
        "source": "Alice Johnson",
        "target": "Microsoft",
        "relationType": "WORKS_AT"
      },
      {
        "source": "Alice Johnson",
        "target": "Seattle",
        "relationType": "LIVES_IN"
      }
    ]
  }
}
```

#### 6. add_observations
Add new observations to existing entities.

**Parameters:**
- `observations` (required): Array of ObservationAddition objects

```json
{
  "id":1,
  "method":"add_observations",
  "params":{
    "observations":[
      {
        "entityName": "Alice Johnson",
        "observations": ["Promoted to Senior Engineer"]
      }
    ]
  }
}
```

### Delete Tools

#### 7. delete_entities
Delete entities and ALL their associated relationships (irreversible).

**Parameters:**
- `entityNames` (required): Array of exact entity names to delete

```json
{
  "id":1,
  "method":"delete_entities",
  "params":{
    "entityNames": ["Old Company", "Outdated Person"]
  }
}
```

#### 8. delete_observations
Delete specific observations from existing entities. Entity remains, only the specified observation texts are removed (case-sensitive match required).

**Parameters:**
- `deletions` (required): Array of ObservationDeletion objects

```json
{
  "id":1,
  "method":"delete_observations",
  "params":{
    "deletions":[
      {
        "entityName": "Alice Johnson",
        "observations": ["Old job title"]
      }
    ]
  }
}
```

#### 9. delete_relations
Delete specific relationships between entities while keeping the entities themselves.

**Parameters:**
- `relations` (required): Array of Relation objects

```json
{
  "id":1,
  "method":"delete_relations",
  "params":{
    "relations":[
      {
        "source": "Alice Johnson",
        "target": "Old Company",
        "relationType": "WORKS_AT"
      }
    ]
  }
}
```

## Container Management

### Starting Neo4j

```bash
# Start container (with auto-restart)
podman start mcp-neo4j

# Create with explicit restart policy
podman run -d --name mcp-neo4j \
  --restart=always \
  --network host \
  -v neo4j-data:/data \
  -e NEO4J_AUTH=neo4j/password \
  -e NEO4J_apoc_export_file_enabled=true \
  -e NEO4J_apoc_import_file_enabled=true \
  -e NEO4J_apoc_import_file_use__neo4j__config=true \
  neo4j:5
```

### Stopping Neo4j

```bash
# Stop container
podman stop mcp-neo4j
```

### Restarting Neo4j

```bash
# Stop and start (useful after config changes)
podman restart mcp-neo4j
```

### Auto-Start on Boot

The container is configured with `--restart=always`, which ensures automatic restart when:
- The system reboots
- The container crashes or exits unexpectedly
- Podman daemon restarts

Verify auto-restart policy:
```bash
podman inspect mcp-neo4j | grep -A 5 RestartPolicy
# Expected output:
# "RestartPolicy": {
#     "Name": "always",
#     "MaximumRetryCount": 0
# }
```

### View Logs

```bash
# Real-time logs
podman logs -f mcp-neo4j

# Last 100 lines
podman logs --tail=100 mcp-neo4j
```

### Check Status

```bash
# Container status
podman ps -a --filter name=mcp-neo4j

# Inspect container details
podman inspect mcp-neo4j
```

## Data Persistence

Neo4j data is stored in the podman volume `neo4j-data`, which persists across container restarts. The data directory maps to:

```
/var/lib/containers/storage/volumes/neo4j-data/_data
```

### Backup Neo4j Database

```bash
# Create backup directory
mkdir -p /mnt/WorkDisk/Workplace/llm-server-monitoring/mcp-neo4j-memory/backups

# Stop container, copy data, restart
podman stop mcp-neo4j
cp -r /var/lib/containers/storage/volumes/neo4j-data/_data /mnt/WorkDisk/Workplace/llm-server-monitoring/mcp-neo4j-memory/backups/$(date +%Y%m%d_%H%M%S)
podman start mcp-neo4j
```

### Restore from Backup

```bash
# Stop container
podman stop mcp-neo4j

# Remove existing data and restore backup
rm -rf /var/lib/containers/storage/volumes/neo4j-data/_data/*
cp -r /mnt/WorkDisk/Workplace/llm-server-monitoring/mcp-neo4j-memory/backups/<backup-name>/* /var/lib/containers/storage/volumes/neo4j-data/_data/

# Start container
podman start mcp-neo4j
```

## Troubleshooting

### MCP Client Shows "Not Connected"

1. Check Neo4j container is running:
   ```bash
   podman ps -a --filter name=mcp-neo4j
   ```

2. Test direct connection:
   ```bash
   printf '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2024-11-05","capabilities":{},"clientInfo":{"name":"test","version":"1.0.0"}}}\n{"jsonrpc":"2.0","id":2,"method":"tools/list"}\n' | /home/ddv/anaconda3/envs/neo4j-mcp/bin/mcp-neo4j-memory --db-url bolt://localhost:7687 --username neo4j --password password
   ```

3. Verify Neo4j is ready (check logs):
   ```bash
   podman logs mcp-neo4j | grep "Bolt enabled"
   ```

### Container Won't Start

```bash
# Check for port conflicts
ss -tlnp | grep 7687

# Check volume exists
podman volume ls | grep neo4j-data

# Recreate volume if needed
podman volume create neo4j-data
```

### Data Not Persisting

Ensure the volume mount is correct:
```bash
podman inspect mcp-neo4j | grep -A 3 Mounts
```

## Network Configuration

- **Bolt Protocol**: `localhost:7687` (default Neo4j Bolt port)
- **HTTP API**: `localhost:7474` (web browser interface, not exposed)
- **HTTPS**: `localhost:7473` (not exposed)

The container uses `--network host`, meaning it shares the host's network namespace. If you need to expose Neo4j externally, adjust the network configuration accordingly.

## Security Notes

- Default credentials: username=`neo4j`, password=`password`
- **CRITICAL**: Change the default password after initial setup
- The container is not exposed to external networks (no port mapping)
- APOC plugin is enabled for schema inspection and advanced Cypher operations