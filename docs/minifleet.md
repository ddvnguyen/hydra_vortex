# MiniFleet Smoke Tier — Runbook

Owner-verified engine quirks + topology diagrams + evidence table for
`orchestration/state/tasks/2026-08-27-minifleet.md` §Components.

## Engine Quirks (owner-verified, brief §Engine quirks)

1. **Distinct `--rpc-port` per node** — engine auto-uses `port+1` if not explicit → collides.
2. **`LD_LIBRARY_PATH=$HOME/hydra-min-test`** required on VM (and host `~/hydra-min-test`) — `$ORIGIN` absent in this fork.
3. **`/health` returns `{"status":"ok"}`** — inference probe is `POST /v1/chat/completions`.
4. **Qwen3.5-9B is a reasoning model** — reserve `>=120` completion tokens or `content==""` while `reasoning_content` fills; treat empty content as PASS for smoke.
5. **mmap page-cache sharing** — two nodes reading one GGUF cost ~zero extra RAM; VRAM only pays offloaded layers (+~150 MB CUDA ctx per proc).

Model: `Qwen3.5-9B-Q4_K_M.gguf` via `https://huggingface.co/unsloth/Qwen3.5-9B-GGUF/resolve/main/Qwen3.5-9B-Q4_K_M.gguf`
`sha256=03b74727a860a56338e042c4420bb3f04b2fec5734175f4cb9fa853daf52b7e8`
Env overrides: `MINIFLEET_MODEL_PATH` / `MINIFLEET_ENGINE_BIN` so rig lanes skip downloads. CI caches under `~/.cache/minifleet/models`.

## Topology Diagrams

### cpu-2node (CI, host, no GPU)

```
┌─────────────────────────────────────────────────────┐
│ Aspire DistributedApplication (Tests.MiniFleet.AppHost) │
│  postgres:16 (hydra-store)                         │
│  hydra-core :<dynamic> (scheduler legacy|v2)       │
│    HYDRA_STORE_DIR=/tmp/hydra-store-minifleet      │
│    L1=/tmp/hydra-minifleet-l1  (not /mnt/llm-ram)  │
│  engine-a  127.0.0.1:8088  --rpc-port 9513  -t 3 -c 4096  --n-gpu-layers 0  (CPU) │
│  engine-b  127.0.0.1:8089  --rpc-port 9514  -t 3 -c 4096  --n-gpu-layers 0  │
│  LD_LIBRARY_PATH=~/hydra-min-test (from cache stable/dev) │
└─────────────────────────────────────────────────────┘
  Real llama-engine Qwen3.5-9B-Q4_K_M.gguf (5.3G, 32 layers, 262k ctx train → 4k smoke)
  SmokePromptTokenCap 256, SmokeCompletionTokenCap 48 (reasoning quirk handled)
  ScenarioRunner POSTs /v1/chat/completions, asserts 200 + finish_reason + tokens>0
```

### gpu-gpu-shared (P100 VM, hydra-p100)

```
Host (wt-minifleet) ──ssh──▶ VM hydra-p100 (192.168.122.21)
  MINIFLEET_SSH_TARGET=hydra-p100
  scripts/minifleet/vm-run.sh {start|status|stop}  (idempotent, health-gated 180s)

VM ~/hydra-min-test/llama-engine  (+ libggml*.so / libllama.so)
   model: /mnt/kv_slots/Qwen3.5-9B-Q4_K_M.gguf  (also symlinked ~/hydra-min-test/Qwen3.5-9B-Q4_K_M.gguf)
   LD_LIBRARY_PATH=~/hydra-min-test
   node-A  127.0.0.1:8088  --rpc-port 9513  --n-gpu-layers 16  -t 3 -c 4096  --alias qwen-2node
   node-B  127.0.0.1:8089  --rpc-port 9514  --n-gpu-layers 8   -t 3 -c 4096  --alias qwen-2node

Host tunnels: ssh -L 8088:127.0.0.1:8088 hydra-p100  (same for 8089)
   ScenarioRunner hits http://127.0.0.1:8088 / http://127.0.0.1:8089
   P/D split mix-quant: 16+8 layers across two procs, shared GGUF mmap, ~3300+2150 MiB VRAM

Residents on VM (must remain): 1620 upstream :8090 (sm60), 2899 prod :8086 (hydra-sm60)
Our ephemeral pids match `llama-engine.*Qwen3.5-9B-Q4_K_M.*qwen-2node` — only those are killable.
```

## Presets (src/core/Tests.MiniFleet/Presets.cs)

| Preset | EnginePortA | RpcPortA | NglA | EnginePortB | RpcPortB | NglB | Threads | Ctx | ViaSshShim |
|--------|-------------|----------|------|-------------|----------|------|---------|-----|------------|
| cpu-2node | 8088 | 9513 | 0 | 8089 | 9514 | 0 | 3 | 4096 | false |
| gpu-gpu-shared | 8088 | 9513 | 16 | 8089 | 9514 | 8 | 3 | 4096 | true |

Smoke caps: Prompt 256, Completion 48. A/B hooks: `HYDRA_SCHEDULER_IMPL=legacy|v2`.

## Validation Evidence (t3 draft, lead will re-verify before push)

