# Hydra Agent Test — Running coding agents on top of Hydra

## Concept

A "hydra agent test" means running a coding agent that uses Hydra as its
**sole** LLM backend — the agent's thinking, tool calls, and responses are
all powered by the Hydra multi-GPU inference system. This is the most
realistic end-to-end test because it exercises the full stack (Coordinator →
Store → Hydra Head → llama-engine) under real agent workloads.

Both **Pi** and **Opencode** providers support Hydra models via Paseo.

## Prerequisites

Hydra services must be deployed and healthy:

```bash
curl -s http://localhost:9000/health | python3 -m json.tool
```

Expected: `"status": "healthy"` with all 3 nodes (rtx, rtx3060, p100).

## Pi — direct local connection

Pi reads model definitions from `~/.pi/agent/models.json`. The `hydra`
provider connects to `http://localhost:9000/v1`

```json
{
  "providers": {
    "hydra": {
      "baseUrl": "http://localhost:9000/v1",
      "api": "openai-completions",
      "apiKey": "not-needed",
      "models": [
        { "id": "moe-35b-solo",       "name": "Qwen 3.6 35B-A3B MoE (SOLO RTX 5060 Ti)" },
        { "id": "moe-35b-pd",         "name": "Qwen 3.6 35B-A3B MoE (P/D Split)" },
        { "id": "dense-27b-combined", "name": "Qwen 3.6 27B Dense (COMBINED 5060 Ti + 3060)" },
        { "id": "hydra-auto",         "name": "Hydra Auto (picks best model per prompt)" }
      ]
    }
  }
}
```

Create a Pi agent on any hydra model with provider string `pi/hydra/<model-id>`:

```json
{
  "relationship": { "kind": "subagent" },
  "workspace": { "kind": "current" },
  "title": "Hydra Pi test",
  "provider": "pi/hydra/moe-35b-solo",
  "initialPrompt": "Run `curl -s http://localhost:9000/health` then tell me what model you are running on.",
  "settings": { "modeId": "build" }
}
```

The agent uses Hydra for **all** LLM operations — planning, tool calling,
and response generation. The entire cognitive pipeline runs on the local
Hydra GPUs, with no cloud model involved.

### Available Pi model IDs

| Provider string | Hydra model |
|-----------------|-------------|
| `pi/hydra/moe-35b-solo` | 35B-A3B MoE, SOLO on RTX 5060 Ti |
| `pi/hydra/moe-35b-pd` | 35B-A3B MoE, P/D split (RTX prefill + P100 decode) |
| `pi/hydra/dense-27b-combined` | 27B Dense, COMBINED (5060 Ti + 3060) |
| `pi/hydra/hydra-auto` | Auto-routed (picks best model per prompt) |

## Opencode — local Hydra via opencode provider

The opencode provider supports Hydra models. Model IDs are prefixed with
`hydra/` (e.g. `hydra/moe-35b-solo` not `moe-35b-solo`). The opencode
provider routes these through the local Hydra Coordinator at `localhost:9000`.

### Available Opencode model IDs

| Provider string | Hydra model |
|-----------------|-------------|
| `opencode/hydra/moe-35b-solo` | 35B-A3B MoE, SOLO on RTX 5060 Ti |
| `opencode/hydra/moe-35b-pd` | 35B-A3B MoE, P/D split (RTX prefill + P100 decode) |
| `opencode/hydra/dense-27b-combined` | 27B Dense, COMBINED (5060 Ti + 3060) |
| `opencode/hydra/hydra-auto` | Auto-routed (picks best model per prompt) |

### Opencode agent modes

The opencode provider supports these modes via `settings.modeId`:

| Mode | Description |
|------|-------------|
| `build` | Default agent, executes tools based on configured permissions |
| `plan` | Plan mode, disallows all edit tools |
| `lead` | Team lead, orchestrates work by planning and delegating |

Note: `hydra-auto` and `hydra-dense` appear as modes in `inspect_provider`
but the correct way to select a Hydra model is through the **provider
string**, not the mode. Always use `settings.modeId: "build"` with a
Hydra model provider string.

### Creating an Opencode agent

```json
{
  "relationship": { "kind": "subagent" },
  "workspace": { "kind": "current" },
  "title": "Hydra Opencode test",
  "provider": "opencode/hydra/moe-35b-solo",
  "initialPrompt": "Read `src/core/Hydra.Core/Models/GpuSpec.cs` and summarize it.",
  "settings": { "modeId": "build" }
}
```

Key differences from Pi:
- Provider string uses `opencode/hydra/<model-id>` (not `pi/hydra/...`)
- `settings.modeId` is **required** (use `"build"` for most tasks)
- Model ID includes the `hydra/` prefix in the provider string

## Orchestration preferences

Paseo reads `~/.paseo/orchestration-preferences.json` to pick the default
provider for different agent roles:

```json
{
  "providers": {
    "impl": "opencode/hydra/moe-35b-solo",
    "ui": "opencode/hydra/dense-27b-combined",
    "research": "opencode/hydra/moe-35b-solo",
    "planning": "opencode/hydra/dense-27b-combined",
    "audit": "opencode/hydra/moe-35b-solo"
  },
  "preferences": [
    "Hydra routes inference through the local Coordinator at localhost:9000."
  ]
}
```

Skills that create agents (e.g. `paseo-committee`, `paseo-advisor`) read
the `providers` map to select the right provider for their role. The value
must be a valid `provider/model` string (e.g. `opencode/hydra/moe-35b-solo`,
not `opencode/hydra/balanced` which is not a real model ID).

## Concurrency and slot management

Hydra GPUs have limited concurrency:
- **RTX 5060 Ti**: 2 slots (primary worker)
- **RTX 3060**: 1 slot (COMBINED peer)
- **P100**: 1 slot (P/D decode fallback)

When running multiple Paseo agents simultaneously, they compete for the
same GPU slots. This causes:
- `Service Unavailable: No worker available` — all slots occupied
- `EnginePrefill RPC returned BUSY` — specific slot is busy
- `prefill_slot_busy` warnings in coordinator logs

**Run agents sequentially** to avoid slot contention. If you need parallel
agents, ensure the total slot count across all agents does not exceed the
available GPU slots.

Additionally, `paseo_send_agent_prompt` to a busy agent may fail with
`"A foreground turn is already active"` — wait for the current turn to
complete before sending follow-up prompts.

## Verifying the agent is really on Hydra

1. **Check health** — the agent should run `curl http://localhost:9000/health`
   and see all 3 GPU nodes healthy.

