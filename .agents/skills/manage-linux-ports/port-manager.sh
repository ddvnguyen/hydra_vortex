#!/bin/bash
# Quick Linux Ports Management Utility
# Usage: ./port-manager.sh [command] [options]

set -e

usage() {
  cat << EOF
Linux Ports Management Utility

Usage: $0 [command] [options]

Commands:
  list              List all listening ports and services
  check PORT        Check which service is using a specific port
  available         Find first available port (starting from 8000)
  available RANGE   Find available ports in range (e.g., 8000:9000)
  test PORT         Test if port is listening
  used              Show all ports in use
  free-port BASE    Find first free port starting from BASE
  audit             Comprehensive port audit report
  monitor           Watch port changes in real-time

Examples:
  $0 list                    # Show all listening ports
  $0 check 8080              # What's using port 8080?
  $0 available 3000:4000     # Find free ports in range
  $0 test 5432               # Is PostgreSQL port open?
  $0 free-port 5000          # Find free port from 5000 onwards
  $0 audit                   # Full system port audit

EOF
  exit 0
}

# Colors for output
RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
BLUE='\033[0;34m'
NC='\033[0m' # No Color

require_sudo() {
  if [[ $EUID -ne 0 ]]; then
    echo -e "${RED}This command requires sudo privileges${NC}"
    exit 1
  fi
}

list_ports() {
  echo -e "${BLUE}=== Listening Ports and Services ===${NC}"
  sudo ss -tlnp 2>/dev/null | awk '
    NR==1 {print; next}
    {
      split($4, addr, ":");
      port = addr[length(addr)];
      service = $6;
      gsub(/.*\(/, "", service);
      gsub(/\).*/, "", service);
      printf "%-10s %-40s %s\n", port, $1 " " $2 " " $3, service
    }
  ' | column -t || {
    echo -e "${YELLOW}Falling back to netstat${NC}"
    sudo netstat -tlnp 2>/dev/null | grep LISTEN | tail -n +2
  }
}

check_port() {
  local port=$1
  if [[ -z "$port" ]]; then
    echo -e "${RED}Error: Please specify a port number${NC}"
    exit 1
  fi
  
  echo -e "${BLUE}=== Port $port Status ===${NC}"
  
  result=$(sudo ss -tlnp 2>/dev/null | grep ":$port " || true)
  
  if [[ -n "$result" ]]; then
    echo -e "${GREEN}✓ Port $port is IN USE${NC}"
    echo -e "\nDetails:"
    echo "$result" | awk '{print $0}'
  else
    echo -e "${GREEN}✓ Port $port is FREE${NC}"
  fi
}

test_port() {
  local port=$1
  if [[ -z "$port" ]]; then
    echo -e "${RED}Error: Please specify a port number${NC}"
    exit 1
  fi
  
  echo -e "${BLUE}Testing port $port...${NC}"
  
  if timeout 1 bash -c "echo >/dev/tcp/localhost/$port" 2>/dev/null; then
    echo -e "${GREEN}✓ Port $port is OPEN and responding${NC}"
    return 0
  else
    echo -e "${YELLOW}✗ Port $port is CLOSED or not responding${NC}"
    return 1
  fi
}

find_available() {
  local range="${1:-8000:8100}"
  local start="${range%:*}"
  local end="${range#*:}"
  
  if [[ -z "$start" ]] || [[ -z "$end" ]]; then
    start=8000
    end=8100
  fi
  
  echo -e "${BLUE}=== Finding available ports in range $start-$end ===${NC}"
  
  local count=0
  for port in $(seq "$start" "$end"); do
    if ! sudo ss -tln 2>/dev/null | grep -q ":$port "; then
      echo -e "${GREEN}Available: $port${NC}"
      ((count++))
      if [[ $count -ge 5 ]]; then
        echo -e "${YELLOW}(showing first 5 available ports)${NC}"
        break
      fi
    fi
  done
  
  if [[ $count -eq 0 ]]; then
    echo -e "${RED}No available ports found in range $start-$end${NC}"
  fi
}

