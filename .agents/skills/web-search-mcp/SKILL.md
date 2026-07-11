# Web Search MCP Server Skill

## Overview

This skill covers using the **Web Search MCP Server** for live web search and content retrieval. This MCP server provides three tools for searching the web and extracting content without requiring API keys.

## Available Tools

### 1. `full-web-search` (Primary Tool)

Search the web and fetch complete page content from top results. This is the most comprehensive web search tool — it searches the web and then follows the resulting links to extract their full page content.

**When to use:** Comprehensive research, detailed information retrieval, when you need full article content.

**Parameters:**
| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `query` | string | Yes | - | Search query to execute |
| `limit` | number | No | 5 | Number of results with full content (1-10) |
| `includeContent` | boolean | No | true | Whether to fetch full page content |
| `maxContentLength` | number | No | 5000 | Maximum characters per result content |

**Example:**
```
use_mcp_tool({
  server_name: "web-search-mcp",
  tool_name: "full-web-search",
  arguments: {
    query: "TypeScript best practices 2024",
    limit: 3
  }
})
```

### 2. `get-web-search-summaries`

Lightweight search returning only result snippets/descriptions without following links.

**When to use:** Quick lookups, when you only need brief search results, high-level overview.

**Parameters:**
| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `query` | string | Yes | - | Search query to execute |
| `limit` | number | No | 5 | Number of search results to return (1-10) |

**Example:**
```
use_mcp_tool({
  server_name: "web-search-mcp",
  tool_name: "get-web-search-summaries",
  arguments: {
    query: "latest AI news",
    limit: 3
  }
})
```

### 3. `get-single-web-page-content`

Extract full content from a single web page URL.

**When to use:** When you need detailed content from a specific URL, reading a known article or documentation page.

**Parameters:**
| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `url` | string | Yes | - | URL of the web page to extract |
| `maxContentLength` | number | No | 5000 | Maximum characters for extracted content |

**Example:**
```
use_mcp_tool({
  server_name: "web-search-mcp",
  tool_name: "get-single-web-page-content",
  arguments: {
    url: "https://example.com/article"
  }
})
```

## Supported Search Engines

The server supports multiple search engines including Bing, DuckDuckGo, Brave, Exa, Google, Baidu, CSDN, Juejin, and Startpage. The default engine is configured via the `DEFAULT_SEARCH_ENGINE` environment variable.

## Best Practices

### Choosing the Right Tool

| Goal | Tool |
|------|------|
| Deep research with full content | `full-web-search` |
| Quick overview / multiple options | `get-web-search-summaries` |
| Read a specific URL | `get-single-web-page-content` |

### Query Optimization
- Use specific, descriptive queries for better results
- Include relevant keywords and context
- Avoid overly broad searches

### Rate Limiting
- Maximum ~10 requests per minute
- Maintain reasonable search frequency
- Add delays between searches when doing bulk research

### Error Handling
- Some sites may block content extraction (both axios and browser methods can fail)
- If `get-single-web-page-content` fails for a URL, try `full-web-search` with the query instead
- Network errors, rate limiting, and CAPTCHAs can cause failures

## Setup (Server Administrator)

### Quick Start with npx
```bash
npx open-websearch@latest
```

### With Custom Configuration
```bash
DEFAULT_SEARCH_ENGINE=duckduckgo ENABLE_CORS=true npx open-websearch@latest
```

### Docker Deployment
```bash
docker run -d --name web-search -p 3000:3000 -e ENABLE_CORS=true -e CORS_ORIGIN=* ghcr.io/aas-ee/open-web-search:latest
```

### MCP Client Configuration (Claude Dev / VSCode)
```json
{
  "mcpServers": {
    "web-search": {
      "transport": {
        "type": "streamableHttp",
        "url": "http://localhost:3000/mcp"
      }
    }
  }
}
```

## Environment Variables

| Variable | Default | Description |
|----------|---------|-------------|
| `DEFAULT_SEARCH_ENGINE` | `bing` | Default search engine (bing, duckduckgo, exa, brave, etc.) |
| `ENABLE_CORS` | `false` | Enable CORS |
| `PORT` | `3000` | Server port |
| `USE_PROXY` | `false` | Enable HTTP proxy |
| `PROXY_URL` | `http://127.0.0.1:7890` | Proxy server URL |

## Response Format

All tools return structured text content:

- **`full-web-search`**: Returns search results with full extracted page content
- **`get-web-search-summaries`**: Returns result titles, URLs, and descriptions
- **`get-single-web-page-content`**: Returns page title, word count, and full content

## Troubleshooting

| Issue | Solution |
|-------|----------|
| No results returned | Check query validity, verify network connectivity |
| Content extraction fails | Try a different tool or search engine, site may block scraping |
| Rate limiting errors | Reduce request frequency, add delays between calls |
| Server not responding | Verify server is running: `curl http://localhost:3000/health` |