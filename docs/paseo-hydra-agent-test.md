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

## Test methodology — read this before writing the prompt

The rest of this document explains **how to wire an agent to Hydra**. This section
explains **how to make the run count as a test**. Skipping it produces a run that
looks green and proves nothing.

### 1. The test is the conversation, not the answer

The point is not "did the agent answer correctly" — a 35B model answering a question
tells you about the model, not about Hydra. The point is **does the stack behave
correctly under sustained, stateful, multi-turn load**.

Hydra's whole reason to exist is KV cache reuse across turns. A single-turn request
exercises none of it. Specifically, these paths are **only** reachable from turn 2
onward:

- warm slot (`kv segment empty`, full prompt, engine computes `n_common`)
- prefix reuse / `cached_tokens`
- session affinity routing
- the `n_tokens > n_past` invariant and the 1-token trick

**Required:** a genuine multi-turn conversation of **at least 5–6 turns** where each
turn depends on the previous ones, so context actually accumulates. Ask the agent to
build on its own earlier answers.

> **Anti-pattern — this has happened, do not repeat it.** Sending one prompt that
> contains four independent numbered steps ("run health, then git log, then read a
> file, then grep") is **not** a multi-turn test. The agent executes them in a single
> turn, context never accumulates across requests, and warm slot is never exercised.
> A run like that was once reported as an end-to-end pass while the exact code path
> under test had never executed. If your prompt could be satisfied in one turn, it is
> not a test of Hydra.

**How to actually drive multiple turns.** `create_agent` starts the conversation;
each subsequent turn is a separate `send_agent_prompt` to the **same `agentId`**.
That is the only way context accumulates — a new `create_agent` starts cold and
resets the session, which defeats the entire test.

```
create_agent(provider="opencode/hydra/moe-35b-solo", initialPrompt=<turn 1>)  -> agentId
send_agent_prompt(agentId, prompt=<turn 2>)   # wait for each turn to finish
send_agent_prompt(agentId, prompt=<turn 3>)
...
```

Wait for each turn to complete before sending the next — a prompt sent into a busy
agent fails with `"A foreground turn is already active"`.

A usable 6-turn shape, where every turn depends on the last:

1. "List the services under `src/` and pick the one with the most `.cs` files."
2. "For the service you picked, summarise what its largest file does."
3. "Name the three riskiest functions in that file and say why."
4. "Pick the riskiest one and describe its failure modes."
5. "Write (do not save) a test that would catch the first failure mode."
6. "Review the test you just wrote — what would it miss?"

Each turn forces the model to reread its own prior output, so the prompt grows
monotonically and prefix reuse is exercised on every turn after the first.

### 2. Monitor during the run, not after

Reconstructing timings from logs afterwards is not monitoring — you cannot see
queue depth, slot contention, or a stall while it is happening, and you will miss
anything the logs rotate away. **Start monitoring before you send the first prompt.**

Run these concurrently with the agent, in background shells:

```bash
# 1. Per-request timeline — the primary signal
podman logs -f hydra-infra_core_1 2>&1 | grep --line-buffered "event=request_timeline"

# 2. Engine-side KV reuse decisions (this is what #470 changed)
podman logs -f hydra-infra_head-rtx5060ti_1 2>&1 | grep --line-buffered -E "N_COMMON|restored logits|slot .* released"

# 3. Crash / restart watch — a restart loop is silent in the timeline
podman logs -f hydra-infra_head-rtx5060ti_1 2>&1 | grep --line-buffered -E "GGML_ASSERT|exit_code|attempting restart"
```

Also open **Grafana :3000** (`bash scripts/start-env.sh` if the infra pod is down) and
watch GPU utilisation and VRAM alongside the request timeline. Prometheus is :9091.

### 3. Pass/fail criteria — check these, do not eyeball

State the expected value **before** the run, then verify. Prose conclusions like
"felt fast" are not results.

| # | Criterion | How to check | Fail looks like |
|---|---|---|---|
| 1 | `cached_tokens` climbs turn over turn | `usage.prompt_tokens_details.cached_tokens` in each response | flat 0 → prefix reuse is broken |
| 2 | `n_common` fires on turns ≥ 2 | `#PD-TRACE N_COMMON` in engine logs | absent → warm slot path not taken |
| 3 | `restore_kv_ms == 0` on warm turns | `event=request_timeline` | non-zero → doing a full restore when it should reuse |
| 4 | `queue_wait_ms` ≈ 0 for a single agent | `event=request_timeline` | seconds/minutes → slot contention or a stuck lease |
| 5 | Throughput within ~2× of baseline | `tokens_out / (decode_ms/1000)` | see baseline table below |
| 6 | No engine restarts during the run | crash-watch shell | any `attempting restart` invalidates the run |
| 7 | `reasoning_content` present when reasoning is on | response JSON | empty while `content` is also empty → fields being dropped |
| 8 | Route type matches intent | `route_type=` in timeline | unexpected `migration`/`affinity` means the router disagreed with you |

Compute throughput per request:

```bash
podman logs hydra-infra_core_1 2>&1 | grep "request_timeline" | grep "status=done" \
 | sed 's/^\[[^]]*\] //' | sort -u | python3 -c "
import sys,re
for line in sys.stdin:
    f=dict(re.findall(r'([a-z_]+)=([^\s]*)',line))
    t=int(f.get('tokens_out') or 0); d=float(f.get('decode_ms') or 0)
    if t and d: print(f\"{f.get('node'):<7}{f.get('route_type'):<13}{t:>5} tok {d:>8.0f} ms {t/(d/1000):>7.1f} tok/s  queue {float(f.get('queue_wait_ms') or 0)/1000:>6.1f}s\")
"
```

### 4. Baselines

| Node | Decode baseline | Notes |
|---|---|---|
| RTX 5060 Ti | ~200 tok/s | SOLO. COMBINED layer-split is materially slower — the peer's share moves over RPC |
| RTX 3060 | ~60 tok/s | COMBINED peer |
| P100 | 28 tok/s | P/D decode side |

Sustained throughput **2–4× under baseline**, multi-minute `queue_wait_ms`, or a single
request taking minutes to emit a few hundred tokens are all **findings**, not noise.
Record them with numbers. A trivial 4-tool-call agent task once took over an hour this
way, and the cause was the stack, not the task.

### 5. Choosing the model

Pin an explicit model. Do **not** use `hydra-auto` for a verification run — it may
route into a profile you did not intend, and a routing surprise mid-test invalidates
your criteria. Use `hydra-auto` only when routing itself is what you are testing.

If a profile is known-broken, say so in the run notes rather than silently switching.

### 6. Recording the result

Report, at minimum: the model used, number of turns, the per-request table from §3,
which criteria passed and which failed with values, and anything observed in Grafana.
If you did not run a criterion, say "not run" — never imply coverage you do not have.

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

## Verifying the agent is really routed to Hydra

> These checks prove **routing** — that inference is reaching the local stack instead
> of a cloud model. They do **not** prove the implementation under test is correct.
> A run can pass every check below and still have exercised none of the code you
> changed. For correctness, use the pass/fail criteria in
> [Test methodology §3](#3-passfail-criteria--check-these-do-not-eyeball).

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
   podman logs hydra-infra_core_1 2>&1 | grep autoroute_resolved | tail -5
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
