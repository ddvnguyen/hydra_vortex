#!/usr/bin/env bash
# setup-env.sh — Configure gh CLI with a GitHub App installation token.
#
# Usage:
#   source .env 2>/dev/null; eval $(./scripts/setup-env.sh)
#
# Dependencies: openssl, curl, jq, gh, doppler
#
# Data flow:
#   1. APP_ID / APP_INSTALLATION_ID ← GitHub Variables (gh api)
#      Fallback: set in .env or exported in shell
#   2. Private key ← Doppler Secret (GITHUB_APP_PRIVATE_KEY)
#   3. JWT → openssl RS256 → curl → GitHub API → installation token
#   4. Output: export GH_TOKEN="..."

set -euo pipefail

# ── Config ────────────────────────────────────────────────────────────
ENV_FILE="${HYDRA_ENV_FILE:-.env}"
DOPPLER_PROJECT="hydra-vortex"
DOPPLER_CONFIG="dev"
DOPPLER_SECRET_KEY="GITHUB_APP_PRIVATE_KEY"

# ── Dependency checks ─────────────────────────────────────────────────
for cmd in openssl curl jq gh doppler; do
  if ! command -v "$cmd" &>/dev/null; then
    echo "❌ Required tool '$cmd' is not installed." >&2
    exit 1
  fi
done

# Validate Doppler project and config access up front
if ! doppler secrets --project "$DOPPLER_PROJECT" --config "$DOPPLER_CONFIG" &>/dev/null; then
  echo "❌ Unable to access Doppler scope: project '$DOPPLER_PROJECT', config '$DOPPLER_CONFIG'." >&2
  echo "   Please check your network connection or run 'doppler login'." >&2
  exit 1
fi

# ── Step 1: Resolve APP_ID / APP_INSTALLATION_ID ──────────────────────
APP_ID="${APP_ID:-}"
APP_INSTALLATION_ID="${APP_INSTALLATION_ID:-}"

# Try GitHub Variables via REST API
if [ -z "$APP_ID" ]; then
  APP_ID=$(gh api "repos/:owner/:repo/actions/variables/APP_ID" --jq '.value' 2>/dev/null || echo "")
fi
if [ -z "$APP_INSTALLATION_ID" ]; then
  APP_INSTALLATION_ID=$(gh api "repos/:owner/:repo/actions/variables/APP_INSTALLATION_ID" --jq '.value' 2>/dev/null || echo "")
fi

# Fallback: source .env
if { [ -z "$APP_ID" ] || [ -z "$APP_INSTALLATION_ID" ]; } && [ -f "$ENV_FILE" ]; then
  set +a; source "$ENV_FILE" 2>/dev/null || true; set -a
fi

if [ -z "$APP_ID" ]; then
  echo "❌ APP_ID not set." >&2
  echo "   Run: gh variable set APP_ID --body \"<your-app-id>\"" >&2
  exit 1
fi

if [ -z "$APP_INSTALLATION_ID" ]; then
  echo "❌ APP_INSTALLATION_ID not set." >&2
  echo "   Run: gh variable set APP_INSTALLATION_ID --body \"<value>\"" >&2
  exit 1
fi

# Cache to .env for future runs
if [ -f "$ENV_FILE" ]; then
  if ! grep -q '^export APP_ID=' "$ENV_FILE" 2>/dev/null; then
    { echo ""; echo "# ── GitHub App (auto-synced by setup-env.sh) ─────────────"
      echo "export APP_ID=\"$APP_ID\""
      echo "export APP_INSTALLATION_ID=\"$APP_INSTALLATION_ID\""
    } >> "$ENV_FILE"
    echo "✅ Cached APP_ID / APP_INSTALLATION_ID in $ENV_FILE" >&2
  fi
fi

# ── Step 2: Fetch Private Key from Doppler ────────────────────────────
echo "🔑 Fetching key from Doppler ($DOPPLER_PROJECT > $DOPPLER_CONFIG)..." >&2

# Pulls the secret dynamically without exposing it on disk or terminal logs
PRIVATE_KEY=$(doppler secrets get "$DOPPLER_SECRET_KEY" -p "$DOPPLER_PROJECT" -c "$DOPPLER_CONFIG" --plain 2>/dev/null || echo "")

if [ -z "$PRIVATE_KEY" ]; then
  echo "❌ Failed to fetch '$DOPPLER_SECRET_KEY' from Doppler." >&2
  echo "   Verify the key exists in project '$DOPPLER_PROJECT' under config '$DOPPLER_CONFIG'." >&2
  exit 1
fi

# Format validation block
if ! echo "$PRIVATE_KEY" | head -1 | grep -q '^-*BEGIN.*PRIVATE KEY-*'; then
  echo "❌ Secret '$DOPPLER_SECRET_KEY' does not look like a valid PEM key." >&2
  exit 1
fi

# ── Step 3: Generate JWT (RS256, valid 10 min) ────────────────────────
header=$(echo -n '{"alg":"RS256","typ":"JWT"}' | openssl base64 -e -A | tr -d '=' | tr '/+' '_-')
now=$(date +%s)
iat=$((now - 60))
exp=$((now + 600))
payload=$(echo -n "{\"iat\":$iat,\"exp\":$exp,\"iss\":\"$APP_ID\"}" | openssl base64 -e -A | tr -d '=' | tr '/+' '_-')

# Process substitution passes the key in memory safely
signature=$(echo -n "$header.$payload" | openssl dgst -sha256 -sign <(echo "$PRIVATE_KEY") | openssl base64 -e -A | tr -d '=' | tr '/+' '_-')
JWT="$header.$payload.$signature"

# ── Step 4: Exchange JWT for an Installation Access Token ──────────────
TOKEN_RESPONSE=$(curl -s -X POST \
  -H "Authorization: Bearer $JWT" \
  -H "Accept: application/vnd.github+json" \
  "https://api.github.com/app/installations/$APP_INSTALLATION_ID/access_tokens")

APP_TOKEN=$(echo "$TOKEN_RESPONSE" | jq -r '.token')

if [ "$APP_TOKEN" = "null" ] || [ -z "$APP_TOKEN" ]; then
  echo "❌ Failed to fetch installation token:" >&2
  echo "$TOKEN_RESPONSE" | jq . >&2
  exit 1
fi

# ── Output ────────────────────────────────────────────────────────────
echo "export GH_TOKEN=\"$APP_TOKEN\""
echo "echo '✅ Switched gh CLI identity to GitHub App (install $APP_INSTALLATION_ID)'" >&2
