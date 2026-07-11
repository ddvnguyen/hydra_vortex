---
description: Manage network ports in Linux. Find available ports for new services, identify which services use specific ports, and explore current port usage across the system.
keywords: [linux, ports, network, services, lsof, netstat, ss, port-scanning, system-exploration]
---

# Linux Ports Management Skill

## Overview

This skill provides comprehensive techniques to:
- **Discover available ports** for new services
- **Map ports to services** and identify what's using each port
- **Explore the current system** port configuration and usage
- **Check for port conflicts** before deploying services
- **Monitor port availability** across different port ranges

## Prerequisites

- Linux system with standard utilities installed
- `ss`, `lsof`, `netstat`, or `nc` commands (most Linux distributions include these)
- Sufficient permissions (may need `sudo` for some operations)
- Optional: `curl`, `telnet` for port connectivity testing

## Common Use Cases

### 1. Find All Listening Ports and Associated Services

**Using `ss` (recommended, modern approach):**
```bash
sudo ss -tlnp | grep LISTEN
```

**Using `netstat` (older systems):**
```bash
sudo netstat -tlnp | grep LISTEN
```

**Using `lsof`:**
```bash
sudo lsof -i -P -n | grep LISTEN
```

**Output interpretation:**
```
LISTEN    0      4096                    127.0.0.1:8080                0.0.0.0:*    users:(("python",pid=12345,fd=3))
          ↑       ↑                       ↑                                        ↑
        state   queue                 address:port                          process info
```

### 2. Check Specific Port Usage

**Find what's using a specific port:**
```bash
sudo lsof -i :8080
sudo ss -tlnp | grep :8080
sudo netstat -tlnp | grep :8080
```

**More detailed port information:**
```bash
sudo lsof -i :8080 -P
```

### 3. Find Available Ports in a Range

**List all listening ports (extract port numbers only):**
```bash
sudo ss -tln | awk 'NR>1 {print $4}' | sed 's/.*://' | sort -n
```

**Find first available port above 8000:**
```bash
for port in {8000..8100}; do
  if ! sudo ss -tln 2>/dev/null | grep -q ":$port "; then
    echo "Available: $port"
    break
  fi
done
```

**Check a specific port is free (before deploying):**
```bash
if ! sudo ss -tln 2>/dev/null | grep -q ":5432 "; then
  echo "Port 5432 is available"
else
  echo "Port 5432 is in use"
fi
```

### 4. Map All Ports to Services

**Human-readable format with process details:**
```bash
sudo ss -tlnp | awk '
  NR>1 {
    split($4, addr, ":");
    port = addr[length(addr)];
    gsub(/.*\(/, "", $6);
    gsub(/\).*/, "", $6);
    print port "\t" $6
  }
' | sort -n -t$'\t' -k1
```

**With state and protocol information:**
```bash
sudo ss -tlnp | awk 'NR>1 {
  split($4, addr, ":");
  port = addr[length(addr)];
  print $1, port, $6
}' | column -t
```

### 5. Check Port Status by Protocol

**TCP ports only:**
```bash
sudo ss -tln
```

**UDP ports only:**
```bash
sudo ss -uln
```

**Both TCP and UDP:**
```bash
sudo ss -tlun
```

**Include established connections:**
```bash
sudo ss -tln
```

### 6. Identify Ephemeral vs Well-Known Ports

**Well-known ports (0-1023, requires root):**
```bash
sudo ss -tln | awk 'NR>1 && $4 ~ /:([0-9]|[1-9][0-9]|[1-9][0-9][0-9])$/'
```

**Registered ports (1024-49151):**
```bash
sudo ss -tln | awk 'NR>1 && $4 ~ /:([1-9][0-9]{3}|[1-4][0-9]{4})$/'
```

**Find ephemeral port range:**
```bash
cat /proc/sys/net/ipv4/ip_local_port_range
```

### 7. Test Port Connectivity

**Quick test if port is listening:**
```bash
timeout 1 nc -zv localhost 8080
echo $?  # 0 = port open, 1 = port closed
```

**Using curl for HTTP ports:**
```bash
curl -s -o /dev/null -w "%{http_code}" http://localhost:8080
```

**Test multiple ports:**
```bash
for port in 8080 8081 8082; do
  timeout 1 nc -zv localhost $port 2>&1 && echo "Port $port: OPEN" || echo "Port $port: CLOSED"
done
```

### 8. Monitor Port Changes in Real-time

**Watch for new listening ports:**
```bash
watch -n 2 'sudo ss -tln | grep LISTEN'
```

**Get detailed process info for a port:**
```bash
sudo lsof -i :8080 -F | grep -E "^[pRc]"
```

### 9. Find Services by Name/Pattern

**Find all ports used by a specific service:**
```bash
sudo lsof -i -P -n | grep python
```

**Check multiple processes:**
```bash
sudo ss -tlnp | grep -E "nginx|apache|node"
```

### 10. System Exploration Scripts

**Comprehensive port audit:**
```bash
#!/bin/bash
echo "=== LISTENING PORTS SUMMARY ==="
echo "Total listening ports:"
sudo ss -tln | wc -l
echo ""
echo "=== ACTIVE SERVICES ==="
sudo ss -tlnp | awk 'NR>1 {
  split($4, addr, ":");
  port = addr[length(addr)];
  split($6, proc, "/");
  printf "%-8s %-20s %s\n", port, proc[2], proc[1]
}' | column -t | sort -k1 -n
echo ""
echo "=== EPHEMERAL RANGE ==="
cat /proc/sys/net/ipv4/ip_local_port_range
```

**Check common service ports:**
```bash
#!/bin/bash
PORTS=(22 80 443 3306 5432 6379 8080 8443 9090)
echo "Checking standard ports..."
for port in "${PORTS[@]}"; do
  status=$(sudo ss -tln 2>/dev/null | grep -q ":$port " && echo "IN USE" || echo "FREE")
  printf "Port %-5s: %s\n" "$port" "$status"
done
```

## Troubleshooting

### "Permission denied" errors
```bash
# Most port operations require sudo
sudo ss -tln
sudo lsof -i -P -n
```

### Port appears in use but service not running
```bash
# Service crashed but port still in TIME_WAIT state
# Check TCP state specifically
sudo ss -tln | grep TIME_WAIT

# Usually clears after 30-120 seconds, or configure SO_REUSEADDR
```

### Finding which service owns a port
```bash
# If lsof shows permission denied
sudo lsof -i :PORT_NUMBER -P

# Check systemd service for port
sudo systemctl status | grep -i PORT_NUMBER
```

## Integration with Monitoring Stacks

When deploying new services to monitoring stacks:

```bash
# Pre-deployment check
required_ports=(8080 9090 5432)
available=true
for port in "${required_ports[@]}"; do
  if sudo ss -tln | grep -q ":$port "; then
    echo "ERROR: Port $port already in use"
    available=false
  fi
done
[ "$available" = true ] && echo "All ports available" || exit 1
```

## Summary Table

| Tool | Purpose | Syntax | Speed |
|------|---------|--------|-------|
| `ss` | Modern socket stats | `ss -tln` | Fast |
| `netstat` | Legacy stats | `netstat -tln` | Medium |
| `lsof` | File descriptor info | `lsof -i -P -n` | Slower |
| `nc` | Connectivity test | `nc -zv host port` | Fast |

