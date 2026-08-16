# Hydra — Development Runbook

## Service Map

```
Client (HTTP) → Hydra.Core :9000             [C#/.NET 10 — HTTP API + Store + Router]
                 │ HTTP completions                │ HTTP completions
                 │ RPC StateGet/Put (:9503)        │ RPC StateGet/Put (:9502)
                 ▼                                 ▼
            Hydra Head RTX                    Hydra Head P100            [Go]
            │  llama :8080                    │  llama :8086            [C++ fork]
            │  hydra RPC :9503                │  hydra RPC :9502
            │  node_exporter :9100            │  node_exporter :9100
            │  nvidia_exporter :9835          │  nvidia_exporter :9835
            ▼                                 ▼

            /mnt/llm-ram/store/ (tmpfs, managed by Hydra.Core)
```

| Port    | Service       | Lang     | Node      | Purpose                     |
|---------|---------------|----------|-----------|-----------------------------|
| 9000    | Hydra.Core    | C#       | host      | HTTP API (OpenAI-compat)    |
| 9500    | Hydra.Core    | C#       | host      | Store RPC (internal)        |
| 9501    | Hydra.Core    | C#       | host      | Metrics endpoint            |
| 9700    | Hydra Head    | Go       | 5060 Ti   | Head API (/status, /health) |
| 8080    | llama-engine  | C++      | 5060 Ti   | HTTP completions (RTX)      |
| 9503    | llama-engine  | C++      | 5060 Ti   | hydra RPC (StateGet/Put)    |
| 9701    | Hydra Head    | Go       | RTX 3060  | Head API (/status, /health) |
| 8081    | llama-engine  | C++      | RTX 3060  | HTTP completions (3060)     |
| 9504    | llama-engine  | C++      | RTX 3060  | hydra RPC + ggml-RPC peer   |
| 9100    | node_exporter | Go       | host      | Host metrics                |
| 9835    | nvidia_exporter | Go    | host      | GPU metrics                 |
| 9700    | Hydra Head    | Go       | P100      | Head API (/status, /health) |
| 8086    | llama-engine  | C++      | P100      | HTTP completions (VM)       |
| 9502    | llama-engine  | C++      | P100      | hydra RPC (StateGet/Put)    |
| 9100    | node_exporter | Go       | P100      | Host metrics                |
| 9835    | nvidia_exporter | Go    | P100      | GPU metrics                 |

---

## Prerequisites

- .NET 10 SDK
- Python >= 3.13 with `pip install -e .[all]` (from project root, for system tests)
- VS Code extensions: C# Dev Kit, EditorConfig
- tmpfs mount: `sudo bash infra/setup-ramdisk.sh`
- Podman log driver set to `k8s-file` (create/edit `~/.config/containers/containers.conf`):
  ```ini
  [containers]
  log_driver = "k8s-file"
  ```
  **Required:** Promtail scrapes container log files directly via `docker_sd_configs`;
  the default `journald` driver has no file-backed logs to scrape. Existing containers
  must be recreated after changing this setting.

---

## Quick Start

### One command (recommended)

```bash
bash scripts/start-env.sh
```

