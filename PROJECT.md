# LLM Monitoring Stack — Implementation Plan

**Setup:** llama.cpp server (Ubuntu host, RTX 5060 Ti 16GB) + RPC node (VM, Tesla P100 16GB)  
**Goal:** Deep infrastructure observability with Bifrost gateway + Langfuse tracing, ready for cloud agent integration

> **⚠️ DEPRECATED STACK:** The original LiteLLM-based stack is documented in `docs/deprecated/`. This document reflects the current **Bifrost + Langfuse** architecture. For reference, see:
> - [Deprecated TensorZero Plan](docs/deprecated/DEPRICATED_TENSORZERO-PLAN.md) (TensorZero gateway — also deprecated)
> - Original LiteLLM phases (GPU exporters, node_exporter, RPC bandwidth monitoring — retained in docker-compose.monitoring.yml)

---

## Architecture Overview

```
┌─────────────────────────────────────────────────────────────────┐
│                        CLIENTS / AGENTS                         │
│          (local apps, cloud agents, OpenAI-compat tools)        │
└──────────────────────────┬──────────────────────────────────────┘
                           │ OpenAI API format
                           ▼
┌─────────────────────────────────────────────────────────────────┐
│                   Bifrost Gateway :8088                         │
│   • OpenAI-compat proxy     • Load balancing                    │
│   • OTel trace export       • Prom /metrics                     │
│   • RPC forwarding to P100  • Caching (in-memory/Redis)         │
└──────────────────────────┬──────────────────────────────────────┘
                           │ OpenAI-compat forward (via host-gateway)
                           ▼
┌─────────────────────────────────────────────────────────────────┐
│              llama.cpp server  :8080  (Ubuntu host)             │
│                   Gemma 4 26B MoE GGUF Q4                       │
│              --metrics --slots --tensor-split 0.75,0.25         │
└──────────────────────────┬──────────────────────────────────────┘
                           │ RPC (PCIe passthrough / VM network)
                           ▼
┌─────────────────────────────────────────────────────────────────┐
│                 RPC node VM  :50052  (Tesla P100)               │
│               rpc-server -H 0.0.0.0 -p 50052                   │
└─────────────────────────────────────────────────────────────────┘

TRACING PLANE — Bifrost OTel → Langfuse

  Bifrost Gateway :8088
       │ OTel OTLP HTTP (POST /api/public/otel/v1/traces)
       │ Authorization: env.LANGFUSE_AUTH (Base64 of pk-sk key pair)
       ▼
  ┌────────────────────────┐
  │ Langfuse Web    :3000  │
  │ UI + Ingestion API    │
  └────┬───────────────────┘
       │ Worker processes events to:
       ▼
  ┌──────────┐  ┌───────────────┐  ┌───────┐  ┌─────────┐
  │ Postgres │  │ ClickHouse    │  │ Redis │  │ MinIO   │
  │  :5432   │  │   :8123       │  │ :6379 │  │ :9000   │
  └──────────┘  └───────────────┘  └───────┘  └─────────┘

INFRASTRUCTURE PLANE — Prometheus (retained for GPU/hardware monitoring)

  Prometheus :9090 (Docker container on Ubuntu host)
    ├── llama.cpp /metrics        → PP/TG t/s, KV%, slots, queue
    ├── Bifrost /metrics          → request count, latency, overhead
    ├── nvidia_gpu_exporter host  → RTX 5060 Ti VRAM, util, temp
    ├── nvidia_gpu_exporter VM    → P100 VRAM, util, temp
    ├── node_exporter host        → CPU, RAM, network
    ├── node_exporter VM          → VM CPU, RAM, network
    └── custom RPC bandwidth      → tensor transfer monitoring

  Grafana :3001 (Docker container on Ubuntu host)
    ├── Dashboard 1: LLM Inference (llamacpp + Bifrost)
    ├── Dashboard 2: GPU Hardware (both nodes)
    ├── Dashboard 3: RPC Network Bottleneck
    └── Langfuse UI at http://localhost:3000 (separate observability plane)

NOTE: All new components use containers for easy management.
      Container-to-host networking uses `host-gateway` (Podman) or
      `host.docker.internal` (Docker).
```

---

## Quick Reference — Port Map

