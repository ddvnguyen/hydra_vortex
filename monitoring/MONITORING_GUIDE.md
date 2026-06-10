# Agent Monitoring Report Guideline

Use this when asked to generate a system monitoring report for Hydra.

## 1. Collect Data

Run these commands and capture output:

### Container inventory
```bash
podman ps --format "table {{.Names}}\t{{.Image}}\t{{.CreatedAt}}\t{{.Status}}\t{{.Ports}}"
```

### Health checks
```bash
curl -s http://localhost:9000/health
curl -s http://localhost:9000/status
curl -s http://localhost:9000/metrics | grep "hydra_"
```

### Hydra.Core metrics
```bash
curl -s http://localhost:9000/metrics | grep "hydra_"
curl -s http://localhost:9501/metrics | grep "hydra_store"
```

### llama-server
```bash
curl -s http://localhost:8080/slots   # RTX
curl -s http://192.168.122.21:8086/slots  # P100
```

### Prometheus
```bash
curl -s http://localhost:9091/api/v1/targets
curl -s http://localhost:9091/api/v1/label/__name__/values
```

### GPU (DCGM)
```bash
curl -s http://localhost:9835/metrics | grep -E "DCGM_FI_DEV_GPU_UTIL|DCGM_FI_DEV_POWER|DCGM_FI_DEV_TEMP"
```

### Recent logs
```bash
podman compose logs --since=10m hydra-core | grep -v "health_ok\|HTTP Request\|connect_tcp\|send_request\|receive_response\|response_closed\|close\."
podman compose logs --since=10m llama-rtx
podman compose logs --since=10m llama-p100
```

### Git state
```bash
git log --oneline -3
git describe --tags --always --dirty
```

### Runtime versions
```bash
podman exec hydra_core_1 sh -c "dotnet --info 2>&1 | head -5"
```

---

## 2. Report File Naming

Use `report-{YYYY-MM-DD}T{HHMM}Z.md` convention so multiple reports coexist:

```
monitoring/report-2026-05-28T1206Z.md
monitoring/report-2026-05-29T0800Z.md
```

---

## 3. Report Template Sections

Each report must contain these sections:

### 1. System Identity
- Git tag, branch, commit hash
- Milestones delivered vs partial
- Runtime versions (.NET, Python, CUDA)
- Model name and hardware

### 2. Service Inventory
- Table: container name, image, ports (internal + host), runtime, status (✅/❌)
- Include infra services (Loki, Prometheus, Grafana, etc.)
- Note any natively-running services (llama-server on KVM)

### 3. Current Operating State
- Prometheus targets — all jobs listed with health status
- Core status — requests, sessions, routing stats, node health
- Core Store metrics — ops, bytes, durations
- Core agent metrics — save/restore ops, slots
- llama-server — tokens processed, slot state
- GPU — utilization, power, temperature (DCGM)
- Host — node-exporter availability

### 4. Routing Behavior
- Describe the routing algorithm (affinity → long prompt → least-loaded)
- Show observed request distribution per node
- Explain WHY distribution looks the way it does (e.g. "test client repeats same messages → affinity")
- Verify design expectations:
  - Long prompts → RTX?
  - New unique sessions → P100?
  - Repeat sessions stay on RTX?
  - Load balanced?

### 5. Problems Found
Label each with severity:

| Label | Meaning |
|-------|---------|
| 🔴 P1 | Data loss, broken UX, service unavailable |
| 🟡 P2 | Bug, missing feature, degraded experience |
| 🟡 P3 | Minor: cosmetic, missing instrumentation, edge case |
| 🔵 P4 | Info: not tested, planned work, nice-to-have |

For each problem include:
- **File:** path and line number
- **Root cause:** what's wrong
- **Impact:** what breaks or degrades
- **Evidence:** relevant log/metric excerpt

### 6. Key Metrics Snapshot
Single table of the most important numbers (requests, ops, bytes, tokens, uptime)

### 7. Recommendations
- Immediate (before next milestone)
- Short-term
- Medium-term

---

## 4. Routing Analysis Checklist

- [ ] Check session table distribution (RTX vs P100)
- [ ] Check node health and slot counts
- [ ] Identify test vs production traffic (source IPs)
- [ ] Verify new unique sessions go to P100
- [ ] Verify repeat sessions stay on RTX (affinity)
- [ ] Check if long prompts (>4096 tokens) hit RTX
- [ ] Check slot busy state (stuck `isProcessing`?)

---

## 5. Common Problem Patterns

| Symptom | Likely Cause | Check / Fix |
|---------|-------------|-------------|
| All requests on RTX | Session affinity from repeat content | Check `derive_session_id()` collisions |
| P100 metrics empty | Core metrics endpoint not responding | `podman compose restart hydra-core` |
| Prometheus target down | Container IP changed after restart | `podman compose restart prometheus` or `podman compose up -d --force-recreate` |
| slots_idle=0 always | Slot stuck `isProcessing` | Check llama `/slots` endpoint |
| Session count grows unbounded | No eviction loop | `session_table.evict_stale()` never called |
| n_past always 0 | Router never reads slot state | `update_n_past()` never called |
| Grafana dashboard not found | dashboard-providers.yml missing or wrong mount path | Check docker-compose volume mounts for Grafana |
| Loki not ready | Fresh restart, needs 15s warmup | Wait and retry |

---

## 6. Key Endpoints Reference

| Service | What to check | URL |
|---------|--------------|-----|
| Core | Health, status, metrics | `:9000/health`, `:9000/status`, `:9000/metrics` |
| Core Store RPC | Metrics | `:9501/metrics` |
| llama RTX | Slots, health, metrics | `:8080/slots`, `:8080/health`, `:8080/metrics` |
| llama P100 | Slots, health | `192.168.122.21:8086/slots`, `192.168.122.21:8086/health` |
| Prometheus | Targets, labels, query | `:9091/api/v1/targets`, `:9091/api/v1/label/__name__/values` |
| Grafana | Dashboards, datasources | `:3000/api/search`, `:3000/api/datasources` |
| Loki | Readiness | `:3100/ready` |
| GPU | DCGM metrics | `:9835/metrics` |
| Node | host metrics | `:9100/metrics` |
