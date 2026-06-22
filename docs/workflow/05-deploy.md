# 5. Deploy (only if the change touches runtime or the fork)

**Goal:** get the merged change running on the nodes. Commands: `DevelopmentRunBook.md`
("llama-server build", "P100 VM setup", "Quick Start").

## Services (C#/Python)
Redeploy via the control plane —
`cd infra && docker compose -f docker-compose.infra.yml -f docker-compose.hydra.yml up -d`
(or `start-env.sh`). Confirm health endpoints.

## llama.cpp fork change — three parts

### 1. Build per node
Build the engine-enabled llama-server with the `hydra-fork` branch patches:

| Node   | Arch   | CUDA      | Build flags                              | Output path                    |
|--------|--------|-----------|------------------------------------------|--------------------------------|
| RTX    | sm_120 | CUDA 13.2 | `GGML_CUDA_FORCE_CUBLAS=ON`              | `src/llama-cpp/build_sm120/bin/llama-engine` |
| P100   | sm_60  | CUDA 12.9 | `GGML_CUDA_FORCE_CUBLAS=ON`              | `src/llama-cpp/build_sm60/bin/llama-engine`  |

(Build output is named `llama-engine` since the #261/#262 rename. Deployed
paths below still use `llama-server` until the infra config migrates.)

**sm_60 build (P100) — explicit CUDA toolkit path** to avoid CMake cache cross-contamination from sm_120 build:
```bash
cmake -S . -B build_sm60 \
  -DCMAKE_BUILD_TYPE=Release \
  -DGGML_CUDA=ON \
  -DGGML_CUDA_FORCE_CUBLAS=ON \
  -DCUDAToolkit_ROOT=/usr/local/cuda-12.9 \
  -DCMAKE_CUDA_COMPILER=/usr/local/cuda-12.9/bin/nvcc \
  -DCMAKE_INSTALL_RPATH=/opt/software/llama-cpp-hydra-sm60/hydra-sm60/lib \
  -DCMAKE_BUILD_RPATH=/opt/software/llama-cpp-hydra-sm60/hydra-sm60/lib \
  -DLLAMA_CUDA_BY_DEFAULT=OFF \
  -DLLAMA_RPC=ON \
  -DLLAMA_SERVER=ON && \
cmake --build build_sm60 --config Release -j$(nproc) --target llama-engine
```

### 2. Push OCI image for binary distribution
Hydra-head pulls the llama-server binary from a container image in ghcr.io. After building, push:

**RTX:**
```bash
# Build the container image with the new binary baked in
podman build -f infra/hydra-head/Dockerfile.rtx -t hydra-head:rtx .
# Or push just the binary as an OCI artifact:
skopeo copy --format=oci \
  dir:src/llama-cpp/build_sm120/bin/llama-engine \
  docker://ghcr.io/ddvnguyen/llama-server:engine
```

**P100:**
```bash
# Build a minimal OCI image with just the binary
cd src/llama-cpp/build_sm60/bin
podman build -t llama-server-sm60:engine -f- . <<'DOCKERFILE'
FROM scratch
COPY llama-engine /llama-server
ENTRYPOINT ["/llama-server"]
DOCKERFILE

# Tag and push
podman tag llama-server-sm60:engine ghcr.io/ddvnguyen/llama-server-sm60:engine
podman push ghcr.io/ddvnguyen/llama-server-sm60:engine
```

Verify the image digest:
```bash
skopeo inspect docker://ghcr.io/ddvnguyen/llama-server-sm60:engine | jq .Digest
```

### 3. Push the fork + bump submodule

**Order matters — always push the fork BEFORE the parent submodule bump is
merged.** A parent commit that points at an un-pushed submodule SHA leaves the
PR unreviewable and breaks fresh clones. See `02-implement.md` for the
contributor-side rule and `04-commit-pr.md` for the verification step.

This step assumes the contributor already pushed during step 2 of the task
lifecycle (`02-implement.md`). If the submodule bump merged before the fork
was pushed, the only remediation is a follow-up PR that re-points to a
reachable SHA.

**Cross-repo coordination.** When a Hydra feature requires a C++ change, the
work must produce a **fork issue** in `ddvnguyen/llama.cpp` *and* a **fork PR**
merged to `hydra-fork` *before* the parent submodule bump lands. The
`08-llama-fork.md` step is the canonical coordinator for that flow; this
deploy step is the parent-side mirror of the same work.

