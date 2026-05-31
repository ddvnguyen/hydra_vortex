# Hydra E2E Test Guide

For coding agents and developers running or extending the end-to-end test suite.

---

## Test Tiers

The suite is split into two tiers. **Always know which tier you are running.**

### Tier 1 — Mocked (no real services)

RPC connections are patched in-process. Run on any machine, any time.

| File | What it tests |
|------|---------------|
| `test_m1_e2e.py` | Coordinator HTTP: routing, session affinity, migration, eviction, health/status endpoints |
| `test_m2_e2e.py` | Prefix checkpoints, chunked dedup routing, store_restore action, n_past guard |

### Tier 2 — Full stack (real services required)

All 6 services must be up and healthy before running. GPU hardware required.

| File | What it tests |
|------|---------------|
| `test_e2e.py` | Direct RPC path: llama RTX → save → Store → restore → llama P100, cache hit verification |
| `test_full_workflow_e2e.py` | Full stack through Coordinator: completions, streaming, session lifecycle, migration, prefix checkpoints |
| `test_large_prompt_e2e.py` | Large prompts (8K–48K tokens) through Coordinator, llama metrics verification, continuation |
| `test_stress_e2e.py` | 4 concurrent completions, cross-session consistency, timing ratio assertions |

---

## Install

From the repo root:

```bash
pip install -e ".[dev]"
```

For full stack tests that hit monitoring endpoints, install all extras:

```bash
pip install -e ".[all]"
```

---

## Run — Tier 1 (mocked)

No environment variables needed.

```bash
pytest tests/e2e/test_m1_e2e.py tests/e2e/test_m2_e2e.py -v
```

With JUnit XML output for CI:

```bash
pytest tests/e2e/test_m1_e2e.py tests/e2e/test_m2_e2e.py -v \
  --junit-xml=TestResults/e2e-mocked.xml
```

---

## Run — Tier 2 (full stack)

### Service start order

Start in this order and wait for each to be healthy before proceeding:

```
1. llama-server RTX     :8080   (host machine)
2. llama-server P100    :8086   (KVM VM 192.168.122.21)
3. Hydra Store          :9500   (host machine)
4. Hydra Agent RTX      :9601   (host machine)
5. Hydra Agent P100     :9602   (KVM VM 192.168.122.21)
6. Coordinator          :9000   (host machine)
```

Quick health check before running:

```bash
curl -sf http://localhost:9000/health | python3 -m json.tool
```

The response `"status": "healthy"` means all nodes are reachable.

### Environment variables

All have defaults matching the standard local setup. Override only when addresses differ.

| Variable | Default | Description |
|----------|---------|-------------|
| `COORD_URL` | `http://localhost:9000` | Coordinator HTTP endpoint |
| `RTX_LLAMA_URL` | `http://localhost:8080` | RTX llama-server |
| `P100_LLAMA_URL` | `http://192.168.122.21:8086` | P100 llama-server (VM) |
| `LLAMA_RTX_URL` | `http://localhost:8080` | RTX llama (used by large/stress tests) |
| `LLAMA_P100_URL` | `http://192.168.122.21:8086` | P100 llama (used by large/stress tests) |
| `RTX_AGENT_HOST` | `127.0.0.1` | RTX agent RPC host |
| `RTX_AGENT_PORT` | `9601` | RTX agent RPC port |
| `P100_AGENT_HOST` | `192.168.122.21` | P100 agent RPC host (VM) |
| `P100_AGENT_PORT` | `9602` | P100 agent RPC port |
| `STORE_HOST` | `127.0.0.1` | Store RPC host |
| `STORE_PORT` | `9500` | Store RPC port |

### Run commands

Run all full-stack tests (marked `@pytest.mark.e2e`):

```bash
pytest tests/e2e/ -m e2e -v --timeout=300
```

Run a single file:

```bash
pytest tests/e2e/test_full_workflow_e2e.py -v --timeout=300
```

Skip the slow large-prompt parametrized tests:

```bash
pytest tests/e2e/ -m e2e -v --timeout=300 \
  --ignore=tests/e2e/test_large_prompt_e2e.py \
  --ignore=tests/e2e/test_stress_e2e.py
```

With JUnit XML output:

```bash
pytest tests/e2e/ -m e2e -v --timeout=300 \
  --junit-xml=TestResults/e2e-full.xml
```

---

## Critical constraints

These are not test bugs — they reflect real hardware invariants:

- **n_tokens MUST be > n_past** when sending a continuation after KV restore. If the continuation prompt has fewer tokens than `n_past`, llama nukes the cache. The Coordinator's n_past guard handles this automatically, but tests verify it.
- **cache_n > 0** in the response `timings` confirms the KV cache was actually used after a restore. A `cache_n = 0` result means the restore failed silently or the continuation prompt was too short.
- **prompt_ms < 5000** on a cached continuation confirms the fast path was taken. A full re-prefill on a 48K context would take 12+ minutes on P100.
- **SSM truncation is broken** for qwen35moe (`--cache-prompt` is useless). Full KV state save/restore is the only working migration path.

---

## Adding new e2e tests

- **Mocked tests** (no services): do not use `@pytest.mark.e2e`. Patch RpcClient via `unittest.mock.patch` at `coordinator.health.RpcClient` and `coordinator.state_manager.RpcClient`.
- **Full-stack tests** (real services): add `@pytest.mark.e2e` and `@pytest.mark.asyncio`. Use `uuid4().hex[:12]` session IDs to avoid collisions between runs. Always clean up sessions in a `try/except` block at the end.
- Do not add `@pytest.mark.e2e` to mocked tests — it controls which tests run in CI and which require hardware.
