#!/usr/bin/env bash
# Deploy Hydra Head to GPU nodes
# Usage: bash scripts/deploy-hydra-head.sh [rtx|p100|rtx3060|rtx+rtx3060|all]
#
# core + head-rtx5060ti + head-rtx3060 are three independent containers in
# one compose project (infra/docker-compose.hydra.yml); the two heads share
# the `hydra-head:rtx` image and both depend on `core`, but are otherwise
# unrelated. `rtx+rtx3060` and `all` build the shared image and bring up
# `core` ONCE (deploy_shared_setup), then deploy the requested heads
# concurrently (run_concurrent) — each targets only its own compose
# service (`up -d <service>`, not a whole-project `up -d`), so they don't
# step on each other. P100 has no compose/image dependency at all (SSH +
# systemd on a separate VM) and joins the same concurrent batch for `all`.
#
# RTX path (since #322 / PR #328):
#   - Build Go binary + container image
#   - Deploy via `podman compose -f infra/docker-compose.hydra.yml up -d
#     head-rtx5060ti`, userns=host (so the in-container promtail can read
#     /mnt/containers/ctr.log directly, no socat proxy needed).
#   - The compose file is the source of truth for mount paths,
#     env vars, health checks, and resource limits.
#
# RTX 3060 path (since feat/add-rtx-3060-head):
#   - Same Go binary and same `hydra-head:rtx` image (the compose's
#     `head-rtx3060` service bind-mounts the rtx3060 node config and
#     sets `CUDA_VISIBLE_DEVICES=1` + `nvidia.com/gpu=1`).
#   - The fat sm_86+sm_120 llama-server binary at
#     `src/llama-cpp/build_sm86_sm120/bin/` is bind-mounted into the
#     container; the 3060 picks the sm_86 SASS path.
#
# P100 path (still uses systemd, not in compose):
#   - rsync binary + configs to hydra-p100
#   - install / enable systemd service
#   - hydra-head is rebuilt with the configurable health-check
#     values (PR #328) so the slow-VM-disk model load (3-5 min)
#     doesn't trigger the kill loop any more.

set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$REPO_ROOT"

GREEN='\033[0;32m'; RED='\033[0;31m'; YELLOW='\033[1;33m'; BOLD='\033[1m'; NC='\033[0m'
ok()   { echo -e "  ${GREEN}✓${NC} $*"; }
warn() { echo -e "  ${YELLOW}⚠${NC}  $*"; }
fail() { echo -e "  ${RED}✗${NC} $*"; }
step() { echo -e "\n${BOLD}==> $*${NC}"; }
die()  { fail "$*"; exit 1; }

# `podman compose up -d` is idempotent by design — it skips recreating a
# container podman believes is already running. That check trusts podman's
# cached state, not reality: if conmon dies out from under a container
# (observed 2026-08-02 — no OOM, no GPU reset, conmon just vanished),
# `podman inspect` keeps reporting Running:true with a PID that no longer
# exists on the host forever after, and every subsequent `compose up` quietly
# no-ops against the zombie instead of recreating it. The health-wait then
# burns its full timeout against a container nothing is running in, on every
# single deploy, until a human notices and force-removes it by hand.
# Reap it proactively: if the recorded PID is dead, the container is a
# zombie regardless of what podman's status string says — remove it so the
# following `compose up` is forced to create a fresh one.
reap_zombie_container() {
  local name="$1"
  if ! podman container exists "$name" 2>/dev/null; then
    return 0
  fi
  local pid
  pid=$(podman inspect "$name" --format '{{.State.Pid}}' 2>/dev/null || echo 0)
  if [ "$pid" != "0" ] && ! kill -0 "$pid" 2>/dev/null; then
    warn "Container $name is desynced (podman reports it running; PID $pid is dead on the host) — removing so compose recreates it"
    podman rm -f "$name" 2>/dev/null || true
  fi
}