| Service | Port | Purpose | Runs on |
|---|---|---|---|
| Bifrost Gateway | **8088** | OpenAI-compat API + /metrics | Ubuntu host (container) |
| llama.cpp server | 8080 | LLM inference + /metrics | Ubuntu host |
| RPC node | 50052 | Tensor computation offload | P100 VM |
| Langfuse UI | **3000** | Trace viewer + API | Podman (container) |
| Postgres (Langfuse) | **5432** | Transactional DB | Podman (container) |
| ClickHouse (Langfuse) | **8123** | OLAP traces/observations | Podman (container) |
| Redis (Langfuse) | **6379** | Cache + queue | Podman (container) |
| MinIO (Langfuse) | **9000/9001** | Blob store (events) | Podman (container) |
| Prometheus | **9090** | Infrastructure metrics | Docker (container) |
| Grafana | **3001** | Visualization | Docker (container) |
| GPU exporter (host) | 9835 | RTX 5060 Ti metrics | Ubuntu host (systemd) |
| GPU exporter (VM) | 9835 | P100 metrics | P100 VM (systemd) |
| node_exporter (host) | 9100 | Host system metrics | Ubuntu host (systemd) |
| node_exporter (VM) | 9100 | VM system metrics | P100 VM (systemd) |
| **SearXNG** | **8099** | Web search MCP backend | Podman (container) |
| Neo4j Bolt | **7687** | Knowledge graph MCP (Bolt) | Podman (container) |

---

## Phase 1 — Deploy Monitoring Stack (Bifrost + Langfuse)

**Estimated time: 2 hours**

### 1.1 Prerequisites

```bash
# Install podman and podman-compose
sudo dnf install podman podman-compose    # Fedora/RHEL/Rocky
pip install podman-compose                # Alternative via pip

# Verify
podman --version
podman-compose --version
```

### 1.2 Clone or set up the stack

```bash
cd /mnt/WorkDisk/Workplace/llm-server-monitoring
cp -r monitoring-bifrost-langfuse/ llm-stack/
cd llm-stack/
```

### 1.3 Secrets management

The `.env` file contains all secrets. Review and change any CHANGEME values:

```bash
# Generate random secrets (openssl)
openssl rand -hex 32    # for NEXTAUTH_SECRET, ENCRYPTION_KEY, etc.

# Set permissions
chmod 600 .env
echo ".env" >> ../../.gitignore   # prevent committing secrets
```

### 1.4 Create Podman network

```bash
podman network create llm-net
```

### 1.5 Boot the stack (Phase 1: Bifrost + Langfuse)

```bash
cd llm-stack/
podman-compose up -d

# Watch startup logs — wait ~60s for all health checks to pass
podman-compose logs -f
```

### 1.6 Verify containers are healthy

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
# minio-init       Exited (0)    ← one-shot bucket creator, normal
```

### 1.7 Set up Langfuse

1. Open http://localhost:3000
2. Create admin account (first signup = admin)
3. Create a project e.g. "llm-local"
4. Go to **Settings → API Keys** → create key pair
5. Copy `pk-lf-...` and `sk-lf-...`

```bash
# Build Base64 auth token for Bifrost OTel plugin
export LANGFUSE_AUTH="Basic $(echo -n 'pk-lf-YOUR_PUBLIC_KEY:sk-lf-YOUR_SECRET_KEY' | base64)"

# Update .env and restart Bifrost
sed -i "s|LANGFUSE_AUTH=REPLACE_WITH_BASE64_OF_PK_SK|LANGFUSE_AUTH=${LANGFUSE_AUTH}|" .env
podman-compose restart bifrost
```

### 1.8 Test end-to-end flow

```bash
# Test the OpenAI-compat endpoint through Bifrost (host port)
curl -X POST http://localhost:8088/v1/chat/completions \
  -H "Content-Type: application/json" \
  -d '{
    "model": "qwen3-35b",
    "messages": [{"role": "user", "content": "say hello"}],
    "max_tokens": 50
  }'

