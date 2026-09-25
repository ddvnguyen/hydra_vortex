# task-16ec332378 — Atlas map gap: engine-side GET /experts.json — RESULTS

Date: 2026-09-26 (recovery turn after worker kill 00:02 Sep 26). Track t-d1cf43b723.
Model: Qwen3.8-Flash-Next-APEX-I-Mini (6-shard, `/mnt/SSD/qwen3.8-flash-next-apex-mini/`), CPU only (`-ngl 0`), port 8123.
Binary: `build-atlas-route/bin/llama-server` (GGML_CUDA=OFF, Release) from the fork worktree
`/mnt/WorkDisk/workspace/worktree/1q3ry0vb/fork-flash-next-colibri` (branch `feat/flash-next-colibri`, tip `0f766227d`).
Rig was busy (atlas-790 sidecar on :18442, GPUs occupied) — smoke ran CPU-only; no other workload touched.

## Engine SHA reconciliation (why 0f766227d)

- `b803e1cf9` was committed on detached HEAD in the main checkout's submodule at 23:31 Sep 25
  and accidentally includes the unrelated task-ae9ee14bee `tests/CMakeLists.txt` block
  (11 lines, `test-moe-snapshot-coverage`).
- The previous worker cherry-picked the series onto the fork branch and **amended**, producing
  `0f766227d` at 23:35 Sep 25 (reflog: `be9294f8f` cherry-pick → amend). Tree delta
  `b803e1cf9..0f766227d` = exactly that unrelated 11-line block. Otherwise identical trees.
- Therefore **`0f766227d` is the correct engine SHA**; `b803e1cf9` is orphaned (reachable only
  via reflog, no branch/remote). The unrelated block stays uncommitted in the worktrees for
  task-ae9ee14bee's own commit. `0f766227d` is local-only (no remote contains it);
  push/PR remains an owner decision (same posture as the prior `3111fb786` bump).
- All artifacts above were built from `0f766227d` (binaries present since 23:36).

## Live smoke matrix (curl, engine on 127.0.0.1:8123)

Artifact dir switched between cases via `HYDRA_EXPERT_ATLAS` → symlink `live`:

| Case | Dir | GET | Status | Body |
|---|---|---|---|---|
| A absent | `empty/` | `/experts.json`, `/v1/experts.json` | **404** | `{error:{message:"File Not Found",type:not_found_error,code:404}}` |
| B1 FNV id | `patched/` | `/experts.json` | **200** | real artifact: `provenance.engine_id = 90d76305512fb6f7`, schema_version 2, 2431 expert entries |
| B2 short id | `patched/` | `/expert-ranks.json` | **200** | real artifact: `provenance.engine_id = qwen4exp:Qwen3.8-Flash-Next-APEX-I-Mini-00001-of-00006.gguf`, 48 layers |
| B3 | `patched/` | `/v1/expert-ranks.json` | **200** | byte-identical to B2 |
| C refused | shipped `tools/atlas/out` (`engine_id:"qwen38"`) | both | **404** | refusal — foreign artifact bytes never served |
| D corrupt | `corrupt/` | `/experts.json` | **500** | `{error:"atlas artifact is not valid JSON: /tmp/atlas-route-smoke/live/experts.json"}` |

Expected engine id (independently recomputed): FNV-1a64 over
`qwen4exp:Qwen3.8-Flash-Next-APEX-I-Mini-00001-of-00006.gguf:78707708480`
(sum of all 6 shard sizes) = **`90d76305512fb6f7`** — served the 200 in B1.

Teardown: server killed, port 8123 released, `pgrep` shows only the pre-existing foreign
atlas-790 sidecar (port 18442). Before/after captures: `/tmp/atlas-route-pgrep-{before,after}.txt`.

## Unit test

`build-atlas-route/bin/test-hydra-atlas-file` re-run from `0f766227d` build: exit 0, **all 21
ATLAS_CHECKs ok** (`unit-test-run.log`). Note: the earlier "22/22" note in the handoff was a
miscount; the test defines 21 checks.

## Caveats / findings for the owner

1. **Shipped artifact id mismatch (follow-up):** `tools/atlas/emit.py` writes
   `ENGINE_ID = "qwen38"`; the engine accepts only the exact FNV hex id or the
   `$arch:$basename[:$size]` short form. The shipped `tools/atlas/out/*.json` therefore gets
   **refused 404** until `emit.py` emits one of the accepted forms (recommend the short form
   `qwen4exp:<basename>` or the FNV id) and the artifacts are regenerated.
2. **Fork-wide 404 body shape (pre-existing, not this task's defect):**
   `tools/server/server-http.cpp:145` error handler unconditionally replaces every 404 body
   with the generic OpenAI-style `{"message":"File Not Found"}` shape. The descriptive atlas
   error bodies exist at the `atlas_file()` layer (unit-tested) but the wire carries the
   generic shape. Statuses are unaffected. Suggest a separate follow-up to pass through
   handler-provided 404 bodies.

## Artifacts

- `smoke-matrix.txt` — the curl matrix transcript (status codes).
- `A1-experts-404.body.json`, `B1-experts-200.body.json`, `B2-ranks-200.body.json`,
  `C1-experts-refused.body.json`, `D1-experts-corrupt.body.json` — captured response bodies.
- `unit-test-run.log` — test-hydra-atlas-file 21/21 ok.
- `smoke-server.log` — llama-server startup/teardown log.
- Parent commits: submodule bump `b456bf4f7` (gitlink `1f3f099cb` → `0f766227d`), PROJECT_STATUS note — both in `baseline-flash-next` (local only).
- Fork commit: `0f766227d` on `feat/flash-next-colibri` (local only; no remote contains it).