TARGET="${1:-all}"

# ── Auth Token Management ─────────────────────────────────────────────────────
TOKEN_FILE="$REPO_ROOT/.hydra-head-token"

generate_token() {
  # An explicit token in the environment always wins — this is how CI supplies
  # it (secrets.HYDRA_HEAD_AUTH_TOKEN), since .hydra-head-token is gitignored
  # and therefore never present in a fresh Actions checkout.
  if [ -n "${HYDRA_HEAD_AUTH_TOKEN:-}" ]; then
    ok "Using auth token from HYDRA_HEAD_AUTH_TOKEN environment variable"
    return
  fi

  if [ -f "$TOKEN_FILE" ]; then
    ok "Using existing auth token from $TOKEN_FILE"
    return
  fi

  # Do NOT mint a token when running unattended. Previously this branch ran in
  # CI (no token file in the Actions checkout) and generated a fresh random
  # token, which deploy_p100() then pushed to the VM — silently re-keying the
  # P100 on every run to a value neither the coordinator nor the other nodes
  # knew. Minting is only ever correct for a human bootstrapping a new host.
  if [ -n "${CI:-}" ] || [ -n "${GITHUB_ACTIONS:-}" ]; then
    die "No auth token available. Set the HYDRA_HEAD_AUTH_TOKEN secret in the repository — refusing to generate one in CI, which would re-key the running nodes."
  fi

  step "Generating new auth token"
  # Generate a random 32-byte hex token
  openssl rand -hex 32 > "$TOKEN_FILE"
  chmod 600 "$TOKEN_FILE"
  ok "Generated new auth token: $TOKEN_FILE"
}

get_token() {
  if [ -n "${HYDRA_HEAD_AUTH_TOKEN:-}" ]; then
    printf '%s' "$HYDRA_HEAD_AUTH_TOKEN"
    return
  fi
  if [ ! -f "$TOKEN_FILE" ]; then
    die "Auth token not found at $TOKEN_FILE and HYDRA_HEAD_AUTH_TOKEN is unset. Run: openssl rand -hex 32 > $TOKEN_FILE"
  fi
  cat "$TOKEN_FILE"
}

# ── Go Build ─────────────────────────────────────────────────────────────────
build_go() {
  step "Building hydra-head (Go)"

  export PATH=$HOME/go-sdk/go/bin:$PATH
  if ! command -v go &>/dev/null; then
    die "Go not found. Install with: mkdir -p ~/go-sdk && cd /tmp && wget https://go.dev/dl/go1.25.0.linux-amd64.tar.gz && tar -C ~/go-sdk -xzf go1.25.0.linux-amd64.tar.gz — see docs/build-environment.md"
  fi

  go build -C "$REPO_ROOT/src/head" -o "$REPO_ROOT/bin/hydra-head" .
  ok "Built bin/hydra-head ($(stat -c '%s' bin/hydra-head) bytes)"
}

# ── Container Image Build ────────────────────────────────────────────────────
build_rtx_image() {
  step "Building hydra-head:rtx image"

  if ! command -v podman &>/dev/null; then
    die "podman not found"
  fi

  podman build -f infra/hydra-head/Dockerfile.rtx -t hydra-head:rtx .
  ok "Built container image hydra-head:rtx"
}

