---
name: setup-container-monitoring-stack
description: "Use when: Setting up an isolated monitoring stack with Docker or Podman. Generates container configs, secure compose files, and validation instructions for Prometheus, Grafana, and LiteLLM."
---

# Containerized Monitoring Stack Skill

## Scope
Build a safe, isolated monitoring stack in containers for:
- Prometheus
- Grafana
- LiteLLM

Host service:
- `llama.cpp` remains on host at `http://host.containers.internal:8080` (Podman) or `http://host.docker.internal:8080` (Docker)

## What This Skill Does

- Writes secure `podman-compose.yml` and `docker-compose.yml`
- Enforces rootless / non-root container execution
- Applies network isolation via bridge network
- Uses read-only config mounts and temporary local volumes
- Sets resource limits and capability drops
- Provides a safety validation script

## Usage Examples

- `/setup-container-monitoring-stack Generate a podman-compose stack for Prometheus, Grafana, and LiteLLM`
- `/setup-container-monitoring-stack Validate that containers are isolated from the host`
- `/setup-container-monitoring-stack Convert compose config for Docker compatibility`

## Setup Workflow

1. Place `prometheus.yml` and Grafana provisioning under `./grafana/provisioning`
2. Start containers with Podman or Docker
3. Run `.github/containers/test-isolation.sh podman`
4. Confirm Prometheus target `http://localhost:9090/targets`
5. Open Grafana at `http://localhost:3000`

## Notes

- For Podman, use `host.containers.internal` to reach host services.
- For Docker, use `host.docker.internal`.
- Do not use `network_mode: host`.
- Keep all config mounts `:ro`.
