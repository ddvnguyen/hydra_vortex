#!/bin/bash
# Test MCP SearXNG server with a sample JSON-RPC request
# Usage: ./scripts/test-mcp.sh

SEARXNG_URL="${SEARXNG_URL:-http://localhost:8080}"

echo "Testing MCP SearXNG server (SearXNG URL: $SEARXNG_URL)..."

# Tool call: search_web
cat <<'EOF' | mcp-searxng
{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"web_search","arguments":{"query":"test search"}}}
EOF

echo ""
echo "Done."