# ── C# Coordinator Image Build ───────────────────────────────────────────────
# The Coordinator C# binary is the only part of the system whose
# source changes don't get picked up by `podman compose up -d`'s
# normal cache invalidation (the Dockerfile layers may all be
# unchanged even when the C# source has). This function computes
# a fast hash over the C# source tree and only rebuilds the
# `hydra-core` image when the hash differs from the last build.
#
# Stamps the hash in bin/.hydra-core-source-hash; deletes the
# stamp file (or use FORCE_REBUILD_CORE=1) to force a rebuild.
build_core_image() {
  step "Building hydra-core image (C# Coordinator)"

  if ! command -v podman &>/dev/null; then
    die "podman not found"
  fi

  local stamp_file="$REPO_ROOT/bin/.hydra-core-source-hash"
  local source_hash
  # Exclude obj/ and bin/ — those are dotnet build outputs that change
  # on every local build even when no source file changed, which would
  # make the hash unstable and force spurious rebuilds. Only hash files
  # under the source tree (cs/csproj/props/sln).
  source_hash=$( \
    find "$REPO_ROOT/src/core" \
      -type d \( -name obj -o -name bin \) -prune -o \
      -type f \( -name "*.cs" -o -name "*.csproj" -o -name "*.props" -o -name "*.sln" \) -print \
      2>/dev/null | sort \
      | xargs sha256sum 2>/dev/null \
      | sha256sum | cut -c1-16)
  local cached_hash=""
  [ -f "$stamp_file" ] && cached_hash=$(cat "$stamp_file")

  if [ "${FORCE_REBUILD_CORE:-0}" = "1" ]; then
    cached_hash=""
    warn "FORCE_REBUILD_CORE=1 — forcing hydra-core image rebuild"
  fi

  if [ -n "$cached_hash" ] \
     && [ "$cached_hash" = "$source_hash" ] \
     && podman image exists localhost/hydra-core:latest 2>/dev/null; then
    ok "hydra-core image is up to date (source hash $source_hash matches $stamp_file)"
    return 0
  fi

  podman build -f "$REPO_ROOT/infra/Dockerfile" --target core -t hydra-core:latest "$REPO_ROOT" 2>&1 | tail -5
  mkdir -p "$(dirname "$stamp_file")"
  echo "$source_hash" > "$stamp_file"
  ok "Built hydra-core:latest (source hash $source_hash, stamp at $stamp_file)"
}

# ── Build-Type Gate (smoke test) ──────────────────────────────────────────────
# Fail fast if the llama-server binary we'd bind-mount (RTX) or scp
# (P100) is a static build. Static builds hang in the post-init phase
# on RTX sm_120 (see ddvnguyen/hydra_vortex#346). After #349, the
# build type is surfaced in `llama-server --version` as [shared] or
# [static], and scripts/ci/check-build-type.sh enforces it.
check_llama_build_type_local() {
  local bind_src="$REPO_ROOT/src/llama-cpp/build_sm120_v3/bin/llama-server"
  if [ ! -x "$bind_src" ]; then
    # Fall back to the legacy path with a useful error if it doesn't exist either.
    bind_src="$REPO_ROOT/src/llama-cpp/build_sm120/bin/llama-server"
  fi
  if [ ! -x "$bind_src" ]; then
    # No local binary. That is now the DEFAULT, not an error:
    # docker-compose.hydra.yml no longer bind-mounts build_sm86_sm120/bin —
    # hydra-head pulls the engine from the OCI ref pinned in node-rtx.yaml.
    # The local bind-mount is opt-in via docker-compose.hydra.local-build.yml.
    #
    # This gate exists only to catch a *static* local build (#346), so when
    # there is no local build to check there is nothing to protect against.
    # Failing here blocked every CI deploy, since the Actions checkout never
    # has a compiled binary.
    if grep -qE '^\s*source:\s*(ghcr\.io|docker\.io|quay\.io)/' \
         "$REPO_ROOT/infra/hydra-head/config/node-rtx.yaml" 2>/dev/null; then
      ok "No local llama-server build; node-rtx.yaml pulls from OCI — skipping local build-type gate"
      return 0
    fi
    die "llama-server binary not found at $REPO_ROOT/src/llama-cpp/build_sm120_v3/bin/llama-server (or build_sm120/bin/), and node-rtx.yaml has no OCI source to fall back on — build it with DevelopmentRunBook.md"
  fi
  step "Build-type gate (RTX local binary)"
  bash "$REPO_ROOT/scripts/ci/check-build-type.sh" "$bind_src" || \
    die "RTX llama-server is a static build; would hang in post-init. Fix: rebuild with -DBUILD_SHARED_LIBS=ON. See #346."
}

