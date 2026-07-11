---
name: validate-container-isolation
description: "Use when: Testing container isolation and safety for the monitoring stack. Validates namespace separation, read-only config mounts, network isolation, and capability restrictions in Docker/Podman containers."
---

# Container Isolation Validation Skill

## Scope
Validate that the containerized monitoring stack is safe and cannot affect the host system.

## What This Skill Does

- Runs namespace isolation checks
- Ensures config mounts are read-only
- Verifies containers do not use host network mode
- Confirms capability restrictions are applied

## Usage Examples

- `/validate-container-isolation Run isolation tests against the monitoring compose stack`
- `/validate-container-isolation Check that Prometheus cannot access host filesystem or host network`

## Workflow

1. Start the compose stack in Podman or Docker
2. Run `.github/containers/test-isolation.sh podman`
3. Verify the script reports PASS for namespace, config, and network isolation
4. Fix any identified issues in compose config

## Checks Included

- `podman exec llm-prometheus ps aux` should not see host PID 1 or host process list
- `podman exec llm-prometheus touch /etc/prometheus/prometheus.yml` should fail
- `podman exec llm-prometheus ping -c1 <host-ip>` should fail
- Containers should be configured with `no-new-privileges:true`
- Compose files should use bridge networking only
