# Hydra — Ports, Services, Env Vars & Deploy Flow

> Canonical reference for **what runs where, on what port, with what env
> vars, and how the deploy flow works**. This doc is the source of truth
> for service endpoints; if a port or env var is wrong here, fix this
> doc and the affected scripts. Referenced from `CLAUDE.md` "Read These
> First" and `docs/hydra-system-pod.md`.

Last verified: 2026-07-15 against the live system.

---

## 1. Service / Port Table

The host runs **two pods** (one for Hydra, one for the observability infra)
plus **host-network processes** (ssh, Paseo daemon, opencode). All ports
are host-level because both pods use `network_mode: host`.

### 1.1 `pod_hydra-system` (the inference stack)

| Service | Container | Port(s) | URL | Notes |
|---|---|---|---|---|
| **Hydra.Core** (C#) | `hydra-system_core_1` | `9000` (HTTP API) | http://localhost:9000 | OpenAI-compat chat completions, /health, /metrics |
| | | `9500` (Store RPC) | hydra://localhost:9500 | chunked dedup, prefix checkpoint storage |
| | | `9501` (Store health) | http://localhost:9501/health | separate bind for K8s probes |
| **Head — RTX 5060 Ti** (Go + llama) | `hydra-system_head-rtx5060ti_1` | `9700` (Head API) | http://localhost:9700 | health, status, /metrics |
| | | `8080` (llama HTTP) | http://localhost:8080 | OpenAI-compat, direct |
| | | `9503` (llama RPC) | `hydra://localhost:9503` | StateGet/Put, M2 zero-copy, ggml-RPC |
| | | `9100` (node_exporter) | http://localhost:9100/metrics | in-container sidecar |
| | | `9835` (nvidia_gpu_exporter) | http://localhost:9835/metrics | in-container sidecar |
| **Head — RTX 3060** (Go + llama) | `hydra-system_head-rtx3060_1` | `9701` (Head API) | http://localhost:9701 | second head, same image, `CUDA_VISIBLE_DEVICES=1` |
| | | `8081` (llama HTTP) | http://localhost:8081 | direct |
| | | `9504` (llama RPC + ggml-RPC peer) | `hydra://localhost:9504` | also serves as ggml-RPC peer for COMBINED-mode dispatch |

**Key gotcha — port 8081 must be free.** The rtx3060's llama-server binds
`8081` inside the container. With `network_mode: host` this is the
**host's** `8081`. If any other service is on host `:8081`, the rtx3060
crash-loops (silent bind failure → health probe sees that other service
→ "invalid character 'G' looking for beginning of value" → 10 failures
→ restart). **If you change the rtx3060's port, also update
`infra/hydra-core/config/workers.json` `llama_url`** (it hardcodes
`http://localhost:8081`).

### 1.2 `infra-host` pod (observability / store)

| Service | Container | Port | URL | Notes |
|---|---|---|---|---|
| **Grafana** | `infra-grafana` | `3000` | http://localhost:3000 | dashboards, hydra metrics |
| **Prometheus** | `infra-prometheus` | `9090` | http://localhost:9090 | metrics, alert rules |
| | | `9091` | http://localhost:9091 | secondary / federation (rare) |
| **Loki** | `infra-loki` | `3100` | http://localhost:3100 | log aggregation |
| **OTel Collector** | `infra-otel-collector` | `4317` (gRPC) | otel://localhost:4317 | OTLP receiver |
| | | `4318` (HTTP) | http://localhost:4318 | OTLP/HTTP receiver (hydra-head pushes here) |
| **PostgreSQL** (Store) | `infra-postgres` | `5432` | postgres://localhost:5432 | `hydra_store` DB; `pg_isready` health |
| **pgAdmin** | `infra-pgadmin` | none (web only via reverse path) | via openwebui or curl | management UI, no host port |
| **Open WebUI** | `infra-openwebui` | `8080` (HTTP) | http://localhost:8080 | web chat client; **collides with rtx llama-server's host :8080** — see `infra-renderer` collision lesson below |
| **Grafana Image Renderer** | `infra-renderer` | **`28081`** (was `8081` — moved in PR #440) | http://localhost:28081 | used by Grafana for PNG dashboard export |

**Key gotcha — `infra-renderer` was on `8081` until 2026-07-15.** That
clashed with the rtx3060's llama-server (also wants `8081`); the
rtx3060 was in a 91+ cycle restart loop until we moved the renderer
to `28081`. The renderer's env var changed name across image versions:
`HTTP_PORT=8081` is silently ignored by the current image; the CLI
flag is `--server.addr` / env `SERVER_ADDR=:28081`. If you see the
renderer still binding `:8081` after an env change, the env var name
is wrong.

### 1.3 P100 (KVM VM at `192.168.122.21`)

| Service | Port | URL | Notes |
|---|---|---|---|
| llama-server (decode-only) | `8086` (HTTP) | http://192.168.122.21:8086 | OpenAI-compat |
| | `9502` (hydra RPC) | `hydra://192.168.122.21:9502` | StateGet/Put |

Reached over the NAT bridge into the VM. **Note: the P100 uses a
different port (`8086`) than the host GPUs (`8080`/`8081`)** — the
Core's `workers.json` reflects this.

### 1.4 Host processes

| Service | Port | Notes |
|---|---|---|
| SSH | `22` | |
| Paseo daemon | `6767` | MCP server |
| opencode | `4096` | coding-agent runtime |
| coder (ide) | `2112`, `2113` | |

---

## 2. Env Vars

### 2.1 `infra-hydra-system` env (from `.env` + `compose`)

These come from `.env` in the worktree root, loaded by
`scripts/deploy-hydra-head.sh` and substituted into the compose file
via `${HYDRA_*}` placeholders.

| Env var | Source | Default | Effect |
|---|---|---|---|
| `HYDRA_HEAD_RTX_NODE_CONFIG` | `.env` | `node-rtx.yaml` (single model-agnostic profile) | which node config the rtx head uses |
| `HYDRA_HEAD_RTX3060_NODE_CONFIG` | `.env` | `node-rtx3060.yaml` (peer-only) | same for rtx3060 |
| `HYDRA_HEAD_AUTH_TOKEN` | `.hydra-head-token` (generated by deploy script) | random 64-hex | bearer token for head API |
| `HYDRA_COORD_CONFIG_FILE` | `.env` | `/etc/hydra/config/workers.json` | bind-mounted into Core; tells Core which workers exist and their URLs. Single model-agnostic profile (#481 Phase 2c) — per-model config is in models.json. |
| `HYDRA_LLAMA_ENGINE` | `.env` (MoE) | `true` | enables engine-mode (vs legacy server-mode) |
| `HYDRA_COORD_COMBINED_ENABLED` | `.env` (MoE) | `true` | enables COMBINED-mode routing |
| `HYDRA_COORD_MULTI_ENGINE_POLICY` | `.env` (MoE) | `combined` | multi-GPU strategy: `combined` / `static` / `solo` |
| `HYDRA_COORD_MULTI_ENGINE_THRESHOLD` | `.env` (MoE) | `4096` | token threshold above which the MultiEngineRouter considers 2-GPU |
| `HYDRA_COORD_ALLOW_CROSS_MODEL_KV_REUSE` | `.env` (MoE) | `true` | allow 35B MoE Q3_K prefill → P100 Q5_K decode (cross-model KV reuse) |
| `HYDRA_COORD_ALLOWED_MODELS` | `compose` | (model list) | hard cap on which models the Core will accept |
| `HYDRA_COORD_NO_STORE_KV_RESTORE` | `compose` | `false` | disable the store-side KV restore path (for testing) |

### 2.2 `infra-host` env (Quadlet + compose)

Quadlet env vars live in `infra/quadlets/*.container` and are copied to
`~/.config/containers/systemd/` by `scripts/deploy-infra.sh`. Compose env
vars live in `infra/docker-compose.infra.yml`.

| Env var | Where | Effect |
|---|---|---|
| `SERVER_ADDR=:28081` | renderer Quadlet + compose | renderer's bind port (was `HTTP_PORT=8081` — silently ignored) |
| `GF_RENDERING_SERVER_URL=http://localhost:28081/render` | grafana Quadlet + compose | grafana's renderer URL |
| `GF_RENDERING_CALLBACK_URL=http://localhost:3000/` | grafana Quadlet + compose | renderer callback to grafana |
| `GF_AUTH_ANONYMOUS_ENABLED=true` | grafana Quadlet | anonymous read-only access (dev only) |
| `POSTGRES_*` | postgres Quadlet | DB credentials (dev defaults: `hydra`/`hydra`) |
| `REGISTRY_AUTH_FILE` | `compose` | podman auth for ghcr.io pulls (rtx head container) |
| `NVIDIA_VISIBLE_DEVICES`, `CUDA_VISIBLE_DEVICES`, `LD_LIBRARY_PATH` | compose + Quadlet | GPU pinning + CUDA libs |

---

## 3. Quadlet Deploy Flow (the gotcha that bit us on PR #440)

The infra-host pod services run as **systemd-managed Quadlet
containers** (NOT docker compose), located in
`~/.config/containers/systemd/`. The git-tracked files in
`infra/quadlets/*.container` are **generated** from
`infra/docker-compose.infra.yml` by `scripts/regenerate-quadlets.sh`,
then post-processed by `scripts/patch-quadlets.sh`, then copied to
`~/.config/containers/systemd/` by `scripts/deploy-infra.sh`.

### 3.1 The correct flow to change a Quadlet

```bash
# 1. Edit the CANONICAL source: infra/docker-compose.infra.yml
#    (or infra/docker-compose.hydra.yml for the hydra pod)
vim infra/docker-compose.infra.yml  # e.g. add - SERVER_ADDR=:28081 to renderer env

# 2. Regenerate the .container file
bash scripts/regenerate-quadlets.sh

# 3. Patch the generated file (adds Notify=healthy, [Install], etc.)
bash scripts/patch-quadlets.sh

# 4. Commit the regenerated + patched .container file
git add infra/quadlets/
git commit -m "fix(infra): ..."

# 5. Deploy
bash scripts/deploy-infra.sh
# This copies the .container files to ~/.config/containers/systemd/,
# runs `systemctl --user daemon-reload`, and restarts the services.
```

### 3.2 What I did wrong on PR #440 (lesson learned)

I edited `infra/quadlets/infra-renderer.container` directly, then
copied the file to `~/.config/containers/systemd/`, then reloaded the
daemon and restarted. The renderer still bound `:8081`.

**The root cause was a different bug**: the env var name changed.
The old file said `HTTP_PORT=8081`; the new image's CLI uses
`SERVER_ADDR`. Both the git file and the deployed file needed
`SERVER_ADDR=:28081`. Once I fixed the env var name, the port
move worked.

### 3.3 The other gotcha: env var name drift across image versions

**Symptom**: `systemctl --user daemon-reload` + restart of a Quadlet
service **does not move the port** the process binds, even though
the `.container` file was edited and `daemon-reload` was run.

**Diagnostic**: exec into the new container, `ps -e` shows the right
process, but `ss -tlnp` shows the OLD port. The container env
(`podman exec <name> env | grep PORT`) shows the new value, but the
process ignored it.

**Cause**: the env var NAME was wrong. Most service CLIs (kingpin,
spf13/cobra, urfave/cli) take specific named env vars. The default
fallback is whatever's hardcoded in the binary.

**Fix**: check the binary's `--help` (e.g. `podman exec <name>
<binary> --help`) for the actual env var / flag name. Examples:

| Service | Old env var | Current env var |
|---|---|---|
| `grafana-image-renderer` | `HTTP_PORT=8081` (ignored) | `SERVER_ADDR=:28081` |
| `loki` | (config file only) | (config file only) |
| `prometheus` | (config file only) | (config file only) |
| `postgres` | `POSTGRES_*` (stable) | `POSTGRES_*` (stable) |

---

## 4. Hardcoded Port References (places to update if a port changes)

When changing any of the following ports, **all of these files must
be updated in lockstep**. The lead has a state file
`orchestration/state/201-245-246-prefix-restore.md` (and similar) that
flags the port-touching work; future port changes should update both
the code and the state file.

### 4.1 Port `8081` (rtx3060 llama-server)

| File | Line | Content |
|---|---|---|
| `infra/hydra-head/config/node-rtx3060.yaml` | `port: 8081` | head's llama-server config |
| `infra/hydra-core/config/workers.json` | `rtx3060.llama_url: http://localhost:8081` | Core's view of rtx3060's HTTP |
| `infra/prometheus/prometheus.yml` | `hydra-llama-rtx3060: targets: ["localhost:8081"]` | Prometheus scrape |
| `docs/architecture.md`, `docs/diagrams.md` | various | architecture diagrams |
| `docs/hydra-system-pod.md` | "ports 8081/9504" | TL;DR |

### 4.2 Port `28081` (Grafana renderer)

| File | Line | Content |
|---|---|---|
| `infra/quadlets/infra-renderer.container` | `Environment=SERVER_ADDR=:28081` | Quadlet (generated) |
| `infra/quadlets/infra-grafana.container` | `GF_RENDERING_SERVER_URL=http://localhost:28081/render` | Quadlet (generated) |
| `infra/docker-compose.infra.yml` | renderer + grafana sections | canonical source |
| `~/.config/containers/systemd/infra-{renderer,grafana}.container` | (copies of the above) | runtime deploy |

### 4.3 Port `8086` (P100 llama)

| File | Line | Content |
|---|---|---|
| `infra/hydra-core/config/workers.json` | `p100.host: localhost` (refers to `192.168.122.21` indirectly) | |
| `infra/prometheus/prometheus.yml` | `hydra-llama-p100: targets: ["192.168.122.21:8086"]` | |
| `docs/RUNBOOK.md`, `docs/architecture.md`, `docs/diagrams.md` | various | |

---

## 5. Quick-Reference: full verify block

```bash
# Core (the entry point)
curl -s http://localhost:9000/health | jq .

# Core's view of all 3 nodes
curl -s http://localhost:9000/metrics | grep -E "hydra_(core|llama|prefix)" | head

# Heads (Go agent + llama subprocess on each host GPU)
curl -s http://localhost:9700/health   # RTX 5060 Ti head + llama :8080
curl -s http://localhost:9701/health   # RTX 3060 head + llama :8081
curl -s http://localhost:8080/health   # rtx llama-server direct
curl -s http://localhost:8081/health   # rtx3060 llama-server direct

# Observability
curl -s http://localhost:3000/api/health                            # Grafana
curl -s http://localhost:9090/-/healthy                              # Prometheus
curl -s http://localhost:28081/health 2>/dev/null | head -1         # renderer (was :8081 before PR #440)
curl -s -X POST http://localhost:4318/v1/traces -H 'Content-Type: application/json' -d '{}'   # OTel HTTP receiver (expect 200/400)

# P100 (over the bridge)
curl -s http://192.168.122.21:8086/health

# Postgres
pg_isready -h localhost -p 5432 -U hydra -d hydra_store

# GPU (host-level)
nvidia-smi

# Quadlet services
systemctl --user status infra-renderer infra-grafana infra-prometheus infra-loki
```

---

## 6. Related docs

- `docs/hydra-system-pod.md` — how the hydra pod comes up, verify block, debug recipes
- `docs/architecture.md` — system topology, routing, lifecycle
- `docs/monitoring-observability.md` — observability stack details
- `docs/diagrams.md` — ASCII diagrams of the data flow
- `docs/RUNBOOK.md` — operator's quick-reference for live debugging
- `docs/build-environment.md` — go SDK path, CUDA toolkit versions, build flags
- `docs/workflow/05-deploy.md` — deploy lifecycle for runtime/fork changes
- `scripts/deploy-hydra-head.sh` — the main deploy script (use this for hydra)
- `scripts/deploy-infra.sh` — the Quadlet deploy script (use this for infra)
- `scripts/regenerate-quadlets.sh` + `scripts/patch-quadlets.sh` — the Quadlet generation flow

## 7. Change history

| Date | Change | PR / commit |
|---|---|---|
| 2026-07-15 | Created; first comprehensive ports+env doc | this commit |
| 2026-07-15 | Moved `infra-renderer` from `:8081` → `:28081` (env var `HTTP_PORT` → `SERVER_ADDR`) | #440 |
| 2026-07-14 | Added `hydra_warm_slot_evicted_for_short_prompt_total` metric, n_past guard synthetic test, routing decision tree docs | #436 |
| 2026-07-14 | Added `hydra_prefix_save_failures_total` counter back to `CoordinatorMetrics` (had been dropped by the w246-fix rebase) | #433 |
| 2026-07-14 | C# n_past guard for prefix-checkpoint save/restore | #428, #429 |
| earlier | Promoted to current architecture | (multiple PRs) |