# Should return a response from llama-server.
# Within ~5s, a trace should appear in Langfuse UI at http://localhost:3000 → Traces tab.
```
cod
---

## Phase 2 — Bifrost Configuration Deep Dive

### 2.1 Provider configuration (config.json)

Bifrost uses `config.json` for provider definitions. The key file is at:
`monitoring-bifrost-langfuse/bifrost/config.json`

```json
{
  "providers": [
    {
      "key": "local-llama",
      "name": "llama-server (local)",
      "provider": "openai",        // OpenAI-compatible API format
      "base_url": "http://host-gateway:8080/v1",
      "api_key": "none",
      "models": ["qwen3-35b", "qwen3-35b-a3b"],
      "load_balancing": {
        "strategy": "round-robin" // supports multiple providers per model
      },
      "timeouts": {
        "request_timeout_ms": 120000,
        "connection_timeout_ms": 5000
      }
    }
  ],

  "plugins": [
    {
      "enabled": true,
      "name": "otel",             // OTel trace export to Langfuse
      "config": {
        "service_name": "bifrost",
        "collector_url": "http://langfuse-web:3000/api/public/otel",
        "trace_type": "genai_extension",
        "protocol": "http",
        "headers": {
          "Authorization": "env.LANGFUSE_AUTH"
        }
      }
    },
    {
      "enabled": true,
      "name": "telemetry",         // Prometheus metrics at /metrics
      "config": {
        "prometheus_path": "/metrics"
      }
    },
    {
      "enabled": true,
      "name": "logging",           // Structured logging
      "config": {
        "log_level": "info"
      }
    }
  ]
}
```

### 2.2 Key Bifrost capabilities confirmed via source research

| Capability | Status | Details |
|---|---|---|
| OpenAI-compatible provider | ✅ Built-in | `lib/provider/openai.rs` — native implementation |
| OTel trace export to Langfuse | ✅ Built-in | Uses `opentelemetry-http = "0.31"` for HTTP OTLP |
| Prometheus metrics | ✅ Built-in | `/metrics` endpoint via telemetry plugin |
| Load balancing (round-robin) | ✅ Built-in | Multiple providers per model → failover |
| In-memory cache | ✅ Built-in | TTL-based caching with optional semantic cache |
| RPC forwarding to llama.cpp | ⚠️ Manual | Configure `base_url` to point to host-gateway:8080 |
| Cloud model fallback | ✅ Ready | Add additional providers in config.json |

### 2.3 Bifrost metrics at /metrics (Prometheus)

```bash
curl http://localhost:8088/metrics
# Look for: bifrost_requests_total, bifrost_request_duration_seconds, etc.
```

---

## Phase 3 — GPU Hardware Exporters

**Estimated time: 45 minutes**

### 3.1 On Ubuntu host (RTX 5060 Ti)

Install nvidia_gpu_exporter as a systemd service:

```bash
wget https://github.com/utkuozdemir/nvidia_gpu_exporter/releases/latest/download/nvidia-gpu-exporter_linux_amd64.deb
sudo dpkg -i nvidia-gpu-exporter_linux_amd64.deb
sudo systemctl enable --now nvidia_gpu_exporter
# Verify
curl -s http://localhost:9835/metrics | grep nvidia_smi_utilization_gpu
```

### 3.2 On P100 VM

Same install, same steps. Confirm the VM's GPU is visible:

```bash
nvidia-smi  # should show Tesla P100
sudo systemctl enable --now nvidia_gpu_exporter
```

Open port 9835 in the VM firewall so Prometheus on the host can scrape it:

```bash
sudo ufw allow from <HOST_IP> to any port 9835
```

### 3.3 Key GPU metrics exposed

| Metric | Relevance to your setup |
|---|---|
| `nvidia_smi_memory_used_bytes` | VRAM consumed per card — critical for split tuning |
| `nvidia_smi_utilization_gpu_ratio` | Whether P100 is sitting idle (bad tensor-split) |
| `nvidia_smi_temperature_gpu` | Thermal throttle risk |
| `nvidia_smi_power_draw_watts` | Power draw — cost awareness |
| `nvidia_smi_memory_free_bytes` | Headroom for KV cache growth |

---

## Phase 4 — Node Exporter + RPC Bandwidth Monitoring

**Estimated time: 1 hour**

### 4.1 Install node_exporter on both machines

```bash
# On both Ubuntu host and P100 VM
wget https://github.com/prometheus/node_exporter/releases/latest/download/node_exporter-*.linux-amd64.tar.gz
tar xvf node_exporter-*.tar.gz
sudo mv node_exporter-*/node_exporter /usr/local/bin/
sudo useradd -rs /bin/false node_exporter

sudo tee /etc/systemd/system/node_exporter.service <<EOF
[Unit]
Description=Node Exporter
[Service]
User=node_exporter
ExecStart=/usr/local/bin/node_exporter --collector.textfile.directory=/var/lib/node_exporter/textfile
[Install]
WantedBy=multi-user.target
EOF

