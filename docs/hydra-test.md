# Hydra TEST — P100 Isolated Rig Runbook (T2)

Test counterpart to `DevelopmentRunBook.md` / prod `infra/docker-compose.hydra.yml`.
Same infra (Postgres/Redis/Loki/Prom/OTel on :3100/:9091/:4318), isolated by DB + ports.

## Overview

- **Goal:** Paseo agent workflow tests exercising the real Hydra.Core + Hydra Head + llama-engine stack without touching production traffic or RTX rigs.
- **Topology T2:** two Hydra.Core test instances (core-A / core-B), each bound to one head + one engine. Mirrors prod's `hydra-system` layout but port-shifted +10000.
- **Source:** `infra/docker-compose.hydra-test.yml` (6 containers), `src/core/Hydra.Core/appsettings.Test.json` (shared, gated by `HYDRA_INSTANCE=test`), `infra/sql/init-hydra-test.sql` (N1 DB).
- **Engine binaries:** same resident builds as prod at `~/hydra-min-test/llama-engine`; no rebuild. `LD_LIBRARY_PATH=$HOME/hydra-min-test` for both engines.

## Port Plan (+10000 vs prod)

Prod: core :9000/:9500/:9501, heads :9700/:9701, engines :8080/:8081/:8086.
Test shifts +10000:

| Service | Port | Purpose | Prod counterpart |
|---------|------|---------|------------------|
| hydra-core-test-a | 19000 | HTTP API (OpenAI-compat) | :9000 |
| hydra-core-test-a | 19500 | Store RPC | :9500 |
| hydra-core-test-a | 19501 | Store debug/metrics | :9501 |
| hydra-core-test-b | 19001 | HTTP API | :9000 |
| hydra-core-test-b | 19502 | Store RPC | :9500 |
| hydra-core-test-b | 19503 | Store debug/metrics | :9501 |
| hydra-head-test-a | 19700 | Head API (/status, /health) | :9700 |
| hydra-head-test-b | 19701 | Head API | :9701 |
| llama-engine-test-a | 18086 | HTTP completions | :8080 / :8086 |
| llama-engine-test-a | 19513 | hydra RPC (StateGet/Put) | :9502 / :9503 |
| llama-engine-test-b | 18087 | HTTP completions | :8081 / :8086 |
| llama-engine-test-b | 19514 | hydra RPC | :9504 |

Volumes: `/mnt/llm-ram/hydra-test-store` and `/tmp/hydra-test-l1` (host tmpfs dirs, mode 1777/777).

## DB Plan (N1)

- **Isolation:** separate Postgres database `hydra_test` on the same `postgres` container as prod (`hydra_store`). No separate container (N1), no separate schema (N2) — avoids infra duplication while guaranteeing data isolation.
- **Init:** `infra/sql/init-hydra-test.sql` — idempotent `DO $$ ... EXCEPTION WHEN duplicate_database` block. **Not** auto-mounted into `infra/docker-compose.infra.yml` (avoids touching prod compose). Applied by `scripts/hydra-test/up.sh` via:
  ```bash
  podman exec pg psql -U hydra -d postgres -c "SELECT 'CREATE DATABASE hydra_test OWNER hydra' WHERE NOT EXISTS (SELECT 1 FROM pg_database WHERE datname='hydra_test')\\gexec"
  # fallback: psql -f infra/sql/init-hydra-test.sql
  ```
- **Verify:** `podman exec pg psql -U hydra -l` should list `hydra`, `hydra_store`, and `hydra_test`.
- **Connection:** `HYDRA_STORE_PG_CONN=Host=localhost;Database=hydra_test;Username=hydra;Password=hydra` (also `HYDRA_CHUNK_CACHE_PG_CONN`). Password override via env/user-secret in real prod.

## Env-Var Matrix

| Var | Value | Scope |
|-----|-------|-------|
| `HYDRA_INSTANCE` | `test` | Both cores — gates `appsettings.Test.json` + `HydraTestConfig` validation. Unset = prod (untouched). |
| `HYDRA_INSTANCE_ID` | `A` \| `B` | Per-core identity; must be `A` or `B` when `HYDRA_INSTANCE=test`. `Routing.AllowedInstanceIds=[A,B]`. |
| `HYDRA_COORD_PORT` | `19000` (A) / `19001` (B) | Test cores |
| `HYDRA_STORE_PORT` | `19500` (A) / `19502` (B) | Test stores |
| `HYDRA_STORE_DEBUG_PORT` | `19501` (A) / `19503` (B) | Test metrics |
| `HYDRA_COORD_CONFIG_FILE` | `/etc/hydra/config/workers-test-a.json` / `workers-test-b.json` | Per-core worker binding (engine-A vs engine-B) |
| `HYDRA_HEAD_AUTH_TOKEN` | `$(cat .hydra-head-token)` | Required for heads (same as prod) |
| `LD_LIBRARY_PATH` | `$HOME/hydra-min-test` | Both engines (host dir bind-mounted to `/opt/hydra-min-test`) |
| `CUDA_VISIBLE_DEVICES` | `0` (A) / `1` (B) | Engine GPU pinning |