# Check the FAT sm_86+sm_120 binary used by both head-rtx (5060 Ti)
# and head-rtx3060 in the same pod. Same build-type rules: shared-lib
# only, no static (see #346). Falls back to build_sm120_v3 (sm_120 only)
# so a deploy still works if the fat build hasn't been done yet — the
# 3060 will then run via PTX JIT (slow but functional, see #368).
check_llama_build_type_local_fat() {
  local bind_src="$REPO_ROOT/src/llama-cpp/build_sm86_sm120/bin/llama-server"
  if [ ! -x "$bind_src" ]; then
    warn "Fat sm_86+sm_120 build not present at $bind_src; falling back to sm_120-only at build_sm120_v3/. RTX 3060 will run via PTX JIT (slower)."
    return 0
  fi
  step "Build-type gate (RTX fat binary)"
  bash "$REPO_ROOT/scripts/ci/check-build-type.sh" "$bind_src" || \
    die "Fat llama-server is a static build. Fix: rebuild with -DBUILD_SHARED_LIBS=ON. See #346."
}

check_llama_build_type_p100() {
  step "Build-type gate (P100 VM binary)"
  local vm_bin="/opt/software/llama-cpp-hydra-sm60/hydra-sm60/bin/llama-server"

  # Run --version ON THE VM. The previous implementation scp'd just the
  # executable to a temp dir and checked it there — but that file is a small
  # launcher (~16 KB) that dynamic-links libllama-server-impl.so and the
  # libggml-*.so beside it. Copied without them it cannot start, so --version
  # emitted no "[shared]" token and check-build-type.sh (which treats a
  # missing token as static) reported every P100 deploy as a static build.
  # That is exactly the #498 failure mode — an executable separated from its
  # shared libraries — reproduced inside the check itself. Checking in place
  # is both simpler and actually tests the artifact as it will run.
  local version_out
  if ! version_out=$(ssh -o ConnectTimeout=10 -o BatchMode=yes hydra-p100 \
        "'$vm_bin' --version 2>&1" 2>/dev/null); then
    warn "Could not run $vm_bin on hydra-p100 — skipping P100 build-type check"
    return 0
  fi

  if ! grep -q '\[shared\]' <<<"$version_out"; then
    die "P100 llama-server is not a shared-lib build (output: ${version_out:-<empty>}). Fix: rebuild with -DBUILD_SHARED_LIBS=ON. See #346."
  fi
  ok "P100 llama-server reports [shared]"
}

# ── Pre-deploy Cleanup ───────────────────────────────────────────────────────
# Host-side exporter Quadlets (infra-node-exporter / infra-nvidia-
# exporter) were removed in commit TBD. The in-container hydra-head
# now owns :9100/:9835 exclusively. infra-promtail was removed in
# #363 — per-child labeled writers push logs to the OTel Collector
# directly. This function is a no-op kept for backward-compat with
# hosts that still have the old Quadlets installed.
stop_host_sidecars() {
  if ! command -v systemctl &>/dev/null; then return; fi
  export DBUS_SESSION_BUS_ADDRESS="unix:path=/run/user/$(id -u)/bus"
  export XDG_RUNTIME_DIR="/run/user/$(id -u)"
  for svc in infra-node-exporter infra-nvidia-exporter; do
    if systemctl --user is-active --quiet "$svc.service" 2>/dev/null; then
      systemctl --user stop "$svc.service" 2>/dev/null || true
      ok "Stopped host $svc (replaced by hydra-head child)"
    fi
  done
  unset DBUS_SESSION_BUS_ADDRESS XDG_RUNTIME_DIR
}