sudo systemctl enable --now node_exporter
```

Open port 9100 on the VM: `sudo ufw allow from <HOST_IP> to any port 9100`

### 4.2 RPC bandwidth monitoring with textfile collector

**Identify your RPC network interface** (the interface the host uses to reach the P100 VM):

```bash
ip route get <P100_VM_IP>  # shows which interface
# likely: eth0, enp3s0, or virbr0
```

**Create the RPC bandwidth exporter script:**

```bash
sudo mkdir -p /var/lib/node_exporter/textfile
sudo tee /usr/local/bin/rpc-bandwidth-exporter.sh <<'EOF'
#!/bin/bash
IFACE="${RPC_INTERFACE:-eth0}"   # change to your RPC interface
TEXTFILE="/var/lib/node_exporter/textfile/rpc_bandwidth.prom"
INTERVAL=2

read_bytes() { awk -v iface="$1:" '$1==iface {print $2}' /proc/net/dev; }
write_bytes() { awk -v iface="$1:" '$1==iface {print $10}' /proc/net/dev; }

RX1=$(read_bytes $IFACE); TX1=$(write_bytes $IFACE)
sleep $INTERVAL
RX2=$(read_bytes $IFACE); TX2=$(write_bytes $IFACE)

RX_BPS=$(( (RX2 - RX1) / INTERVAL ))
TX_BPS=$(( (TX2 - TX1) / INTERVAL ))

cat > "$TEXTFILE" <<PROM
# HELP rpc_network_receive_bytes_per_second Bytes/sec on RPC interface
# TYPE rpc_network_receive_bytes_per_second gauge
rpc_network_receive_bytes_per_second{interface="$IFACE"} $RX_BPS
# HELP rpc_network_transmit_bytes_per_second Bytes/sec on RPC interface
# TYPE rpc_network_transmit_bytes_per_second gauge
rpc_network_transmit_bytes_per_second{interface="$IFACE"} $TX_BPS
PROM
EOF

sudo chmod +x /usr/local/bin/rpc-bandwidth-exporter.sh
```

**Run it continuously via systemd:**

```bash
sudo tee /etc/systemd/system/rpc-bandwidth-exporter.service <<EOF
[Unit]
Description=RPC Bandwidth Prometheus Exporter
After=network.target
[Service]
Type=simple
Environment=RPC_INTERFACE=eth0
ExecStart=/bin/bash -c 'while true; do /usr/local/bin/rpc-bandwidth-exporter.sh; done'
Restart=always
[Install]
WantedBy=multi-user.target
EOF

sudo systemctl enable --now rpc-bandwidth-exporter
```

---

## Phase 5 — Prometheus + Grafana Docker Compose (Infrastructure)

**Estimated time: 1 hour**

### 5.1 Update docker-compose.monitoring.yml for the new setup

Update `docker-compose.monitoring.yml` to point at Bifrost's `/metrics` instead of LiteLLM's:

```yaml
# In the scrape_configs section, update:
- job_name: bifrost
  static_configs:
    - targets: ['host.docker.internal:8088']   # Bifrost /metrics endpoint

- job_name: llamacpp
  static_configs:
    - targets: ['host.docker.internal:8080']   # llama.cpp /metrics (now on port 8080)
```

### 5.2 Grafana datasource provisioning

```yaml
apiVersion: 1
datasources:
  - name: Prometheus
    type: prometheus
    url: http://prometheus:9090
    isDefault: true
    access: proxy
