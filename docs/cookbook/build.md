# build — core (C#), head (Go), llama-engine (C++)

Exact build commands for the three components. Distilled from
`DevelopmentRunBook.md`, `CLAUDE.md` (Build Environment Quirks), and the deploy
scripts. For *why* each piece exists see `docs/decisions/` and
`docs/architecture.md`.

## Environment quirks (read first)

- **`go` is NOT in the default PATH.** It lives at `~/go-sdk/go/bin/go` (v1.23.4,
  tarball install). Always:
  ```bash
  export PATH=$HOME/go-sdk/go/bin:$PATH
  go version   # go version go1.23.4 linux/amd64
  ```
- **No sudo** on the host. Root work goes through `podman exec` or user-level
  systemd units.
- **CUDA toolkits** at `/opt/software/cuda/{12.9, 13.2, 13.2.1, 13.3}/`.
  Set `-DCUDAToolkit_ROOT` (cmake) / `CUDA_PATH` per target arch: P100 (sm_60) needs a
  **CUDA 12.x** toolkit — the verified 12.9 minimal toolkit is at
  `~/opt/cuda-12.9-min` (authoritative for sm_60 builds as of 2026-08-28,
  see `docs/hydra-test/evidence/EVIDENCE-SUMMARY.md`); RTX targets build with
  13.2.
- Full environment reference: `docs/build-environment.md`.

## Hydra.Core (C# / .NET 10)

Single binary: HTTP API + Store RPC + embedded coordinator.

```bash
# Local build
dotnet build src/core/Hydra.Core

# Local test (per-project — no runsettings needed)
dotnet test src/core/Tests.Shared
dotnet test src/core/Tests.Core
dotnet test src/core/Tests.E2E/     # hermetic (Aspire + fake engine), PR-merge gate

# Full solution (MUST pass runsettings — parallel projects collide on Postgres)
dotnet test src/Hydra.sln --settings src/Hydra.runsettings --verbosity normal
```

**Container image** (`infra/Dockerfile` has only a `core` target):

```bash
podman build --no-cache --target core -f infra/Dockerfile -t localhost/hydra-core:latest .
```

Gotchas:

- `dotnet test src/Hydra.sln` **hangs** without `--settings src/Hydra.runsettings`
  (parallel project execution → PG port/connection contention).
- Verify a rebuilt DLL actually contains your change before debugging logic:
  ```bash
  podman cp hydra-system_core_1:/app/Hydra.Core.dll /tmp/core.dll
  strings -e l /tmp/core.dll | grep -c "some_unique_log_string"
  ```
  `-e l` is required (.NET string literals are UTF-16 LE). The runtime image has
  no `strings` binary — copy the DLL to the host first.

## Hydra Head (Go)

```bash
export PATH=$HOME/go-sdk/go/bin:$PATH

# Build (RunBook form)
go build -C src/head -o ../../bin/hydra-head .
# (up.sh uses the equivalent: go build -o bin/hydra-head ./src/head/...)

# Test
go test -C src/head ./internal/...

# Container image (Dockerfile.rtx COPYs bin/hydra-head — build the binary first)
podman build -f infra/hydra-head/Dockerfile.rtx -t localhost/hydra-head:rtx .
```

Gotchas:

- The `localhost/hydra-head:rtx` image is shared by the RTX 5060 Ti and RTX 3060
  head containers. If the image tag exists but is stale, `podman rmi
  localhost/hydra-head:rtx` and rebuild.
- P100 has **no container image** — `deploy-hydra-head.sh p100` rsyncs the Go
  binary + config + systemd unit over SSH.

## llama-engine (C++ fork)

The fork lives in `src/llama-cpp` (submodule, `hydra-fork` branch of
`ddvnguyen/llama.cpp`).

### CI/CD build (recommended)

Build + package + push OCI image in one manual dispatch, on the self-hosted RTX
runner with persistent ccache. No local CUDA toolchain access needed:

```bash
gh workflow run hydra-build.yml --repo ddvnguyen/llama.cpp --ref hydra-fork \
  -f build_llama_engine=true \
  -f build_llama_server=false \
  -f arch_sm86_sm120=true \
  -f arch_sm60=false \
  -f runner_target=local \
  -f execution_mode=matrix

gh run list --repo ddvnguyen/llama.cpp --workflow hydra-build.yml --limit 5
gh run watch <run-id> --repo ddvnguyen/llama.cpp
```

Resulting images: `ghcr.io/ddvnguyen/llama-server:<arch>-<binary>-<fork-version>-<short-sha>`
(e.g. `sm86-sm120-llama-engine-0.1.0-a157edf`). To deploy:
`gh workflow run ci.yml -f deploy-llama=true -f llama-tag-suffix=<fork-version>-<short-sha>`.

### Fast local iteration (edit → build → verify)

`hydra-dev` preset: same fat `86;120` archs and correctness flags, but no LTO
and ccache wired in — single-file rebuilds drop from 120–180 s to seconds once
warm. **Not** for producing the deploy binary (LTO affects steady-state decode
perf).

