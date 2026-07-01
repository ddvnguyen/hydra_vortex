# Build Environment

How to build each language in this Hydra repo. **Read this first** if `go` /
`dotnet` / `podman build` fails with "not found" — the toolchains live in
non-standard paths.

## TL;DR

```bash
# Go (hydra-head) — NOT in default PATH
export PATH=$HOME/go-sdk/go/bin:$PATH

# C# (Hydra.Core) — uses dotnet from /usr/bin (default)
# No env needed

# Python (tests, scripts) — system python3
# No env needed

# CUDA — multiple toolkits installed under /opt/software/cuda
# Use the one matching your target arch (see llama-bench-guide.md)
```

## Go — `src/head/` (hydra-head)

The Go toolchain is **not** in the system PATH. It lives in `~/go-sdk/`
because it was installed from a tarball (no `apt` access):

```bash
/home/ddv/go-sdk/go/bin/go       # v1.23.4 (or whatever the active version is)
~/go-sdk/go/bin/go                # same — $HOME expands to /home/ddv
```

### One-time setup (per shell)

```bash
export PATH=$HOME/go-sdk/go/bin:$PATH
go version
# go version go1.23.4 linux/amd64
```

To make it persistent, add to `~/.bashrc` or `~/.zshrc`:

```bash
echo 'export PATH=$HOME/go-sdk/go/bin:$PATH' >> ~/.bashrc
```

### Build

```bash
cd src/head
go build -o bin/hydra-head .              # → 11.6 MB binary
go test ./...                              # 5 packages, ~13s
```

### Install / upgrade

If `~/go-sdk/go/bin/go` is missing or you need a newer Go:

```bash
mkdir -p ~/go-sdk
cd /tmp
wget https://go.dev/dl/go1.25.0.linux-amd64.tar.gz
tar -C ~/go-sdk -xzf go1.25.0.linux-amd64.tar.gz
~/go-sdk/go/bin/go version
```