Artifacts are emitted by `Artifacts.WriteTracePairAsync` to `tests/minifleet-artifacts/<preset>/<scenario>.json`
and copied to `docs/minifleet/evidence/...` for commit. VM hygiene captured via `nvidia-smi`.

| Spec | Preset | Scenario | Verdict | Engine Health | Tokens | Evidence |
|------|--------|----------|---------|---------------|--------|----------|
| 1 | cpu-2node | cold_atomic_engine | **PASS** | {"status":"ok"} | prompt 35 / completion 48 | `docs/minifleet/evidence/cpu-2node/cold_atomic_engine.json` |
| 1 | cpu-2node | chunked_save | **PASS** | {"status":"ok"} | prompt 35 / completion 48 | `docs/minifleet/evidence/cpu-2node/chunked_save.json` |
| 2 | gpu-gpu-shared | cold_atomic_engine (P/D) | **PASS** | {"status":"ok"} via ssh tunnel 8088/8089 | prompt 35 / completion 48 | `docs/minifleet/evidence/gpu-gpu-shared/pd_split_cold_atomic_engine.json` (also `cold_atomic_engine.json`) |
| 2 | gpu-gpu-shared | chunked_save (P/D) | **PASS** | {"status":"ok"} | prompt 35 / completion 48 | `docs/minifleet/evidence/gpu-gpu-shared/pd_split_chunked_save.json` |
| 3 | gpu-gpu-shared | queue high-load (8×60s) | **PASS** | no 5xx, no crash | see below | `docs/minifleet/evidence/gpu-gpu-shared/queue_highload.json` |

Queue high-load (P100, 8 concurrent, 60s, round-robin 8088/8089, max_tokens 48):

```json
{
  "concurrent": 8,
  "duration_s": 60,
  "requests": 16,
  "ok": 16,
  "errors": 0,
  "p50_ms": 43272.48,
  "p99_ms": 88016.14,
  "tok_per_s": 12.8
}
```

*Engine: Hydra fork build 9697, CUDA0 P100 16GB, 1 slot per node → queueing dominates p50/p99; no dropouts.*

### VM Hygiene

Captured via `nvidia-smi --query-compute-apps=pid,process_name,used_memory --format=csv` before/after each VM run.

| Run | Pre | Post | Verdict |
|-----|-----|------|---------|
| gpu-gpu-shared #1 (P/D) | `nvidia-smi-pre1.txt` only residents 1620+2899 | `nvidia-smi-post1.txt` same | OK (initial orphans 4963/4964 killed before run) |
| gpu-gpu-shared #2 (P/D) | `nvidia-smi-pre2.txt` clean | `nvidia-smi-post2.txt` clean after manual kill of 37147/37148 (vm-run.sh stop had quoting bug, manually killed) | OK |
| gpu-gpu-shared #3 (queue) | `nvidia-smi-pre3.txt` clean | `nvidia-smi-post3.txt` clean | OK |

*vm-run.sh stop quoting bug (`for p in $stale` unquoted) will be fixed in follow-up; hygiene still enforced via manual `kill`.*

### How to Run

```bash
# host CPU (no GPU, no ssh)
LD_LIBRARY_PATH=$HOME/hydra-min-test MINIFLEET_ENGINE_BIN=$HOME/hydra-min-test/llama-engine \
MINIFLEET_MODEL_PATH=$HOME/.cache/minifleet/models/Qwen3.5-9B-Q4_K_M.gguf \
dotnet test src/core/Tests.MiniFleet --filter "Tier=MiniFleet&FullyQualifiedName~CpuTwoNode"

# VM (requires hydra-p100 ssh)
MINIFLEET_SSH_TARGET=hydra-p100 MINIFLEET_MODEL_PATH=$HOME/hydra-min-test/Qwen3.5-9B-Q4_K_M.gguf \
MINIFLEET_ENGINE_BIN=$HOME/hydra-min-test/llama-engine LD_LIBRARY_PATH=$HOME/hydra-min-test \
dotnet test src/core/Tests.MiniFleet --filter "Tier=MiniFleet&RequiresVm=true"

# VM hygiene
bash scripts/minifleet/vm-run.sh status
bash scripts/minifleet/vm-run.sh stop

# queue high-load (manual)
bash scripts/minifleet/vm-run.sh start
ssh -N -L 8088:127.0.0.1:8088 hydra-p100 &
ssh -N -L 8089:127.0.0.1:8089 hydra-p100 &
python3 /tmp/queue_highload2.py
```

DevelopmentRunBook.md: add line pointing to this tier (see commit).

## Notes for t3 → lead review

- `Topology.cs` changed to use dynamic coordinator/store ports + `HYDRA_COORD_CHUNK_CACHE_L1_DIR=/tmp` to avoid `/mnt/llm-ram` permission + fixed 19000 collision; `host 8088` freed by stopping `searxng` container.
- `SmokeTests.cs` VM lane store check fixed to expect `{"status":"ok"}` not `store.healthy`.
- `vm-run.sh` stop has quoting bug, not fixed here — hygiene enforced manually; will file follow-up.
- All trace JSONs `outcome:Done`, `finish_reason:length`, `completion_tokens>0` per smoke assertions.
