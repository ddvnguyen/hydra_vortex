# test-lane — hydra-test compose + P100 engines (isolated 2-core rig)

Bring up / check / tear down the Hydra TEST lane: two Hydra.Core instances
(core-A / core-B) + two heads on the RTX host, two bare llama-engines on the
P100 VM. Isolated from prod by ports (+10000) and by the `hydra_test`
Postgres DB. Distilled from `docs/hydra-test.md`,
`infra/docker-compose.hydra-test.yml`, `scripts/hydra-test/`, and the
2026-08-28 bring-up evidence.

## Topology (live-verified 2026-08-28)

| Service | Where | Ports |
|---|---|---|
| hydra-core-test-a | host (podman) | 19000 HTTP · 19500 Store RPC · 19501 metrics |
| hydra-core-test-b | host (podman) | 19001 HTTP · 19502 Store RPC · 19503 metrics |
| hydra-head-test-a | host (podman) | 19700 |
| hydra-head-test-b | host (podman) | 19701 |
| llama-engine-test-a | P100 VM, bare | 18086 HTTP · 19513 hydra RPC (n-gpu-layers 16) |
| llama-engine-test-b | P100 VM, bare | 18087 HTTP · 19514 hydra RPC (n-gpu-layers 8) |

- Both engines share the single P100 via mmap page-cache — **single-GPU
  shared-VRAM P/D, not true 2-GPU split**.
- Model: Qwen3.5-9B Q4_K_M (alias `qwen3.5-9b-test` in
  `infra/hydra-core/config/models-test.json`).
- `workers-test-a/b.json` point the cores at `192.168.122.21:1808x` — the
  VM has no podman, so engines run bare there (minifleet pattern).
- DB: `hydra_test` on the shared `infra-postgres` container (N1 isolation).

## Bring up

```bash
# 0. Prereqs: infra stack (postgres/loki/prom/otel) up —
#    bash scripts/start-infra.sh
#    Model + engine present on the VM (verify before up):
#    ssh hydra-p100 "ls -lh ~/hydra-min-test/ ~/hydra-fork-fix-sm60/"

# 1. Host side (idempotent; builds localhost/hydra-core:latest and
#    localhost/hydra-head:rtx if missing, creates hydra_test DB via psql
#    \gexec, waits up to 120s for all health endpoints):
export HYDRA_HEAD_AUTH_TOKEN=$(cat .hydra-head-token)
bash scripts/hydra-test/up.sh

# 2. P100 engines (bare on the VM). The lane script used on 2026-08-28 lived
#    at ~/hydra-test-lane/run-v4.sh; the pattern (minifleet vm-run.sh):
ssh hydra-p100
cd ~/hydra-fork-fix-sm60            # self-contained sm_60 engine (fork tip 67ceb00bd)
export LD_LIBRARY_PATH=$PWD
M=~/hydra-min-test/Qwen3.5-9B-Q4_K_M.gguf
nohup ./llama-engine -m $M --host 0.0.0.0 --port 18086 --rpc-port 19513 \
  --n-gpu-layers 16 -t 3 -c 16384 --cache-type-k q8_0 --cache-type-v q8_0 \
  --cont-batching --flash-attn --alias hydra-test-lane > ~/hydra-test-lane/engineA.log 2>&1 &
nohup ./llama-engine -m $M --host 0.0.0.0 --port 18087 --rpc-port 19514 \
  --n-gpu-layers 8 -t 3 -c 16384 --cache-type-k q8_0 --cache-type-v q8_0 \
  --cont-batching --flash-attn --alias hydra-test-lane > ~/hydra-test-lane/engineB.log 2>&1 &

# 3. Sanity:
curl -s http://localhost:19000/v1/models | jq .
curl -s http://localhost:19001/v1/models | jq .
curl -s http://192.168.122.21:18086/health
```

`-c 16384` (not the compose default 65536) was the fix for KV-cache VRAM
overflow on the shared P100 — see `infra/paseo/hydra-test-provider.md`.

## Status / rehearsal / teardown

```bash
bash scripts/hydra-test/status.sh          # per-service OK/DEGRADED/DOWN + verdict
bash scripts/hydra-test/agent-rehearsal.sh # 6-step agent-task rehearsal; logs to docs/hydra-test/evidence/
bash scripts/hydra-test/down.sh            # idempotent: compose down + orphan rm + pid leak check
```

## VM hygiene contract

The P100 VM hosts residents you must NOT kill: prod engine `:8086` (pid
~2899) and upstream minicpm `:8090` (pid ~1620).

- **NEVER bare `pkill llama-server`** — an earlier run killed the owner's
  upstream service. Kill patterns must match BOTH the lane marker (`--alias
  hydra-test-lane`) and the model name, and only pids you recorded.
- After teardown, `nvidia-smi --query-compute-apps=pid,used_memory
  --format=csv,noheader` must show only the residents.
- `down.sh`'s `pgrep -f "llama-engine.*1808[67]"` check catches host-side
  engine leaks (engines belong on the VM, not the host).

## Gotchas

- **LD_LIBRARY_PATH:** in-container it must be the bind-mount target
  (`/opt/hydra-min-test`), not the host path (`$HOME/...` expands wrong
  inside the container). Bare-on-VM: point it at the engine's own build
  prefix dir.
- **`nvidia` runtime:** the compose uses `runtime: nvidia`; if missing on a
  box, fall back to `devices: [/dev/nvidia0, /dev/nvidiactl, /dev/nvidia-uvm]`.
- **Engine build age:** sm_60 builds between fork `234083a45` and `3206b13b6`
  fail every PREFILL M2 (`hash pre-pass hashed 0 B`). The test workers run
  Mixed (`worker_type: 3`), so they hit this path — use the fixed
  `67ceb00bd` build.
- **Per-worker safe concurrency = slots−1:** with ≥2 concurrent cold_atomic
  requests to one worker, the self-lease deadlock 499s (tracked in PR #711).
- **Prod isolation:** `HYDRA_INSTANCE=test` gates all test config; unset =
  prod, zero-trust no-op. `agent-rehearsal.sh` step 6 asserts prod `:9000`
  counters are unchanged.

## Point an agent at the lane

- OpenCode provider block + `paseo run --provider opencode --model
  hydra-test/qwen3.5-9b-test` → `infra/paseo/hydra-test-provider.md`.
- Full design + env-var matrix → `docs/hydra-test.md`.
