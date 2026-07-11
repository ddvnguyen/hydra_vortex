# LLM Stack Runbook
## Bifrost + Langfuse + Laminar — Podman Self-Hosted

---

## Directory layout

```
llm-stack/
├── .env                        # All secrets (never commit this)
├── docker-compose.yml          # Phase 1: Bifrost + Langfuse
├── docker-compose.laminar.yml  # Phase 2: + Laminar
├── bifrost/
│   └── config.json             # Bifrost provider + plugin config
└── clickhouse/
    └── config.xml              # Enforce UTC timezone
```

---

## Prerequisites

```bash
# Install podman and podman-compose
sudo dnf install podman podman-compose    # Fedora/RHEL/Rocky
# or
sudo apt install podman                   # Ubuntu
pip install podman-compose

# Verify
podman --version
podman-compose --version
```

---

## Phase 1: Bifrost + Langfuse

### Step 1 — Secrets

The `.env` file was generated with random secrets. Review it and change
any CHANGEME values. Keep it out of git:

```bash
echo ".env" >> .gitignore
chmod 600 .env
```

### Step 2 — Podman network (rootless)

```bash
podman network create llm-net
```

### Step 3 — Boot the stack

```bash
cd llm-stack/
podman-compose up -d

# Watch startup logs (wait ~60s for all health checks to pass)
podman-compose logs -f
```

### Step 4 — Verify all containers are healthy

```bash
podman ps --format "table {{.Names}}\t{{.Status}}"

# Expected output:
# bifrost          Up X minutes
# langfuse-web     Up X minutes (healthy)
# langfuse-worker  Up X minutes
# postgres         Up X minutes (healthy)
# clickhouse       Up X minutes (healthy)
# redis            Up X minutes (healthy)
# minio            Up X minutes (healthy)
# minio-init       Exited (0)  ← one-shot bucket creator, normal
```

### Step 5 — Set up Langfuse

1. Open http://localhost:3000
2. Create your admin account (first signup = admin)
3. Create a new project e.g. "llm-local"
4. Go to **Settings → API Keys** → create a key pair
5. Copy `pk-lf-...` (public key) and `sk-lf-...` (secret key)

```bash
# Build the Base64 auth token Bifrost needs
export LANGFUSE_AUTH="Basic $(echo -n 'pk-lf-YOUR_PUBLIC_KEY:sk-lf-YOUR_SECRET_KEY' | base64)"
echo $LANGFUSE_AUTH   # keep this

# Update .env
sed -i "s|LANGFUSE_AUTH=REPLACE_WITH_BASE64_OF_PK_SK|LANGFUSE_AUTH=${LANGFUSE_AUTH}|" .env

# Restart Bifrost to pick up the new env var
podman-compose restart bifrost
```

### Step 6 — Test Bifrost → llama-server → Langfuse

```bash
# Test the OpenAI-compat endpoint through Bifrost
curl -X POST http://localhost:8088/v1/chat/completions \
  -H "Content-Type: application/json" \
  -d '{
    "model": "qwen3-35b",
    "messages": [{"role": "user", "content": "say hello"}],
    "max_tokens": 50
  }'

# Should return a response from your llama-server.
# Within ~5s a trace should appear in Langfuse UI at
# http://localhost:3000 → Traces tab.
```

### Step 7 — Verify Bifrost metrics (Prometheus)

```bash
curl http://localhost:8088/metrics
# Look for bifrost_requests_total, latency histograms, etc.
```

### Step 8 — Check Bifrost dashboard

Open http://localhost:8088/ui for the built-in real-time dashboard.

---

## Architecture (Phase 1)

```
Your App / Client
      │ POST /v1/chat/completions
      ▼
  ┌─────────┐   OpenAI-compat proxy   ┌──────────────┐
  │ Bifrost │ ──────────────────────► │ llama-server │
  │  :8088  │ ◄──────────────────────  │   :8081      │
  └────┬────┘  (P100 + 5060Ti RPC)   └──────────────┘
       │
       │ OTel OTLP HTTP
       │ POST /api/public/otel/v1/traces
       ▼
  ┌────────────────┐      ┌───────────────┐
  │ Langfuse Web   │ ───► │ Langfuse      │
  │     :3000      │      │ Worker :3030  │
  └────┬───────────┘      └───────────────┘
       │                        │
       ▼                        ▼
  ┌──────────┐  ┌────────────┐  ┌───────┐
  │ Postgres │  │ ClickHouse │  │ Redis │
  │  :5432   │  │   :8123    │  │ :6379 │
  └──────────┘  └────────────┘  └───────┘
                                     │
                                     ▼
                               ┌─────────┐
                               │  MinIO  │
                               │  :9000  │
                               └─────────┘
```

---

## Phase 2: Add Laminar

### When to add Laminar

Add Laminar when you need:
- Deep **agent trace debugging** (multi-step, nested tool calls)
- **Browser agent** session replay alongside traces
- **SQL over traces** for custom queries
- **LangGraph** execution visualization

### Step 1 — Add Laminar secrets to .env

```bash
cat >> .env << 'EOF'

# --- Laminar ---
LMNR_POSTGRES_DB=laminar
LMNR_POSTGRES_PASSWORD=laminarpass123CHANGEME
# Optional: required only for Laminar Signals (AI monitoring) feature
GOOGLE_GENERATIVE_AI_API_KEY=your_gemini_key_here
EOF
```

### Step 2 — Boot with both compose files

