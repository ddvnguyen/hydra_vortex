# Hydra System Test Guide

For coding agents and developers running or extending the system test suite.

---

## Test Tiers

The suite is split into two tiers. **Always know which tier you are running.**

### Tier 1 — Mocked (no real services)

RPC connections are patched in-process. Run on any machine, any time.

| File | What it tests |
|------|---------------|
| `test_m1_system.py` | Coordinator HTTP: routing, session affinity, migration, eviction, health/status endpoints |
| `test_m2_system.py` | Prefix checkpoints, chunked dedup routing, store_restore action, n_past guard |

### Tier 1.5 — Standalone Store + PG (only Store + PostgreSQL needed, no GPUs)

Requires Store and PostgreSQL, but no GPU services.  Start with:
`docker compose up -d postgres store`

| File | What it tests |
|------|---------------|
| `test_store_persistence_e2e.py` | Store RPC persistence: PutChunked/GetChunked dedup, manifest CRUD, debug endpoint, write-behind verification, Store restart + recovery |

### Tier 2 — Full stack (real services required)

All 6 services must be up and healthy before running. GPU hardware required.

| File | What it tests |
|------|---------------|
| `test_system.py` | Direct RPC path: llama RTX → save → Store → restore → llama P100, cache hit verification |
| `test_full_workflow_system.py` | Full stack through Coordinator: completions, streaming, session lifecycle, migration, prefix checkpoints |
| `test_large_prompt_system.py` | Large prompts (8K–48K tokens) through Coordinator, llama metrics verification, continuation |
| `test_stress_system.py` | 4 concurrent completions, cross-session consistency, timing ratio assertions |
| `test_persistence_system.py` | PG metadata verification after real migration: sessions/chunks tables populated correctly, Store debug stats |

---

## Run — Tier 1.5 (Store + PostgreSQL, no GPU)

Requires only Store + PG from the compose file:

```bash
docker compose up -d postgres store
pytest tests/system/test_store_persistence_e2e.py -v
```

This exercises the full Store persistence layer (chunked ops, manifest CRUD, dedup, write-behind, restart recovery) without needing any GPU services.

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
pytest tests/system/test_m1_system.py tests/system/test_m2_system.py -v
```

With JUnit XML output for CI:

```bash
pytest tests/system/test_m1_system.py tests/system/test_m2_system.py -v \
  --junit-xml=TestResults/system-mocked.xml
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
| `P100_AGENT_HOST` | `127.0.0.1` | P100 agent RPC host (same host as coordinator) |
| `P100_AGENT_PORT` | `9602` | P100 agent RPC port |
| `STORE_HOST` | `127.0.0.1` | Store RPC host |
| `STORE_PORT` | `9500` | Store RPC port |
| `STORE_DEBUG_URL` | `http://127.0.0.1:9501` | Store HTTP debug endpoint |
| `PG_DSN` | `postgresql://hydra:hydra@localhost:5432/hydra_store` | PostgreSQL DSN for persistence verification |

### Run commands

Run all full-stack tests (marked `@pytest.mark.system`):

```bash
pytest tests/system/ -m system -v --timeout=300
```

Run a single file:

```bash
pytest tests/system/test_full_workflow_system.py -v --timeout=300
```

Skip the slow large-prompt parametrized tests:

```bash
pytest tests/system/ -m system -v --timeout=300 \
  --ignore=tests/system/test_large_prompt_system.py \
  --ignore=tests/system/test_stress_system.py
```

With JUnit XML output:

```bash
pytest tests/system/ -m system -v --timeout=300 \
  --junit-xml=TestResults/system-full.xml
```

---

## Critical constraints

These are not test bugs — they reflect real hardware invariants:

- **n_tokens MUST be > n_past** when sending a continuation after KV restore. If the continuation prompt has fewer tokens than `n_past`, llama nukes the cache. The Coordinator's n_past guard handles this automatically, but tests verify it.
- **cache_n > 0** in the response `timings` confirms the KV cache was actually used after a restore. A `cache_n = 0` result means the restore failed silently or the continuation prompt was too short.
- **prompt_ms < 5000** on a cached continuation confirms the fast path was taken. A full re-prefill on a 48K context would take 12+ minutes on P100.
- **SSM truncation is broken** for qwen35moe (`--cache-prompt` is useless). Full KV state save/restore is the only working migration path.

---

## Adding new system tests

- **Mocked tests** (no services): do not use `@pytest.mark.system`. Patch RpcClient via `unittest.mock.patch` at `coordinator.health.RpcClient` and `coordinator.state_manager.RpcClient`.
- **Full-stack tests** (real services): add `@pytest.mark.system` and `@pytest.mark.asyncio`. Use `uuid4().hex[:12]` session IDs to avoid collisions between runs. Always clean up sessions in a `try/except` block at the end.
- Do not add `@pytest.mark.system` to mocked tests — it controls which tests run in CI and which require hardware.
