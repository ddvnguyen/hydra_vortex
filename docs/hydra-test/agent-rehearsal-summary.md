# Agent Rehearsal Summary — 2026-08-29

## Run: v1b Criterion (a) — Hydra TEST end-to-end agent rehearsal

**Timestamp**: 20260829T014625Z
**Script**: `scripts/hydra-test/agent-rehearsal.sh`
**Evidence**: `docs/hydra-test/evidence/agent-rehearsal-20260829T014625Z.jsonl`

### Results

| Step | Name | Core | Status | Tokens | Latency (s) |
|------|------|------|--------|--------|-------------|
| 1 | System-prompt task briefing | core-A | 200 | 64 | 10.04 |
| 2 | Tool-call-style JSON request | core-A | 200 | 64 | 10.97 |
| 3 | Multi-turn follow-up | core-A | 200 | 64 | 10.73 |
| 4a | Burst request 1 | core-A | 200 | 64 | 18.39 |
| 4b | Burst request 1 | core-B | 200 | 64 | 21.78 |
| 4c | Burst request 2 | core-A | 200 | 64 | 15.19 |
| 4d | Burst request 2 | core-B | 200 | 64 | 17.32 |
| 4e | Burst request 3 | core-A | 200 | 64 | 16.51 |
| 4f | Burst request 3 | core-B | 200 | 64 | 16.94 |
| 5 | Context retention | core-A | 200 | 64 | 11.28 |
| 6 | Prod isolation | prod | 200 | 0 | 0.00 |

**All 11 log entries: 200 OK, completion_tokens > 0 (except step 6 which is a model-list probe).**

### Prod isolation

- `curl -s -o /dev/null -w "%{http_code}" http://localhost:9000/v1/models` → **200** (prod alive, untouched)
- Step 6 byte-compared prod `/v1/models` before and after all test traffic: **unchanged**

### What was tested

1. **System prompt processing** — agent acknowledges task briefing
2. **Structured JSON output** — tool-call-style response (mimics function calling)
3. **Multi-turn context** — follow-up references prior conversation turn
4. **Concurrent burst** — 3 sequential requests per core, parallel across cores (respects cold_atomic self-lease limit of 1 in-flight per core)
5. **Context retention** — 4-turn conversation, response references earlier task name
6. **Prod isolation** — prod :9000 response byte-matched before/after test traffic

### Assumptions

- Hydra TEST rig was already running (cores :19000/:19001, engines on P100 VM)
- max_tokens capped at 64 per request (small 9B model)
- Cold_atomic concurrency: 1 in-flight per core (slots-1 limit)
