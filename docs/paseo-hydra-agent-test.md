# Paseo Hydra Agent Test

Live verification of the Hydra system using a Paseo agent running inference on
the Hydra DENSE model. The agent's LLM backend **is** the Hydra system under
test — no curl-based test scripts are needed.

## What This Tests

- COMBINED mode activation (RTX 5060 Ti + RTX 3060 layer-split)
- End-to-end request flow: Paseo agent → opencode → Hydra Coordinator → llama-engine → response
- Engine prefill RPC (opcode 0x42) working correctly
- Multi-turn session handling (warm affinity, migration, slot lifecycle)

## Prerequisites

- Hydra system running (Coordinator `:9000`, RTX Head `:9700`, P100 Head `192.168.122.21:9700`)
- DENSE profile active (`scripts/set-profile.sh dense`)
- Paseo daemon running (`paseo daemon status`)

## Step 1: Deploy (if needed)

```bash
bash scripts/start-env.sh
```

Confirm all services healthy before proceeding:

```bash
curl -s http://localhost:9000/health | jq .
curl -s http://localhost:9700/status | jq .
curl -s http://192.168.122.21:9700/status | jq .
```

## Step 2: Create Paseo Agent

Use provider `opencode/hydra/balanced` — this routes inference through the
Hydra Coordinator API, which dispatches to the COMBINED-mode llama-engine.

### Prompt Guidelines

- **Send a simple, natural task prompt** — NOT curl commands or test scripts.
- The agent's inference **is** the test. If it produces a valid response, the
  model and routing work end-to-end.
- The orchestrator (the agent calling `paseo_create_agent`) runs the
  verification checks in Step 3 — **not the test agent itself**.

### Good Prompts

| Prompt | Why it works |
|--------|-------------|
| "Write a Python function that checks if a string is a palindrome. Include type hints, a docstring, and at least 3 test cases." | Exercises code generation, type system knowledge, structured output |
| "Explain the difference between a stack and a queue. Give a real-world analogy for each." | Tests reasoning and explanation quality |
| "Write a SQL query to find the second highest salary in an employees table." | Tests domain-specific knowledge |
| "What are the time complexities of quicksort, mergesort, and heapsort? Compare them." | Tests structured comparison |

### Bad Prompts (do NOT use)

- "Run `curl http://localhost:9000/health`" — this tests curl, not the model
- "Check GPU usage with nvidia-smi" — orchestrator's job, not the agent's
- "Verify COMBINED mode is active" — orchestrator checks logs after the agent finishes

### Create Agent Command

```python
paseo_create_agent(
    provider="opencode/hydra/balanced",
    relationship={"kind": "subagent"},
    workspace={"kind": "current"},
    title="hydra-dense-verify",
    initialPrompt="<simple task prompt from above>"
)
```

Wait for the agent to finish (notification arrives via `notifyOnFinish`).

## Step 3: Post-Agent Verification

All verification runs from the **orchestrator** (the calling agent), NOT from
the test agent. These checks confirm the Hydra system behaved correctly during
the agent's inference.

### 3a. Coordinator Logs — Confirm COMBINED Mode

```bash
podman logs hydra-system_core_1 --tail 50 2>&1 \
  | grep -E "multiengine_active|route_type=cold_combined|cold_atomic|migration"
```

**Pass:** `multiengine_active` and `route_type=cold_combined` appear for the
agent's session. **Fail:** Only `migration` or `cold_atomic` — COMBINED mode
did not activate.

### 3b. GPU Monitoring

```bash
nvidia-smi --query-gpu=name,memory.used,memory.total,utilization.gpu --format=csv
```

**Pass:** Both RTX 5060 Ti and RTX 3060 show VRAM usage and utilization. 
**Fail:** Only one GPU active — the peer GPU is not participating.

### 3c. Health Endpoints

```bash
curl -s http://localhost:9000/health | jq .
curl -s http://localhost:9700/status | jq .
curl -s http://192.168.122.21:9700/status | jq .
```

**Pass:** All nodes `healthy`, `stuck_slots: 0`. 
**Fail:** Any node unhealthy or stuck slots > 0.

### 3d. Engine Prefill — No Fallback Warnings

```bash
podman logs hydra-system_core_1 2>&1 \
  | grep "engine_prefill_fell_back_to_http" | tail -5
```

**Pass:** No warnings (or only `not_implemented` for old binary scenario). 
**Fail:** `engine_rpc_error` or `BUSY/NotFound` fallback warnings — the engine
prefill RPC is failing.

### 3e. Request Metrics

```bash
curl -s http://localhost:9000/metrics | grep "hydra_requests_total{"
```

**Pass:** `cold_combined` count incremented by the agent's requests. 
**Fail:** No `cold_combined` entries — requests bypassed COMBINED mode.

### 3f. Agent Output Quality

Check the agent's response for:
- Syntactically correct output (valid Python/SQL/etc.)
- Contains all requested elements (type hints, docstrings, test cases)
- No truncation or garbled text

## Step 4: Pass/Fail Criteria

| Check | Pass | Fail |
|-------|------|------|
| Agent completes task | Returns valid, complete output | Timeout, empty, error |
| COMBINED mode activated | `route_type=cold_combined` in logs | Only `migration` or `cold_atomic` |
| No engine fallback | No `engine_rpc_error` fallback warnings | Fallback warnings present |
| Both GPUs used | Both show VRAM usage + utilization | Only one GPU active |
| No stuck slots | `stuck_slots: 0` on all nodes | Stuck slots > 0 |
| All nodes healthy | All health endpoints return `healthy` | Any node unhealthy |
| Output quality | Correct, complete, non-truncated | Garbled or incomplete |

## Quick Reference: Full Test Sequence

```bash
# 1. Deploy
bash scripts/start-env.sh

# 2. Create paseo agent (from orchestrator)
# Provider: opencode/hydra/balanced
# Prompt: "Write a Python function that checks if a string is a palindrome..."

# 3. Wait for agent to finish

# 4. Verify COMBINED mode
podman logs hydra-system_core_1 --tail 50 2>&1 \
  | grep -E "multiengine_active|route_type=cold_combined"

# 5. Check GPUs
nvidia-smi --query-gpu=name,memory.used,memory.total,utilization.gpu --format=csv

# 6. Check health
curl -s http://localhost:9000/health | jq .

# 7. Check for fallback warnings
podman logs hydra-system_core_1 2>&1 \
  | grep "engine_prefill_fell_back_to_http" | tail -5

# 8. Check metrics
curl -s http://localhost:9000/metrics | grep "hydra_requests_total{"
```