# ── Auth File Sanity Check ──────────────────────────────────────────────────
# The in-container hydra-head needs to read the host's podman auth.json
# to pull llama-server from ghcr.io. With userns=host the container
# user (uid 1000) IS host user (uid 1000), so the file just needs to
# be at the standard path and be readable. If it's 600, the chmod below
# is a no-op; if it's 644, no change. We do this defensively because
# the persistent copy at ~/.config/containers/auth.json is what the
# user actually maintains; the /run/user/1000/... copy is a tmpfs
# shadow of it.
check_auth_file() {
  local auth_file="$HOME/.config/containers/auth.json"
  local xdg_auth="/run/user/$(id -u)/containers/auth.json"
  for f in "$auth_file" "$xdg_auth"; do
    if [ -f "$f" ]; then
      local mode
      mode=$(stat -c '%a' "$f")
      if [ "$mode" = "600" ]; then
        chmod 644 "$f" && ok "chmod 644 $f (was 600; in-container uid 1000 needs to read it)"
      fi
    fi
  done
}

# ── Shared setup: build the image once, bring up `core` once ─────────────────
# Everything RTX and RTX3060 have in common — the `hydra-head:rtx` image
# build and the `core` service — happens here, exactly once, before any
# per-head work starts. This is what makes it safe to deploy the heads
# concurrently afterward: neither of them touches the image build or the
# whole-project compose state again, only their own service.
deploy_shared_setup() {
  step "Shared setup (image build + core)"

  build_go
  build_core_image
  generate_token
  AUTH_TOKEN=$(get_token)
  build_rtx_image
  stop_host_sidecars
  check_auth_file

  # Ensure the promtail positions volume is removed (was used
  # by the promtail binary inside hydra-head; Promtail is
  # gone in #363, so the volume is no longer needed).
  if podman volume exists hydra-head-promtail-positions 2>/dev/null; then
    podman volume rm hydra-head-promtail-positions 2>/dev/null || true
  fi

  # Drop the old manually-created 'hydra-system' pod (from before the
  # compose existed). It's safe to remove — compose will recreate. Must
  # happen before any per-service `compose up`, and only once — this is
  # a whole-project-level operation, not something the per-head paths
  # should ever repeat.
  for old_pod in hydra-system pod_hydra-system; do
    if podman pod exists "$old_pod" 2>/dev/null; then
      timeout 10 podman pod rm -f "$old_pod" 2>/dev/null || true
    fi
  done

  # Load profile from .env (set by set-profile.sh) so podman-compose
  # resolves ${VAR} substitutions (HYDRA_HEAD_RTX_NODE_CONFIG, etc.).
  # Only export HYDRA_* vars to avoid leaking unrelated .env entries.
  if [ -f "$REPO_ROOT/.env" ]; then
    while IFS='=' read -r key val; do
      [[ "$key" =~ ^HYDRA_ ]] && export "$key=$val"
    done < <(grep -v '^#' "$REPO_ROOT/.env" | grep -v '^$')
  fi

  # Export the token so podman-compose picks it up via ${HYDRA_HEAD_AUTH_TOKEN:?}
  # (exported here, inherited by every concurrent deploy_*_only subshell).
  export HYDRA_HEAD_AUTH_TOKEN="$AUTH_TOKEN"

  reap_zombie_container hydra-system_core_1

  # Service-scoped — brings up ONLY `core`. head-rtx5060ti/head-rtx3060
  # both declare `depends_on: core`, so a bare `up -d` (no service arg)
  # would also try to reconcile them; scoping it avoids that entirely.
  if ! podman compose -f infra/docker-compose.hydra.yml up -d core 2>&1 | tail -10; then
    die "podman compose up (core) failed — check the output above. Common causes: HYDRA_HEAD_AUTH_TOKEN not exported, image not built, or userns conflict."
  fi
  ok "Compose up: core in pod hydra-system"

  step "Waiting for core health"
  for i in 1 2 3 4 5 6 7 8 9 10 11 12 13 14 15; do
    sleep 3
    if curl -sf http://localhost:9000/health >/dev/null 2>&1; then
      ok "core (:9000) is healthy"
      break
    fi
    if [ "$i" = "15" ]; then
      warn "core health not green after 45s — check 'podman ps' and 'podman logs hydra-system_core_1'"
    fi
  done
}

