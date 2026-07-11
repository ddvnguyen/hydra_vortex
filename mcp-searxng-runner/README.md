# MCP SearXNG Runner

A containerized MCP (Model Context Protocol) server that bridges LLM agents to the [SearXNG](https://github.com/searxng/searxng) metasearch engine, enabling AI agents to perform web searches via JSON-RPC.

## Architecture

```
┌─────────────┐     MCP (stdio)      ┌──────────────────┐     HTTP      ┌────────────┐
│  LLM Agent  │ ◄───────────────────► │  mcp-searxng     │ ◄────────────► │  SearXNG   │
│             │    JSON-RPC over      │  (Node.js)       │   JSON API   │  (Python)  │
│  (Claude,   │      stdio           │  :8090           │              │  :8080     │
│   Cursor,    │                      └──────────────────┘              └────────────┘
│   VS Code)   │                                                              
└─────────────┘
```

- **SearXNG**: Runs in its own container on port `8080`, aggregating results from Google, Bing, DuckDuckGo, etc.
- **mcp-searxng**: MCP bridge server that converts JSON-RPC tool calls to SearXNG API queries.

## Quick Start

pull image back 
docker pull searxng/searxng
podman pull searxng/searxng

### 1. Build and Start

```bash
# Build the MCP bridge image
podman build -t mcp-searxng:latest .

# Start SearXNG
podman compose up -d searxng

# Wait for SearXNG to be ready
curl -s http://localhost:8080/search?q=test -o /dev/null -w "%{http_code}"
# Should return 200

# Start MCP bridge
podman run -d --name mcp-searxng \
  -e SEARXNG_URL=http://localhost:8080 \
  --network mcp-searxng_default \
  mcp-searxng:latest
```

### 2. Test the MCP Bridge

```bash
# Direct test
cat <<'EOF' | mcp-searxng
{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"web_search","arguments":{"query":"test search"}}}
EOF

# Or use the helper script
./scripts/test-mcp.sh
```

### 3. Connect to LLM Agent

**Claude Desktop** (`~/Library/Application Support/Claude/claude_desktop_config.json` on macOS, `~/.config/Claude/claude_desktop_config.json` on Linux):

```json
{
  "mcpServers": {
    "searxng": {
      "command": "podman",
      "args": ["run", "--rm", "-e", "SEARXNG_URL=http://host.docker.internal:8080", "mcp-searxng:latest"]
    }
  }
}
```

**Cursor** (Settings → Developer → MCP Config):

```json
{
  "mcpServers": {
    "searxng": {
      "command": "podman",
      "args": ["run", "--rm", "-e", "SEARXNG_URL=http://host.docker.internal:8080", "mcp-searxng:latest"]
    }
  }
}
```

## MCP Tools

| Tool | Description |
|------|-------------|
| `web_search` | Search the web via SearXNG. Args: `query` (required), `engines` (optional, comma-separated engine names), `language` (optional, ISO 639-1 code) |

## Configuration

### SearXNG Engines

Edit `searxng/settings.yml` to enable/disable search engines:

```yaml
engines:
  - name: google
    engine: google
    disabled: false
  - name: bing
    engine: bing
    disabled: false
  - name: duckduckgo
    engine: duckduckgo
    disabled: false
```

### Available Search Engines

Common engine names: `google`, `bing`, `duckduckgo`, `wikipedia`, `yahoo`, `brave`, `startpage`, `qwant`, `ecosia`.

## Project Structure

```
├── Containerfile           # MCP bridge container image definition
├── docker-compose.yml      # Podman/Docker compose (optional)
├── .gitignore
├── README.md
├── searxng/
│   └── settings.yml        # SearXNG configuration
└── scripts/
    └── test-mcp.sh         # MCP bridge test script
```

## Security

- Containers run as non-root user (`1000:1000`)
- Capabilities dropped: `ALL`, only `NET_BIND_SERVICE` added
- No `network_mode: host`
- Config volumes mounted read-only (`:ro`)
- Resource limits set (memory, CPU)

## License

MIT