Prod is zero-trust: when `HYDRA_INSTANCE` is unset, `HydraTestConfig.ValidateIfTestInstance()` is a no-op and no test config is read.

## How to Bring Up

```bash
export HYDRA_HEAD_AUTH_TOKEN=$(cat .hydra-head-token)
bash scripts/hydra-test/up.sh
# waits for all 6 containers healthy (poll 5s, timeout 120s), prints:
#   Core-A  : http://localhost:19000/v1/chat/completions
#   Core-B  : http://localhost:19001/v1/chat/completions
#   Head-A  : http://localhost:19700/status
#   Head-B  : http://localhost:19701/status
```

Manual equivalent:
```bash
podman compose -f infra/docker-compose.infra.yml -f infra/docker-compose.hydra-test.yml up -d
curl -s http://localhost:19000/v1/models | jq .
curl -s http://localhost:19001/v1/models | jq .
```

## How to Tear Down

```bash
bash scripts/hydra-test/down.sh
# idempotent: podman compose ... down + rm orphans + pgrep check for 18086/18087

# Status at any time:
bash scripts/hydra-test/status.sh
# prints per-service OK/DEGRADED/DOWN + overall verdict
```

## How to Point a Paseo Workflow at It

1. **Provider config** (`infra/paseo-providers-hydra-test.yaml`):
   ```yaml
   id: hydra-test
   type: openai-compat
   base_url: http://localhost:19000
   default_model: minicpm5-1b  # alias from infra/hydra-core/config/models.json
   api_key: not-required
   description: Hydra TEST instance (2-core P/D split on P100 VM, dedicated hydra_test DB)
   ```
   Lead copies to `~/.paseo/providers.yaml` after rig is up; Paseo daemon picks it up on next config reload.

2. **Agent workflow:** set the workflow's provider to `hydra-test` (Paseo per-workflow param). The agent's tool chain then routes LLM calls to `http://localhost:19000` (core-A) or `:19001` (core-B). For load-split tests, the xUnit driver hits both cores 5/5 concurrent.

3. **Verify no prod contamination:** the `Tests.AgentWorkflow` xUnit snapshots `hydra_requests_total` (or skips if not exposed) before/after the 10 completions and asserts it is unchanged on `:9000`.

## VM Hygiene

- Engines run as `ubuntu:22.04` containers with `network_mode: host`, host bind-mounts for `~/hydra-min-test` (binary + libs), `/mnt/SSD` (models, ro), `/mnt/llm-ram` (KV, rw). No host stray `llama-engine --port 18086` pids — `down.sh` `pgrep -f "llama-engine.*1808[67]"` catches leaks.
- Store dirs: `/mnt/llm-ram/hydra-test-store` (40G tmpfs per core entrypoint) + `/tmp/hydra-test-l1` (L1 chunk cache, 2G cap). Backups under `/mnt/SSD/hydra-backup-test-{a,b}` (separate from prod `/mnt/SSD/hydra-backup`).
- Heads are the same `localhost/hydra-head:rtx` image as prod; configs are `infra/hydra-head/config/node-test-{a,b}.yaml` (to be added when wiring real engines).

## Blast-Radius Mitigations

- **Port isolation:** +10000 offset guarantees no prod port collision; `network_mode: host` but distinct ports.
- **DB isolation:** N1 separate DB `hydra_test` — test writes cannot affect `hydra_store` manifests/chunks.
- **Token cap:** test workflows should enforce `max_tokens` (e.g. 32 in `Tests.AgentWorkflow`) and queue-depth watchdogs; `HYDRA_TEST_URL` only on the test Paseo profile, prod URL on a different env.
- **Always-on policy (v1):** keep test stack up via `restart: unless-stopped`; tear down via `down.sh` if cost shows up (P100 already owned, overhead ~200 MB RSS + 30G tmpfs shared with prod).
- **Health gating:** `up.sh` refuses to report success until all 6 health endpoints pass; `status.sh` gives a one-line verdict per service for quick triage.
- **No prod file touches:** `infra/docker-compose.hydra.yml`, prod `appsettings*.json`, and `#703` lane are untouched — checked by PR diff scope.
