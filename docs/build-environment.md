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
for the canonical build commands.

### Quick reference

| Target | CUDA | Build dir | Output |
|---|---|---|---|
| RTX 5060 Ti (sm_120) | 13.2 | `build_sm120/` | `bin/llama-engine` (17 KB launcher) + `lib*.so` (942 MB) |
| P100 (sm_60) | 12.9 | `build_sm60/` | `bin/llama-server` (63 MB monolithic) |

```bash
# RTX
cmake -S src/llama-cpp -B src/llama-cpp/build_sm120 \
  -DCMAKE_BUILD_TYPE=Release -DGGML_CUDA=ON \
  -DCUDAToolkit_ROOT=/usr/local/cuda-13.2 \
  -DLLAMA_CUDA_BY_DEFAULT=OFF -DLLAMA_RPC=ON -DLLAMA_SERVER=ON
cmake --build src/llama-cpp/build_sm120 -j$(nproc) --target llama-server

# P100 — needs explicit CUDA 12.9 path (CMake cache cross-contamination)
cmake -S src/llama-cpp -B src/llama-cpp/build_sm60 \
  -DCMAKE_BUILD_TYPE=Release -DGGML_CUDA=ON \
  -DCUDAToolkit_ROOT=/usr/local/cuda-12.9 \
  -DCMAKE_CUDA_COMPILER=/usr/local/cuda-12.9/bin/nvcc \
  -DLLAMA_CUDA_BY_DEFAULT=OFF -DLLAMA_RPC=ON -DLLAMA_SERVER=ON
cmake --build src/llama-cpp/build_sm60 -j$(nproc) --target llama-server
```

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
