# Hydra-TEST bring-up evidence — 2026-08-28

## Live run: Workflow=HydraTest (feat/hydra-test-p100 @ local, rig UP)

| Run | Result | Duration |
|-----|--------|----------|
| 1 | **Passed!** Failed: 0, Passed: 1, Skipped: 0 | 58 s |
| 2 | **Passed!** Failed: 0, Passed: 1, Skipped: 0 | 50 s |
| 3 | **Passed!** Failed: 0, Passed: 1, Skipped: 0 | 51 s |

Test content: 10 chat completions (5 → core-A :19000, 5 → core-B :19001),
per-core sequential / cross-core parallel; asserts 200 OK, completion_tokens>0,
no 5xx; prod-contamination check vs prod :9000 (auto-skips if metric absent).

Sanity single-shot through core-A before suite: `200 OK, 16 tokens, finish=length, 5.48 s`.

## Topology verified LIVE

- Host (podman): hydra-core-test-a :19000/:19500/:19501, hydra-core-test-b :19001/:19502/:19503 —
  `HYDRA_INSTANCE=test`, `ASPNETCORE_ENVIRONMENT=Test` → `appsettings.Test.json` loaded,
  `hydra_test` Postgres DB (N1 isolation), models-test.json (qwen3.5-9b-test).
- VM 192.168.122.21 (bare, minifleet pattern): engine-A :18086 (ngl 16), engine-B :18087 (ngl 8),
  hydra RPC :19513/:19514, shared single P100 via mmap.
- Cores poll engines healthy: `health_poll_ok Node=test-a Slots=1` / `Node=test-b Slots=1`.
- PREFILL M2 streamed 101.7 MiB per request (zero `M2 stream failed` after engine fix).
- KV chunks landed in L1 (tmpfs, 208 files) — L2/PG write is GC-lazy (count 0 at rest is expected).

## Engine used

Fork-tip `67ceb00bd` (v9720) built on host with the M2 hash-pre-pass fix
(`3206b13b6` backport via branch `fix/470-m2-hash-sm60-backport`), linked against
the VM's existing sm_60 (ARCHS=600) ggml libs — the CUDA 13.x toolchains on the
host cannot emit sm_60; the 12.9 toolkit referenced by the original build tree is
missing from disk (owner was restoring it; reported).

## VM hygiene

| Snapshot | Compute apps |
|----------|-------------|
| pre (before any test lane) | 1620 :8090, 2899 :8086, 40372/40373 minifleet leftovers (freed via exact-signature kill) |
| post suite (test lane up) | 2899 :8086, 55089+55090 (test lane), 1620 :8090 |
| final (after teardown) | 2899 :8086 (prod), 56714 :8090 (upstream — restarted) |

## Incident log (self-reported)

1. `pkill -f llama-server` (bare, during wrong-arch engine relaunch) killed the
   owner's resident upstream `ik-llama-minicpm.service` (pid 1620, :8090).
   Restored via `systemctl --user start ik-llama-minicpm.service` — health `{"status":"ok"}`.
   Hygiene pattern for future lanes: NEVER bare-pkill by binary name; always
   require the lane marker AND the model in the pattern.
2. Prod :8086 resident (2899) and prod host core :9000 were never touched and
   stayed 200 OK throughout.

## Findings (for issues, not blockers here)

1. **Coordinator cold_atomic self-lease deadlock** (repro'd live): with ≥2
   concurrent same-worker cold_atomic requests, `CanServeRequest(Decode)`
   requires a FREE slot while the item's own up-front `DecodeLease` occupies
   one → items deadlock until client timeout (499), `stream_done_no_lease` ×N.
   Per-worker safe concurrency = slots−1. Needs owner/architect decision
   (#470-critical area — no core change made in this branch).
2. **Engine M2 hash pre-pass bug on sm_60 builds** — any sm_60 engine built from
   `a7b40fdce` (or anything after `234083a45` but before `3206b13b6`) fails every
   PREFILL M2 with `hash pre-pass hashed 0 B`. Prod P100 is decode-only so it is
   dormant there; Mixed sm_60 workers will hit it. Fix = rebuild engine from
   fork tip with a CUDA 12.x toolkit (host 12.9 toolkit currently missing).

## Addendum (2026-08-28 late): real sm_60 engine from 12.9 minimal toolkit

The 12.9 minimal toolkit was located at `~/opt/cuda-12.9-min` on the host
(nvcc 12.9.86, micromamba build with the math_functions header patch,
per HEADER-PATCH-NOTICE.md). Full clean rebuild of fork tip `67ceb00bd`:

    cmake -G Ninja -DCMAKE_CUDA_ARCHITECTURES=60 -DGGML_CUDA=ON -DGGML_RPC=ON \
      -DCMAKE_CUDA_HOST_COMPILER=/usr/bin/g++-14 (gcc >14 guard in 12.9 host_config.h) \
      -DCUDAToolkit_ROOT=~/opt/cuda-12.9-min

- CUB/Thrust symbols bake `SM_600` → true sm_60 SASS; `ARCHS = 600` at engine start.
- Deployed self-contained to `~/hydra-fork-fix-sm60/` on the VM (no lib mixing).
- Test lane relaunched on it (run-v4.sh); **Workflow=HydraTest re-run: PASSED (54 s)**;
  PREFILL M2 + STATE_GET M2 both stream 101 MiB, zero failures.

Toolkit location is authoritative for all future sm_60 builds.