# ── Deploy: RTX via compose ──────────────────────────────────────────────────
# Standalone entry point (CLI / single-target CI step): does the shared
# setup itself, then deploys just this head. For concurrent multi-head
# deploys, deploy_shared_setup runs once up front and deploy_rtx_only runs
# directly — see the `all` / `rtx+rtx3060` cases below.
deploy_rtx() {
  deploy_shared_setup
  deploy_rtx_only
}

deploy_rtx_only() {
  step "Deploying head-rtx5060ti (compose)"

  check_llama_build_type_local

  # Drop any pre-compose standalone container (we used to run a single
  # hydra-head-rtx container via `podman run`; the compose brings it
  # up under the pod_hydra-system name).
  if podman container exists hydra-head-rtx 2>/dev/null; then
    podman stop hydra-head-rtx 2>/dev/null || true
    podman rm hydra-head-rtx 2>/dev/null || true
  fi

  reap_zombie_container hydra-system_head-rtx5060ti_1

  # Service-scoped — only touches head-rtx5060ti. `core` is already up
  # (deploy_shared_setup), so this is safe to run concurrently with
  # deploy_rtx3060_only, which only ever touches head-rtx3060.
  if ! podman compose -f infra/docker-compose.hydra.yml up -d head-rtx5060ti 2>&1 | tail -10; then
    die "podman compose up (head-rtx5060ti) failed — check the output above."
  fi
  ok "Compose up: head-rtx5060ti in pod hydra-system"

  step "Waiting for head-rtx5060ti health"
  for i in 1 2 3 4 5 6 7 8 9 10 11 12 13 14 15; do
    sleep 3
    if curl -sf http://localhost:9700/health >/dev/null 2>&1; then
      ok "head-rtx5060ti (:9700) is healthy"
      break
    fi
    if [ "$i" = "15" ]; then
      warn "head-rtx5060ti health not green after 45s — check 'podman ps' and 'podman logs hydra-system_head-rtx5060ti_1'"
    fi
  done

  # Verify the 2 in-container sidecar exporters are responding
  # (promtail :9080 removed in #363 — per-child writers push
  # directly to the OTel Collector.)
  for i in 1 2 3 4 5; do
    sleep 3
    if curl -sf http://localhost:9100/metrics >/dev/null 2>&1 \
       && curl -sf http://localhost:9835/metrics >/dev/null 2>&1; then
      ok "Sidecars up: node_exporter :9100, nvidia_gpu_exporter :9835"
      break
    fi
  done

  # Verify the OTel Collector is healthy and the per-service
  # push is working.
  sleep 5
  if curl -sf http://localhost:13133/ >/dev/null 2>&1; then
    ok "OTel Collector health: OK (port 13133)"
  else
    warn "OTel Collector :13133 not responding — check systemctl --user status infra-otel-collector"
  fi
}

