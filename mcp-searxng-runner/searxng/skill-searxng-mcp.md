# SearXNG MCP Server Skill

## Overview

This skill provides access to the SearXNG Meta Search Engine via MCP (Model Context Protocol). SearXNG is a free internet metasearch engine which aggregates results from various search services and databases, without tracking the user.

## Prerequisites

1. **SearXNG containers running** - Ensure the SearXNG and MCP servers are running:
   ```bash
   cd /home/ddv/Documents/sources/mcp-searxng-runner
   podman compose up -d
   ```

2. **SearXNG accessible** - Verify SearXNG is running on port 8088:
   ```bash
   curl -s -o /dev/null -w "%{http_code}" http://localhost:8088/search?q=test
   # Expected: 200
   ```

## Available Tools

### 1. `searxng_web_search`

Performs a web search using the SearXNG API.

**Parameters:**
- `query` (required): The search query string
- `pageno` (optional): Search page number (starts at 1, default: 1)
- `time_range` (optional): Time range of search — `day`, `month`, or `year`
- `language` (optional): Language code for search results (e.g., `en`, `fr`, `de`, `all`). Default is instance-dependent.
- `safesearch` (optional): Safe search filter level — `0` (None), `1` (Moderate), `2` (Strict). Default: 0

**Example Usage:**
```json
{
  "name": "searxng_web_search",
  "arguments": {
    "query": "latest AI news",
    "time_range": "month",
    "language": "en"
  }
}
```

### 2. `web_url_read`

Reads the content from a URL for further information retrieval.

**Parameters:**
- `url` (required): The URL to read
- `startChar` (optional): Starting character position for content extraction (default: 0)
- `maxLength` (optional): Maximum number of characters to return
- `section` (optional): Extract content under a specific heading (searches for heading text)
- `paragraphRange` (optional): Return specific paragraph ranges (e.g., `1-5`, `3`, `10-`)
- `readHeadings` (optional): Return only a list of headings instead of full content

**Example Usage:**
```json
{
  "name": "web_url_read",
  "arguments": {
    "url": "https://example.com/article",
    "maxLength": 5000
  }
}
```

## Configuration

### Cline MCP Setup in VS Code

To add the SearXNG MCP server to Cline in VS Code, edit the MCP settings file at:
```
~/.cline/data/settings/cline_mcp_settings.json
```

Add the following configuration to the `mcpServers` object:

```json
"searxng": {
  "transport": {
    "type": "stdio",
    "command": "podman",
    "args": ["exec", "-i", "mcp-searxng", "mcp-searxng"],
    "env": {}
  }
}
```

The full `cline_mcp_settings.json` should look like:

```json
{
  "mcpServers": {
    "searxng": {
      "transport": {
        "type": "stdio",
        "command": "podman",
        "args": ["exec", "-i", "mcp-searxng", "mcp-searxng"],
        "env": {}
      }
    }
  }
}
```

### Docker Desktop Alternative

If using Docker Desktop instead of Podman, the transport configuration would be:

```json
{
  "mcpServers": {
    "searxng": {
      "transport": {
        "type": "stdio",
        "command": "docker",
        "args": ["exec", "-i", "mcp-searxng", "mcp-searxng"],
        "env": {}
      }
    }
  }
}
```

## Troubleshooting

### SearXNG not responding

1. Check container status:
   ```bash
   podman compose ps
   ```

2. Check logs:
   ```bash
   podman logs searxng
   ```

3. Test SearXNG endpoint:
   ```bash
   curl -s -o /dev/null -w "%{http_code}" http://localhost:8088/search?q=test
   ```

### MCP server not responding

1. Check MCP container status:
   ```bash
   podman compose ps
   ```

2. Check MCP logs:
   ```bash
   podman logs mcp-searxng
   ```

3. Test MCP connection manually:
   ```bash
   podman exec -i mcp-searxng /bin/bash -c '
     echo "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/list\",\"params\":{}}" | mcp-searxng
   '
   ```

### Common Issues

- **Port mismatch**: SearXNG is configured to listen on port 8080 internally, mapped to 8088 externally via `8088:8080`
- **limiter.toml error**: If you see `TypeError: schema of /etc/searxng/limiter.toml is invalid!`, remove the limiter.toml file — the latest SearXNG version does not require it
- **CORS errors**: The SearXNG settings.yml has CORS enabled for localhost:8088 — ensure your configuration matches

## Architecture

```
┌─────────────┐     stdio      ┌──────────────┐     HTTP      ┌──────────┐
│   Cline     │ ◄─────────────►│  MCP SearXNG │ ◄────────────►│ SearXNG  │
│  (VS Code)  │    MCP Protocol│   Server     │   Port 8080   │ Container│
└─────────────┘                └──────────────┘                └──────────┘
```

## Notes

- SearXNG runs in a container with restricted capabilities (`cap_drop: ALL`, `no-new-privileges:true`)
- The MCP server runs as non-root user (`1000:1000`)
- The MCP server connects to SearXNG via the Docker network (`searxng-net`)
- Both containers are isolated in a dedicated bridge network