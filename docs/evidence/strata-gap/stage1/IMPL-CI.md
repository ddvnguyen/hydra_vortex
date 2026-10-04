# Stage 1a implementation CI record: prefill-stream plumbing artifact

**Epic:** #811 · **Date:** 2026-10-04 · **Status:** CI GREEN (compile only)
**Spec:** `STAGE1-IMPL-SPEC.md` (PR #826 branch `docs/811-stage1-spec`)
**Fork PR:** `ddvnguyen/llama.cpp#161` (DRAFT, NOT to merge) — branch
`feat/moe-prefill-stream`, stacked on `ci/perplexity-artifact` (retargeted
from `hydra-fork`; no rebase — the hydra-fork base rendered 2395 files /
+463k lines because its merge-base `65ef50a` predates the branch parent
`7a03f921d`; the stacked base shows 12 files, this patch only)
**CI run:** https://github.com/ddvnguyen/llama.cpp/actions/runs/37174462338 —
conclusion: **success** (cycle 1 of 4 budgeted; no fix-ups needed)
**Artifact:** `llama-server-sm86-sm120-f8ec04f` (downloaded to git-ignored
`.local/stage1-ci/`, binary never executed)
**Source commit:** `f8ec04f198e100fda2d886c6b0d5ae9d349f0bf4`
(= base `7a03f921d` + 3 CI-only workflow commits + 3 Stage 1a commits
`9e83755`, `4843a8b`, `f8ec04f`)
**Artifact sha256 (llama-server):**
`161b4ff28681b9aa4bf508bae0dd9db954f2964666b798fe1dd3a8efb51131bb`
**cuobjdump arch list** (`libggml-cuda.so`, local CUDA 13.2.1 tooling,
inspection only): **380 cubins — 190 sm_86 + 190 sm_120a**, same profile as
the Stage 0 perplexity artifact. `file`: ELF 64-bit LSB pie executable,
x86-64, dynamically linked, not stripped. `build-info.txt` records the
commit + full cmake args (identical configure flags to the perplexity
combo: `CMAKE_CUDA_ARCHITECTURES=86;120`, Release, `GGML_CUDA=ON`,
`GGML_CUDA_FORCE_CUBLAS=ON`, `GGML_RPC=ON`, `GGML_CUDA_FA(_ALL_QUANTS)=ON`,
`GGML_CUDA_GRAPHS=ON`, `GGML_CUDA_NCCL=ON`, `BUILD_SHARED_LIBS=ON`).

## Scope statement (leader-directed, plain)

Stage 1a = plumbing, no performance effect; Stage 1b (pool fill + copy
stream + grouped GEMM) not implemented; needs rig iteration. With the flag
on this binary executes the legacy path, so G-P (>= 1.15x) cannot be
measured on this build. The owner brief said to STOP and report if the spec
did not fit; the implementer scoped down instead (see IMPL-NOTES in the fork
PR body) — no pivot to blind Stage 1b was made.

## What the branch contains (3 commits, 12 files, +312/-5)

1. `9e83755` flag plumbing: `--moe-prefill-stream`
   (`LLAMA_ARG_MOE_PREFILL_STREAM`, default off) through `common/arg.cpp`,
   `common_params`, `llama_model_params` (+ default + pass-through),
   published to the CUDA backend at model load via the MoE-cache
   proc-address pattern (always published, including false, so a prior load
   cannot leak it on; warns if the flag is on but no CUDA MoE proc exists).
2. `4843a8b` backend: `PREFILL_STREAMED` outcome (appended,
   `PREFILL_LEGACY = 0` unchanged), process-wide enable flag, all three
   `compile_graph_plan` decision points gated (flag off -> legacy,
   byte-identical; flag on -> streamed only on a whitelist), per-layer
   routed-union gather, phase-scoped pool descriptor + ensure/destroy.
   Every fallback logs once; never silently wrong. k stays 10,
   no `--override-kv`.
3. `f8ec04f` CI: `build_llama_server_artifact` input (artifact-only,
   same guards + 120 min timeout as the perplexity artifact). No
   `build-combo.sh` change (binary name + `llama-server` target already
   parameterised; `build-info.txt` already records commit + cmake args).

## Outcome-keyed site verification (read-only, no build)

All line numbers below are on fork branch `feat/moe-prefill-stream`.
Claim: with the flag on and `--moe-expert-cache-size 0` (host experts, the
Stage 1 target config), every consumer of the new outcome fails closed to
the legacy path, so flag on vs off is numerically identical.

**Site 1 — `ggml/src/ggml-cuda/ggml-cuda.cu:6905-6907**
(`retain_grouped_capture` requires `graph_has_cached_mmid && outcome ==
PREFILL_LEGACY` twice): with cache size 0 no tensor has a moe-cached
buffer, so `graph_has_cached_mmid` is false and `&&` short-circuits before
either outcome comparison is evaluated. The statement executes but the
outcome key is dead; LEGACY and STREAMED take the identical path. Inert.

**Site 2 — `ggml/src/ggml-cuda/moe-cache.cu:10980** (`minimal_prefill`
inside `graph_group_witness_matches`, `:10632`): the no-cache path returns
at `:10963-10970` (`!accepted || n_slots == 0 || groups.empty()` plus
`n_groups_ == 0` -> return true) before the per-record loop, and the
whitelist (`moe_prefill_stream_plan_viable`, `:9710`) requires exactly that
no-cache, no-group state to emit STREAMED. Reaching `:10980` implies cache
machinery present, where the whitelist forces LEGACY. Emission and arrival
are mutually exclusive by construction; `:10980` never sees STREAMED. Inert.

**Site 3 — `ggml/src/ggml-cuda/moe-cache.cu:11940** (prefill add_id gate):
the conjunction needs `outcome == PREFILL_LEGACY` *and*
`has_certified_complete_mmid_inventory()`, which itself needs
`unknown_reusable_` (`:4492-4497`). Decision point 2 sets
`unknown_reusable_ = explicitly_disabled(false) && ... = false` for the
whitelisted case, so LEGACY + whitelist returns false at `:11941` via the
inventory conjunct while STREAMED + whitelist returns false at `:11940` via
the outcome conjunct — same return, no side effects either way, witness
loop below unreachable in both. Inert.

Reachability with flag on + cache size 0: only site 1's statement executes
(outcome key short-circuited); `:10980` and the `:11940` witness path do
not execute.

## Untested

- `ggml_cuda_moe_prefill_union_gather` has no unit test (pure host logic,
  desk-checked only).
- Nothing runtime-verified: no prefill wall, no link duty, no G-K1/G-K2,
  no G-V/G-L/G-G. CI green proves compile only.
- Pool ensure/destroy are Stage 1b entry points with no in-tree caller in
  v1 (declared in `moe-cache.cuh`, defined non-static so no unused warnings;
  failure mode is fallback, but no runtime path exercises them yet).
- The `llama-server` binary in `.local/stage1-ci/` was never executed
  (per the task rules); `file`/`ldd`/`cuobjdump` inspection only.

## Open questions / notes

- The unrelated `build-cann.yml` push-triggered run fails on this branch
  (run `37174450946`, 0 s, "workflow file issue" at startup). Pre-existing:
  it fails identically on `ci/perplexity-artifact` pushes (`37167411732`,
  `37164152782`, `37164033341`) and older branches (`36332160377`); the
  file parses as YAML. Not caused by, and out of scope for, this patch.
- `gh pr edit` (title/base) fails in this environment with a GraphQL
  classic-Projects mutation error; title retarget and base retarget to
  `ci/perplexity-artifact` were done via REST PATCH instead. No rebase was
  performed per the leader's instruction.
- G-K2 floor (G-0b) still not run; unchanged from `STAGE0-RESULTS.md:344`.
- Stage 1b owner: rig iteration after t0020 frees the 3060, planned by the
  leader with a registry-recommended dev model.
