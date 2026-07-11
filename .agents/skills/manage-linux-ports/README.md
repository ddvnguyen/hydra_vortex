# Linux Ports Management Skill

## Overview

This skill provides comprehensive tools and techniques for managing network ports in Linux systems. It helps you:

- **Discover available ports** for deploying new services
- **Identify port usage** - quickly see which service is using a specific port
- **Explore system configuration** - understand your current port landscape
- **Prevent port conflicts** - check for port availability before deployment
- **Monitor port changes** - watch for new services using ports

## Files in This Skill

### 1. `SKILL.md` (Main Reference)
Comprehensive documentation with:
- 10 common use cases with code examples
- Tool comparisons (`ss`, `netstat`, `lsof`, `nc`)
- Troubleshooting guide
- Integration examples for monitoring stacks

### 2. `port-manager.sh` (Practical Utility Script)
Ready-to-use bash script with these commands:

```bash
# List all listening ports
sudo ./port-manager.sh list

# Check specific port
sudo ./port-manager.sh check 8080

# Find available ports
sudo ./port-manager.sh available
sudo ./port-manager.sh available 3000:4000

# Test if port is responding
sudo ./port-manager.sh test 5432

# Find first free port from base
sudo ./port-manager.sh free-port 5000

# Run comprehensive audit
sudo ./port-manager.sh audit

# Monitor port changes live
sudo ./port-manager.sh monitor
```

## Quick Start

### Using the Utility Script
```bash
cd /path/to/manage-linux-ports

# Make executable (one time)
chmod +x port-manager.sh

# Check what's listening
sudo ./port-manager.sh list

# Find available ports
sudo ./port-manager.sh available 8000:9000

# Pre-deployment check
sudo ./port-manager.sh check 8080
```

### Using Direct Commands
```bash
# Modern systems (preferred)
sudo ss -tlnp

# Legacy systems
sudo netstat -tlnp

# Get detailed process info
sudo lsof -i :8080 -P
```

## Common Scenarios

### Scenario 1: New Service Deployment
Need to deploy a service but unsure which port to use?

```bash
# Quick check
sudo ./port-manager.sh available 5000:6000

# Then verify before deployment
sudo ./port-manager.sh check 5432
```

### Scenario 2: Port Conflict Debug
Something is using "your" port?

```bash
# Find the culprit
sudo ./port-manager.sh check 3000

# Get full process details
sudo lsof -i :3000
```

### Scenario 3: System Audit
Understand what's running on your system

```bash
# Comprehensive overview
sudo ./port-manager.sh audit

# Live monitoring
sudo ./port-manager.sh monitor
```

### Scenario 4: Monitoring Stack Deployment
Before deploying Prometheus, Grafana, etc.

```bash
# Check multiple ports at once
for port in 8080 9090 3000 5432; do
  sudo ./port-manager.sh check $port
done

# Find available ranges
sudo ./port-manager.sh available 8000:10000
```

## Technical Details

### Port Ranges
- **Well-known (0-1023)**: System/privileged ports, require sudo
- **Registered (1024-49151)**: User applications
- **Dynamic/Ephemeral (49152-65535)**: Temporary connections

Check your system's ephemeral range:
```bash
cat /proc/sys/net/ipv4/ip_local_port_range
```

### Tools Explained

| Tool | Best For | Speed | Notes |
|------|----------|-------|-------|
| `ss` | Modern systems | Fast | Preferred, part of iproute2 |
| `netstat` | Legacy/compatibility | Medium | Deprecated on some systems |
| `lsof` | Detailed process info | Slower | Better for troubleshooting |
| `nc` | Connectivity testing | Fast | Simple port probe |

## Troubleshooting

### "Port already in TIME_WAIT state"
Port shows in use but service is stopped:
```bash
# Normal - will clear in 30-120 seconds
sudo ss -tln | grep TIME_WAIT

# For testing, can configure SO_REUSEADDR in application
```

### "Permission denied"
Most commands need sudo:
```bash
sudo ./port-manager.sh list
```

### Port shows occupied but not visible in process list
Might be bound to local interface only:
```bash
sudo ss -tlnp | grep 127.0.0.1:8080
```

## Integration Examples

### Docker/Container Deployment
```bash
#!/bin/bash
# Find free ports before starting containers
HTTP_PORT=$(sudo ./port-manager.sh free-port 8000)
HTTPS_PORT=$(sudo ./port-manager.sh free-port 8443)

docker run -p $HTTP_PORT:80 -p $HTTPS_PORT:443 myservice
```

### Monitoring Stack Pre-flight
```bash
#!/bin/bash
# Check ports before deploying monitoring
REQUIRED=(8080 9090 3000 5432)
for port in "${REQUIRED[@]}"; do
  sudo ./port-manager.sh check $port || exit 1
done
```

### Service Health Check
```bash
#!/bin/bash
# Verify service ports are responsive
services=("8080" "8081" "5432")
for port in "${services[@]}"; do
  sudo ./port-manager.sh test $port || alert "Port $port not responding"
done
```

## Related Skills

- **setup-container-monitoring-stack**: Deployment of monitoring services (uses this skill for port validation)
- **setup-prometheus-monitoring**: Prometheus configuration (needs port availability checks)
- **validate-container-isolation**: Network validation (references port configuration)

## Performance Notes

- `ss` commands: ~50-100ms for systems with few ports
- `lsof` commands: ~200-500ms (more detailed but slower)
- Script operations: <1s for typical queries
- Monitor mode: Updates every 2 seconds

## References

- [ss man page](http://man7.org/linux/man-pages/man8/ss.8.html)
- [Linux /proc/sys/net documentation](https://www.kernel.org/doc/html/latest/networking/index.html)
- [Socket states and TCP connection lifecycle](https://en.wikipedia.org/wiki/TCP_congestion_control)

## Support

For issues or questions:
1. Check `SKILL.md` for detailed examples
2. Review troubleshooting section above
3. Use `port-manager.sh --help` for command options
4. Test with basic `ss` or `netstat` commands directly