```

### 5.3 Start the infrastructure stack

```bash
cd ~/llm-monitor
docker compose up -d
# Grafana: http://localhost:3001
# Prometheus: http://localhost:9090
```

---

## Phase 6 — Grafana Dashboards

**Estimated time: 30 minutes**

### 6.1 Import prebuilt dashboards

In Grafana → Dashboards → Import:

| Dashboard | Source | ID / method |
|---|---|---|
| **LLM Inference** | flox/llamacpp-monitoring | Import JSON from their repo |
| **GPU Hardware** | nvidia-gpu-metrics | ID: `14574` |
| **Node / Network** | Node Exporter Full | ID: `1860` |

### 6.2 Custom panels to add manually

After importing, add these panels to the LLM Inference dashboard:

**Bifrost request rate** (PromQL):
```
rate(bifrost_requests_total[5m])
```

**Bifrost latency overhead** (PromQL):
```
histogram_quantile(0.95, rate(bifrost_request_duration_seconds_bucket[5m]))
```

**RPC bandwidth panel**:
```
rpc_network_receive_bytes_per_second{interface="eth0"} / 1048576
```
Title: "RPC inbound MB/s (tensor data from P100)"

**GPU utilization ratio** (should be ~2.5–3x for 75/25 split):
```
nvidia_smi_utilization_gpu_ratio{node="rtx5060ti"} / nvidia_smi_utilization_gpu_ratio{node="p100"}
```

### 6.3 Alerts to configure

| Alert | Condition | Action |
|---|---|---|
| KV cache pressure | `llamacpp:kv_cache_usage_ratio > 0.80` | Warn: consider tuning |
| RPC bandwidth saturation | `rpc_network_bandwidth_utilization_ratio > 0.70` | Warn: network bottleneck |
| P100 idle | `nvidia_smi_utilization_gpu_ratio{node="p100"} < 0.10` | Info: adjust tensor-split |
| VRAM near full | `nvidia_smi_memory_free_bytes < 1073741824` | Critical: OOM risk |

---

## Phase 7 — Laminar Agent Trace Debugging (Optional — Phase 2)

**Estimated time: 1 hour**

Add Laminar when you need deep agent trace debugging, browser session replay, or LangGraph visualization.

### 7.1 Boot with both compose files

```bash
cd llm-stack/
podman-compose \
  -f docker-compose.yml \
  -f docker-compose.laminar.yml \
  up -d
```

### 7.2 Set up Laminar

1. Open http://localhost:5667
2. Create project + API key
3. In your app SDK:

```python
from lmnr import Laminar
Laminar.initialize(
    project_api_key="your-lmnr-key",
    base_url="http://localhost",
    http_port=8000,
)
```

### 7.3 Dual tracing — Bifrost→Langfuse AND app→Laminar

Both tools run in parallel. Bifrost sends gateway-level traces to Langfuse. Your app SDK sends function/agent traces to Laminar.

---

## Phase 8 — Validation Checklist

Run through this after each phase to confirm everything is wired correctly.

```bash
# Phase 1 — llama.cpp (port 8080)
curl -s http://localhost:8080/health
curl -s http://localhost:8080/metrics | grep llamacpp

# Phase 1 — Bifrost (port 8088)
curl -X POST http://localhost:8088/v1/chat/completions \
  -H "Content-Type: application/json" \
  -d '{"model":"qwen3-35b","messages":[{"role":"user","content":"hello"}]}'

# Phase 1 — Langfuse traces (check UI at http://localhost:3000)

# Phase 2 — Bifrost metrics (port 8088)
curl http://localhost:8088/metrics | grep bifrost_requests_total

# Phase 3 — GPU exporters
curl -s http://localhost:9835/metrics | grep nvidia_smi_memory_used
curl -s http://<P100_VM_IP>:9835/metrics | grep nvidia_smi_memory_used

# Phase 4 — RPC bandwidth
cat /var/lib/node_exporter/textfile/rpc_bandwidth.prom

# Phase 5 — Prometheus targets
curl -s http://localhost:9090/api/v1/targets | python3 -m json.tool | grep health

# Phase 6 — Grafana
open http://localhost:3001
```

---

## File Structure Summary

```
~/llm-server-monitoring/
├── monitoring-bifrost-langfuse/          # Current stack config
│   ├── .env                              # Master env config (never commit)
│   ├── docker-compose.yml                # Phase 1: Bifrost + Langfuse
│   ├── docker-compose.laminar.yml        # Phase 2: + Laminar
│   ├── config.json                       # Bifrost provider + plugins
│   ├── clickhouse/
│   │   └── config.xml                    # ClickHouse UTC timezone + tuning
│   └── RUNBOOK.md                        # Full deployment runbook
├── docker-compose.monitoring.yml         # Infrastructure: Prometheus + Grafana
├── llm-stack/                            # Working copy (cp from monitoring-bifrost-langfuse)
├── docs/deprecated/                      # Deprecated stack documentation
│   ├── DEPRICATED_TENSORZERO-PLAN.md     # TensorZero migration plan (deprecated)
│   └── tensorzero/                       # TensorZero local config files
├── NEW_MONITORING_STACK/                 # Source reference (keep for comparison)
│   ├── .env
│   ├── docker-compose.yml
│   ├── docker-compose.laminar.yml
│   ├── config.json
│   ├── clickhouse/config.xml
│   └── RUNBOOK.md
└── mem0/                                 # Memory agent (separate project)
```

## MCP Services — Startup & Port Reference

**Additional MCP services beyond Bifrost+Langfuse** that connect to Cline via `podman exec`.

### SearXNG + MCP-SearXNG (Web Search)

| Container | Port | Purpose |
|---|---|---|
| searxng | **8099** | SearXNG search engine UI/API |
| mcp-searxng | — | MCP server connecting to SearXNG (internal port 8080) |

**Startup commands:**
```bash
# Build mcp-searxng image
cd ~/llm-server-monitoring/mcp-searxng-runner && podman build -t mcp-searxng:latest .