```bash
WORK=/mnt/WorkDisk/Workplace/hydra_vortex/src/llama-cpp
cd $WORK
cmake --preset hydra-dev -DCUDAToolkit_ROOT=/opt/software/cuda/13.2 \
  -DCMAKE_CUDA_COMPILER=/opt/software/cuda/13.2/bin/nvcc
cmake --build build-hydra-dev --target llama-engine -j$(nproc)
ccache -s    # confirm hits are incrementing
```

### Manual fat build (RTX 5060 Ti + RTX 3060, sm_86+sm_120, CUDA 13.2)

Fallback when CI is unavailable. Same flags `hydra-build.yml` uses:

```bash
CUDA_PATH=/opt/software/cuda/13.2
cmake -B build_sm86_sm120 -G Ninja \
  -DCMAKE_CUDA_ARCHITECTURES="86;120" \
  -DCPACK_PACKAGE_NAME="ik-llama-sm86-sm120-cuda13.2" \
  -DGGML_CUDA=ON -DGGML_CUDA_FORCE_CUBLAS=ON \
  -DGGML_CUDA_FA=ON -DGGML_CUDA_FA_ALL_QUANTS=ON \
  -DGGML_CUDA_GRAPHS=ON -DGGML_CUDA_NCCL=ON \
  -DGGML_RPC=ON -DGGML_NVML=ON -DGGML_NATIVE=ON \
  -DCMAKE_BUILD_TYPE=Release -DBUILD_SHARED_LIBS=ON \
  -DCMAKE_BUILD_RPATH='$ORIGIN' -DCMAKE_INSTALL_RPATH='$ORIGIN' \
  -DCMAKE_BUILD_WITH_INSTALL_RPATH=ON \
  -DCMAKE_INTERPROCEDURAL_OPTIMIZATION=ON \
  -DLLAMA_BUILD_EXAMPLES=OFF -DLLAMA_BUILD_TESTS=OFF
cmake --build build_sm86_sm120 --target llama-engine -j$(nproc)

# Sanity: --version must show [shared] (static builds hang on RTX — #346)
build_sm86_sm120/bin/llama-engine --version
# Both archs present in the fat cubin set:
cuobjdump --list-elf build_sm86_sm120/bin/libggml-cuda.so | head
```

The RTX head containers bind-mount `build_sm86_sm120/bin/` — no OCI pull needed
on the host.

### Manual P100 build (sm_60, CUDA 12.x)

Pascal needs **GCC ≤ 14** host compiler (CUDA 12.9 rejects GCC 15+).

```bash
CUDA_PATH=~/opt/cuda-12.9-min   # verified 12.9 minimal toolkit (2026-08-28)
cmake -B build_sm60_v2 -G Ninja \
  -DCMAKE_CUDA_ARCHITECTURES="60" \
  -DCMAKE_CUDA_HOST_COMPILER="/usr/bin/g++-14" \
  -DGGML_RPC=ON -DGGML_CUDA=ON -DGGML_CUDA_FORCE_CUBLAS=ON \
  -DGGML_CUDA_FORCE_MMQ=OFF -DGGML_CUDA_FA_ALL_QUANTS=OFF \
  -DGGML_NATIVE=ON -DCPACK_INCLUDE_COMMANDS=ON \
  -DCMAKE_BUILD_RPATH='$ORIGIN' -DCMAKE_INSTALL_RPATH='$ORIGIN' \
  -DCMAKE_BUILD_TYPE=Release -DLLAMA_BUILD_TESTS=OFF \
  -DBUILD_SHARED_LIBS=ON -DLLAMA_BUILD_EXAMPLES=OFF \
  -DCMAKE_INTERPROCEDURAL_OPTIMIZATION=ON
cmake --build build_sm60_v2 --target llama-server -j$(nproc)
```

## Engine gotchas

- **`bin/llama-engine`, not `bin/llama-server`**, for COMBINED mode: the
  `--rpc-engine / --combined-ot-pattern / --ggml-rpc-port` flags are only in
  `llama-engine` (it strips them from argv before common parsing;
  `llama-server` rejects them).
- **Dual-GPU warmup crash:** on a 5060 Ti + 3060 host, standalone model warmup
  on CUDA1 can fail with `CUDA error: no kernel image is available...` —
  pre-existing, not a build problem. Test with `CUDA_VISIBLE_DEVICES=0`.
  Production is unaffected (3060 runs `--peer-only`, no model load).
- sm_60 engine builds from `a7b40fdce` (after `234083a45`, before `3206b13b6`)
  fail **every** PREFILL M2 with `hash pre-pass hashed 0 B`. Build from a fork
  tip that contains the `3206b13b6` fix (fork tip `67ceb00bd` does).

## Related recipes

- Deploy what you just built → `deploy.md`
- Live-rig test tiers against a running stack → `DevelopmentRunBook.md`
  (Running Tests, Tier 2–4)
