# Issue-708 / Hydra-TEST scaffold — lead zero-trust verify result 2026-08-28 ~16:00 ICT

## Verdict: BLOCKED (5 critical, 4 medium, 2 low)

The scaffold cannot be approved as-is. Worker `81b26743` did a lot of structural
work correctly, but the bring-up path is not viable on the actual P100 rig
without fixes. Per LEAD_CHARTER §2.1 zero-trust, do NOT push. The good news:
most issues are test-rig-specific and isolated; no production code touched.

### Critical (block rig bring-up)

1. **Engine image is `ubuntu:22.04` with no CUDA stack** — bind-mount of
   `~/hydra-min-test` only carries the engine binary + a few shim libs, not
   the CUDA runtime / libcuda / libnccl / sm_60 files. The minifleet worker
   documented the same trap ("LD_LIBRARY_PATH must point at the engine build
   prefix dir"). The engine will not start.
   **Fix:** use a CUDA-enabled base image (`nvidia/cuda:12.9.0-runtime-ubuntu22.04`)
   and bind-mount JUST the binary; let the container provide CUDA.

2. **No nvidia-container-runtime / device passthrough** — engines have
   `image: ubuntu:22.04` and no `runtime: nvidia` / no `--device nvidia0`.
   Even with CUDA libs in the container, the GPU is invisible.
   **Fix:** add `runtime: nvidia` (or use `--device nvidia0 --device nvidiactl
   --device nvidia-uvm` if nvidia-container-toolkit isn't installed
   system-wide; on the P100 host it's a KVM VM, may need different approach).

3. **P/D split uses two GPUs on a single-GPU VM** — `CUDA_VISIBLE_DEVICES=0`
   for engine-A, `=1` for engine-B. P100 VM has ONE GPU. Engine-B's CUDA
   context will fail.
   **Fix:** both engines use `CUDA_VISIBLE_DEVICES=0`; mmap page-cache sharing
   means both can read the same GGUF and split layers (16+8 = 24 layers).
   Document explicitly that this is single-GPU shared-VRAM, not true P/D split.

4. **Engine model is 35B-A3B MOE, not the pinned 9B** — owner ruled "v1
   simple task due to 9B Q4"; loading 35B at ngl 16+8 likely exceeds 16 GB
   P100 VRAM.
   **Fix:** switch to `Qwen3.5-9B-Q4_K_M.gguf` (sha `03b7472…7e8`) per the
   minifleet spec — same model the just-verified rig uses, fits easily in
   16 GB at ngl 16+8 = 24 layers.

5. **`appsettings.Test.json` is never actually loaded** — .NET's default
   config builder reads `appsettings.{ASPNETCORE_ENVIRONMENT}.json` ONLY when
   the env is set. The worker added the file but never sets
   `ASPNETCORE_ENVIRONMENT=test` in the compose env. The
   `HydraTestConfig.ValidateIfTestInstance()` gate works (it checks env), but
   the FILE that contains `ConnectionStrings.Postgres=hydra_test` is dead code.
   **Fix:** add `ASPNETCORE_ENVIRONMENT=Test` to the core containers' env list.
   The existing config builder will then automatically merge
   `appsettings.Test.json` on top of `appsettings.json`.

### Medium (will fail at bring-up or break in subtle ways)

6. **`workers-test-a.json` / `workers-test-b.json` referenced but not created**
   — core containers will crash at startup looking for
   `/etc/hydra/config/workers-test-*.json`.
   **Fix:** add the files (per-instance worker list — copy from prod
   `workers.json` and trim to 1 engine each) or remove
   `HYDRA_COORD_CONFIG_FILE` env if the default is fine.

7. **`node-test-a.yaml` / `node-test-b.yaml` referenced but not created** —
   head containers will crash.
   **Fix:** add the per-instance head configs (copy from prod
   `node-p100-mini.yaml` and rewrite port+model paths).

8. **`image: localhost/hydra-core:latest` and `image: localhost/hydra-head:rtx`
   not verified** — worker reported `dotnet build Hydra.Core` worked but did
   not actually `podman build` the images.
   **Fix:** lead to verify or document the build command in `up.sh`.

9. **`init-hydra-test.sql` `DO $$ ... EXCEPTION WHEN duplicate_database` will
   fail on clean Postgres** — `CREATE DATABASE` can't run inside a
   transaction; `DO` blocks are transactional. The up.sh's first attempt
   silently fails; the cores then connect to a non-existent `hydra_test` DB.
   **Fix:** drop the `DO` block; just use the `\gexec` path in up.sh (which
   the worker already wrote). The SQL file is a no-op artifact; up.sh is
   the source of truth. Or: replace the SQL file with a single
   `SELECT 'CREATE DATABASE ...' \gexec` line and document that up.sh applies it.

### Low (cosmetic / future-proofing)

10. **`api_key: not-required` is a string, not a "no auth" flag** — works but
    misleading. Use empty string `""` or document the actual Paseo field name
    for "no auth".

11. **Paseo provider yaml uses `localhost:19000`** — fine for host-local
    workflows, but the P100 VM is a KVM VM at `192.168.122.21`, so a Paseo
    agent running on the host will hit `localhost:19000` (host's loopback,
    not the VM's).
    **Fix:** provider should be `http://192.168.122.21:19000` for host-side
    Paseo daemons. Or document the assumption that the Paseo daemon runs
    INSIDE the P100 VM.

## Action: respawn a focused fix worker with the above 11-item list

- Reuse the existing worktree `/mnt/WorkDisk/Workplace/wt-hydra-test` (branch
  `feat/hydra-test-p100`).
- Same Paseo model that worked for minifleet r4
  (`muse-spark-1.2-contributor-free`, t3).
- Tighter brief: 11 specific issues to fix, one commit per logical unit,
  `draft:` prefix.
- Do NOT touch anything outside the 11 issues; do NOT redesign.
- After fix: lead re-verifies.
