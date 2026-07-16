# Paseo Hydra Agent — Deploy & Test

## Purpose

Deploy C# (Hydra.Core) changes to the live environment and verify via the
Coordinator API. Used for the `fix-session-affinity-kv` branch (PR #443 fixes).

## Deploy Steps

### 1. Build Docker image (no-cache to pick up code changes)
```bash
podman build --no-cache -f infra/Dockerfile --target core \
  -t localhost/hydra-core:latest .
```

### 2. Restart compose stack
```bash
cd infra
export HYDRA_HEAD_AUTH_TOKEN=$(cat .hydra-head-token)
podman compose -f docker-compose.hydra.yml down
podman compose -f docker-compose.hydra.yml up -d
```

### 3. Wait for health
```bash
for i in $(seq 1 24); do
  curl -sf http://localhost:9000/health > /dev/null && break
  sleep 5
done
curl -s http://localhost:9000/health | python3 -m json.tool
```

## Test Scripts

### FIX 1 — Warm affinity pins session
```bash
# Turn 1: short prompt → resolves to moe-35b-solo
curl -s http://localhost:9000/v1/chat/completions \
  -H "Content-Type: application/json" \
  -d '{"model":"hydra-auto","messages":[{"role":"user","content":"Hi"}],"stream":false,"max_tokens":5}' \
  | python3 -c "import sys,json; d=json.load(sys.stdin); print('model:', d.get('model','?'))"

# Turn 2: large prompt → should STILL be moe-35b-solo (not flip to moe-35b-pd)
python3 -c "
import requests, json
prompt = 'x ' * 5000
r = requests.post('http://localhost:9000/v1/chat/completions', json={
    'model': 'hydra-auto',
    'messages': [{'role': 'user', 'content': prompt}],
    'stream': False, 'max_tokens': 5
}, timeout=120)
d = r.json()
m = d.get('model','?')
print('Turn 2 model:', m)
print('PASS' if 'Mini' in m else 'FAIL')
"
```

### FIX 2 — Decode model file for P/D decode worker
```bash
curl -s --max-time 120 http://localhost:9000/v1/chat/completions \
  -H "Content-Type: application/json" \
  -d '{"model":"moe-35b-pd","messages":[{"role":"user","content":"Hi"}],"stream":false,"max_tokens":5}' \
  | python3 -c "
import sys,json; d=json.load(sys.stdin)
hm = d.get('hydra_metrics',{})
mp = hm.get('model_path','')
print('model_path:', mp)
print('PASS' if 'Balanced' in mp else 'FAIL' if 'Mini' in mp else 'INFO')
"
```

### FIX 3 — Dead JSON key removed
```bash
grep -n "allow_cross_model_kv_reuse" infra/hydra-core/config/models.json \
  && echo "FAIL" || echo "PASS"
```

## Live Test Results (2026-07-16)

| Fix | Status | Notes |
|-----|--------|-------|
| FIX 3 | **PASS** | Dead key removed from models.json |
| FIX 1 | **PARTIAL** | BoundModel is persisted and warm affinity works. However, AutoRouter's `ChooseBySwapCost` selects `dense-27b-combined` (tier 3) over `moe-35b-solo` (tier 1) because it picks the highest quality tier first. The `dense-27b-combined` model should have `auto_eligible: false` in models.json since it requires COMBINED GPU topology and is not part of the mix-quant routing set. This is a config issue, not a code bug. |
| FIX 2 | **PARTIAL** | The `isDecodeRole = item.RouteType == "cold_pd"` check only covers the initial cold_pd route. When a session migrates (warm follow-up with store state), `RouteType` is `"migration"` and the decode-role check doesn't trigger. The `hydra_config` injection uses `PrefillModelFileName` for both prefill and decode in migration routes. Fix: expand the check to cover migration routes too, or always use decode-role when the decode worker is a P/D peer. |

### Root Cause: Missing `HYDRA_COORD_MODELS_FILE`

The compose file (`infra/docker-compose.hydra.yml`) did not set
`HYDRA_COORD_MODELS_FILE`, so `ModelConfigLoader.InstanceOrNull` was null.
AutoRouter always returned null, and all requests fell through to old routing.
Fixed by adding the env var to the compose environment section.

### Residual Issues

1. **AutoRouter quality-tier selection**: `ChooseBySwapCost` picks highest tier
   first. `dense-27b-combined` (tier 3) wins over `moe-35b-solo` (tier 1) for
   any prompt size. Fix: set `"auto_eligible": false` on `dense-27b-combined`.

2. **Decode-role coverage**: `isDecodeRole` check in DecodeAsync only covers
   `cold_pd` routes. Migration routes also need decode-role handling. Fix:
   expand to `item.RouteType is "cold_pd" or "migration" or "affinity"` when
   the worker is the P/D decode worker.

3. **hydra_config injection in migration path**: The migration route's
   DecodeAsync doesn't inject `model_path` in `hydra_config` at all (the
   log shows `decode_model=Qwopus3.6-35B-A3B-v1-APEX-I-Mini` which is the
   default, not from config). The `hydra_config` is only injected when
   `w.ModelAlias` is non-null. In migration routes, `w` is the worker from
   PickDecode, which may not have `ModelAlias` set.
