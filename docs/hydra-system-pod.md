# Hydra System — start / stop / debug

The "hydra system" on the host is **three services in one pod**:
`hydra-system_core_1` (the C# coordinator + Store),
`hydra-system_head-rtx_1` (the Go node agent for the **RTX 5060 Ti**,
manages the llama-engine subprocess on CUDA0 + 3 monitoring sidecars),
and `hydra-system_head-rtx3060_1` (the Go node agent for the **RTX 3060**,
manages the llama-engine subprocess on CUDA1 + its own ggml-RPC backend on
:9504 for COMBINED-mode peer dispatch). They all share a network namespace
(`network_mode: host`) and a userns (`userns_mode: host`), so they can talk
over `127.0.0.1` and read each other's host files (ctr.log, auth.json, etc.).

The P100 is a separate VM and is NOT in this compose — it runs
hydra-head under systemd. See `scripts/deploy-hydra-head.sh p100`.

## TL;DR

```bash
# Build (only if you changed src/core or src/head or the Dockerfile)
bash scripts/deploy-hydra-head.sh rtx
bash scripts/deploy-hydra-head.sh rtx3060   # second head in the same pod; same image

# Or rebuild the fat sm_86+sm_120 llama-engine if the fork changed
(cd src/llama-cpp && mkdir -p build_sm86_sm120 && cd build_sm86_sm120 && \
  cmake .. -G Ninja -DCMAKE_CUDA_ARCHITECTURES="86;120" \
    -DGGML_CUDA=ON -DGGML_RPC=ON -DGGML_NVML=ON \
    -DBUILD_SHARED_LIBS=ON -DCUDAToolkit_ROOT=/opt/software/cuda/13.2 && \
  cmake --build . -j$(nproc) --target llama-engine)

# Verify
podman pod ls                              # should show pod_hydra-system with 4 containers
curl -s http://localhost:9000/health      # core (should show 3 nodes: rtx, rtx3060, p100)
curl -s http://localhost:9700/health      # head (RTX 5060 Ti, ports 8080/9503)
curl -s http://localhost:9701/health      # head (RTX 3060, ports 8081/9504)
ss -tlnp | grep -E '9504|9505'             # ggml-RPC backends
curl -s http://localhost:13133/ | head -1   # OTel Collector health
```

## The pod

```
pod_hydra-system
  ├── pod_hydra-system-infra     (pause, host network, userns=host)
  ├── hydra-system_core_1         (C# coordinator, port 9000, /health on :9501)
  ├── hydra-system_head-rtx_1     (Go agent, port 9700, llama :8080, sidecars :9100/:9835)
  └── hydra-system_head-rtx3060_1 (Go agent, port 9701, llama :8081, sidecars, ggml-RPC :9504)
```

**Why one pod, not two separate containers:**
- core + heads need to talk over `127.0.0.1` (core's `Store` API
  hydra-head calls, hydra-head's health checker probes `core:9000`,
  and the rtx head reaches rtx3060's ggml-RPC backend on `localhost:9504`
  for COMBINED engine mode).
- All three need to share the host network namespace (so they see the
  host podman socket, ctr.log, and host postgres on `127.0.0.1:5432`).
- All three need `userns_mode: host` so the in-container Hydra.Core can
  read `/mnt/containers/*/ctr.log` (mode 700) and
  `/mnt/SSD/hydra-backup` (mode 770) — both owned by host uid 1000
  (ddv).
- Podman rejects `--userns` combined with `--pod` on a service;
  the `userns_mode: "host"` must be at the **pod** level
  (`infra/docker-compose.hydra.yml` top-level), not on the
  individual services. The deploy script + compose file are
  already set up this way.

## The canonical start sequence

```bash
# 1. The host-side exporter Quadlets (infra-node-exporter /
#    infra-nvidia-exporter / infra-promtail) are GONE — they were
#    removed in commit TBD. The in-container hydra-head now owns
#    ports :9100/:9835/:9080. If you see "bind: address already in
#    use" on those ports, check for stragglers:
ss -tlnp | grep -E ':(9080|9100|9835) '

# 2. (one-time per host) the host auth file must be world-readable
#    for the in-container uid 1000 (= ddv) to read it.
chmod 644 ~/.config/containers/auth.json
chmod 644 /run/user/1000/containers/auth.json

# 3. Build + deploy via compose (the script handles all of this).
bash scripts/deploy-hydra-head.sh rtx
```

The script does (in order):
1. `go build` → `bin/hydra-head` (the Go agent)
2. `podman build` → `localhost/hydra-head:rtx` (image with the Go
   binary baked in)
3. ~~Stops the 3 host sidecar Quadlets~~ (no-op since they're gone)
4. `chmod 644` the auth files (defensive, no-op if already 644)
5. `podman compose -f infra/docker-compose.hydra.yml up -d`
6. Waits for both healthchecks
7. Verifies the OTel Collector is healthy (`curl -so/dev/null
   -w'%{http_code}\n' http://localhost:13133/` returns 200) and that
   the hydra streams are non-empty in Grafana Explore
   (`{component="hydra"}`, `{component="llama-server", node="rtx"}`)

## Why a "manual podman run" doesn't work

If you try to start hydra-core or hydra-head-rtx with a bare
`podman run` (no compose), you'll hit one or more of these traps:

| Trap | Symptom | Why |
|---|---|---|
| `userns_mode: "host"` on the service | `Error: --userns and --pod cannot be set together` | Pod-level userns; service-level conflicts with pod creation |
| Forget `--network host` | `Failed to bootstrap PG schema: Connection refused (127.0.0.1:5432)` | Postgres is on the host's loopback; without `--network host`, the container sees its own loopback (empty) |
| ~~Forget the host sidecar stop~~ (gone) | ~~`bind: address already in use` on :9100/:9835/:9080~~ | ~~host infra-promtail / nvidia-exporter / node-exporter are still bound to these ports~~ — **removed**: these Quadlets were deleted, the in-container children own the ports now |
| Forget `chmod 644` on the auth file | `failed to pull binary: open /run/host-ctrs-auth.json: permission denied` (in container log) | in-container uid 1000 can't read a 600 file owned by host uid 1000 unless userns is `host` AND the file is world-readable |
| Forget `HYDRA_COORD_CHUNK_CACHE_DIR` | `/mnt/containers` fills to 100% in a few hours; Loki / podman start failing | The default `HYDRA_COORD_CHUNK_CACHE_DIR` is `/tmp/hydra-coord-chunk-cache` (overlay, unbounded). Without overriding, 50+ GB of KV state accumulates in the overlay until the partition fills. |

The compose file encodes all of these. **Don't bypass it.**

## Common debug commands

```bash
# Are both containers in the pod?
podman pod ls
# Should show: pod_hydra-system ... 3 containers

# Container statuses
podman ps --filter label=com.docker.compose.project=hydra-system

# Core health
curl -s http://localhost:9000/health | jq
# {"status": "healthy", "nodes": {"rtx": {...}, "p100": {...}}, "store": {"healthy": true}}

# Head + all 3 sub-processes (llama, node_exporter, nvidia_exporter).
# (promtail removed in #363 — per-child labeled writers push directly
# to the OTel Collector.)
curl -s http://localhost:9700/status | jq '.processes'

# Promtail actually shipping (not 0)?
curl -s http://localhost:13133/ | head -1   # OTel Collector health
# Should show a non-zero number after a few minutes

# Are the 4 sub-processes running, not crash-looping?
curl -s http://localhost:9700/status | python3 -c "
import sys, json
d = json.load(sys.stdin)
for k, v in d['processes'].items():
    print(f'  {k}: state={v[\"state\"]:8s} restart={v[\"restart_count\"]}')
"

# Last 20 hydra-core log lines
podman logs --tail 20 hydra-system_core_1

# Last 20 hydra-head-rtx log lines
podman logs --tail 20 hydra-system_head-rtx_1
```

## Stopping / restarting

```bash
# Stop the whole pod (both containers)
podman pod stop pod_hydra-system

# Start it back up
podman pod start pod_hydra-system

# Tear down completely (data is on tmpfs /mnt/llm-ram + SSD
# /mnt/SSD/hydra-backup + Postgres; all preserved)
podman compose -f infra/docker-compose.hydra.yml down

# Bring back up
podman compose -f infra/docker-compose.hydra.yml up -d
```

## Troubleshooting playbook

### "Container is Up but Unhealthy" (port 9700 not listening)

```bash
podman logs --tail 20 hydra-system_head-rtx_1 | grep -E "Error|HTTP server"
# Look for: "Error starting HTTP server" → port already in use.
# If yes: systemctl --user stop infra-{node-exporter,nvidia-exporter,promtail}.
```

### "Write-behind: failed to copy {Hash}"

```bash
# Check chunk cache size
podman exec hydra-system_core_1 du -sh /mnt/llm-ram/chunk-cache
# If > 25 GB: tmpfs is full; chunks are getting evicted before
# write-behind can copy them. Either bump the tmpfs size in
# infra/docker-compose.hydra.yml (`size=50G`) or shorten the
# write-behind cycle.
```

### "Loki shows zero hydra logs / promtail_sent_bytes_total = 0"

```bash
# 1. In-container userns:host working?
podman exec hydra-system_head-rtx_1 cat /proc/self/uid_map
# Should show: "0 1000 1" (root in container = uid 1000 on host)

# 2. Promtail can read the host ctr.log files?
podman exec hydra-system_head-rtx_1 ls -la /mnt/containers/overlay-containers/ 2>&1 | head -3
# Should NOT show "Permission denied"

# 3. Is the auth file world-readable?
ls -la ~/.config/containers/auth.json
# Should show: -rw-r--r-- (644, not 600)
```

### "Cannot connect to postgres on 127.0.0.1:5432"

```bash
# Postgres is in pod infra-host. Check it's up:
podman ps --filter name=postgres

# If hydra-core can't reach it, it's almost certainly missing
# --network host. The compose file has network_mode: host — if
# you bypassed compose, the container has its own network namespace
# and 127.0.0.1 means the container itself (no postgres).
```

### "/mnt/containers 100% full"

```bash
# 1. Which overlay is biggest?
du -sh /mnt/containers/overlay/* | sort -hr | head -5

# 2. If it's the hydra-core overlay and /tmp inside is huge,
#    the chunk cache is in the overlay instead of tmpfs. Check:
podman exec hydra-system_core_1 ls /tmp/hydra-coord-chunk-cache 2>&1
# If it exists, you forgot HYDRA_COORD_CHUNK_CACHE_DIR=/mnt/llm-ram/chunk-cache
# in the compose. See infra/docker-compose.hydra.yml:40.

# 3. Manual cleanup (only as a last resort; the next restart will
#    fill up again if the underlying cause isn't fixed):
podman system prune -f    # removes dangling images/containers
rm -rf /mnt/containers/overlay/<id>/diff/tmp/hydra-coord-chunk-cache
```

## When to NOT bypass the compose

- Adding/changing a mount path, env var, or healthcheck — **edit
  the compose file**, not a `podman run` flag.
- Debugging a one-off issue — fine to `podman exec` into the
  container, but **don't `podman run` a parallel container** with
  the same port — it will conflict with the one in the pod.
- Restarting the agent quickly to pick up a new Go binary — the
  compose `up` will see the image hash changed and recreate the
  `head-rtx_1` container. Don't `podman stop hydra-system_head-rtx_1`
  + `podman run …` manually; you'll re-introduce the
  `userns_mode` / `network host` / mount / user traps.