# ── Deploy: P100 via systemd (not in compose) ────────────────────────────────
# P100 runs as user vm1 with user-level systemd. No sudo required.
# Paths: /home/vm1/hydra/{bin,config}
deploy_p100() {
  step "Deploying to P100 (VM, user-level systemd)"

  if ! ssh -o ConnectTimeout=5 -o BatchMode=yes hydra-p100 true 2>/dev/null; then
    die "Cannot reach hydra-p100 via SSH (check ~/.ssh/config)"
  fi

  # Resolve the token here rather than relying on deploy_rtx() having run
  # first in the same shell. deploy_rtx() and deploy_rtx3060() both do this;
  # deploy_p100() did not, so `deploy-hydra-head.sh p100` on its own died
  # with "AUTH_TOKEN: unbound variable" under `set -u`. CI invokes the three
  # targets as three separate processes, so it hit this every time.
  generate_token
  AUTH_TOKEN=$(get_token)

  check_llama_build_type_p100

  # Create directories (user-level, no sudo needed)
  ssh hydra-p100 "mkdir -p /home/vm1/hydra/bin /home/vm1/hydra/config /home/vm1/.config/hydra-head"

  # Copy binary
  rsync -avz bin/hydra-head hydra-p100:/home/vm1/hydra/bin/hydra-head
  ok "Copied hydra-head binary"

  # Copy config files (incl. the new health: section from PR #328 —
  # node-p100.yaml overrides max_fails: 30 so the slow-VM-disk
  # model load doesn't get killed).
  rsync -avz infra/hydra-head/config/global.yaml hydra-p100:/home/vm1/hydra/config/global.yaml
  rsync -avz infra/hydra-head/config/node-p100.yaml hydra-p100:/home/vm1/hydra/config/node-p100.yaml
  rsync -avz infra/hydra-head/config/preset-p100.ini hydra-p100:/home/vm1/hydra/config/preset-p100.ini
  ok "Copied config files"

  # Create environment file with auth token. Written over stdin rather than
  # interpolated into the remote command string, which exposed the token in
  # `ps` output on the VM for the lifetime of the ssh command.
  ssh hydra-p100 "umask 077 && cat > /home/vm1/.config/hydra-head/env" \
    <<<"HYDRA_HEAD_AUTH_TOKEN=$AUTH_TOKEN"
  ssh hydra-p100 "chmod 600 /home/vm1/.config/hydra-head/env"
  ok "Created auth token environment file"

  # Copy user-level systemd service
  scp infra/hydra-head/hydra-head.user.service hydra-p100:/home/vm1/.config/systemd/user/hydra-head.service
  ssh hydra-p100 "systemctl --user daemon-reload"
  ok "Installed user-level systemd service"

  # Restart service
  ssh hydra-p100 "systemctl --user restart hydra-head"
  ok "Restarted hydra-head service"

  # Wait for health
  sleep 3
  if ssh hydra-p100 "curl -sf http://localhost:9700/health" &>/dev/null; then
    ok "Hydra Head P100 is healthy"
  else
    warn "Hydra Head P100 not responding yet (may still be starting; model load = 3-5 min on P100 VM disk)"
  fi
}

# ── Deploy: RTX 3060 via compose (same pod, second service) ──────────────
# The `head-rtx3060` service is a sibling of `head-rtx` in the same
# `pod_hydra-system`. It reuses the `hydra-head:rtx` image; the
# compose `command:` points it at node-rtx3060.yaml and api-port
# 9701, and `nvidia.com/gpu=1` + `CUDA_VISIBLE_DEVICES=1` restrict
# it to the 3060.
#
# Standalone entry point (CLI / single-target CI step): does the shared
# setup itself, then deploys just this head. For concurrent multi-head
# deploys, deploy_shared_setup runs once up front and deploy_rtx3060_only
# runs directly — see the `all` / `rtx+rtx3060` cases below.
deploy_rtx3060() {
  deploy_shared_setup
  deploy_rtx3060_only
}