```bash
# 0. Verify the parent commit's pinned SHA is reachable on the fork.
#    (This must already be true before merge; this is a belt-and-braces check.)
SHA=$(git ls-tree HEAD src/llama-cpp | awk '{print $3}')
URL=$(git config --file .gitmodules --get submodule.src/llama-cpp.url)
BRANCH=$(git config --file .gitmodules --get submodule.src/llama-cpp.branch)
git ls-remote "$URL" "refs/heads/$BRANCH" | grep -q "$SHA" \
  || { echo "FATAL: $SHA is not on $URL  $BRANCH — fix before deploy"; exit 1; }

# 1. Re-confirm the fork branch is up to date with the pinned SHA.
#    (The CI build only fetches the submodule pointer; it does not push it.)
cd src/llama-cpp
git fetch origin
git push origin "$BRANCH"   # no-op if already up to date

# 2. (Optional) Bump the parent pointer in a follow-up commit if a
#    separate fix landed on the fork but not in the parent. The normal
#    case is: the parent commit that pinned the SHA was already in the
#    PR, and the fork push was done in step 2 of the task lifecycle.
cd ../..
git add src/llama-cpp
git diff --cached --submodule=log   # confirm the diff is the SHA only
git commit -m "chore: bump llama.cpp submodule to <sha>"
```

**Reminder for `deploy-llama` CI job** — it checks out the submodule with
`submodules: true`. A dangling pinned SHA makes the CI job fail at the
checkout step, not at the deploy step. Catch this at PR time, not at deploy.

## Deploy to RTX
RTX runs hydra-head as a **container** (`hydra-head-rtx`). Rebuild and redeploy:

```bash
# Rebuild hydra-head + container image + restart
bash scripts/deploy-hydra-head.sh rtx
```

This builds the Go binary, builds the container image (which bakes in the llama-server binary from `infra/hydra-head/Dockerfile.rtx`), stops the old container, and starts the new one.

## Deploy to P100 VM
P100 runs hydra-head as a **user systemd service** (`systemctl --user`). No sudo needed —
the `vm1` user owns `/opt/software/llama-cpp-hydra-sm60/hydra-sm60/bin/`.

### If hydra-head also changed
```bash
bash scripts/deploy-hydra-head.sh p100
```

### If only the llama-server binary changed
The hydra-head checks `binaries.llama-server.dest` on startup and skips the OCI pull
if the binary already exists on disk. To deploy a new llama-server binary directly:

```bash
# Build sm_60 binary (see section 1), then:
rsync -avz src/llama-cpp/build_sm60/bin/llama-engine \
  hydra-p100:/opt/software/llama-cpp-hydra-sm60/hydra-sm60/bin/llama-server

# Restart hydra-head to pick up the new binary:
ssh hydra-p100 "systemctl --user restart hydra-head"
```

### Force re-pull from OCI
If you pushed a new `:engine` tag to ghcr.io and want hydra-head to re-download:

```bash
# Via the hydra-head API (must be running):
TOKEN=$(cat .hydra-head-token)
curl -X POST -H "Authorization: Bearer $TOKEN" \
  http://192.168.122.21:9700/update
```

This triggers `POST /update` which re-pulls the binary from the OCI registry
regardless of whether the file exists at `dest`.

## P100 VM working notes

- **SSH alias**: `hydra-p100` → `~/.ssh/config` entry for `192.168.122.21`, user `vm1`,
  key `~/.ssh/vm_agent_01`.
- **No sudo on VM**: All deployed paths under `/opt/software/llama-cpp-hydra-sm60/` and
  `/home/vm1/hydra/` are owned by `vm1:vm1` (755). Binary copies and service restarts
  run as the `vm1` user — no password required.
- **User systemd**: Service definition at
  `~/.config/systemd/user/hydra-head.service`. Manage with
  `systemctl --user {start,stop,restart,status} hydra-head`.
- **Logs**: `journalctl --user -u hydra-head -f`.
- **Service file path**: `/home/vm1/.config/systemd/user/hydra-head.service`
- **Config files**: `/home/vm1/hydra/config/global.yaml` and `node-p100.yaml`
- **Working directory**: `/home/vm1/hydra`
- **Binary destination (from OCI pull)**: `/opt/software/llama-cpp-hydra-sm60/hydra-sm60/bin/llama-server`
- **Direct binary replacement** (preferred for quick updates):
  ```bash
  rsync -avz path/to/new/llama-server hydra-p100:/opt/software/llama-cpp-hydra-sm60/hydra-sm60/bin/llama-server
  ssh hydra-p100 "systemctl --user restart hydra-head"
  ```

## Verify the deployed version

Check each node:
```bash
curl http://localhost:8080/version       # RTX
curl http://192.168.122.21:8086/version  # P100
curl http://localhost:9700/health         # RTX hydra-head
curl http://192.168.122.21:9700/health   # P100 hydra-head
curl http://localhost:9000/health         # Coordinator (both nodes reported)
```

→ Next: `06-monitoring.md`
