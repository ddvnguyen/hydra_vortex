---
name: container-safety
applyTo: "**/*docker-compose*.yml,**/*podman-compose*.yml,.github/containers/**"
description: "Container configs must isolate from host. Use rootless podman, network bridges, read-only mounts, dropped capabilities, user namespaces."
---

# Container Safety & Isolation Guidelines

## Principle: Containers Must Not Affect Host

### 1. Rootless Podman (Preferred)
```bash
# Migrate to rootless
podman system migrate
```

Service config:
```yaml
services:
  prometheus:
    user: "1000:1000"
    security_opt:
      - no-new-privileges:true
    cap_drop:
      - ALL
    cap_add:
      - NET_BIND_SERVICE
```

### 2. Network Isolation
```yaml
networks:
  monitoring-net:
    driver: bridge
    ipam:
      config:
        - subnet: 172.20.0.0/16

services:
  prometheus:
    networks:
      - monitoring-net
```
✅ No `network_mode: host`  
✅ Internal bridge only  
✅ Host reaches llama.cpp via explicit network route  

### 3. Volume Mounts — Read-Only Configs
```yaml
volumes:
  prometheus-data:
    driver: local
  grafana-storage:
    driver: local

services:
  prometheus:
    volumes:
      - ./prometheus.yml:/etc/prometheus/prometheus.yml:ro
      - prometheus-data:/prometheus
```

### 4. Capability Restrictions
```yaml
cap_drop:
  - ALL
cap_add:
  - NET_BIND_SERVICE        # Prometheus, Grafana
  # - NET_ADMIN             # Only LiteLLM if monitoring RPC bandwidth
```

### 5. Resource Limits
```yaml
deploy:
  resources:
    limits:
      memory: 2G
      cpus: '2'
```

### 6. Validation Tests
```bash
# Verify namespace isolation
podman exec prometheus ps aux | grep -c $$ && echo "FAIL: Host PIDs visible" || echo "PASS"

# Verify read-only config
podman exec prometheus touch /etc/prometheus/prometheus.yml && echo "FAIL: Config writable" || echo "PASS: Read-only"

# Verify network isolation (should fail)
podman exec prometheus ping -c1 $(hostname -I | awk '{print $1}') && echo "FAIL: Can reach host" || echo "PASS: Isolated"
```

## Safety Checklist
- [ ] `security_opt: [no-new-privileges:true]` on all
- [ ] `cap_drop: [ALL]` + minimal `cap_add`
- [ ] Volumes use `:ro` for configs
- [ ] No `network_mode: host`
- [ ] Memory/CPU limits set
- [ ] User is 1000:1000

## Related

- Skill: `setup-container-monitoring-stack` — generates compliant compose configs
- Skill: `validate-container-isolation` — automated safety tests
- Agent: `monitoring-ops` — design questions
