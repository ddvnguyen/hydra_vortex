# deploy — hydra-system pod, images, token env

Deploy the production stack: `pod_hydra-system` (core + head-rtx + head-rtx3060,
one pod, host network) on the RTX host, and hydra-head under user systemd on
the P100 VM (`hydra-p100` → 192.168.122.21). Distilled from
`scripts/deploy-*.sh`, `docs/hydra-system-pod.md`, and `DevelopmentRunBook.md`.

## The auth token (do this first)

Both compose projects refuse to start without the head auth token
(`${HYDRA_HEAD_AUTH_TOKEN:?}` in `infra/docker-compose.hydra.yml`).

```bash
# One-time generation (.hydra-head-token is gitignored):
[ -f .hydra-head-token ] || openssl rand -hex 32 > .hydra-head-token

# Export before any podman compose up:
export HYDRA_HEAD_AUTH_TOKEN=$(cat .hydra-head-token)
```

`deploy-hydra-head.sh` resolves the token from the `HYDRA_HEAD_AUTH_TOKEN`
env, the repo secret, or `.hydra-head-token` — and **refuses to generate one
in CI** (that would re-key running nodes).

## Deploy the heads (and the pod)

```bash
export PATH=$HOME/go-sdk/go/bin:$PATH    # go is not in default PATH

bash scripts/deploy-hydra-head.sh rtx      # 5060 Ti: go build → image → compose up
bash scripts/deploy-hydra-head.sh rtx3060  # 3060 peer: same image, second service
bash scripts/deploy-hydra-head.sh p100     # VM: rsync binary + configs + systemd unit
bash scripts/deploy-hydra-head.sh all      # builds image once, deploys concurrently
```

What the script does (RTX path): `go build` → `podman build
localhost/hydra-head:rtx` → `chmod 644` the ghcr auth files →
`podman compose -f infra/docker-compose.hydra.yml up -d <service>` → waits for
healthchecks → verifies the OTel Collector (`http://localhost:13133/` → 200)
and that hydra streams are non-empty in Loki.

## Deploy Hydra.Core

```bash
# With optional version bump:
bash scripts/deploy-hydra.sh              # build + deploy
bash scripts/deploy-hydra.sh patch        # bump patch + build + deploy

# Or manual rebuild + redeploy:
export HYDRA_HEAD_AUTH_TOKEN=$(cat .hydra-head-token)
podman build --no-cache --target core -f infra/Dockerfile -t localhost/hydra-core:latest .
podman compose -f infra/docker-compose.hydra.yml up -d --build core
```

> **GOTCHA — `up -d` without `--build` serves a STALE image.** The `core`
> service has a `build:` block; always pass `--build` (add `--no-cache` if you
> suspect a reused layer). A request that behaves "old" after a redeploy is
> almost always a stale image, not a logic bug — verify per
> `build.md` (`strings -e l` on the live DLL).

## Start everything (idempotent)

```bash
bash scripts/start-env.sh               # quadlets + infra + core + head rtx + head p100
bash scripts/start-env.sh --skip-p100   # RTX-only (VM unavailable)

# Individual stacks:
bash scripts/start-infra.sh             # observability only (Grafana/Prom/Loki)
bash scripts/start-hydra.sh             # core + heads (loads .env profile first)
```

`start-hydra.sh` sources `.env` (written by `scripts/set-profile.sh`) so
podman-compose resolves `${VAR}` substitutions in the compose file.

## One-time P100 VM setup

```bash
bash scripts/setup-p100.sh
```

Installs hydra-head + sidecar binaries in user scope (no sudo needed on the
VM), enables `loginctl enable-linger`, configures the SSH alias. Day-to-day
P100 startup is `bash scripts/deploy-hydra-head.sh p100`.

P100 model load takes ~30–90 s; the head's `/status` returns 503 until llama
is READY (readiness gate).

## Pod lifecycle

```bash
podman pod ls                          # pod_hydra-system, 3 containers
podman pod stop pod_hydra-system       # stop the whole pod
podman pod start pod_hydra-system      # start it back up
podman compose -f infra/docker-compose.hydra.yml down   # full teardown (data preserved)
podman compose -f infra/docker-compose.hydra.yml up -d  # bring back up
```

## Verify

```bash
curl -s http://localhost:9000/health      # core — nodes rtx, rtx3060, p100 + store
curl -s http://localhost:9700/status | jq '.processes'   # head RTX (llama 8080, rpc 9503)
curl -s http://localhost:9701/status | jq '.processes'   # head 3060 (llama 8081, rpc 9504)
curl -s http://192.168.122.21:9700/status | jq '.processes'  # head P100 (llama 8086, rpc 9502)
```

## Gotchas

- **Don't bypass the compose** with bare `podman run` — it encodes
  `network_mode: host`, pod-level `userns_mode: host`, mounts, and the token.
  The trap table (and why each one breaks) is in `docs/hydra-system-pod.md`.
- **ghcr.io auth:** `~/.config/containers/auth.json` must be `chmod 644` and
  synced to `/run/user/1000/containers/auth.json` (tmpfs — deploy reads the
  tmpfs copy; a reboot can lose it). Symptom: `failed to pull binary:
  open /run/host-ctrs-auth.json: permission denied`.
- **Port 8081 collision:** the 3060 llama-server needs host :8081 free. The
  Grafana image renderer moved off it to :28081 (PR #440) — if you change the
  renderer port, update `GF_RENDERING_SERVER_URL` and check
  `workers.json` `rtx3060.llama_url`.
- **`HYDRA_COORD_CHUNK_CACHE_DIR`:** must point at tmpfs
  (`/mnt/llm-ram/chunk-cache` in the compose). Default lands on the overlay
  and fills `/mnt/containers` in hours.
- **Stuck P100 llama-server** (CUDA kernel wedged): `systemctl --user stop`
  hangs; force it:
  ```bash
  ssh hydra-p100 "sudo kill -9 \$(pgrep llama-server)"
  ssh hydra-p100 "systemctl --user reset-failed llama-p100 && systemctl --user start llama-p100"
  ```
- Profile switching (MoE ↔ Dense): `bash scripts/set-profile.sh {moe|dense}`
  then `podman compose -f infra/docker-compose.hydra.yml up -d`.
- Pull a new engine image without restarting the head:
  `curl -X POST http://localhost:9700/update -H "Content-Type: application/json"
  -d '{"name":"llama-server","source":"ghcr.io/..."}'`, then
  `curl -X POST 'http://localhost:9700/restart?name=llama'`.

## Related recipes

- Build steps → `build.md`
- Test lane (isolated 2-core P100 rig) → `test-lane.md`
- Monitoring after deploy → `monitoring.md`
- Full port/env reference → `docs/PORTS_AND_ENV.md`