2. **Check the engine** — `curl http://localhost:8080/version` returns
   `"engine":"llama-engine"`.

3. **Check head status** — the agent can inspect the Hydra Head:
   ```bash
   TOKEN=$(cat /mnt/WorkDisk/Workplace/hydra_vortex/.hydra-head-token)
   curl -s -H "Authorization: Bearer $TOKEN" http://localhost:9700/status
   ```
   Expected: `llama.state == "running"`, `health.healthy == true`.

4. **Check coordinator logs** — confirm routing:
   ```bash
   podman logs hydra-system_core_1 2>&1 | grep autoroute_resolved | tail -5
   ```
   Expected: `autoroute_resolved Head=rtx ...` for requests routed to RTX.

5. **No cloud dependency** — Pi agents satisfy this fully. Opencode agents
   connect through the Zen cloud API for the agent protocol but the LLM
   inference itself goes to the local Hydra Coordinator.

## Configuration files

- **Pi**: `~/.pi/agent/models.json` — add/edit hydra models under the
  `hydra` provider entry
- **Opencode**: `~/.config/opencode/opencode.jsonc` or `./opencode.jsonc` —
  the `hydra` provider is an OpenAI-compatible provider pointing at
  `localhost:9000/v1`
- **Paseo orchestration**: `~/.paseo/orchestration-preferences.json` —
  default provider per agent role

## Troubleshooting

- **"No worker available"**: GPU slots are busy — wait for the previous
  request to finish. Check `slots_idle` in
  `curl http://localhost:9000/health`.
- **"EnginePrefill RPC returned BUSY"**: The specific worker's slots are
  full. Wait and retry, or use a different model that routes to a less
  loaded worker.
- **"A foreground turn is already active"**: The Paseo agent is still
  processing a previous prompt. Wait for it to finish before sending
  another.
- **llama-server not responding**: Check podman logs — model loading takes
  ~30s after container start:
  ```bash
  podman logs hydra-system_head-rtx5060ti_1 --tail 20
  ```
- **RTX 3060 unhealthy**: The head container may have a read-only bind
  mount issue. Check with:
  ```bash
  podman logs hydra-system_head-rtx3060_1 --tail 10 | grep ERROR
  ```
- **Agent gets fallback/error text**: Verify the provider string matches
  a valid model ID from the tables above. `opencode/hydra/balanced` is
  not a valid model — use `opencode/hydra/moe-35b-solo` instead.
