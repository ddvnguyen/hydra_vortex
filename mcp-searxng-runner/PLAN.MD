# SearXNG MCP Project - Plan & Resolution

## Project Overview

This project is a **MCP (Model Context Protocol) server** that wraps a SearXNG instance, exposing web search capabilities to AI agents.

### Architecture
```
┌─────────────┐     STDIO     ┌──────────────────┐     HTTP     ┌──────────┐
│  AI Agent   │ ◄───────────► │ MCP SearXNG      │ ◄─────────► │ SearXNG  │
│             │               │ Runner (Node.js)  │            │ Container│
└─────────────┘               └──────────────────┘            └──────────┘
                                                                    │
                                                                    ▼
                                                           ┌──────────────────┐
                                                           │ Multiple Search  │
                                                           │ Engines (Google, │
                                                           │ Bing, DDG, etc.) │
                                                           └──────────────────┘
```

### Components
- **mcp-searxng-runner**: Node.js MCP server that communicates with AI agents via STDIO
- **SearXNG**: Self-hosted metasearch engine running in a separate container
- **limiter.toml**: Rate limiting / bot detection configuration for SearXNG
- **docker-compose.yml**: Orchestrates both containers on a shared network

### SearXNG Configuration (`searxng/settings.yml`)
- Port: 8088
- Image proxy: enabled
- Search formats: HTML + JSON
- Enabled engines: Google, Bing, DuckDuckGo
- Disabled engines: ahmia, torch, wikidata (see fixes below)

### MCP Server Features
- Search queries via `search` tool
- Multiple result formats
- Configurable engine selection

---

## Fixes Applied

### 1. Inactive Tor Engines (ahmia, torch)
**Problem**: These .onion search engines are no longer functional in recent SearXNG versions.

**Fix**: Added to `settings.yml`:
```yaml
disabled:
  engines:
    - ahmia
    - torch
```

### 2. Wikidata 403 Error
**Problem**: Wikidata API returns 403 Forbidden.

**Fix**: Added `wikidata` to disabled engines list in `settings.yml`.

### 3. Limiter.toml Schema Update
**Problem**: Custom/incomplete limiter schema with missing sections.

**Fix**: Replaced with official SearXNG schema from `searxng/searxng` repository.

### 4. Secret Key Rotation
**Problem**: Development secret key in use.

**Fix**: Generated cryptographically secure 256-bit key for production use.

---

## Deployment

### Start
```bash
podman-compose up -d
```

### Stop
```bash
podman-compose down
```

### Access
- SearXNG Web UI: `http://localhost:8088`
- MCP Server: STDIO transport (configured in AI agent settings)

### Docker Compose
```yaml
version: '3'
services:
  searxng:
    image: searxng/searxng:latest
    ports:
      - "127.0.0.1:8088:8088"
    volumes:
      - ./searxng/settings.yml:/etc/searxng/settings.yml:ro
      - ./searxng/limiter.toml:/etc/searxng/limiter.toml:ro
    networks:
      - searxng-net
    restart: unless-stopped

  mcp-searxng:
    image: ghcr.io/mcserxng-runner:latest
    environment:
      SEARXNG_URL: http://searxng:8088
    depends_on:
      - searxng
    stdin_open: true
    tty: true
    networks:
      - searxng-net
```

---

## Status: ✅ All Issues Resolved
Container starts clean with no errors in logs.