# Start SearXNG (port 8099 for web access, 8080 internal)
podman run -d --name searxng \
  --network mcp-searxng-runner_searxng-net \
  --user "1000:1000" \
  --security-opt no-new-privileges:true \
  --cap-drop ALL \
  --cap-add NET_BIND_SERVICE \
  -p 8099:8080 \
  -v ./searxng/settings.yml:/etc/searxng/settings.yml:ro \
  --memory 512M --cpus 1 \
  --restart unless-stopped \
  searxng/searxng:latest

# Start MCP server (internal port 8080, no host port)
podman run -d --name mcp-searxng \
  --network mcp-searxng-runner_searxng-net \
  --user "1000:1000" \
  --security-opt no-new-privileges:true \
  --cap-drop ALL \
  -e SEARXNG_URL=http://searxng:8080 \
  --memory 256M --cpus 0.5 \
  --restart "no" \
  mcp-searxng:latest

# Verify
curl -s -o /dev/null -w "%{http_code}" http://localhost:8099/search?q=test
```

> ⚠️ **Port conflict note:** SearXNG was originally assigned port **8088** which conflicts with Bifrost Gateway. Use **8099** instead.

### Neo4j (Knowledge Graph MCP)

| Container | Port | Purpose |
|---|---|---|
| mcp-neo4j | **7687** (Bolt) | Neo4j database for MCP memory service |

**Startup commands:**
```bash
# Start neo4j container with Bolt protocol
podman run -d --name mcp-neo4j \
  --network neo4j-net \
  -e NEO4J_AUTH=neo4j/password \
  -p 7687:7687 \
  neo4j:latest

# Verify Bolt connection
podman logs mcp-neo4j | grep "Bolt enabled"
```

### MCP Settings in Cline

All MCP services connect via `podman exec`:

```json
{
  "searxng": {
    "command": "podman",
    "args": ["exec", "-i", "mcp-searxng", "mcp-searxng"]
  },
  "mcp-neo4j-memory": {
    "command": "/home/ddv/anaconda3/envs/neo4j-mcp/bin/mcp-neo4j-memory",
    "args": ["--db-url", "bolt://localhost:7687", "--username", "neo4j", "--password", "password"]
  }
}
```

---

## Cloud Agent Integration Roadmap

When ready to connect cloud agents to your local server:

1. **Any OpenAI-compatible agent** — point `base_url` to `http://<HOST_IP>:8088/v1` (Bifrost)
2. **Add cloud fallback** — add additional providers in `config.json` with their model_name + API key
3. **Load balancing** — Bifrost supports round-robin across multiple providers per model for failover
4. **Observe in Langfuse** — all traces flow through Bifrost OTel plugin → Langfuse UI

Bifrost supports any OpenAI-compatible endpoint as a provider, making it easy to add Anthropic, OpenAI, Google Gemini, AWS Bedrock, and other cloud models alongside your local llama.cpp server.

---

## Legacy: LiteLLM Architecture (Deprecated — Retained for Reference)

The original architecture used **LiteLLM** (`:4000`) as the gateway instead of Bifrost (`:8080`):

| Component | Old | New |
|---|---|---|
| LLM Gateway | LiteLLM :4000 + /metrics :4001 | **Bifrost** :8088 + /metrics :8088 |
| Observability | Prometheus/Grafana only | **Langfuse v3** :3000 (OTel tracing) + Prometheus |
| Cloud Fallback | config.yaml model_list | config.json providers array |
| Tracing | None | **OTel → Langfuse** via Bifrost OTel plugin |

To revert to LiteLLM, see the original phases in the git history or `docs/deprecated/`.

---

*Last updated: May 2026 | Setup: RTX 5060 Ti + Tesla P100 RPC | Model: Gemma 4 26B MoE*