(Can't `apt install golang-go` — no sudo in this env.)

### Common pitfalls

| Symptom | Cause | Fix |
|---|---|---|
| `go: command not found` | `~/go-sdk/go/bin` not in PATH | `export PATH=$HOME/go-sdk/go/bin:$PATH` |
| `go.mod: no such file` | Ran from repo root, not `src/head/` | `cd src/head` first |
| `cannot find package` after a refactor | stale build cache | `go clean -cache` |

## C# — `src/core/` (Hydra.Core)

.NET 10 SDK is in the default PATH:

```bash
dotnet --version
# 10.0.109
```

### Build

```bash
dotnet build src/Hydra.sln -c Release
dotnet test  src/core/Tests.Shared/
dotnet test  src/core/Tests.Core/
```

### Coverage

```bash
dotnet test --collect:"XPlat Code Coverage" src/core/Tests.Core/
# Report at src/core/Tests.Core/TestResults/*/coverage.cobertura.xml
```

## Python — `tests/`, `scripts/`

System Python 3.14:

```bash
python3 --version
# Python 3.14.4
```

### Tests

```bash
# System tests
pytest tests/system

# Bench harness (PR #307)
python3 tests/bench/compare.py --baseline tests/bench/baselines/main.json --current report.json
```

No `pip install` needed — system packages only. If you do need a venv:

```bash
python3 -m venv .venv
source .venv/bin/activate
```

## C++ — `src/llama-cpp/` (the llama.cpp fork — submodule)

This is a **git submodule**. CMake + GCC + CUDA. See `docs/llama-bench-guide.md`
for the canonical build commands and `docs/combined-reservation-design.md` for
the COMBINED engine-mode wire-up that ties this binary to the C# Coordinator.

### Build targets

The fork has **two** binaries the Hydra system uses:

- `llama-server` — the standard server (P100, fallback). Accepts all standard
  common args. Does **not** accept the COMBINED-mode flags
  (`--rpc-engine`, `--combined-ot-pattern`, `--ggml-rpc-port`) — the
  `extract_hydra_capability_flags` filter only lives in `llama-engine`.
- `llama-engine` — drop-in replacement for `llama-server` PLUS the COMBINED
  engine mode. Used by both the 5060 Ti (head) and the 3060 (peer) on the host.
  Accepts the same args as `llama-server` plus the hydra flags.

### Quick reference

| Target | CUDA | Build dir | Output | Notes |
|---|---|---|---|---|
| RTX 5060 Ti (sm_120) | 13.2 | `build_sm120_v3/` | `bin/llama-engine` (17 KB launcher) + `lib*.so` (~942 MB) | Head's `n-gpu-layers: all` |
| RTX 3060 (sm_86)    | 13.2 | `build_sm86/`     | `bin/llama-engine` (10 MB launcher) + `lib*.so` | Peer's `n-gpu-layers: 99` (12 GB VRAM) |
| FAT sm_86+sm_120    | 13.2 | `build_sm86_sm120/` | One SASS image with both archs (159 MB) | **Preferred** — same binary serves both 5060 Ti + 3060. See cmake below. |
| P100 (sm_60)        | 12.9 | `build_sm60/`     | `bin/llama-server` (63 MB monolithic) | P100 stays on its own build (separate CUDA 12.9 toolchain) |

```bash
# FAT sm_86+sm_120 — one binary, both archs. CMAKE_CUDA_ARCHITECTURES is the
# magic: cubins for sm_86 (Ampere, RTX 3060) and sm_120a (Blackwell, RTX
# 5060 Ti) compiled into libggml-cuda.so.0.13.1. Confirmed via
# `cuobjdump --list-elf` (see PR #373). -DGGML_RPC=ON is required for the
# COMBINED engine mode (the embedded ggml-RPC backend) — was OFF in the
# initial fat build; see fork-side issue #376.
mkdir -p src/llama-cpp/build_sm86_sm120 && cd src/llama-cpp/build_sm86_sm120 && \
  cmake .. -G Ninja \
    -DCMAKE_BUILD_TYPE=Release \
    -DBUILD_SHARED_LIBS=ON \
    -DGGML_NATIVE=ON \
    -DGGML_CUDA=ON -DGGML_CUDA_FA=ON -DGGML_CUDA_FA_ALL_QUANTS=OFF \
    -DGGML_CUDA_FORCE_CUBLAS=ON -DGGML_CUDA_GRAPHS=ON -DGGML_CUDA_NCCL=ON \
    -DGGML_RPC=ON -DGGML_NVML=ON \
    -DCMAKE_CUDA_ARCHITECTURES="86;120" \
    -DCUDAToolkit_ROOT=/opt/software/cuda/13.2 \
    -DCMAKE_INSTALL_RPATH='$ORIGIN;$ORIGIN/..' && \
  cmake --build . --config Release -j$(nproc) --target llama-engine

# RTX 5060 Ti only (sm_120) — used as fallback for hosts that don't build
# the fat binary
mkdir -p src/llama-cpp/build_sm120_v3 && cd src/llama-cpp/build_sm120_v3 && \
  cmake .. -G Ninja \
    -DCMAKE_BUILD_TYPE=Release -DBUILD_SHARED_LIBS=ON \
    -DGGML_NATIVE=ON -DGGML_CUDA=ON -DGGML_CUDA_FA=ON \
    -DGGML_CUDA_FORCE_CUBLAS=ON -DGGML_RPC=ON -DGGML_NVML=ON \
    -DCMAKE_CUDA_ARCHITECTURES="120" \
    -DCUDAToolkit_ROOT=/opt/software/cuda/13.2 \
    -DCMAKE_INSTALL_RPATH='$ORIGIN;$ORIGIN/..' && \
  cmake --build . --config Release -j$(nproc) --target llama-engine

# RTX 3060 only (sm_86) — small build, ~10 min, in case you want a per-arch
# binary. Same cmake as the fat one but CMAKE_CUDA_ARCHITECTURES="86" alone.
cd src/llama-cpp && mkdir -p build_sm86 && cd build_sm86 && \
  cmake .. -G Ninja -DCMAKE_BUILD_TYPE=Release -DBUILD_SHARED_LIBS=ON \
    -DGGML_CUDA=ON -DGGML_RPC=ON -DGGML_NVML=ON \
    -DCMAKE_CUDA_ARCHITECTURES="86" \
    -DCUDAToolkit_ROOT=/opt/software/cuda/13.2 && \
  cmake --build . --config Release -j$(nproc) --target llama-engine

# P100 — needs explicit CUDA 12.9 path (CMake cache cross-contamination)
mkdir -p src/llama-cpp/build_sm60 && cd src/llama-cpp/build_sm60 && \
  cmake .. -G Ninja \
    -DCMAKE_BUILD_TYPE=Release -DGGML_CUDA=ON -DGGML_CUDA_FA=ON \
    -DGGML_CUDA_FORCE_CUBLAS=ON -DGGML_RPC=ON -DGGML_NVML=ON \
    -DCUDAToolkit_ROOT=/opt/software/cuda/12.9 \
    -DCMAKE_CUDA_COMPILER=/opt/software/cuda/12.9/bin/nvcc \
    -DCMAKE_INSTALL_RPATH=/opt/software/llama-cpp-hydra-sm60/hydra-sm60/lib \
    -DCMAKE_BUILD_RPATH=/opt/software/llama-cpp-hydra-sm60/hydra-sm60/lib \
    -DLLAMA_CUDA_BY_DEFAULT=OFF -DLLAMA_RPC=ON -DLLAMA_SERVER=ON && \
  cmake --build . --config Release -j$(nproc) --target llama-engine
```

### Verifying the fat binary has both archs

```bash
# Should print alternating ELF files: libggml-cuda.1.sm_86.cubin
# and libggml-cuda.2.sm_120a.cubin (and so on)
cuobjdump --list-elf /path/to/build_sm86_sm120/bin/libggml-cuda.so | head
```

### Verifying the ggml-RPC backend is exposed at runtime

The peer's `node-rtx3060.yaml` has `ggml-rpc-port: 9504`. When the 3060's
llama-engine is up, you should see:
```bash
ss -tlnp | grep 9504
# LISTEN 0  16  0.0.0.0:9504  *  users:(("llama-engine",...))
```
The head's `--ggml-rpc-port 9505` (rtx) stays commented out until #376 lands —
with it enabled the engine currently crashes at model load inside
`ggml_backend_rpc_add_server` (libggml-rpc.so offset +0x11183).

### Build-time gates

- `BUILD_SHARED_LIBS=ON` is **mandatory**. The launcher is dynamically linked
  to `libllama-server-impl.so`, `libllama-common.so`, `libggml*.so`, etc. A
  static build (`BUILD_SHARED_LIBS=OFF`, e.g. `build_sm120_static/`) hangs in
  the post-init phase on the 5060 Ti — see #346.
- `GGML_NATIVE=ON` tunes the build for the host CPU. Cheap, do it.
- `GGML_CUDA_FORCE_CUBLAS=ON` skips the cuBLASLt path on Ada/Blackwell where
  it has perf issues with some Q3_K / Q4_K dequant patterns. We want the
  consistent cuBLAS path.
- `GGML_CUDA_FA=ON` enables flash-attn. We need it for ctx > 8K.
- `GGML_CUDA_FA_ALL_QUANTS=OFF` keeps the Q5_K/Q6_K flash-attn kernels
  disabled (perf is worse for those quants on Blackwell).
- `GGML_RPC=ON` is required for COMBINED engine mode (peer backend). The
  initial fat build had it OFF — re-configure + rebuild if you're on
  `build_sm86_sm120` from before 2026-07-01.
- `CMAKE_CUDA_ARCHITECTURES="86;120"` for the fat binary. Use `"120"` or
  `"86"` for the per-arch builds. The `120a` (Blackwell arch-specific) shows
  up in `cuobjdump` automatically because the toolchain knows.

### Common build failures

| Symptom | Cause | Fix |
|---|---|---|
| `invalid argument: --rpc-engine` at startup (in llama-server, not llama-engine) | using `llama-server` binary in the head's config; it doesn't have the COMBINED filter | switch `llama.binary` to `llama-engine` (see `infra/hydra-head/config/node-rtx.yaml`) |
| `RPC backend not available in this build` | forgot `-DGGML_RPC=ON` | reconfigure with `-DGGML_RPC=ON` + rebuild |
| `nvcc not found` when targeting sm_60 | CMake cache picked up CUDA 13.2 path | delete `build_sm60/CMakeCache.txt` and re-run with `CUDAToolkit_ROOT=/opt/software/cuda/12.9` |
| `unsupported GNU version 15` | gcc too new for older CUDA | use CUDA 12.9 (P100) or install `gcc-13` and use `CXX=/usr/bin/g++-13` for the build |
| `BUILD_SHARED_LIBS=OFF` causes `llama-engine` to hang on first request | static build has no libllama-server-impl.so, falls through to a broken code path | set `BUILD_SHARED_LIBS=ON` (default in our cmake calls above) |

## CUDA toolkit selection

Multiple CUDA versions installed under `/opt/software/cuda/`:

| Version | Path | Used for |
|---|---|---|
| 12.9 | `/opt/software/cuda/12.9/` | P100 (sm_60) builds |
| 13.2 | `/opt/software/cuda/13.2/` | RTX (sm_120) builds |
| 13.2.1 | `/opt/software/cuda/13.2.1/` | (extra copy) |
| 13.3 | `/opt/software/cuda/13.3/` | (extra copy) |

The `llama.cpp` build needs the CUDA toolkit root passed explicitly because
CMake caches the path from the first build and re-uses it for the second
(sm_60 build with sm_120 cache → wrong nvcc → cryptic build errors).

## Podman / containers

```bash
podman --version
# podman 5.x
```

The podman storage is at `/mnt/containers/overlay/` (77 GB partition).
**Don't run `podman system prune` without checking** — it can remove
images that the running containers depend on. See `docs/eval-tests.md` for
the safe cleanup procedure.

## Path summary (cheat sheet)

| Tool | Path | Notes |
|---|---|---|
| go | `~/go-sdk/go/bin/go` | not in default PATH |
| dotnet | `/usr/bin/dotnet` | 10.0.109 |
| python3 | `/usr/bin/python3` | 3.14.4 |
| cmake | `/usr/bin/cmake` | |
| gcc / g++ | `/usr/bin/gcc` `/usr/bin/g++` | |
| podman | `/usr/bin/podman` | storage at `/mnt/containers/` |
| CUDA 12.9 | `/opt/software/cuda/12.9/` | P100 builds |
| CUDA 13.2 | `/opt/software/cuda/13.2/` | RTX builds |
| hydra-head binary | `bin/hydra-head` (after `go build`) | deploy to P100 via rsync |
| llama-server binary | `src/llama-cpp/build_sm{60,120}/bin/` | mounted into hydra-head container |

## What to check first when a build fails

1. **Wrong working dir?** `cd src/head` for Go, `src/core/` for C#,
   repo root for C++/Python.
2. **PATH issue?** `which go` should return `~/go-sdk/go/bin/go` for Go.
3. **Stale cache?** `go clean -cache` for Go, `rm -rf build_*/` for C++.
4. **Wrong CUDA version?** P100 needs 12.9, RTX needs 13.2. The build
   needs `DCUDAToolkit_ROOT=...` set explicitly (see `docs/llama-bench-guide.md`).
5. **Permission issue?** This env has no sudo. `apt` / `dnf` /
   `systemctl` (root) won't work. Use `dpkg --root=...` or
   `podman exec --user=0` if you need root.
