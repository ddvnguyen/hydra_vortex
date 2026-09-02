#!/usr/bin/env bash
# devtest status — prints container health + HTTP verdict per devtest service.
# NEVER touches prod services beyond a read-only guard check.
set -uo pipefail

declare -A HEALTH_MAP=(
  ["hydra-core-devtest"]="http://localhost:19000/v1/models"
  ["hydra-head-devtest"]="http://localhost:19700/health"
  ["hydra-engine-devtest"]="http://localhost:18086/health"
  ["llama-baseline"]="http://localhost:18080/health"
)

echo "Hydra DEVTEST status ($(date -u +%Y-%m-%dT%H:%M:%SZ))"
echo "------------------------------------------------------------"
printf "%-22s %-10s %s\n" "SERVICE" "VERDICT" "DETAIL"
echo "------------------------------------------------------------"

overall_ok=true
for svc in hydra-core-devtest hydra-head-devtest hydra-engine-devtest llama-baseline; do
  url="${HEALTH_MAP[$svc]}"
  cstate="$(podman inspect --format '{{.State.Status}}' "$svc" 2>/dev/null || echo "not-found")"
  health="$(podman inspect --format '{{if .State.Health}}{{json .State.Health.Status}}{{end}}' "$svc" 2>/dev/null | tr -d '"' || echo "")"
  http_code="$(curl -s -o /dev/null -w "%{http_code}" --max-time 2 "$url" 2>/dev/null || echo "000")"
  verdict="DOWN"
  detail="container=$cstate http=$http_code"
  if [[ -n "$health" ]]; then detail="$detail health=$health"; fi
  if [[ "$http_code" == "200" ]]; then verdict="OK"
  elif [[ "$cstate" == "running" ]]; then verdict="DEGRADED"; overall_ok=false
  else overall_ok=false
  fi
  printf "%-22s %-10s %s\n" "$svc" "$verdict" "$detail"
done

echo "------------------------------------------------------------"
if [[ "$overall_ok" == "true" ]]; then
  echo "OVERALL: OK (devtest lanes healthy)"
else
  echo "OVERALL: DEGRADED/DOWN (devtest not fully healthy — prod untouched)"
fi

# Prod guard (read-only)
echo ""
echo "Prod guard (read-only, never modified):"
for port in 9000 8080 8086; do
  code="$(curl -s -o /dev/null -w "%{http_code}" --max-time 1 "http://localhost:${port}/health" 2>/dev/null || echo "000")"
  if [[ "$code" == "200" ]]; then echo "  prod :$port UP (guard)"; else echo "  prod :$port not responding (hardware-absent or down)"; fi
done
