# 5. Deploy (only if the change touches runtime or the fork)

**Goal:** get the merged change running on the nodes. Commands: `DevelopmentRunBook.md`
("llama-engine / llama-server — build & package", "P100 VM setup", "Quick Start").

## Services (C#/Python)
Redeploy via the control plane —
`cd infra && docker compose -f docker-compose.infra.yml -f docker-compose.hydra.yml up -d`
(or `start-env.sh`). Confirm health endpoints.

## llama.cpp fork change — three parts

### 1. Build & push via CI/CD (preferred — do not build locally)

Trigger `hydra-build.yml` in `ddvnguyen/llama.cpp` (manual-dispatch only,
`hydra-fork` branch). It builds **and** pushes the OCI image in one
dispatch — a coding agent does not need local CUDA toolchain access or to
run cmake itself. Full flag reference and fallback manual-build commands:
`DevelopmentRunBook.md` → "llama-engine / llama-server — build & package".

```bash
gh workflow run hydra-build.yml --repo ddvnguyen/llama.cpp --ref hydra-fork \
  -f build_llama_engine=true -f build_llama_server=false \
  -f arch_sm86_sm120=true -f arch_sm60=false \
  -f runner_target=local -f execution_mode=matrix

gh run list --repo ddvnguyen/llama.cpp --workflow hydra-build.yml --limit 5
gh run watch <run-id> --repo ddvnguyen/llama.cpp
```

Check both boxes for the architecture you need (`arch_sm86_sm120` for
RTX 5060 Ti + RTX 3060, `arch_sm60` for P100 — P100 always builds
`llama-server`, RTX picks `llama-engine`/`llama-server` per the binary
checkboxes) and it fans out one build per combo.

### 2. Resulting OCI image

```
ghcr.io/ddvnguyen/llama-server:<arch>-<binary>-<fork-version>-<short-sha>
ghcr.io/ddvnguyen/llama-server:<arch>-<binary>-latest
```

`<fork-version>` is `src/llama-cpp/VERSION` (bumped by hand — this fork has
no semver of its own otherwise). RTX/RTX 3060 share the `sm86-sm120-llama-engine`
tag prefix; P100 uses `sm60-llama-server`.

To actually deploy a build's artifact, trigger the `deploy-llama` job in this
repo's `ci.yml` (`gh workflow run ci.yml -f deploy-llama=true -f
llama-tag-suffix=<fork-version>-<short-sha>`, or `latest`). It pins the
`source:` tag in `infra/hydra-head/config/node-{rtx,rtx3060,p100}.yaml`,
commits that, and redeploys all three nodes via `deploy-hydra-head.sh`.
Leave `llama-tag-suffix` blank to redeploy with whatever tag is already
checked in (the old behavior).

RTX/RTX 3060 default to the OCI pull as of this change — the old bind-mount
of a local `src/llama-cpp/build_sm86_sm120/bin/` build (which used to shadow
the pull) is now opt-in via `infra/docker-compose.hydra.local-build.yml`, for
fast local iteration on the fork. Only fall back to a manual `podman build -f
.github/workflows/hydra-build.Dockerfile` + `podman push` if CI/CD is
genuinely unavailable.

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