find_first_free() {
  local base="${1:-8000}"
  
  echo -e "${BLUE}Finding first free port from $base...${NC}"
  
  for port in $(seq "$base" 65535); do
    if ! sudo ss -tln 2>/dev/null | grep -q ":$port "; then
      echo -e "${GREEN}First available port: $port${NC}"
      return 0
    fi
  done
  
  echo -e "${RED}No available ports found${NC}"
  return 1
}

show_used() {
  echo -e "${BLUE}=== All Ports in Use ===${NC}"
  sudo ss -tln 2>/dev/null | awk '
    NR>1 {
      split($4, addr, ":");
      port = addr[length(addr)];
      ports[port] = 1
    }
    END {
      n = asorti(ports, sorted_ports)
      for (i=1; i<=n; i++) {
        printf "%s\n", sorted_ports[i]
      }
    }
  ' | sort -n
}

run_audit() {
  echo -e "${BLUE}╔════════════════════════════════════════╗${NC}"
  echo -e "${BLUE}║    SYSTEM PORT AUDIT REPORT            ║${NC}"
  echo -e "${BLUE}╚════════════════════════════════════════╝${NC}"
  echo ""
  
  echo -e "${YELLOW}→ Total Listening Ports:${NC}"
  sudo ss -tln 2>/dev/null | tail -n +2 | wc -l
  echo ""
  
  echo -e "${YELLOW}→ Ephemeral Port Range:${NC}"
  cat /proc/sys/net/ipv4/ip_local_port_range
  echo ""
  
  echo -e "${YELLOW}→ Active Services:${NC}"
  sudo ss -tlnp 2>/dev/null | awk '
    NR>1 {
      split($4, addr, ":");
      port = addr[length(addr)];
      service = $6;
      gsub(/.*\//, "", service);
      gsub(/\(.*/, "", service);
      printf "  Port %-6s: %s\n", port, service
    }
  ' | sort -V
  echo ""
  
  echo -e "${YELLOW}→ Protocol Summary:${NC}"
  echo -n "  TCP: "
  sudo ss -tln 2>/dev/null | tail -n +2 | wc -l
  echo -n "  UDP: "
  sudo ss -uln 2>/dev/null | tail -n +2 | wc -l
  echo ""
  
  echo -e "${YELLOW}→ Ports by Range:${NC}"
  echo -n "  Well-known (1-1023): "
  sudo ss -tln 2>/dev/null | awk '$4 ~ /:([0-9]|[1-9][0-9]|[1-9][0-9][0-9])$/ {count++} END {print count}' || echo "0"
  echo -n "  Registered (1024-49151): "
  sudo ss -tln 2>/dev/null | awk '$4 ~ /:([1-9][0-9]{3}|[1-4][0-9]{4})$/ {count++} END {print count}' || echo "0"
  echo ""
}

monitor_ports() {
  echo -e "${BLUE}=== Monitoring port changes (Ctrl+C to stop) ===${NC}"
  watch -n 2 "sudo ss -tlnp | tail -n +2 | awk '{split(\$4, addr, \":\"); port = addr[length(addr)]; print port}' | sort -n"
}

# Main
if [[ $# -eq 0 ]]; then
  usage
fi

require_sudo

case "$1" in
  list)
    list_ports
    ;;
  check)
    check_port "$2"
    ;;
  available)
    find_available "$2"
    ;;
  test)
    test_port "$2"
    ;;
  used)
    show_used
    ;;
  free-port)
    find_first_free "$2"
    ;;
  audit)
    run_audit
    ;;
  monitor)
    monitor_ports
    ;;
  -h|--help|help)
    usage
    ;;
  *)
    echo -e "${RED}Unknown command: $1${NC}"
    usage
    ;;
esac