deploy_rtx3060_only() {
  step "Deploying head-rtx3060 (compose)"

  check_llama_build_type_local_fat

  # Ensure the bind mount source (the FAT sm_86+sm_120 build dir) exists.
  if [ ! -x "$REPO_ROOT/src/llama-cpp/build_sm86_sm120/bin/llama-server" ]; then
    warn "FAT sm_86+sm_120 build not at $REPO_ROOT/src/llama-cpp/build_sm86_sm120/bin/llama-server. The 3060 service will still start; it will PTX-JIT from the sm_120-only binary at build_sm120_v3. See feat/add-rtx-3060-head for the build command."
  fi

  # Drop a pre-compose standalone container if it exists.
  if podman container exists hydra-system_head-rtx3060_1 2>/dev/null; then
    podman stop hydra-system_head-rtx3060_1 2>/dev/null || true
    podman rm hydra-system_head-rtx3060_1 2>/dev/null || true
  fi

  reap_zombie_container hydra-system_head-rtx3060_1

  # Service-scoped — only touches head-rtx3060. `core` is already up
  # (deploy_shared_setup), so this is safe to run concurrently with
  # deploy_rtx_only, which only ever touches head-rtx5060ti.
  if ! podman compose -f infra/docker-compose.hydra.yml up -d head-rtx3060 2>&1 | tail -10; then
    die "podman compose up (head-rtx3060) failed — check the output above."
  fi
  ok "Compose up: head-rtx3060 in pod hydra-system"

  # Wait for the 3060 head to come up on :9701
  step "Waiting for head-rtx3060 health on :9701"
  for i in 1 2 3 4 5 6 7 8 9 10 11 12 13 14 15; do
    sleep 3
    if curl -sf http://localhost:9701/health >/dev/null 2>&1; then
      ok "head-rtx3060 (:9701) is healthy"
      break
    fi
    if [ "$i" = "15" ]; then
      warn "head-rtx3060 :9701 not yet green after 45s — check 'podman logs hydra-system_head-rtx3060_1'"
    fi
  done
}

# ── Run named deploy functions concurrently ───────────────────────────────────
# Used for `all` / `rtx+rtx3060`: once deploy_shared_setup has built the
# image and brought up `core`, RTX, RTX3060, and P100 have no shared
# mutable state — each `deploy_*_only` targets only its own compose
# service (or, for P100, a separate host over SSH entirely) — so there is
# no reason to make them wait on each other. Output is prefixed per branch
# since it interleaves; `wait` on each PID (not `wait` with no args) is
# what lets us catch which branch failed under `set -e`, since a
# backgrounded command's failure doesn't trigger errexit on its own.
run_concurrent() {
  local pids=() names=() fn failed=0
  for fn in "$@"; do
    ( "$fn" 2>&1 | sed -u "s/^/[$fn] /" ) &
    pids+=("$!")
    names+=("$fn")
  done
  local i
  for i in "${!pids[@]}"; do
    if ! wait "${pids[$i]}"; then
      fail "${names[$i]} failed"
      failed=1
    fi
  done
  [ "$failed" = "0" ] || die "One or more concurrent deploys failed (see prefixed output above)"
}

# ── Main ──────────────────────────────────────────────────────────────────────
case "$TARGET" in
  rtx)
    deploy_rtx
    ;;
  rtx3060)
    deploy_rtx3060
    ;;
  p100)
    deploy_p100
    ;;
  rtx+rtx3060)
    deploy_shared_setup
    run_concurrent deploy_rtx_only deploy_rtx3060_only
    ;;
  all)
    deploy_shared_setup
    run_concurrent deploy_rtx_only deploy_rtx3060_only deploy_p100
    ;;
  *)
    die "Unknown target: $TARGET (expected: rtx, rtx3060, p100, rtx+rtx3060, all)"
    ;;
esac

step "Deployment complete"
echo -e "${GREEN}${BOLD}Hydra Head deployed successfully.${NC}"
echo ""
echo "  RTX Core API:  http://localhost:9000/health"
echo "  RTX Head API:  http://localhost:9700/status"
echo "  RTX 3060 Head: http://localhost:9701/status"
echo "  P100 Head API: http://192.168.122.21:9700/status"
echo ""
echo "  Auth token: $TOKEN_FILE"
echo "  Test: curl -H 'Authorization: Bearer \$(cat $TOKEN_FILE)' http://localhost:9700/status | jq"
