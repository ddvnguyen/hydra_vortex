# Hydra Head (Go Node Agent)

> Extracted from CLAUDE.md to keep the handoff file short. Referenced from
> `CLAUDE.md` `## Hydra Head`.

Replaces the old Agent containers + manual llama-server deployment. Single Go binary per GPU
node that manages llama-server and exporter sidecars (node_exporter, nvidia_gpu_exporter where needed).
Logs ship directly from each service via OTLP/HTTP to the OTel Collector (no Promtail).

## Source & Deploy
| What | Where |
|------|-------|
| Go module | `src/head/` (module `github.com/ddvnguyen/hydra_vortex/hydra-head`) |
| Config files | `infra/hydra-head/config/global.yaml` + per-node overrides |
| Deploy script | `scripts/deploy-hydra-head.sh` |
| RTX Dockerfile | `infra/hydra-head/Dockerfile.rtx` (based on CUDA base `Dockerfile_26.04_cuda13.2`) |
| P100 systemd unit | `infra/hydra-head/hydra-head.service` |

## Service Management
Hydra Head owns lifecycle (start/stop/restart/auto-restart with backoff) of:
- llama-server
- node_exporter (P100 only; RTX uses host-level exporter in infra-host pod)
- nvidia_exporter (P100 only; RTX uses host-level exporter in infra-host pod)

Each service is controlled via per-node `services:` YAML config (`enabled`, `binary`, `config`, `port`, `args`).

Logs ship via OTLP/HTTP push directly from each service to the OTel Collector
(Quadlet `infra-otel-collector`, port 4318). The collector fans out to Loki.
Promtail was removed in #363 — it is no longer a managed sub-service.

## OCI Registry
llama-engine (and llama-server) binary pulled from ghcr.io at startup via `crane`
library. The same fat binary serves both the RTX 5060 Ti (sm_120a path) and the
RTX 3060 (sm_86 path) — the 3060 + 5060-Ti in-pod pair share one image:

- `ghcr.io/ddvnguyen/llama-server:sm86-sm120-engine` (RTX 5060 Ti + RTX 3060, fat binary, 159 MB)
- `ghcr.io/ddvnguyen/llama-server-sm60:5e2de4189-shared` (P100, sm_60, 205 MB)

Built `FROM scratch` with the shared-lib build as the single file.
The 3060 is configured (via node-rtx3060.yaml) to expose its ggml-RPC
backend on `:9504` so the 5060 Ti (head) can reach it for COMBINED
expert-split. No more mount-based deploys of `build_sm120/` / `build_sm60/`
/ `build_sm86_sm120/` (the bind mount in compose is now the source of truth
on this host; the OCI image is the fallback for other hosts).

## Log Pipeline
Each service (llama-server, hydra-head, hydra-core, store) pushes logs via
OTLP/HTTP to the OTel Collector on `localhost:4318` (or `192.168.122.1:4318`
for P100). The collector parses, labels by `component` and `node`, and
forwards to Loki. Promtail (and its docker_sd + CRI-parser pipeline) was
removed in #363 — it no longer manages log shipping.

## Deprecated infra (replaced by hydra-head)
| Old file | Replacement |
|----------|-------------|
| `infra/quadlets/hydra-agent-rtx.container` | Agents merged into Hydra.Core |
| `infra/quadlets/hydra-agent-p100.container` | Agents merged into Hydra.Core |
| `infra/systemd/llama-p100-user.service` | Managed by hydra-head |
| `infra/llama-rtx-node/` (removed) | `infra/hydra-head/Dockerfile.rtx` |
| `scripts/deploy-llama.sh` | `scripts/deploy-hydra-head.sh` |