Idempotent — checks what is already running and only starts what is missing. Handles:
- Hydra.Core (single C# binary) via `infra/docker-compose.hydra.yml`
- Infra/observability (Loki + Promtail + Prometheus + Grafana) via `infra/docker-compose.infra.yml`
- Hydra Head RTX via container (`infra/hydra-head/Dockerfile.rtx`)
- Hydra Head P100 via SSH + user systemd on the VM

Requires pre-built hydra-head binary and llama-server OCI images (see **Hydra Head** section).
Use `--skip-p100` if the P100 VM is unavailable.

```bash
bash scripts/start-env.sh --skip-p100   # RTX only
```

### Manual (if needed)

```bash
# Infra/observability (Loki, Promtail, Prometheus, Grafana)
cd infra && podman-compose -f docker-compose.infra.yml up -d

# Hydra.Core (single C# binary with host networking)
podman-compose -f docker-compose.hydra.yml up -d

# Hydra Head — RTX (container)
bash scripts/deploy-hydra-head.sh rtx

# Hydra Head — P100 (VM systemd)
bash scripts/deploy-hydra-head.sh p100

# Both nodes
bash scripts/deploy-hydra-head.sh all

# Verify
curl -s http://localhost:9000/health
curl -s http://localhost:9700/status       # RTX Hydra Head
curl -s http://192.168.122.21:9700/status  # P100 Hydra Head
```

> **Note:** Hydra Head manages llama-server + node_exporter + nvidia_exporter
> on each node. Logs ship via OTLP/HTTP to the OTel Collector (Promtail removed in #363).
> The old `infra/llama-rtx-node/` container and `llama-p100` systemd
> service are [DEPRECATED]. Hydra.Core contacts llama-server directly via HTTP.

---

## VS Code Debug

1. Open `src/Hydra.sln` in VS Code
2. Run > Start Debugging (F5), select a configuration:

### Individual services
| Config                | Starts           |
|-----------------------|------------------|
| `Hydra.Core (:9000)`  | Hydra.Core HTTP API + Store RPC |

### Compound launch (all at once)
Select **All Services (Hydra.Core)** from the Run dropdown — starts the single binary.

### Tests
| Config                    | Runs                     |
|---------------------------|--------------------------|
| `Tests (all .NET)`        | Full suite               |
| `Tests (Shared only)`     | RPC protocol + client/server |
| `Tests (Core only)`       | Storage engine + Store RPC + ChunkEngine + ChunkStore |

---

## Running Tests

```bash
# All .NET tests — projects run sequentially to avoid PG contention
dotnet test src/Hydra.sln --settings src/Hydra.runsettings --verbosity normal

# Individual projects
dotnet test src/core/Tests.Shared           # 29 tests
dotnet test src/core/Tests.Core             # Core tests (Store + Chunk + Routing)

# M2-specific tests
dotnet test src/core/Tests.Core --filter "FullyQualifiedName~Chunk" -v m
dotnet test src/core/Tests.Core --filter "FullyQualifiedName~ChunkCache" -v m

# Tier 1 — Hermetic E2E (Aspire + fake engine, no live stack needed)
dotnet test src/core/Tests.E2E/

# Hermetic E2E is the manual PR-merge gate. It is NOT in ci.yml (push/PR) —
# it runs on demand and satisfies the required "E2E (hermetic)" branch
# protection check:
#   gh workflow run e2e-hermetic.yml --ref <pr-head-branch>
# See .github/workflows/e2e-hermetic.yml for the full flow.

# Tier 2 — Live rig (requires live stack)
dotnet test src/core/Tests.LiveRig/ -v m
dotnet test src/core/Tests.LiveRig/ --filter "FullyQualifiedName~FullWorkflow"

# Tier 3 — Engine parity (HTTP/RPC parity, live-rig tests need live stack)
dotnet test src/core/Tests.EngineParity/

# Tier 4 — Agent workload (opt-in, real GPU + CLI, workflow_dispatch only)
dotnet test src/core/Tests.AgentWorkload/
```

---

## Environment Variables

See [`.env.example`](.env.example) for all configurable values.

| Variable | Default | Service |
|----------|---------|---------|
| `HYDRA_CORE_HOST` | `0.0.0.0` | Hydra.Core |
| `HYDRA_CORE_PORT` | `9000` | Hydra.Core |
| `HYDRA_STORE_PORT` | `9500` | Hydra.Core |
| `HYDRA_STORE_DIR` | `/mnt/llm-ram/store` | Hydra.Core |
| `HYDRA_METRICS_PORT` | `9501` | Hydra.Core |
| `HYDRA_CORE_LOG_LEVEL` | `INFO` | Hydra.Core |
| `HYDRA_CORE_WORKERS` | (JSON) | Hydra.Core worker config |
| `HYDRA_CHUNK_CACHE_DIR` | `/tmp/hydra-chunk-cache` | Hydra.Core local chunk hash cache |
| `HYDRA_COORD_ALLOW_CROSS_MODEL_KV_REUSE` | `false` | Hydra.Core — M-Perf.9 #289 cross-model KV safety override |

Config is compiled-in with defaults. Use environment variables or
`appsettings.json` to override.

---

## Mix-Precision P/D Split Semantics (M-Perf.9 #289)

`HYDRA_COORD_MIX_PRECISION_ENABLED=true` was historically paired with per-worker
`prefill_model_name` / `decode_model_name` to do a Q3_K prefill + Q5_K decode
("mix-precision"). **This configuration is mathematically broken** and is no
longer supported by default:

- The KV cache is **quantization-dependent**: a Q3_K prefill produces different
  K/V values for the same input than a Q5_K prefill, so transferring KV between
  quantizations silently corrupts decode output.
- The cross-model guard in `WorkerSchedulerService.RestoreKvAsync` would
  correctly `Abort` such a transfer (see `specs/rpc-protocol.md` →
  *Cross-Model KV Safety*).
- Operators wanting the old behaviour can set
  `HYDRA_COORD_ALLOW_CROSS_MODEL_KV_REUSE=true` and accept the corrupt-decode risk.

**Recommended configuration** (interpretation *b — same model, different worker choice*):

- `prefill_model_name` and `decode_model_name` are **unset** on all workers
  (see `infra/hydra-core/config/workers.json`).
- Pre-fill and decode use the same model (the resident one), so the cross-model
  guard's `Proceed` outcome applies naturally.
- The router still picks the right worker for each phase — the RTX is preferred
  for prefill, the P100 for decode when the RTX slots are saturated. This is
  the heterogeneous-GPU optimization, *not* a per-phase quantization switch.

**Two-engine, two-models** (interpretation *c* — small distill for prefill,
large target for decode) is the long-term M-Perf track goal but requires a
separate design pass for cross-model token sharing. Not in scope for this PR.

---

## Infrastructure

*(tmpfs for Store is managed automatically by compose — no host setup needed)*

### Hydra Head (Go node agent)

Build and deploy Hydra Head, which manages llama-server + 3 sub-services on each GPU node.

> **⚠️ `go` is NOT in the default PATH.** It lives at `~/go-sdk/go/bin/go` (v1.23.4,
> installed from tarball, no `apt` access). Set it first:
> ```bash
> export PATH=$HOME/go-sdk/go/bin:$PATH
> go version
> # go version go1.23.4 linux/amd64
> ```
> See [`docs/build-environment.md`](docs/build-environment.md) for the full env.

```bash
# Build
go build -C src/head -o ../../bin/hydra-head .

# Run locally (RTX config, requires model files)
bin/hydra-head -global infra/hydra-head/config/global.yaml \
               -node infra/hydra-head/config/node-rtx.yaml \
               -api-port 9700

# Test
go test -C src/head ./internal/...

# Deploy to both nodes
bash scripts/deploy-hydra-head.sh all
```

**APIs:** `GET /status`, `GET /health`, `POST /restart?name=<service>`, `POST /update`

### llama-engine / llama-server — build & package

The fork lives in `src/llama-cpp` (submodule, default branch `hydra-fork` of
`ddvnguyen/llama.cpp`; `.gitmodules` `branch = hydra-fork`).

> **Preferred: CI/CD (`hydra-build.yml`), not a local build.** A coding
> agent (or anyone) should default to triggering the fork's GitHub Actions
> workflow rather than running cmake locally — it builds, packages, and
> pushes the OCI image in one manual dispatch, on the actual RTX host,
> with a persistent ccache. No local CUDA toolchain access needed. The
> manual cmake builds further down are a **fallback** for when CI is
> unavailable, or for the tight edit/build/test loop while actively
> patching the fork (see "Fast local iteration" below).

#### Local builds via `scripts/llama-build.sh` (agent-facing)

When you *do* build locally (tight loop, or CI unavailable), use the helper —
it makes fork builds cheap and worktree-proof:

```bash
bash scripts/llama-build.sh dev                   # L1 stable, iteration build (no IPO)
bash scripts/llama-build.sh dev-nofaq             # L1, FASTEST iteration (drops FA_ALL_QUANTS)
bash scripts/llama-build.sh deploy-sm86-sm120     # L1 stable, deploy-flags build (IPO on)
bash scripts/llama-build.sh test dev -- -DGGML_CUDA_FA_ALL_QUANTS=OFF   # L2 experiment
bash scripts/llama-build.sh list                  # cache state
bash scripts/llama-build.sh prune                 # evict L2 + drop old L2 build dirs
bash scripts/llama-build.sh --clear-l2            # nuke the L2 tier
```

- **`dev-nofaq`** drops `GGML_CUDA_FA_ALL_QUANTS` (the biggest compile-time
  multiplier) for the fastest edit→build loop. The default FA kernel set still
  covers symmetric `q8_0` KV — which is what Hydra runs today — so `dev-nofaq` is
  fine for most fork iteration. Reach for `dev` (FA_ALL_QUANTS on) when testing
  non-default KV cache types (e.g. `q5_1`/`q4_1` experiments).
- **Build parallelism** defaults to `nproc - 8` (12 jobs on the 20-thread
  i7-12700K), leaving ~8 threads free for concurrent work (live engine, MoE CPU
  offload, other agents). Override with `--jobs N` or the `LLAMA_BUILD_JOBS` env
  var, e.g. `--jobs 16` for a faster build when the box is otherwise idle.

What it does:

- **One shared ccache store** (`/mnt/WorkDisk/cache/hydra-ccache`, 15G budget) —
  the same store CI builds into, so a fresh worktree inherits CI-warmed objects.
  Stable builds use ccache namespace `l1`, test builds `l2`.
- **Offline submodule init**: a fresh worktree's `src/llama-cpp` is initialized
  by reusing the shared module repo (`git submodule update --reference -N`),
  ~3s instead of a slow full clone of the fork. Falls back to a network clone
  only when the module has never been initialized on this host.
- **Persistent build dirs outside the worktree**
  (`/mnt/WorkDisk/cache/llama-build/{stable,test}/…`). The in-tree
  `build-hydra-dev` / `build_sm86_sm120` / `build_sm60_v2` paths are symlinks to
  the active dir, so plain `cmake --build build-hydra-dev` still works and the
  fork's `.gitignore` (`/*build*/`) keeps them untracked.
- **Configure-on-change**: identical flags + same submodule SHA reuse the dir
  (ninja only recompiles rules whose command or inputs changed); a flag or CUDA
  change reconfigures automatically. Changing `GGML_CUDA_FA_ALL_QUANTS`, for
  example, recompiles only the `ggml-cuda` target, not the whole tree.
- **Shared 15G budget, L2 evicted first**: when the store is full the `l2`
  namespace is cleared first; only if still over budget are least-used entries
  (last access, not creation time) evicted store-wide. `prune` also drops L2
  build dirs that are not the current active target.

Experiments live in L2 (`test <profile> [--variant NAME] [--cuda VER] [-- -D…]`)
and never affect L1/CI. The deployed artifact always comes from CI
(`hydra-build.yml`); local builds are verification only.

> **Design:** the full decision record (cache model, eviction policy, rejected
> alternatives) is in `docs/decisions/0002-llama-build-cache.md`.

#### CI/CD build (recommended)

`hydra-build.yml` lives in `ddvnguyen/llama.cpp` on the `hydra-fork` branch.
It is manual-dispatch only — this is a private build fork, not upstream
llama.cpp, so auto-triggers on push/PR were deliberately disabled fork-wide
(every workflow in `ddvnguyen/llama.cpp` is `workflow_dispatch`-only except a
handful of unrelated `schedule`/`issues`-based automation). Checkboxes select
which binaries/architectures to build; `runner_target` picks where; multiple
selections fan out per `execution_mode`.

```bash
# Trigger — llama-engine for sm86-sm120, on the local self-hosted RTX host,
# one job per selected combo (defaults shown are also the workflow's defaults):
gh workflow run hydra-build.yml --repo ddvnguyen/llama.cpp --ref hydra-fork \
  -f build_llama_engine=true \
  -f build_llama_server=false \
  -f arch_sm86_sm120=true \
  -f arch_sm60=false \
  -f runner_target=local \
  -f execution_mode=matrix

# Watch it
gh run list --repo ddvnguyen/llama.cpp --workflow hydra-build.yml --limit 5
gh run watch <run-id> --repo ddvnguyen/llama.cpp
```

| Input | Values | Notes |
|---|---|---|
| `build_llama_engine` / `build_llama_server` | boolean checkboxes | Both can be checked; sm_60 always builds `llama-server` regardless (P100 has no COMBINED-mode peer) |
| `arch_sm86_sm120` / `arch_sm60` | boolean checkboxes | Either or both; each checked arch+binary pair becomes one build |
| `runner_target` | `local` \| `cloud` | `local` = self-hosted RTX host, real hardware, ccache persists on disk across every run. `cloud` = GitHub-hosted `ubuntu-latest`, ephemeral ccache via `actions/cache`, slower but useful if the local runner is down |
| `execution_mode` | `matrix` \| `sequential` | `matrix` runs each selected combo as a parallel job; `sequential` runs them one after another in a single job |

Resulting images (one per combo):
```
ghcr.io/ddvnguyen/llama-server:<arch>-<binary>-<fork-version>-<short-sha>
ghcr.io/ddvnguyen/llama-server:<arch>-<binary>-latest
# e.g. ghcr.io/ddvnguyen/llama-server:sm86-sm120-llama-engine-0.1.0-a157edf
```
`<fork-version>` is `src/llama-cpp/VERSION`, bumped by hand.

To deploy the new image, trigger the `deploy-llama` job in this repo's
`ci.yml`: `gh workflow run ci.yml -f deploy-llama=true -f
llama-tag-suffix=<fork-version>-<short-sha>` (or `latest`). It pins the
`source:` tag in the node configs (`infra/hydra-head/config/node-{rtx,rtx3060,p100}.yaml`),
commits it, and redeploys RTX + RTX 3060 + P100. See "Deploy via Hydra Head"
below for the manual/live-update alternative (`POST /update`).

**Prerequisites (fallback path only):** CUDA 12.9 + CUDA 13.2 at `/opt/software/cuda/`, GCC 14 at `/usr/bin/gcc-14`.

```bash
WORK=/mnt/WorkDisk/Workplace/hydra_vortex/src/llama-cpp
cd $WORK
```

#### Fast local iteration (edit → build → verify a fork change)

The `dev` profile of `scripts/llama-build.sh` is the iteration build: the same
`86;120` fat-arch pair and correctness flags as the deploy build (including
`GGML_CUDA_FA_ALL_QUANTS`), but without `CMAKE_INTERPROCEDURAL_OPTIMIZATION`
(LTO) — LTO forces a slow whole-program relink on every rebuild regardless of
which file changed. ccache is wired in for all compilers. After each edit just
re-run it; a single-file change rebuilds in seconds once the cache is warm:

```bash
bash scripts/llama-build.sh dev --target llama-engine

# or plain cmake against the symlinked dir (same cache, same build dir):
cmake --build build-hydra-dev --target llama-engine -j$(nproc)
```

Check ccache is actually being hit: `ccache -s` shows cache hits incrementing
across rebuilds. Once your change is verified, do a real deploy build
(`bash scripts/llama-build.sh deploy-sm86-sm120`) before pushing — the deploy
build keeps LTO on since it affects steady-state decode perf.

#### Manual build (fallback — when CI/CD is unavailable)

The blocks below are the same flags `hydra-build.yml` / `build-combo.sh` use
under the hood; they exist here for direct debugging on the box or when the
self-hosted runner is down. Prefer the CI/CD build above otherwise.

> **ccache:** every manual block sets the `CMAKE_{C,CXX,CUDA}_COMPILER_LAUNCHER`
> so hand-rolled cmake benefits from the same shared store. Prefer
> `bash scripts/llama-build.sh <profile>` which does this plus a persistent
> build dir automatically.
>
> **`GGML_CUDA_FORCE_CUBLAS` is OFF for the RTX builds** (approved): with Q4/Q5
> model quants the custom int8-tensor-core MMQ kernels (default on sm_86/sm_120)
> are used instead of FP16 cuBLAS. Keep `ON` for the sm_60 (P100) build — Pascal
> has no int8 tensor cores, so cuBLAS is the effective path there.
> **CI mismatch:** `build-combo.sh` on `ddvnguyen/llama.cpp` still sets
> `GGML_CUDA_FORCE_CUBLAS=ON`; flip it to `OFF` in a fork PR so the deployed
> artifact matches the local deploy build. Run an A/B (L2:
> `bash scripts/llama-build.sh test deploy-sm86-sm120 --variant cublas-on -- -DGGML_CUDA_FORCE_CUBLAS=ON`)
> before that if you want numbers.

##### RTX 5060 Ti + RTX 3060 (fat sm_86+sm_120, CUDA 13.2)

One SASS image with both archs compiled in. The 5060 Ti (Blackwell, sm_120a)
and the 3060 (Ampere, sm_86) pick their cubin at load time. Saves the
per-arch build dance. The CI/CD path above packages this same build as
`ghcr.io/ddvnguyen/llama-server:sm86-sm120-llama-engine-latest`.

> **Default: shared-lib build (`-DBUILD_SHARED_LIBS=ON`).** Static builds
> (`-DBUILD_SHARED_LIBS=OFF`) hang in the post-init phase on RTX — see
> #346. The build-type label appears in `--version` (e.g. `[shared]`)
> so a stale static build is self-identifying.

```bash
CUDA_PATH=/opt/software/cuda/13.2
cmake -B build_sm86_sm120 -G Ninja \
  -DCMAKE_CUDA_ARCHITECTURES="86;120" \
  -DCMAKE_C_COMPILER_LAUNCHER=ccache \
  -DCMAKE_CXX_COMPILER_LAUNCHER=ccache \
  -DCMAKE_CUDA_COMPILER_LAUNCHER=ccache \
  -DCPACK_PACKAGE_NAME="ik-llama-sm86-sm120-cuda13.2" \
  -DGGML_CUDA=ON \
  -DGGML_CUDA_FORCE_CUBLAS=OFF \
  -DGGML_CUDA_FA=ON \
  -DGGML_CUDA_FA_ALL_QUANTS=ON \
  -DGGML_CUDA_GRAPHS=ON \
  -DGGML_CUDA_NCCL=ON \
  -DGGML_RPC=ON \
  -DGGML_NVML=ON \
  -DGGML_NATIVE=ON \
  -DCMAKE_BUILD_TYPE=Release \
  -DBUILD_SHARED_LIBS=ON \
  -DCMAKE_BUILD_RPATH='$ORIGIN' \
  -DCMAKE_INSTALL_RPATH='$ORIGIN' \
  -DCMAKE_BUILD_WITH_INSTALL_RPATH=ON \
  -DCMAKE_INTERPROCEDURAL_OPTIMIZATION=ON \
  -DLLAMA_BUILD_EXAMPLES=OFF \
  -DLLAMA_BUILD_TESTS=OFF
cmake --build build_sm86_sm120 --target llama-engine -j$(nproc)

# Sanity check: --version must show [shared]
build_sm86_sm120/bin/llama-engine --version
# expected: version: <N> (<sha>) [shared]

# Verify both archs are in the embedded cubins
cuobjdump --list-elf build_sm86_sm120/bin/libggml-cuda.so | head
# expected: alternating libggml-cuda.N.sm_86.cubin / libggml-cuda.M.sm_120a.cubin
```

The RTX 5060 Ti head and the RTX 3060 peer both bind-mount
`build_sm86_sm120/bin/` into their containers (see
`infra/docker-compose.hydra.yml`). No OCI pull needed on the host.

> **Important:** `bin/llama-engine`, NOT `bin/llama-server`. The
> COMBINED engine mode (--rpc-engine / --combined-ot-pattern / --ggml-rpc-port
> flags) is only available in the `llama-engine` binary — it has the
> `extract_hydra_capability_flags` filter that strips the flags from argv
> before common arg parsing. `llama-server` rejects them with
> "invalid argument" at startup.

##### RTX 5060 Ti only (Blackwell sm_120, CUDA 13.2) — fallback for non-fat hosts

```bash
CUDA_PATH=/opt/software/cuda/13.2
cmake -B build_sm120_v3 -G Ninja \
  -DCMAKE_CUDA_ARCHITECTURES="120" \
  -DCPACK_PACKAGE_NAME="ik-llama-sm120-cuda13.2" \
  -DGGML_CUDA=ON \
  -DGGML_CUDA_FORCE_CUBLAS=OFF \
  -DGGML_CUDA_FA=ON \
  -DGGML_CUDA_FA_ALL_QUANTS=ON \
  -DGGML_RPC=ON \
  -DGGML_NVML=ON \
  -DGGML_NATIVE=ON \
  -DCMAKE_BUILD_TYPE=Release \
  -DBUILD_SHARED_LIBS=ON \
  -DCMAKE_BUILD_RPATH='$ORIGIN' \
  -DCMAKE_INSTALL_RPATH='$ORIGIN' \
  -DCMAKE_BUILD_WITH_INSTALL_RPATH=ON \
  -DCMAKE_INTERPROCEDURAL_OPTIMIZATION=ON \
  -DLLAMA_BUILD_EXAMPLES=OFF \
  -DLLAMA_BUILD_TESTS=OFF
cmake --build build_sm120_v3 --target llama-engine -j$(nproc)

# Sanity check: --version must show [shared]
build_sm120_v3/bin/llama-engine --version
# expected: version: <N> (<sha>) [shared]
```

On a host with only this build, the 3060 service still loads the model via
PTX JIT (slow but functional — see PR #368). For COMBINED mode the 3060's
`--ggml-rpc-port 9504` requires native sm_86 cubins, which requires
`CMAKE_CUDA_ARCHITECTURES` to include `86` — i.e. the fat build above.

> **Dual-GPU local testing caveat:** On a host with both RTX 5060 Ti (CUDA0) and
> RTX 3060 (CUDA1) running the fat `build_sm86_sm120` binary, the qwen35 model
> crashes during the `common_context_can_seq_rm` warmup on CUDA1 with
> `CUDA error: no kernel image is available for execution on the device`.
> This is a pre-existing warmup-path issue — not a build problem.
> **Workaround:** restrict to the target GPU for standalone testing:
> ```bash
> CUDA_VISIBLE_DEVICES=0 ./llama-engine -m <model> --rpc-port 9505 ...
> ```
> Production is unaffected: the 3060 runs in `--peer-only` mode (no model load,
> no warmup) so this code path is never hit.

##### RTX 3060 only (Ampere sm_86, CUDA 13.2) — small build for the 3060-only path

```bash
CUDA_PATH=/opt/software/cuda/13.2
cmake -B build_sm86 -G Ninja \
  -DCMAKE_CUDA_ARCHITECTURES="86" \
  -DCPACK_PACKAGE_NAME="ik-llama-sm86-cuda13.2" \
  -DGGML_CUDA=ON \
  -DGGML_CUDA_FORCE_CUBLAS=OFF \
  -DGGML_CUDA_FA=ON \
  -DGGML_CUDA_FA_ALL_QUANTS=ON \
  -DGGML_RPC=ON \
  -DGGML_NVML=ON \
  -DGGML_NATIVE=ON \
  -DCMAKE_BUILD_TYPE=Release \
  -DBUILD_SHARED_LIBS=ON \
  -DCMAKE_BUILD_RPATH='$ORIGIN' \
  -DCMAKE_INSTALL_RPATH='$ORIGIN' \
  -DCMAKE_INTERPROCEDURAL_OPTIMIZATION=ON \
  -DLLAMA_BUILD_EXAMPLES=OFF \
  -DLLAMA_BUILD_TESTS=OFF
cmake --build build_sm86 --target llama-engine -j$(nproc)
```

##### P100 (Pascal sm_60, CUDA 12.9)

Pascal requires **GCC ≤ 14** as the CUDA host compiler (CUDA 12.9 caps at GCC 14;
GCC 15+ fails with `error: unrecognized command-line option '-###'`).

```bash
CUDA_PATH=/opt/software/cuda/12.9
cmake -B build_sm60_v2 -G Ninja \
  -DCMAKE_CUDA_ARCHITECTURES="60" \
  -DCMAKE_CUDA_HOST_COMPILER="/usr/bin/g++-14" \
  -DCMAKE_C_COMPILER_LAUNCHER=ccache \
  -DCMAKE_CXX_COMPILER_LAUNCHER=ccache \
  -DCMAKE_CUDA_COMPILER_LAUNCHER=ccache \
  -DGGML_RPC=ON \
  -DGGML_CUDA=ON \
  -DGGML_CUDA_FORCE_CUBLAS=ON \
  -DGGML_CUDA_FORCE_MMQ=OFF \
  -DGGML_CUDA_FA_ALL_QUANTS=OFF \
  -DGGML_NATIVE=ON \
  -DCPACK_INCLUDE_COMMANDS=ON \
  -DCMAKE_BUILD_RPATH='$ORIGIN' \
  -DCMAKE_INSTALL_RPATH='$ORIGIN' \
  -DCMAKE_BUILD_TYPE=Release \
  -DLLAMA_BUILD_TESTS=OFF \
  -DBUILD_SHARED_LIBS=ON \
  -DLLAMA_BUILD_EXAMPLES=OFF \
  -DCMAKE_INTERPROCEDURAL_OPTIMIZATION=ON
cmake --build build_sm60_v2 --target llama-server -j$(nproc)

# Sanity check
build_sm60_v2/bin/llama-server --version
# expected: version: <N> (<sha>) [shared]
```

#### Deploy via Hydra Head

llama-engine is now deployed via Hydra Head, not manual container/systemd management.
Hydra Head pulls the llama-engine binary from ghcr.io at startup (see OCI Registry in CLAUDE.md).

```bash
# Deploy Hydra Head to RTX 5060 Ti (container with OCI pull)
bash scripts/deploy-hydra-head.sh rtx

# Deploy Hydra Head to RTX 3060 (same image, second compose service; same pod)
bash scripts/deploy-hydra-head.sh rtx3060

# Deploy Hydra Head to P100 (systemd + config push)
bash scripts/deploy-hydra-head.sh p100

# All three nodes
bash scripts/deploy-hydra-head.sh all
```

The deploy script handles building the hydra-head binary, building/pushing the container
image (RTX 5060 Ti and RTX 3060 share one image, `hydra-head:rtx`), copying configs and
systemd unit (P100), and restarting services.

To push updated llama-engine/llama-server binaries to the OCI registry, use
the CI/CD build above — `hydra-build.yml` builds **and** pushes in the same
dispatch (see "CI/CD build (recommended)"). It replaces the old manual
`podman build`/`podman push` steps that used to live here. To deploy the
result, trigger the `deploy-llama` job in `ci.yml` with `llama-tag-suffix`
set to the pushed `<fork-version>-<short-sha>` (or `latest`) — it pins
`source:` in `infra/hydra-head/config/node-{rtx,rtx3060,p100}.yaml`, commits
it, and redeploys. Only fall back to a manual `podman build -f
.github/workflows/hydra-build.Dockerfile` if CI/CD is genuinely unavailable.

Or, for a quick P100 update that bypasses the OCI pull flow, replace the
binary directly on the VM (still supported):
```bash
# 1. Copy to VM (scp uses ~/.ssh/config alias hydra-p100 → vm1@192.168.122.21)
# Both sm60 and sm120 builds now produce `llama-server`. The VM-side filename has always been `llama-server`.
# path/filename is still llama-server until the infra config migrates.
scp build_sm60_v2/bin/llama-server hydra-p100:/tmp/llama-server-new

# 2. Deploy on the VM (must be run interactively — sudo needs a terminal)

ssh hydra-p100
sudo mv /tmp/llama-server-new /opt/software/llama-cpp-hydra-sm60/hydra-sm60/bin/llama-server
sudo chmod +x /opt/software/llama-cpp-hydra-sm60/hydra-sm60/bin/llama-server
systemctl --user restart llama-p100
# Wait ~30s for model to load, then verify:
curl http://192.168.122.21:8086/health
```

To pull the latest binary without restarting Hydra Head:
```bash
curl -X POST http://localhost:9700/update      \
  -H "Content-Type: application/json"           \
  -d '{"name":"llama-server","source":"ghcr.io/ddvnguyen/llama-server-sm120:NEWSHA"}'
curl -X POST http://localhost:9700/restart?name=llama
```

> **Old approach (deprecated):** `infra/llama-rtx-node/docker-compose.yml` (RTX container),
> `systemctl --user restart llama-p100` (P100 systemd). Replaced by hydra-head.

#### Verify id_slot in response

After deploying both, confirm the fork change took effect:

```bash
# Non-streaming request should include "id_slot" in OAI response
curl -s -X POST http://localhost:8080/v1/chat/completions \
  -H "Content-Type: application/json" \
  -d '{"model":"qwen35moe","messages":[{"role":"user","content":"hi"}],"max_tokens":1,"stream":false}' \
  | python3 -c "import sys,json; print(json.load(sys.stdin).get('id_slot','MISSING'))"
# Expected: id_slot=0 or id_slot=1
```

---

## Monitoring

### Log shipping (OTel Collector)

Logs ship via OTLP/HTTP push from each service (llama-server, hydra-head,
hydra-core, store) to the OTel Collector on `localhost:4318` (or
`192.168.122.1:4318` for P100). The collector fans out to Loki.

```bash
# OTel Collector status
systemctl --user is-active infra-otel-collector

# Health endpoint
curl -so/dev/null -w'%{http_code}\n' http://localhost:13133/

# Restart if needed
systemctl --user restart infra-otel-collector
```

### Metrics endpoints

| Endpoint | What |
|---|---|
| `:9000/metrics` | Hydra.Core HTTP API |
| `:9501/metrics` | Hydra.Core Store RPC |
| `:8080/metrics` | llama-server RTX |
| `:9100/metrics` | Node exporter (host) |
| `:9835/metrics` | GPU exporter |

See `CLAUDE.md` `## Monitoring & Observability` for dashboards, alerts, and panel details.

---

## Architecture Notes

- **Single binary**: Hydra.Core handles HTTP API, Store RPC, routing, and llama-server communication
- **State streaming**: Hydra.Core pipes llama state directly via RPC — no disk round-trip
- **sendfile**: Store GET uses `Socket.SendFileAsync` for zero-copy file transfers
- **RPC wire format**: 16-byte request header (magic `0x4859`), 12-byte response header, binary payload
- **Reconnection**: RPC client retries with 100ms / 500ms / 2s backoff (3 attempts)
- **Semaphore**: RPC client serializes all calls through `SemaphoreSlim(1,1)` — one request at a time per client
- **Trace IDs**: Every RPC call carries a `trace_id` propagated through Serilog JSON logs
- **M2 chunked dedup**: KV state split into 1 MB chunks, SHA-256 hashed, content-addressed store. Repeated saves only store delta.
- **ChunkHashTeeStream**: Hydra.Core computes SHA-256 on-the-fly while streaming state from llama → Store, no second pass.
- **LocalChunkCache**: Hydra.Core persists `session_id → [chunk_hashes]` as JSON on disk. LRU eviction prevents unbounded growth.
- **llama shortcut**: If all chunk hashes known in local cache, llama PUT is skipped entirely (restore is no-op).

---

## Project Structure

```
src/
├── Hydra.Shared/          # M0.1 — RPC protocol, client, server base, logging
├── Hydra.Core/            # M0.2/M1/M2 — HTTP API + Store + ChunkEngine + ChunkStore + Router
├── core/                  # C# .NET — Hydra.Shared, Core
│   ├── Tests.Shared/      # RPC round-trips, reconnect, streaming
│   └── Tests.Core/        # Storage engine + ChunkEngine + ChunkStore + Router
├── llama-cpp/             # C++ fork (submodule)
└── tests/                 # Python system tests
```

---

## Common Commands

```bash
# Watch + rebuild on file changes (requires dotnet-watch)
dotnet watch --project src/core/Hydra.Core

# Add a new test project
dotnet new xunit -n Tests.Xyz -o src/Tests.Xyz
dotnet sln add src/Tests.Xyz

# Check all services are healthy
curl -s :9000/health
curl -s :9501/metrics | head
```

---

## Troubleshooting

| Symptom | Likely Cause | Fix |
|---------|-------------|-----|
| Store PUT fails after 30s | Missing tmpfs | `sudo bash infra/setup-ramdisk.sh` |
| Hydra.Core can't connect to Store | Store not initialized | Start Hydra.Core, verify :9500 health |
| `cache_n=0` after restore | `n_tokens <= n_past` | Ensure prompt has more tokens than cached state |
| RPC `InvalidDataException` | Wrong magic byte | Check client/server protocol version match |
| Pipe deadlock in tests | Pipe threshold (32 KB) | Use concurrent reader/writer pattern |
| Chunked save slow | First save = all chunks new (800 MB → ~800 chunks) | Normal. Second save of same session should be fast (delta only) |
| Chunk cache not persisting | `ChunkCacheDir` not writable | Check `HYDRA_CHUNK_CACHE_DIR` (default: `/tmp/hydra-chunk-cache`) |
| `PUT_CHUNKED` returns error | Manifest already exists with different session_id | Session IDs must be unique. Delete manifest first or use different ID |
| `dotnet test src/Hydra.sln` hangs | Parallel project execution → PG port/connection contention | Use `--settings src/Hydra.runsettings` (serializes assemblies) or run per-project |
| GC removed in-use chunks | GC ran while session active | GC only removes chunks NOT referenced by any manifest. Active sessions have manifests. Run GC only during idle periods. |
| `CUDA error: no kernel image is available for execution on the device` during warmup on CUDA1 (RTX 3060) | Dual-GPU host: fat sm_86+sm_120 build + qwen35 arch hits a missing kernel path on CUDA1 during `common_context_can_seq_rm` warmup. **Pre-existing issue, unrelated to RPC changes.** | Restrict to single GPU: `CUDA_VISIBLE_DEVICES=0 ./llama-engine ...`. Production is unaffected (3060 runs `--peer-only`, no model load/warmup). |
| Logs not appearing in Grafana | OTel Collector not running | `systemctl --user restart infra-otel-collector` — check health at `http://localhost:13133/` |

---

## Dev Environment — Operational Notes

### P100 VM Access

```bash
# SSH config alias (in ~/.ssh/config):
#   Host hydra-p100 192.168.122.21
#     HostName 192.168.122.21
#     User vm1
#     IdentityFile ~/.ssh/vm_agent_01
#     IdentitiesOnly yes

ssh hydra-p100 "systemctl --user status llama-p100"
```

### P100 llama-server — Force Restart

When the P100 llama-server is stuck in a CUDA kernel (stuck slot, 97% GPU util),
`systemctl --user stop` will hang (SIGTERM waits for CUDA). Force kill:

```bash
ssh hydra-p100 "sudo kill -9 \$(pgrep llama-server)"
sleep 2
ssh hydra-p100 "systemctl --user reset-failed llama-p100"
ssh hydra-p100 "systemctl --user start llama-p100"
```

Model reload takes ~30-40s on P100 (35B Qwopus MoE).

**Retired section:** The Python coordinator was removed in PR #203. Coordinator logic
is now embedded in Hydra.Core. No separate Python coordinator container exists.

### C# Service Rebuild

Hydra.Core is the single C# binary. Rebuild and redeploy with:

```bash
export HYDRA_HEAD_AUTH_TOKEN=$(cat .hydra-head-token)
podman build --no-cache --target core -f infra/Dockerfile -t localhost/hydra-core:latest .
podman compose -f infra/docker-compose.hydra.yml up -d --build core
```

> **GOTCHA 1 — `up -d` serves a STALE image.**
> `podman compose up -d core` (without `--build`) does **not** rebuild and will
> often stand the container up from a previously-cached image digest, silently
> ignoring your source changes. Always pass `--build`. If you suspect a stale
> layer is being reused, add `--no-cache` to the compose build:
> ```bash
> podman compose -f infra/docker-compose.hydra.yml up -d --build core
> ```
> (The `core` service in `infra/docker-compose.hydra.yml` has a `build:` block,
> so `up -d --build` rebuilds it from source.)

> **GOTCHA 2 — verifying the deployed DLL actually contains your change.**
> After redeploy, confirm the *running* container really runs your new code.
> A request that returns "old" behavior usually means a stale image (see
> GOTCHA 1), not a logic bug. Inspect the assembly inside the live container:
> ```bash
> podman cp hydra-system_core_1:/app/Hydra.Core.dll /tmp/core.dll
> strings -e l /tmp/core.dll | grep -c "some_unique_log_string"
> ```
> `-e l` is required: .NET stores string literals as **UTF-16 little-endian**,
> so plain `strings` (ASCII) and `grep -a` on the raw DLL return **0 matches**
> even when the string is present. The runtime image has no `strings` binary,
> so `podman exec … strings` fails silently — use `podman cp` to the host first.
> Also note the DLL lives at `/app/Hydra.Core.dll` (not `/publish/core/`).

> **GOTCHA 3 — stray keys in `models.json` break the whole-file load.**
> `model_file_aliases` is parsed defensively (non-object keys like `_comment`
> are skipped), but other top-level or nested objects are not. A single
> malformed/mis-typed value makes `ModelConfigLoader.TryLoad` throw and Core
> falls back to the hardcoded registry — then routing-identity → GGUF-alias
> translation silently no-ops. Watch the Core log for
> `model_config_loaded` (good) vs `model_config_fallback` (bad).

### Profile Switching (MoE ↔ Dense)

Two model profiles can be swapped at runtime. Each has its own config
files and routing semantics.

| Profile | Model | GPUs | Routing | Threshold |
|---------|-------|------|---------|-----------|
| **moe** (default) | Qwopus3.6-MoE-35B-A3B-v1-APEX-I-Mini | 5060 Ti + 3060 (COMBINED-OT) + P100 | COMBINED-OT + P/D split | 4096 |
| **dense** | Qwopus3.6-Dense-27B-Coder-Compat-MTP | 5060 Ti + 3060 (COMBINED-static) | COMBINED-static (every req) | 0 |

```bash
bash scripts/set-profile.sh moe     # MoE (35B)
bash scripts/set-profile.sh dense   # Dense (27B)
podman compose -f infra/docker-compose.hydra.yml up -d
```

| Role | MoE | Dense |
|------|-----|-------|
| Head (5060 Ti) | `infra/hydra-head/config/node-rtx.yaml` | `infra/hydra-head/config/node-rtx-27b.yaml` |
| Peer (3060) | `infra/hydra-head/config/node-rtx3060.yaml` | `infra/hydra-head/config/node-rtx3060-27b.yaml` |
| Workers | `infra/hydra-core/config/workers.json` | `infra/hydra-core/config/workers-27b.json` |
| Env file | `.env-moe` | `.env-dense` |

### Worker Configuration

Workers are defined in `WorkerConfig` (C#) and configured via
`HYDRA_CORE_WORKERS` in environment or config file:

```json
[
  {"name":"rtx","llama_url":"http://localhost:8080","llama_rpc_port":9503,
   "worker_type":3,"slots":2,"prefill_priority":1,"decode_priority":2,"decode_speed_tps":200,
   "can_prefill":true,"can_decode":true},
  {"name":"p100","llama_url":"http://192.168.122.21:8086","llama_rpc_port":9502,
   "worker_type":2,"slots":1,"prefill_priority":2,"decode_priority":1,"decode_speed_tps":28,
   "can_prefill":false,"can_decode":true}
]
```

| Field | Meaning |
|-------|---------|
| `worker_type` | 1=PREFILL, 2=DECODE, 3=MIXED |
| `prefill_priority` | Lower = better prefill worker (RTX=1, P100=2) |
| `decode_priority` | Lower = better decode worker (P100=1, RTX=2) |
| `decode_speed_tps` | Estimated decode speed (used in concurrency decision) |
| `max_prefill_tokens` | Optional: cap prefill size for this worker |
| `llama_rpc_port` | Port for hydra RPC (StateGet/Put/Meta) on llama-server

P100 is set to `worker_type=2` (DECODE only) — it will never be selected for
prefill. The scheduler will wait for RTX to become free instead of falling back
to P100's slow prefill (28 tok/s vs RTX's 285 tok/s).

### Prefix Checkpoints (System Prompt Cache)

Hydra.Core caches the system prompt KV to Store and restores it for subsequent
requests. Enabled by default (`prefix_checkpoint_enabled=true`).

**How it works:**
1. Request arrives with `{"role":"system","content":"..."}` in messages
2. Hydra.Core computes `prefix_hash = sha256(system_content)[:16]`
3. Tries to restore `prefix/{hash}` from Store → if found, KV loaded in ~44ms
4. If not found, full prefill runs and prefix is saved to Store afterward
5. Next request with same system prompt restores instantly

**Verify:**
```bash
# Check prefix saves
podman logs hydra-core | grep prefix_checkpoint_saved

# Check prefix restores
podman logs hydra-core | grep prefix_checkpoint_restored

# Check Store for prefix data
curl -s http://localhost:9501/metrics | grep put_manifest
```

**Prefix size examples:**
- OpenCode CLAUDE.md + workflow docs: 424 MB (~11K tokens)
- Simple system prompt: 72 MB (~500 tokens)

### Live Testing Workflow

```bash
# Small atomic request (RTX only)
curl -s -X POST http://localhost:9000/v1/chat/completions \
  -H "Content-Type: application/json" \
  -d '{"model":"qwen35moe","messages":[{"role":"user","content":"hi"}],"max_tokens":50,"stream":false}'

# Large concurrency request (RTX prefill → P100 decode)
curl -s -X POST http://localhost:9000/v1/chat/completions \
  -H "Content-Type: application/json" \
  -d '{"model":"qwen35moe","messages":[{"role":"user","content":"Write a long essay..."}],"max_tokens":32000,"stream":false}'

# With system prompt (triggers prefix checkpoint)
curl -s -X POST http://localhost:9000/v1/chat/completions \
  -H "Content-Type: application/json" \
  -d '{"model":"qwen35moe","messages":[{"role":"system","content":"You are a helpful assistant."},{"role":"user","content":"Explain GPUs."}],"max_tokens":500,"stream":false}'

# Multi-turn (same session_id)
curl -s -X POST http://localhost:9000/v1/chat/completions \
  -H "Content-Type: application/json" \
  -d '{"model":"qwen35moe","messages":[{"role":"user","content":"Explain GPUs."}],"max_tokens":100,"stream":false,"session_id":"multi-1"}'
```

### Monitoring Live Requests

```bash
# Watch Hydra.Core status
curl -s http://localhost:9000/status | python3 -m json.tool

# Watch Hydra.Core logs (non-health)
podman logs -f hydra-core | grep -v "health_ok\|GET /health\|GET /metrics"

# Watch for specific events
podman logs -f hydra-core 2>&1 | grep -E "concurrency|store_restore|prefix|state_saved"

# Check Store health
curl -s http://localhost:9000/health | python3 -c "import sys,json; d=json.load(sys.stdin); print(d['store'])"

# Check GPU
nvidia-smi --query-gpu=index,utilization.gpu,memory.used,temperature.gpu --format=csv,noheader

# Check RTX slots
curl -s http://localhost:8080/slots | python3 -c "
import sys,json
slots=json.load(sys.stdin)
for s in slots if isinstance(slots,list) else slots.get('slots',[]):
 nt=s.get('next_token',[{}])[0] if s.get('next_token') else {}
 print(f\"  slot {s['id']}: proc={s['is_processing']} n_past={s['n_past']} n_remain={nt.get('n_remain','?')}\")
"
```

### Request Flow (by route)

| Route | Condition | Flow |
|-------|-----------|------|
| Affinity | Session slot still warm + n_past guard passes | Skip prefill → decode on same GPU |
| Store Restore | Session has `has_store_state=True` | Restore KV from Store → decode on best decode worker |
| Cold Atomic | `estimated_new_tokens ≤ atomic_threshold` | Prefill + decode on same GPU → save KV to Store |
| Cold Concurrency | `estimated_new_tokens > atomic_threshold` | Prefill on RTX → save KV → restore on P100 → decode |

### Stuck Slot Recovery

A stuck slot means the llama-server slot is `is_processing=true` with `n_remain=0`
but was never freed. The health monitor detects this but doesn't auto-recover (#171).

**Manual recovery:**
```bash
# Option 1: Restart the stuck llama-server
ssh hydra-p100 'systemctl --user restart llama-p100'

# Option 2: Restart the Core (clears tracker state)
podman restart hydra-core
```

---

## Eval Tests

See `docs/eval-tests.md` for full methodology, pass/fail criteria, and monitoring.

### Quick Eval (NIAH passkey retrieval)

```bash
# 2K context, 50% needle depth
bash scripts/eval/run-niah.sh -c 2000 -d 50

# Sweep: 2K + 5K + 8K
bash scripts/eval/run-niah.sh -c 2000,5000,8000 -d 50

# All eval tiers (small: NIAH only, full: NIAH + perplexity)
bash scripts/eval/run-all.sh --small
```

### Checking progress during eval

```bash
# Phase durations (save_kv_ms, restore_kv_ms)
podman logs -f hydra-core 2>&1 | grep "request_timeline\|save_kv\|restore_kv"

# Llama-server activity (both nodes)
podman logs -f llama-cpp 2>&1 | grep "STATE_GET\|STATE_PUT\|n_past\|slot"
ssh hydra-p100 'journalctl --user -u llama-p100 -n 30 --no-pager | grep "STATE\|restored"'

# Token deltas (which GPU did the work)
watch -n 2 '
echo -n "RTX:  "; curl -s http://localhost:8080/metrics 2>/dev/null | grep "^llamacpp:prompt_tokens_total\|^llamacpp:tokens_predicted_total" | tr "\n" " " && echo ""
echo -n "P100: "; curl -s http://192.168.122.21:8086/metrics 2>/dev/null | grep "^llamacpp:prompt_tokens_total\|^llamacpp:tokens_predicted_total" | tr "\n" " " && echo ""
'

# Core health and slot state
curl -s http://localhost:9000/health | python3 -m json.tool
curl -s http://localhost:8080/slots | python3 -c "import sys,json; d=json.load(sys.stdin); [print(f'id={s[\"id\"]} past={s.get(\"n_past\",0)} proc={s.get(\"is_processing\",False)}') for s in d]"
```

### Verifying content quality directly (bypass Core)

When P/D split infrastructure is unstable, test content quality directly on RTX:

```bash
# Send prompt directly to RTX llama-server
curl -s http://localhost:8080/v1/chat/completions \
  -H "Content-Type: application/json" \
  -d '{"model":"balanced","messages":[{"role":"user","content":"What is 2+2?"}],"max_tokens":5}' | python3 -m json.tool

# Check both content AND reasoning_content for passkey in NIAH tests
# Model with --reasoning on puts thinking in reasoning_content,
# content may be empty if max_tokens is too low
```

### P100 recovery (CUDA hang)

Symptoms: `/v1/chat/completions` hangs, RPC StatePut times out, `systemctl` show `Failed with result 'timeout'`.

```bash
# 1. Stop P100 (may take 30s)
ssh hydra-p100 'systemctl --user stop llama-p100'

# 2. Wait for GPU cleanup
sleep 10; ssh hydra-p100 nvidia-smi | grep "MiB"

# 3. Restart + wait for model (~90s)
ssh hydra-p100 'systemctl --user start llama-p100'
watch -n 3 'curl -s http://192.168.122.21:8086/health'

# 4. Verify completions work
curl -s -m15 http://192.168.122.21:8086/v1/chat/completions \
  -H "Content-Type: application/json" \
  -d '{"model":"balanced","messages":[{"role":"user","content":"Hi"}],"max_tokens":3}'

# 5. Restart Core to clear stuck classifier
podman restart hydra-core
```

### Result storage

Test results are written to `tests/results/` (markdown reports + raw JSON + captured logs)
and `/tmp/hydra-eval-results/` (latest HTTP responses + extracted logs).