```bash
podman-compose \
  -f docker-compose.yml \
  -f docker-compose.laminar.yml \
  up -d

# Laminar-specific containers spin up alongside existing ones.
# New containers: lmnr-postgres, lmnr-qdrant, lmnr-app-server, lmnr-frontend
```

### Step 3 — Set up Laminar

1. Open http://localhost:5667
2. Create your account + project
3. Copy the project API key from Settings

### Step 4 — Point your SDK at Laminar

```python
# Python
from lmnr import Laminar
Laminar.initialize(
    project_api_key="your-lmnr-key",
    base_url="http://localhost",
    http_port=8000,
    grpc_port=8000,
)
```

```typescript
// TypeScript / Node
import { Laminar } from '@lmnr-ai/lmnr';
Laminar.initialize({
  projectApiKey: "your-lmnr-key",
  baseUrl: "http://localhost",
  httpPort: 8000,
});
```

### Step 5 — Dual tracing (Bifrost→Langfuse AND app→Laminar)

Both tools can run in parallel. Bifrost sends gateway-level traces to Langfuse.
Your app SDK sends function/agent traces to Laminar.

```python
from lmnr import Laminar, observe
from openai import OpenAI

# Initialize both
Laminar.initialize(project_api_key="lmnr-key", base_url="http://localhost", http_port=8000)
client = OpenAI(base_url="http://localhost:8088/v1", api_key="none")  # via Bifrost

@observe()  # Laminar traces this function
def my_agent(query: str):
    # This call goes: app → Bifrost → llama-server
    # Bifrost sends OTel span → Langfuse
    # Laminar @observe wraps the outer function → Laminar
    response = client.chat.completions.create(
        model="qwen3-35b",
        messages=[{"role": "user", "content": query}]
    )
    return response.choices[0].message.content
```

---

## Architecture (Phase 2)

```
Your App / Agent
      │
      ├── @observe() → Laminar SDK ──► lmnr-app-server:8000
      │                                      │
      │                               ┌──────┴──────┐
      │                          lmnr-postgres  lmnr-qdrant
      │                          (Laminar data)
      │
      └── OpenAI client ──► Bifrost:8088 ──► llama-server:8081
                                 │
                                 └── OTel ──► Langfuse:3000
                                                    │
                                         Postgres + ClickHouse
                                         + Redis + MinIO
```

---

## Useful Commands

```bash
# View all container logs
podman-compose logs -f

# View single service
podman logs -f bifrost

# Stop everything (Phase 1)
podman-compose down

# Stop everything (Phase 1+2)
podman-compose -f docker-compose.yml -f docker-compose.laminar.yml down

# Destroy volumes (DELETES ALL DATA)
podman-compose down -v

# Restart a single service
podman-compose restart bifrost

# Pull latest images
podman-compose pull

# Check ClickHouse is UTC
podman exec clickhouse clickhouse-client --query "SELECT timezone()"
# Must return: UTC
```

---

## Port Reference

| Host Port | Container Port | Service            | Purpose                        |
|-----------|----------------|--------------------|--------------------------------|
| 8088      | 8080           | Bifrost            | OpenAI-compat API + admin UI   |
| 3002      | 3000           | Langfuse Web       | UI, API, OTLP endpoint         |
| 3032      | 3030           | Langfuse Worker    | Background jobs (internal)     |
| 5433      | 5432           | Postgres           | Langfuse transactional DB      |
| 8124      | 8123           | ClickHouse HTTP    | OLAP queries                   |
| 9005      | 9000           | ClickHouse TCP     | Migrations (internal)          |
| 6380      | 6379           | Redis              | Cache + queue                  |
| 9002      | 9000           | MinIO API          | Blob storage (internal)        |
| 9003      | 9001           | MinIO Console      | Bucket management UI           |
| 3001      | 3000           | Grafana            | Dashboards (admin: admin)      |
| 9190      | 9090           | Prometheus         | Metrics (rootlessport conflict on :9090) |
| 5667      | —              | Laminar UI         | Agent trace debugging UI       |
| 8000      | —              | Laminar App Server | SDK OTLP endpoint              |
| 5433      | —              | Laminar Postgres   | Laminar DB (separate from LF)  |
| 6333      | —              | Qdrant             | Vector DB for Laminar          |

> **Note**: Host ports differ from container ports due to conflicts with rootlessport and existing services. Grafana datasource is provisioned to use the internal Prometheus container port `http://prometheus:9090` (not the host port :9190).

---

## Troubleshooting

**ClickHouse fails with "wrong timezone"**
```bash
podman exec clickhouse clickhouse-client --query "SELECT timezone()"
# If not UTC, check clickhouse/config.xml is mounted correctly
podman inspect clickhouse | grep -A5 Mounts
```

**Bifrost traces not appearing in Langfuse**
```bash
# Verify LANGFUSE_AUTH is set correctly
podman exec bifrost env | grep LANGFUSE
# Confirm Langfuse OTLP endpoint is reachable from Bifrost container
podman exec bifrost wget -qO- http://langfuse-web:3000/api/public/health
```

**llama-server unreachable from Bifrost**
```bash
# Test from inside the Bifrost container
podman exec bifrost wget -qO- http://host-gateway:8081/health
# If fails, replace host-gateway with your actual host IP in .env
ip route | grep default | awk '{print $3}'  # your host IP
```

**Podman rootless: host-gateway not resolving**
```bash
# Add to bifrost service in docker-compose.yml:
# extra_hosts:
#   - "host-gateway:YOUR_HOST_IP"
# Get your host IP:
hostname -I | awk '{print $1}'
```
