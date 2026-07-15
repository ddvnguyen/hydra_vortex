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
- Profile active (`scripts/set-profile.sh moe` or `dense`)
- Paseo daemon running (`paseo daemon status`)

### Model Config Matching (CRITICAL)

**The opencode provider config must match the Hydra Core model registry.**
These are two separate config files that must stay in sync:

| Config file | Location | Controls |
|---|---|---|
| `~/.config/opencode/opencode.jsonc` | Global (Paseo daemon reads) | What models Paseo agents can use |
| `~/.paseo/worktrees/<wt>/opencode.jsonc` | Worktree (opencode reads) | What models this workspace uses |
| `infra/hydra-core/config/models.json` | Code (Hydra Core reads via `HYDRA_COORD_MODELS_FILE`) | Model templates + engine config |
| `ModelRegistry.cs` hardcoded fallback | Code (used when env var not set) | Fallback model list |

**If these don't match, requests fail.** Examples:
- opencode sends `model: "moe-35b-pd"` → Hydra doesn't have it → `model_not_found` error
- opencode sends `model: "balanced"` → Hydra only has `moe-35b-solo` → 503 error
- opencode doesn't have `hydra-auto` → user can't select auto-routing

**Sync checklist after adding/removing models:**

1. Add model to `infra/hydra-core/config/models.json`
2. Add model to `ModelRegistry.cs` hardcoded fallback (same entry as #1)
3. Add model to `~/.config/opencode/opencode.jsonc` (global — Paseo reads this)
4. Add model to worktree `opencode.jsonc` (local — opencode reads this)
5. Verify: `curl http://localhost:9000/v1/models` returns all expected aliases
6. Verify: `paseo list-models --provider opencode | grep hydra` shows all models

**Current model map (as of 2026-07-15):**

| Model ID | Type | Opencode display name | Hydra Core |
|---|---|---|---|
| `balanced` | Back-compat alias | Hydra MoE 35B (back-compat) | ✅ |
| `moe-35b-solo` | SOLO mode | Qwen 3.6 35B-A3B MoE (SOLO RTX 5060 Ti) | ✅ |
| `moe-35b-pd` | P/D Split mode | Qwen 3.6 35B-A3B MoE (P/D Split: RTX prefill + P100 decode) | ✅ |
| `dense-27b-combined` | COMBINED mode | Qwen 3.6 27B Dense (COMBINED RTX 5060 Ti + 3060) | ✅ |
| `hydra-auto` | Auto routing | Hydra Auto (picks best model per prompt) | ✅ |

**Note:** The Paseo daemon caches the model list at startup. After editing
`~/.config/opencode/opencode.jsonc`, you must restart the daemon for changes
to take effect: `paseo daemon restart`.

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

## Multi-Turn Eval Testing

Tests the full KV cache lifecycle across multiple turns on the **same session**.
Each turn adds to the conversation context, triggering warm_threshold_exceeded →
re-prefill → BgSave → cache reuse.

### How It Works

1. Create agent with `initialPrompt` (turn 1)
2. Wait for `<paseo-system>` notification that agent finished
3. Send turn 2 via `paseo_send_agent_prompt` to the **same agent ID**
4. Wait for notification again
5. Repeat for turns 3-5

**Critical:** You MUST wait for each turn's `<paseo-system>` notification before
sending the next prompt. Sending prompts while the agent is still processing
causes them to be silently dropped.

### KV Lifecycle Per Turn

| Turn | What Happens | Expected |
|------|-------------|----------|
| 1 | Cold combined — fresh session | prefill_ms ~3-4s, BgSave ~400 MB |
| 2 | warm_threshold_exceeded (NewPrompt > 5120) | Full re-prefill ~96s, BgSave ~2 GB |
| 3+ | Engine cache-prompt hit (engine reuses KV internally) | prefill_ms ~1.3s, BgSave ~2 GB |

### Example: 5-Turn Palindrome Conversation

```python
# Turn 1 — create agent
paseo_create_agent(
    provider="opencode/hydra/balanced",
    relationship={"kind": "subagent"},
    workspace={"kind": "current"},
    title="mt-eval-turn1",
    initialPrompt="You are a Python tutor. Answer briefly."
)
# Wait for <paseo-system> notification

# Turn 2 — send to same agent
paseo_send_agent_prompt(
    agentId="<same agent ID>",
    prompt="What is a palindrome? One sentence."
)
# Wait for notification

# Turn 3 — context grows, may trigger warm_threshold
paseo_send_agent_prompt(
    agentId="<same agent ID>",
    prompt="Write a Python function called is_palindrome."
)
# Wait for notification

# Turn 4
paseo_send_agent_prompt(
    agentId="<same agent ID>",
    prompt="Add edge case handling for empty strings."
)
# Wait for notification

# Turn 5
paseo_send_agent_prompt(
    agentId="<same agent ID>",
    prompt="What is the time complexity of your function?"
)
# Wait for notification
```

### What to Verify

```bash
# 1. warm_threshold_exceeded fires (turn 2+)
podman logs hydra-system_core_1 2>&1 | grep "warm_threshold"

# 2. BgSave runs after each turn
podman logs hydra-system_core_1 2>&1 | grep "bg_saved.*bytes"

# 3. Prefill backfill shows cache hits on turn 3+
podman logs hydra-system_core_1 2>&1 | grep "prefill_backfill"
# Turn 1: prompt_ms ~3500, Turn 2: ~96000, Turn 3+: ~1300 (cache hit)

# 4. Engine stays alive (no crashes)
curl -s http://localhost:8080/health | head -1

# 5. No model_load_failed, no migration to P100
podman logs hydra-system_core_1 2>&1 | grep -c "model_load_failed"
podman logs hydra-system_core_1 2>&1 | grep -c "route_type=migration"
```

### Expected Results (Verified 2026-07-14)

| Turn | Tokens In | Prefill | Decode | BgSave | Notes |
|------|-----------|---------|--------|--------|-------|
| 1 | 1,508 | 3,459ms | 26,748ms | 395 MB (461ms) | Cold combined |
| 2 | 44,894 | 96,366ms | 1,297ms | 2 GB (3,015ms) | warm_threshold → full re-prefill |
| 3 | 44,932 | 1,257ms | 2,713ms | 2 GB (3,012ms) | Cache hit (engine reuses KV) |
| 4 | 44,999 | 1,331ms | 3,545ms | 2 GB (2,214ms) | Cache hit |
| 5 | 45,174 | 1,340ms | 8,312ms | 2 GB (2,286ms) | Cache hit |

### Known Behavior

- **restore_skipped_abort** may fire when BgSave from turn N is still writing
  when turn N+1 tries to restore from Store. This is correct — the engine's
  `--cache-prompt` handles KV reuse internally; the abort prevents serving
  stale KV from a partially-written manifest.
- **send_agent_prompt** returns `status: "idle"` even when the prompt is
  delivered and processed. This is a Paseo UI artifact — the prompt works.

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
