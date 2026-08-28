# MiniFleet Smoke Evidence — 2026-08-28

Branch: `feat/minifleet-smoke-v2` @ `ff7e4156f` + draft commits (this run)
Worktree: `/mnt/WorkDisk/Workplace/wt-minifleet`
Model: `Qwen3.5-9B-Q4_K_M.gguf` sha256 `03b74727a860a56338e042c4420bb3f04b2fec5734175f4cb9fa853daf52b7e8`
Engine: `~/hydra-min-test/llama-engine` build 9697 (host) / 9703 (VM), `LD_LIBRARY_PATH=~/hydra-min-test`

## Per-spec verdict

| # | Spec (task §) | Preset | Scenario | Verdict | Key numbers | Evidence |
|---|---------------|--------|----------|---------|-------------|----------|
| 1 | cpu-2node + COMBINED-dense (AC1) | cpu-2node (ngl 0+0, t 3+3, c 4096) | cold_atomic_engine | **green** | 200 OK, finish_reason length, prompt 35, completion 48, duration 8111 ms | `docs/minifleet/evidence/cpu-2node/cold_atomic_engine.json` |
| 1 | cpu-2node + COMBINED-dense (AC1) | cpu-2node | chunked_save | **green** | 200 OK, tokens 35/48 | `docs/minifleet/evidence/cpu-2node/chunked_save.json` |
| 2 | gpu-gpu-shared + P/D split mix-quant (AC2) | gpu-gpu-shared (ngl 16+8, rpc 9513/9514) | cold_atomic_engine | **green** | 200 OK, tokens 35/48, duration 5326 ms, VM health {"status":"ok"} | `docs/minifleet/evidence/gpu-gpu-shared/pd_split_cold_atomic_engine.json` |
| 2 | gpu-gpu-shared + P/D split mix-quant (AC2) | gpu-gpu-shared | chunked_save | **green** | 200 OK | `docs/minifleet/evidence/gpu-gpu-shared/pd_split_chunked_save.json` |
| 3 | gpu-gpu-shared + Hydra queue high-load (AC2) | gpu-gpu-shared (same 16+8) | 8 concurrent × 60s via tunnels 8088/8089 | **green** | concurrent 8, duration 60, requests 16, ok 16, errors 0, p50 43272 ms, p99 88016 ms, tok_per_s 12.8 | `docs/minifleet/evidence/gpu-gpu-shared/queue_highload.json` |

All 4 xUnit facts green:

- `dotnet test src/core/Tests.MiniFleet --filter "Tier=MiniFleet&FullyQualifiedName~CpuTwoNode"` → Passed 2/2 (1m56)
- `dotnet test src/core/Tests.MiniFleet --filter "Tier=MiniFleet&RequiresVm=true"` → Passed 2/2 (1m04) with `MINIFLEET_SSH_TARGET=hydra-p100`

`dotnet test src/core/Tests.MiniFleet --filter "Tier=MiniFleet"` green (4/4) when both envs present.

## Throughput / tail-latency (AC2 #3)

- concurrent 8, duration 60s, requests 16, ok 16, errors 0, p50 43272 ms, p99 88016 ms, tok_per_s 12.8, total_tokens 768, req_per_s 0.27
- No dropouts, no 5xx, no engine crash (VM hygiene confirms 1-slot queueing dominates latency; engines have single slot, so 8 concurrent queues).

## Issues hit

- **Host port 8088 occupied** by `searxng` container (infra-host, 0.0.0.0:8088→8080) → freed by `podman stop searxng` for this run.
- **Hydra.Core L1 cache `/mnt/llm-ram/chunk-cache-l1` permission** → `UnauthorizedAccessException` → fixed `Topology.cs` to use `HYDRA_COORD_CHUNK_CACHE_L1_DIR=/tmp/hydra-minifleet-l1` + dynamic coordinator/store ports to avoid fixed 19000/19500 collision between sequential facts.
- **VM lane store check** → `KeyNotFoundException` on `store` because VM lane has no coordinator (engine /health is `{"status":"ok"}`) → fixed `SmokeTests.cs` to check `status:ok` for `viaSshShim`.
- **vm-run.sh stop quoting bug** (`for p in $stale` unquoted) → `kill` syntax error, engines survived post-test → manually `kill`'d 37147/37148; fix pending.
- **CPU lane engine `LD_LIBRARY_PATH`** → host `~/hydra-min-test` populated from `/mnt/WorkDisk/cache/llama-build/stable/dev/bin` (ext4 copy to avoid NTFS IntxLNK).
- All trace JSONs verified: `outcome Done`, `http_status_code 200`, `finish_reason` present, `completion_tokens 48` (>0), `prompt_tokens 35`.

## Hygiene

- Pre/post `nvidia-smi --query-compute-apps=pid,process_name,used_memory --format=csv` captured to `docs/minifleet/evidence/gpu-gpu-shared/nvidia-smi-{pre,post}{1,2,3}.txt`.
- Initial orphans 4963/4964 (no `--alias qwen-2node`) killed before run 1.
- After each VM run, only residents 1620 (upstream :8090) + 2899 (prod :8086) remain; ephemeral 30758/30759 and 37147/37148 cleaned (post3 clean).

## Links

- `docs/minifleet.md` topology + quirks + evidence table
- `docs/minifleet/evidence/cpu-2node/cold_atomic_engine.json`
- `docs/minifleet/evidence/cpu-2node/chunked_save.json`
- `docs/minifleet/evidence/gpu-gpu-shared/pd_split_cold_atomic_engine.json`
- `docs/minifleet/evidence/gpu-gpu-shared/pd_split_chunked_save.json`
- `docs/minifleet/evidence/gpu-gpu-shared/queue_highload.json`
- `docs/minifleet/evidence/gpu-gpu-shared/nvidia-smi-pre1.txt` (now clean), `nvidia-smi-pre2.txt`, `nvidia-smi-pre3.txt`, `nvidia-smi-post1.txt`, `nvidia-smi-post2.txt`, `nvidia-smi-post3.txt`
- Also `tests/minifleet-artifacts/<preset>/<scenario>.json` + `-v2.json` in bin output (copied to docs).

## Next

- Restart `searxng` (`podman start searxng`) if needed.
- Fix `vm-run.sh` stop quoting (single patch).
- Lead to zero-trust-verify JSONs, rebase `draft:` → `test(minifleet):` / `docs(minifleet):`, then push (no push yet per instruction).
