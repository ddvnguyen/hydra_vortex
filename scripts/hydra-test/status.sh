#!/usr/bin/env bash
# hydra-test status — prints container health + a 1-line verdict per service.
set -uo pipefail

# Services and their health endpoints
declare -A HEALTH_MAP=(
  ["hydra-core-test-a"]="http://localhost:19000/v1/models"
  ["hydra-core-test-b"]="http://localhost:19001/v1/models"
  ["hydra-head-test-a"]="http://localhost:19700/health"
  ["hydra-head-test-b"]="http://localhost:19701/health"
  ["llama-engine-test-a"]="http://localhost:18086/health"
  ["llama-engine-test-b"]="http://localhost:18087/health"
)

echo "Hydra TEST status ($(date -u +%Y-%m-%dT%H:%M:%SZ))"
echo "------------------------------------------------------------"
printf "%-22s %-10s %s\n" "SERVICE" "VERDICT" "DETAIL"
echo "------------------------------------------------------------"

overall_ok=true
for svc in hydra-core-test-a hydra-core-test-b hydra-head-test-a hydra-head-test-b llama-engine-test-a llama-engine-test-b; do
  url="${HEALTH_MAP[$svc]}"
  # Check podman container state
  cstate="$(podman inspect --format '{{.State.Status}}' "$svc" 2>/dev/null || echo "not-found")"
  health="$(podman inspect --format '{{if .State.Health}}{{json .State.Health.Status}}{{end}}' "$svc" 2>/dev/null | tr -d '"' || echo "")"

  # Probe HTTP
  http_code="$(curl -s -o /dev/null -w "%{http_code}" --max-time 2 "$url" 2>/dev/null || echo "000")"

  verdict="DOWN"
  detail="container=$cstate http=$http_code"
  if [[ -n "$health" ]]; then
    detail="$detail health=$health"
  fi

  if [[ "$http_code" == "200" ]]; then
    verdict="OK"
  elif [[ "$cstate" == "running" ]]; then
    verdict="DEGRADED"
    overall_ok=false
  else
    overall_ok=false
  fi

  # Color hygiene: no ANSI unless tty
  printf "%-22s %-10s %s\n" "$svc" "$verdict" "$detail"
done

echo "------------------------------------------------------------"
if [[ "$overall_ok" == "true" ]]; then
  echo "Overall: OK — all 6 services healthy"
else
  echo "Overall: DEGRADED — some services not healthy (see above)"
fi
