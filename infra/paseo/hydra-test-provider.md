# Hydra TEST Provider — Manual Registration Steps

Paseo does not expose a CLI command to add custom OpenAI-compatible providers.
The built-in providers (claude, codex, copilot, opencode, pi, omp) are hardcoded
adapters. To wire the Hydra TEST rig as a Paseo provider, follow these steps:

## Option A: OpenCode config (recommended for agent workflows)

Add a `hydra-test` provider block to `opencode.jsonc` in the workspace root.
This makes the model available to any opencode agent launched from this workspace.

```jsonc
// Add to the "provider" object in opencode.jsonc:
"hydra-test": {
  "name": "Hydra TEST (qwen3.5-9b-test)",
  "npm": "@ai-sdk/openai-compatible",
  "options": {
    "baseURL": "http://localhost:19000/v1",
    "apiKey": "hydra-test-local",
    "timeout": 120000
  },
  "models": {
    "qwen3.5-9b-test": {
      "name": "Qwen 3.5 9B Test (P100 VM, P/D split)",
      "limit": { "context": 32768, "output": 4096 }
    }
  }
}
```

Then select the model when launching an agent:

```bash
# Via opencode CLI:
opencode --model hydra-test/qwen3.5-9b-test

# Or via Paseo with opencode provider:
paseo run --provider opencode --model hydra-test/qwen3.5-9b-test "your task"
```

## Option B: Paseo daemon config (future, when supported)

If Paseo adds custom provider support, the YAML shape would be:

```yaml
providers:
  - id: hydra-test
    type: openai-compat
    base_url: http://localhost:19000
    default_model: qwen3.5-9b-test
    api_key: ""
```

Place in `~/.paseo/providers.yaml` and reload: `paseo daemon reload`.

## Cores

| Core  | URL                          | Engine        |
|-------|------------------------------|---------------|
| Core-A | http://localhost:19000/v1    | P100 :18086   |
| Core-B | http://localhost:19001/v1    | P100 :18087   |

## Health check

```bash
curl -s http://localhost:19000/v1/models | jq .
# Should list qwen3.5-9b-test and hydra-auto
```

## Applied 2026-08-29: pi harness provider (LIVE)

Registered in `~/.pi/agent/models.json` (the pi harness's real provider registry,
discovered by `pi --list-models`):

    provider id:  hydra-test
    baseUrl:      http://localhost:19000/v1
    api:          openai-completions
    models:       qwen3.5-9b-test, hydra-auto (32.8K ctx / 4.1K out)

Backup of previous config: `~/.pi/agent/models.json.bak-*`.

### Verified end-to-end (pi harness → Hydra TEST rig)

    pi --provider hydra-test --model qwen3.5-9b-test "..."   # plain: 200 OK
    pi --provider hydra-test --model qwen3.5-9b-test "create file via shell..."  # tool loop OK

Tool-calling initially 503'd ("EnginePrefill returned terminal error"): pi's tool
schema inflates the prompt to ~6.7K tokens vs the engines' n_ctx=4096 → prefill
overflow. Fix: engines relaunched with `-c 16384` (VM `~/hydra-test-lane/run-v4.sh`);
VRAM impact ~+1.1 GiB total, well within budget. Test-rig compose default
`n_ctx` for the 9B model should be 16384 going forward.

### Paseo subagent usage (pi harness)

Paseo spawns pi agents with the workspace's pi config, so a Paseo subagent can
target the rig via provider=pi, model=hydra-test/qwen3.5-9b-test once this
models.json entry exists on the host running the paseo daemon (done here).
