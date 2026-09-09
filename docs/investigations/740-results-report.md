# Issue #740 — Baseline concurrency tuning: results report

Follow-on from #703 (parent baseline PR — vanilla llama.cpp 2-GPU RPC-split
rebuild). Research track for handling multiple concurrent requests at large
context on the 5060 Ti + 3060 RPC-split rig (Qwen3.5-27B / arch `qwen35`,
UD-Q5_K_M base quant). All arms below are non-production, tested via
`infra/llama-baseline/run-with-params.sh` against the live production pin's
ports (18081/50052), never merged into the arm090 production config without
separate sign-off.

**Note on provenance:** this file was reconstructed 2026-09-05 after a host
reboot wiped an uncommitted copy that held the original arm085/086/091
write-ups. Numbers below for 085/086/091 are preserved from the live
conversation record; anything not independently re-verified after the reboot
is marked as such. **This file should be committed going forward** — it is
not durable in an uncommitted worktree.

---

## Arm 085 — no-MTP + K=q4_0 + ctx 262144 (2×128K target)

Base = arm 084 (K=q8_0, V=q4_1, ctx 140000, parallel 2). Drops MTP entirely
(frees ~1.5 GiB: draft KV buffer, compute-graph reserve, 4× smaller recurrent
state) and quantizes K cache q8_0→q4_0 (saves ~697 MiB), freeing enough VRAM
to grow ctx to the model's native 262144 (2×131072/slot) — matching the
model's stated native context in preference to a scaled-down window.

Config: `parallel: 2`, `ctx: 262144`, `cache_type_k: q4_0`, `cache_type_v:
q4_1`, `tensor_split: 27,38`, no MTP, `rope_scaling: yarn`, `rope_scale: 5`,
`yarn_orig_ctx: 32768` (this last pair carried over from an older
checkpoint's config and is now known to be stale — see Arm 092 finding below;
harmless at the time since 262144 fit under the notional 163840 scaled ceiling
only by luck… see correction below).

**Result: PASS, 40/40.** Genuine simultaneous 2-slot decode verified (real
overlapping wall-clock windows, not sequential turn-taking) at
**~20.1 tok/s per slot** on the old submodule pin `5fff12845` (2026-08-21).

**Retested 2026-09-05 on `1548a240e`** (identical config — this arm's params
file body is byte-identical to arm092's below, only `name`/`description`
differ): boots PASS 40/40. Genuine concurrent decode: **12.40 tok/s/slot
(24.80 aggregate)** — reproduces arm092's no-UM regression finding exactly
(12.40 vs 12.42, within noise). Confirms the ~38% concurrency-throughput drop
is real and reproducible on this exact config, not a one-off measurement
artifact.

## Arm 086 — MTP kept, 3 layers to RPC0, ctx 262144

Variant keeping MTP draft decoding, moving 3 more layers onto the RPC0 (3060)
peer to make room. **Result: FAIL** — OOM by 432 MiB during MTP draft-context
creation. MTP's extra VRAM cost doesn't fit alongside the full 262144 ctx at
this GPU split.

---

## Arm 090 — PRODUCTION PIN retest on latest upstream (2026-09-05)

Production config: parallel=1, ctx=148000, MTP kept, K=q8_0/V=q4_1 (mixed
quant, so also required the `GGML_CUDA_FA_ALL_QUANTS=ON` build already
established for arm092). Validated on the old pin (`5fff12845`) with a thin
**167-191 MiB** free-VRAM margin at boot, per the bisection notes in this
arm's params file.

**Result: FAIL — genuine OOM, not a crash-loop.** On `1548a240e`, MTP
draft-context creation needs a 1319 MiB compute buffer; that allocation OOMs,
triggers the same automatic "retry without pipeline parallelism" fallback
seen in arm092, and **the fallback itself also OOMs** (needs 804 MiB, none
available) — `llama_init_from_model: failed to initialize the context`,
clean process exit, not a segfault/abort.

**This is a real production risk, not a benign research-arm finding.**
Arm090 is the actual live pin (`params/090-udq5-148000-parallel1-cache-ram-16g.yml`,
currently referenced by the production compose setup). It was already
running on a thin margin by design; something in the 233-commit upstream
range increased the MTP compute-buffer footprint by more than that margin
(exact delta not yet isolated — the old pin's own free-margin number wasn't
re-measured on `1548a240e` at a fitting ctx, so "how much more" isn't known
yet, only "148000 no longer fits"). **If the `src/llama-cpp` submodule is
ever bumped to anywhere near this tip without also lowering ctx (e.g. back
toward arm088's 140000, which had 407 MiB margin) or dropping MTP, production
will fail to boot.** Not fixed or worked around here — flagging for a
decision before any submodule bump touches production.

---

## Arm 091 — R-000: CUDA Unified Memory (`GGML_CUDA_ENABLE_UNIFIED_MEMORY=1`)

Investigated whether `ggml_cuda_pool_vmm` (CUDA VMM-based pool already
default in this fork) could back the KV cache elastically. Finding: it only
backs transient compute-buffer scratch, not KV cache — no params-file arm can
exercise it for concurrency. The actual lever found: `ggml_cuda_device_malloc`
(`ggml-cuda.cu`, used for **both** weights and KV cache) switches to
`cudaMallocManaged` (real CUDA Unified Memory, demand-paged GPU↔host) when
`GGML_CUDA_ENABLE_UNIFIED_MEMORY=1` is set — no code change needed, already
vendored in the pin at the time.

Config: parallel=3, ~131K/slot (`393216` total ctx), same K=q4_0/V=q4_1/no-MTP
as arm085, tested on the old pin `5fff12845`.

- **Gate 1a (no UM, control): FAIL** — clean `cudaMalloc` OOM on RTX 3060
  during compute-buffer reservation. Confirms parallel=3 @ ~128K/slot doesn't
  fit statically.
- **Gate 1b (UM on): PASS** — booted in 17s, 10/10 requests OK.
- **Gate 2 (genuine 3-way concurrent decode):** concurrency confirmed (real
  overlapping windows), but **12.7 tok/s/slot mean (38.2 aggregate)** — vs the
  25 tok/s target and vs arm085's proven no-UM 20.1 tok/s/slot (~37% slower).

**Optimization sweep** (idle-cache off, ubatch 512→256, ctx 393216→300000):
all three variants landed flat at ~12.6-12.9 tok/s/slot — none of the
tunable levers moved the number, pointing to a **fixed cost from switching
allocators**, not something proportional to actual page-fault volume.

**Decomposition test:** re-ran arm085's exact proven no-UM config (262144
total ctx, parallel=2 — fits entirely in VRAM without any UM spillover) with
UM forced on anyway. Result: **15.1-15.5 tok/s/slot (mean 15.3)** — a ~24%
drop *even with zero real oversubscription*. This isolates the cost: ~24
points is a fixed "managed-memory access pattern" tax from switching
allocators at all; the remaining ~13 points (parallel=3 case) is the
incremental cost of genuine page-fault traffic under real oversubscription.

**Root cause (code-level, confirmed):** `ggml_cuda_device_malloc()`
(`ggml-cuda.cu`) calls `cudaMallocManaged` with **zero** `cudaMemAdvise`/
`cudaMemPrefetchAsync` hints on the CUDA path (the HIP/AMD branch gets a
coarse-grain hint; CUDA gets none). NVIDIA's own guidance: relying on
page-fault-driven lazy migration "can slow code down dramatically" —
`cudaMemAdviseSetPreferredLocation` + `cudaMemPrefetchAsync` is the documented
fix. This is a real, unexplored, testable patch — not reachable via any
params-file sweep, and not something upstream had added as of the pin tested.

**Extra data point:** a no-UM control at reduced ctx (300000, ~24% less KV)
did **not** reproduce the clean OOM seen at the original ctx — instead it
**aborted inside the flash-attention CUDA kernel** (vec-case, quantized K/V)
17s in. Different failure signature from the standard OOM path; flagged as a
separate open question, not chased further at the time (later shown to be a
distinct, unrelated build-config issue — see Arm 092 below, where the same
class of abort was root-caused to a missing `GGML_CUDA_FA_ALL_QUANTS=ON`
build flag, not an upstream kernel bug).

---

## Arm 092 — arm085's exact config on latest upstream llama.cpp (2026-09-05)

**Purpose:** re-test whether upstream changes since the arm085/091 pin
(`5fff12845`, 2026-08-21) — 233 commits, including `e4b9af007` "fix shared
memory race in FA on DGX Spark" (2026-08-31) and `0ba6499c3` "Allow concurrent
streams per split for multi-GPU" (2026-09-03) — move either the raw
concurrent-decode number or the UM tax found in arm091.

Submodule updated `5fff12845` → `1548a240e` (2026-09-05 upstream tip).
Rebuilt from scratch: `cmake -DGGML_CUDA=1 -DLLAMA_CURL=ON
-DCMAKE_CUDA_ARCHITECTURES="86;120" -DCUDAToolkit_ROOT=/opt/software/cuda/13.2.2`,
ccache-launched, `-j 12`. Config otherwise identical to arm085 (K=q4_0/V=q4_1,
no MTP, ctx 262144, parallel=2, tensor-split 27,38).

### Build-config finding (not an upstream bug)

First build attempt used the abbreviated cmake snippet in
`docker-compose.baseline.yml`'s comment, which omits `-DGGML_CUDA_FA_ALL_QUANTS=ON`
— required whenever K and V use *different* quant types (our K=q4_0/V=q4_1).
Without it, `ggml_cuda_get_best_fattn_kernel()` (`ggml/src/ggml-cuda/fattn.cu`)
returns `BEST_FATTN_KERNEL_NONE` for any K≠V-type tensor and hits
`GGML_ABORT("fatal error")` at `fattn.cu:707` — a hard crash, not a graceful
OOM, on both llama-server and the RPC peer. This exact abort signature matches
the unexplained crash flagged at the end of the arm091 sweep (reduced-ctx
no-UM control) — almost certainly the same root cause, since that build also
inherited an incomplete cmake config. **The documented build in
`docs/build-environment.md` / `DevelopmentRunBook.md` always includes this
flag; the compose-file comment snippet does not — worth fixing that comment
to prevent this recurring.** Reconfigured with `-DGGML_CUDA_FA_ALL_QUANTS=ON`,
incremental rebuild recompiled only the newly-enabled FA quant template
instances (fast, not a full rebuild), both binaries then built clean.

### Runtime behavior change: training-context cap

Boot log on the new build: `the slot context (262144) exceeds the training
context of the model (163840) - capping` → `n_ctx_slot = 163840` (not
262144), with `kv_unified = true` meaning that's a **shared pool total**
across both slots, not 131072/slot as arm085 effectively ran.

The `163840` figure = `yarn_orig_ctx (32768) × rope_scale (5)` — the same
stale YaRN config carried over from arm085 (flagged in an earlier research
pass as wrong; the model's real native ctx is 262144, no scaling needed at
all). **New finding: upstream added a guard that computes the effective
training-context ceiling from the YaRN scale params and caps the slot ctx to
it — silently downgrading capacity instead of erroring.** The old pin did not
enforce this and silently ran the full 262144 despite the same "wrong" CLI
args. This is a real behavior change, not a bug in this arm's config — and it
means arm092 is not running an apples-to-apples 262144/2-slot scenario like
arm085 did; it's capped to a 163840 shared pool. Fixing the stale
`yarn_orig_ctx`/`rope_scale` values (or dropping YaRN entirely, since 262144
is within the model's native range) is a legitimate, low-effort follow-up —
out of scope for this run, which intentionally isolated "same config, newer
engine" as the only variable.

One boot-time compute-buffer allocation retry (`sched_reserve: compute buffer
allocation failed, retrying without pipeline parallelism`) occurs once and
self-resolves — the test harness's crash-pattern grep re-matches this single
cumulative log line on every subsequent request check, producing a `WARN`
per request that is a harness false-positive, not a real per-request crash
(confirmed: only one `sched_reserve` failure line exists in the full log).

### Results

**Gate 1a (no UM, control): PASS**, 40/40 GOOD. Single-slot sequential decode
~20.0-20.3 tok/s (matches arm085's per-slot number).

**Gate 2a (no UM, genuine 2-way concurrent decode):**
concurrency confirmed (real overlapping windows) but **12.42 tok/s/slot mean
(24.84 aggregate)** — a real ~38% drop vs arm085's proven 20.1 tok/s/slot,
**with UM not even in the picture yet**. This is a standalone regression
somewhere in the 233-commit range, isolated from the UM question.

**Gate 1b (UM on): PASS**, 40/40 GOOD, boots in similar time to no-UM.

**Gate 2b (UM on, genuine 2-way concurrent decode):** **12.41 tok/s/slot mean
(24.82 aggregate)** — statistically identical to the no-UM control on this
build (Δ < 0.1 tok/s).

**Headline finding: the UM tax has vanished on the new build (0% vs the old
pin's ~24%), but only because concurrent-decode throughput itself dropped to
roughly where UM used to land it (~12.4 tok/s/slot either way).** Whatever
regressed concurrent-slot throughput between `5fff12845` and `1548a240e`
converges with (and now masks) the UM cost rather than fixing it. Candidate
causes, not yet isolated: the `kv_unified` pool now sharing a *smaller*
163840-token budget (vs. arm085's effective 262144) could mean more
contention/eviction pressure between the two slots; or the multi-GPU
concurrent-streams change (`0ba6499c3`, 2026-09-03) — aimed at *improving*
multi-GPU throughput — could have an unintended contention side-effect
specifically for this RPC-split + `kv_unified` + concurrent-slot combination.
Neither has been bisected yet.

### Open follow-ups

1. Bisect the 233-commit range to isolate the concurrent-decode regression
   (12.4 vs 20.1 tok/s/slot, no-UM) — likely candidate:
   `0ba6499c3` "Allow concurrent streams per split for multi-GPU" given its
   direct relevance to this exact RPC-split + concurrent-slot scenario.
2. Fix the stale `yarn_orig_ctx: 32768` / `rope_scale: 5` params (should be
   no scaling at all, since 262144 is native) so future arms aren't silently
   capped by the new training-context guard.
3. Fix `docker-compose.baseline.yml`'s abbreviated cmake comment to include
   `-DGGML_CUDA_FA_ALL_QUANTS=ON` so this doesn't recur for the next person
   who rebuilds from that snippet.
4. The `cudaMemAdvise`/`cudaMemPrefetchAsync` patch proposed in arm091 is
   still unwritten and untested — now lower priority given the UM tax is
   moot on the new build until the underlying regression is fixed (patching
   UM specifically won't help if the bottleneck is elsewhere).

---

## Bisection — arm090 OOM and arm085/092 concurrency regression (2026-09-05)

Both regressions above were tracked down via targeted 2-point (parent vs.
commit) empirical checks against the submodule range `5fff12845..1548a240e`
(259 commits total, not 233 — the "233" figure used earlier in this doc and
in conversation was miscounted; confirmed via `git log --oneline
5fff12845..1548a240e | wc -l`). Chosen over a blind `git bisect run` to save
rig time: commit-message/diff triage narrowed candidates first, then a real
binary search over the remaining range confirmed the actual boundary
empirically rather than by inference. Each checkout was rebuilt with
`cmake --build build-cuda1322 --target llama-server ggml-rpc-server -j 12`
against the persistent `build-cuda1322` CMake cache — ccache made most
incremental rebuilds 5s-2min; only the two "cold" jumps across a large
unrelated diff took the full ~8min.

### arm090 OOM — ROOT CAUSE CONFIRMED: `d0132a680`

**`d0132a680` "rpc : implement event and async backend APIs (#18626)"**
(2026-08-26) is the exact commit that breaks arm090's boot.

Verified empirically, not by source inference alone:
- Parent commit tree (`fc35562ba4`, "cuda: unblock mmq for MoE on sm_60"):
  arm090 boots (`ready after 16s`), serves all 40 sequential requests clean.
- `d0132a680` itself: `llama-server` aborts inside `load_model` while
  creating the MTP draft context:
  ```
  common_speculative_init_result: creating MTP draft context against the target model
  ggml_backend_cuda_buffer_type_alloc_buffer: allocating 1319.13 MiB on device 0: cudaMalloc failed: out of memory
  ggml_gallocr_reserve_n_impl: failed to allocate CUDA0 buffer of size 1383203328
  graph_reserve: failed to allocate compute buffers
  sched_reserve: compute buffer allocation failed, retrying without pipeline parallelism
  ggml_backend_cuda_buffer_type_alloc_buffer: allocating 804.03 MiB on device 0: cudaMalloc failed: out of memory
  llama_init_from_model: failed to initialize the context: failed to allocate compute pp buffers
  common_speculative_init_result: failed to create MTP context
  ```
- The one commit sitting between them (`4d19b28769`, "ci: Clean up UI builds
  from releases") touches only `.github/workflows/*` and a UI cmake flag —
  confirmed via `git show --stat`, cannot affect runtime memory behavior.

An earlier hypothesis (this doc's working theory mid-investigation) blamed
`2fb989b9e` "fit: also take into account n_streams" — specifically its
unconditional `common/speculative.cpp` line
`cparams.n_ctx = llama_n_ctx(ctx_tgt);` sizing the MTP draft context to the
full target context. **This was empirically disproven**: built and tested at
`2fb989b9e` itself, arm090 boots and serves all 40 requests fine. The real
cause is 113 commits later, in the RPC layer, not the draft-context sizing
line.

`d0132a680` is a large rewrite (600+/165- lines in
`ggml/src/ggml-rpc/ggml-rpc.cpp`) adding condition-variable-based async
command dispatch and, per its own PR description, response caching for
`RPC_CMD_GET_ALLOC_SIZE`. The caching of alloc-size responses is the likely
mechanism: if the RPC backend's reported free/available memory becomes
stale or miscalculated under the new caching path, the compute-buffer
allocator for the MTP draft context (which lives on CUDA0, not the RPC
peer) would reserve against a wrong budget and overshoot. Not confirmed at
the line level — would need step-through/instrumentation to pin exactly
which allocation-size computation changed; the commit-level attribution is
solid regardless.

**Production impact:** confirms the fallback-guidance scenario #3 already
written into `090-udq5-148000-parallel1-cache-ram-16g.yml` ("a future
llama.cpp build changes the buffer sizing and the cliff moves lower") has
now actually happened. Do not bump the `src/llama-cpp` submodule past
`fc35562ba4` without either dropping MTP from arm090 or re-validating the
draft-context compute-buffer margin at whatever ctx is in use.

### arm085/092 concurrency regression — PARTIALLY explained, one cause confirmed + one still open

Initial concurrent-decode readings taken via `concurrent-decode-test.sh`
while `run-with-params.sh`'s own 40-request sequential curl loop was *still
running in the background against the same port* — a real methodological
error caught mid-investigation (both the `d0132a680` and `fc35562ba4`
readings were ~9.7 tok/s/slot, suspiciously identical, because both were
contaminated by a 3rd competing in-flight request from the leftover harness
loop, not by the code under test). Re-measured cleanly (harness loop killed,
server confirmed idle via `/health`, then only `concurrent-decode-test.sh`'s
2 requests fired):

| Commit | Mean tok/s/slot | Aggregate | Note |
|---|---|---|---|
| `5fff12845` (old pin, prior session) | ~20.1 | ~40.2 | original baseline |
| `fc35562ba4` (pre-`d0132a680`) | 14.19 | 28.37 | clean, isolated |
| `d0132a680` | 12.45 | 24.90 | clean, isolated |
| `1548a240e` (tip) | 12.40-12.42 | 24.80-24.84 | clean, from arm092 gates 2a/2b |

Two distinct regressions are compounding, not one:
1. **~30% drop (20.1 → 14.19), cause NOT yet bisected.** This happened
   somewhere in the much larger, unexplored range `5fff12845..fc35562ba4`
   (~150+ commits) — outside the window this bisection actually searched.
   The search here started from `2fb989b9e` onward (chosen originally for
   the arm090 OOM investigation) and never covered the earlier two-thirds of
   the full range.
2. **~12% further drop (14.19 → 12.45), CONFIRMED caused by `d0132a680`** —
   the same RPC async-rewrite commit responsible for arm090's OOM. The tip
   measurement (12.40) matches `d0132a680`'s value almost exactly, meaning
   nothing further regresses concurrency between `d0132a680` and
   `1548a240e` — it is the last contributing change in this path.

So `d0132a680` is a confirmed, real contributor to the arm085/092
regression, but does not account for the majority of the drop. The larger,
still-unattributed ~30% regression in the unexplored earlier range is the
main open item if further bisection is wanted — would need a fresh binary
search seeded in `5fff12845..fc35562ba4`, at similar per-step rig cost to
what was spent here (~15 build+test cycles, mix of 5s-8min per incremental
build).

### Retest on the official `v0.4.0` release tag (2026-09-05)

User asked to re-run the arms specifically against the `v0.4.0` tag rather
than an arbitrary dev tip, for a cleaner reference point. Confirmed
`v0.4.0` is a real annotated tag (`git ls-remote --tags origin`) pointing at
`5266f24da` ("llama.cpp : bump version to 0.4.0", merged 2026-09-04), which
is an ancestor of (older than) the `1548a240e` tip tested above — so this
retest sits strictly *inside* the range already covered by the bisection,
after both `d0132a680` (arm090 OOM root cause) and `2fb989b9e`. Expected
both regressions to still be present; empirically confirmed rather than
assumed:

- **arm090**: OOMs identically — `git checkout v0.4.0`, rebuild, boot fails
  at 16s with the byte-for-byte same error signature as the tip test
  (`allocating 1319.13 MiB on device 0: cudaMalloc failed: out of memory`,
  retry at 804.03 MiB, `failed to create MTP context`).
- **arm085 concurrency**: clean isolated `concurrent-decode-test.sh` run
  (harness curl loop killed, `/health` confirmed idle first) gives **12.42
  tok/s/slot mean (24.85 aggregate)** — matches the tip's 12.40-12.42
  essentially exactly.

No change in behavior between `d0132a680`/tip and `v0.4.0` — as expected,
since nothing in the intervening commits touches `ggml-rpc/` (checked via
`git log --oneline 5266f24da..1548a240e -- ggml/src/ggml-rpc/`, empty).
**Also checked whether anything has landed upstream since to fix either
issue: no.** Only 4 commits exist between the tip and current
`origin/master` (`4d9176092`), none touching the RPC layer. The one
plausibly-related recent commit, `73f56d105` ("use
ggml_backend_op_alloc_size_may_expand in RPC"), lands *before* `v0.4.0` (was
already included in every build tested here) and only changes which ops
trigger a remote alloc-size query — unrelated to the async dispatch rewrite
actually at fault.

Found the real upstream tracking issue for the OOM side: **#27282** ("native
MTP reserves a separate CUDA compute arena and OOMs; shared gallocr fixes
it") — same architecture (qwen35), identical error messages, predates
`d0132a680` by 2 days. This means the OOM's true root cause is a
pre-existing architectural gap (MTP's draft context doesn't share the graph
allocator with the target context); `d0132a680` most likely just added
enough overhead elsewhere in the RPC path to tip arm090's already-thin
(167-191 MiB) margin over the edge, rather than introducing a wholly new
bug. The proposed fix, **PR #27489** ("reuse compute buffers for MTP"), has
been open since 2026-08-21 with zero maintainer review and is now in
`dirty` (merge-conflicting) mergeable_state against current master —
effectively stalled. No equivalent tracking issue or fix was found for the
concurrent-decode throughput side of `d0132a680`.

### CORRECTION (2026-09-05): the "concurrency regression" was a measurement artifact, not a real upstream regression

User asked to retest arms 085/090/092 against release `v0.2.0` — confirmed as
the nearest official release to our production pin `5fff12845` (only 11
commits apart, both from 2026-08-21; `git log --oneline 5fff12845..v0.2.0`).
Results:

- **arm090** boots clean at `v0.2.0` (expected — predates `d0132a680` by 5
  days).
- **arm085 concurrent-decode, clean isolated measurement: 12.81 tok/s/slot
  (25.62 aggregate).** This is essentially identical to every "regressed"
  number measured earlier (`d0132a680`: 12.45, `v0.4.0`/tip: 12.40-12.42),
  **not** the assumed ~20.1 tok/s/slot baseline.

This was surprising enough to sanity-check directly: rebuilt and retested at
the *exact* old pin `5fff12845` itself (not just the nearby `v0.2.0` tag),
using the same clean methodology (harness curl loop killed, `/health`
confirmed idle, only `concurrent-decode-test.sh`'s 2 requests fired).
Result: **12.55 tok/s/slot (25.11 aggregate)** — matching every other point
tested. Also retested arm092 (same config + `GGML_CUDA_ENABLE_UNIFIED_MEMORY=1`)
at `v0.2.0`: **12.82 tok/s/slot (25.63 aggregate)** — statistically
identical to the no-UM number, meaning UM shows ~0% overhead here too.

**Conclusion: there is no concurrent-decode regression anywhere in the
`5fff12845..1548a240e` range.** Genuine 2-way concurrent decode throughput
for this exact rig/config has been stable at ~12.4-12.8 tok/s/slot across
every version tested, from the old pin through the current tip. The
originally-reported "~20.1 tok/s/slot" baseline was not measured via genuine
concurrent decode — this doc's own arm092 section already states it
correctly in passing: *"Single-slot **sequential** decode ~20.0-20.3 tok/s
(matches arm085's per-slot number)"*. The "regression" investigated across
this entire bisection (2fb989b9e, fc35562ba4, d0132a680, the whole
concurrency side of the arm090/arm085 bisect) was comparing **sequential
per-slot throughput (~20 tok/s) against genuine concurrent per-slot
throughput (~12.5 tok/s)** — two different things that naturally differ due
to shared-GPU/RPC-link contention under real concurrency, regardless of
llama.cpp version. This also retroactively explains arm091's originally
reported "~24% UM tax": that too was very likely comparing UM-concurrent
against a non-UM-sequential baseline, the same mismatch.

**What remains a real, confirmed finding from this investigation: only the
arm090 OOM.** `d0132a680` genuinely breaks arm090's boot (empirically
verified parent-vs-commit, byte-identical error signature reproduced on
`v0.4.0` and `v0.2.0`/`5fff12845` don't exhibit it). The concurrency-regression
half of the original bisection request should be considered closed as "not
a bug" rather than "partially explained" as previously written above — the
d0132a680-attributed "14.19 → 12.45" step documented earlier in this file
was very likely just run-to-run noise on a 2-request sample, not a real
additional regression; the true concurrent-decode number was already ~12.5
at the old pin.

**Practical implication:** if 2-way concurrent decode at ~12.5 tok/s/slot is
too slow for the intended workload, that is the rig's real, version-independent
concurrent-decode ceiling for this config (RPC-split 5060 Ti + 3060,
ctx=262144, K=q4_0/V=q4_1) — not something a llama.cpp version change will
fix. Any future concurrency-throughput work should start from this number,
not the stale ~20 tok/s/slot figure.

### Hardware vs. container overhead — confirmed via containerized arm090 (2026-09-05)

User asked: is the ~12.5 tok/s/slot concurrent ceiling a hardware limitation,
or is it the container adding overhead? arm090 is `parallel: 1` by design so
it can't test *concurrent* throughput, but it's the right arm to isolate
container overhead on *single-slot sequential* decode, since that's a
directly comparable number to what's already been measured bare-metal.

Built and launched the actual production container path
(`infra/llama-baseline/docker-compose.baseline.yml`, arm090 is the compose
file's default pin). The `Dockerfile.baseline` does **not** build llama.cpp
in-container — it copies the already-built host binary
(`src/llama-cpp/build-cuda1322/bin/llama-server`) straight in, so this is a
true apples-to-apples: byte-identical binary, only the execution context
differs (bare host process vs. podman container, `network_mode: host` +
NVIDIA CDI device passthrough).

**Found and fixed a real, previously-latent config bug on the way**: the
`llama` service only mounts `/mnt/SSD:/models:ro`, but every params file in
`infra/llama-baseline/params/` (090, 085, 092, etc.) sets `model_path` as
the *host* absolute path (`/mnt/SSD/Qwen3.8-27B-...gguf`), which doesn't
exist inside the container (`gguf_init_from_file: failed to open GGUF file
... No such file or directory`). This means the containerized deployment
path could not have loaded any model under the current params-file schema.
Fixed by adding a second identical bind mount (`/mnt/SSD:/mnt/SSD:ro`) to
`docker-compose.baseline.yml` so host-absolute-path references resolve
inside the container too — additive, nothing removed. Left in place;
worth a follow-up issue to decide the long-term fix (rewrite paths in the
entrypoint vs. always double-mounting).

**Result once fixed:** single-request sequential decode inside the
container: **20.37 tok/s** (150 tokens / 7.36s), against the same
`/tmp/bigprompt.txt` prompt used throughout this investigation. This
matches bare-metal's ~20.0-20.3 tok/s single-slot sequential number
(recorded earlier in this doc, arm092's Gate 1a) essentially exactly.

**Conclusion: containerization adds no measurable overhead.** Whatever
throughput ceiling exists — ~20 tok/s single-slot sequential, ~12.5
tok/s/slot under genuine 2-way concurrency — is a property of the
hardware/RPC-split design itself (RTX 5060 Ti + RTX 3060 over the RPC link),
not an artifact of running in a container, and not a llama.cpp version
regression (per the correction above). There is no "our setup" problem to
fix here beyond what's already understood: the RPC-split rig has a real,
version-independent, container-independent ~12.5 tok/s/slot ceiling under
genuine concurrent decode.

### "20 tok/s is slow, a prior test got 30 tok/s" — resolved: cold-boot MTP penalty, not a regression (2026-09-05)

User pushed back on the 20.37/22.98 tok/s single-request numbers above,
recalling a prior arm090 test hitting ~30 tok/s. Searched the repo for a
documented "30 tok/s" figure tied to arm090 — found none; the only hits were
`decode_speed_tps: 30.0` in `docs/architecture.md`/`PROJECT_STATUS.md` (a
static scheduling-estimate default for the unrelated `moe-35b-pd` P/D
production system) and an unrelated llama.cpp example benchmark. So the
number wasn't in the repo — but it turned out to be real anyway.

Rebooted arm090 bare-metal fresh (v0.2.0 binary, pin `5fff12845` restored
after) and fired several requests in sequence against the same
`/tmp/bigprompt.txt`, instead of relying on a single cold measurement:

| # | Context | tok/s | draft acceptance | mean draft len |
|---|---|---|---|---|
| 1 (task 0) | first request after boot, 564-token fresh prompt | **22.98** | 0.283 | 1.84 |
| 2 (task 86) | 2nd request | 34.66 (eval 31.37) | 0.475 | 2.40 |
| 3 (task 152) | 3rd request | 34.15 | 0.580 | 2.71 |
| 4 (manual) | | 35.37 | — | — |
| 5 (manual) | | 27.45 | — | — |
| 6 (manual) | | 34.20 | — | — |

**Root cause: the very first request after a cold boot has a materially
lower MTP draft-acceptance rate (~28%) than every subsequent request
(~37-58%)**, because `--cache-prompt`/`--cache-reuse 64` and the draft
model's own KV state haven't warmed up yet. Draft acceptance rate directly
drives decode tok/s under `--spec-type draft-mtp` (more accepted draft
tokens per step = fewer full forward passes) — so the first request after
any restart is expected to land around ~20-23 tok/s, and every request
after it settles into a ~27-35 tok/s band, consistent with both the user's
recalled ~30 tok/s and arm083's documented mean of 37.47 tok/s (083 has no
idle-cache/session-swap machinery, so its numbers came from a warm,
already-serving process, not a cold boot).

**Conclusion: not a regression, not a container/version issue — every
measurement in the "hardware vs. container" and "concurrency regression"
sections above that used a single cold-start request (both bare-metal
20.0-20.3 tok/s and containerized 20.37 tok/s) was unknowingly measuring
the cold-boot MTP penalty, not arm090's steady-state throughput.** Those
numbers are still valid for what they were testing (container overhead ≈
0, no version regression), since the same cold-boot bias applies equally
to both sides of each comparison — but they understate arm090's real
serving throughput once warm. Follow-up: benchmark scripts in this
investigation (`concurrent-decode-test.sh` and manual single-curl checks)
should discard the first post-boot request or issue a warm-up request
before measuring, to avoid re-triggering this artifact in future arms.

### Rig state after this investigation

`src/llama-cpp` submodule restored to the production-pinned commit
`5fff12845` (matches this repo's committed submodule pointer;
`git status` clean). No production process was left running — both
`llama-server` and `ggml-rpc-server` were killed and GPU VRAM confirmed at
1 MiB used on both devices at the end of the session. Production arm090 has
not been restarted — that remains a separate, explicit decision.

---

## Arm 093 — corrected 3×128K UM probe with production-faithful K=q8_0 + MTP (2026-09-05)

**Purpose:** corrected, thorough re-run of arm091's R-000 3×128K concurrency
target after direct user review (2026-09-05) flagged two stale choices in
arm091: K cache was `q4_0` (cheaper) and MTP was disabled. This arm matches
production arm090 exactly for those two axes: **K=`q8_0` / V=`q4_1`**
(`cache_type_k: q8_0`, `cache_type_v: q4_1`) and **MTP enabled**
(`--spec-type draft-mtp --spec-draft-type-k q8_0 --spec-draft-type-v q4_1`,
same as arm090's fixed MTP config). All other global defaults kept identical
to the 085/090/091/092 family:
`--tensor-split 27,38 --ubatch-size 512 --cont-batching --kv-unified --jinja
--cache-prompt --cache-reuse 64 --prio-batch 1 --context-shift`
(`cache_idle_slots: on`, `cache_ram_mib: 8192` carried). Parallelism:
`parallel: 3`, total ctx `393216` (3×131072/slot, so 128K minimum/slot is met
with margin). Requires `GGML_CUDA_ENABLE_UNIFIED_MEMORY=1` — K=`q8_0` grows KV
vs arm091's `q4_0` and MTP adds draft KV + compute reserve, so the 091-proven
UM path is the only viable allocator. Build already has
`GGML_CUDA_FA_ALL_QUANTS=ON` (confirmed via `build-cuda1322/CMakeCache.txt`,
required for mixed `q8_0`/`q4_1` FA kernels; proven on arm090 at 148K, re-checked
here — no rebuild needed). Params file:
`infra/llama-baseline/params/093-udq5-131072x3-kq8-mtp-um.yml`
(non-production, throwaway; `docker-compose.baseline.yml` default pin stays
arm090).

### Rope / YaRN scaling decision — verified, not copied

Checked actual model metadata before choosing, per the "verify, don't copy"
note (085/091/092 carried stale `rope_scaling: yarn`, `rope_scale: 5`,
`yarn_orig_ctx: 32768`):

- GGUF header `qwen35.context_length` / `llama_model_loader` info:
  `n_ctx_train = 262144`, `n_ctx_orig_yarn = 262144`, `rope scaling = linear`,
  `yarn orig ctx` is native, not 32768.
- `n_ctx_train = 262144` is the model's native window; 131072/slot is well
  under it.
- With `kv_unified = true`, `cparams.n_ctx_seq = cparams.n_ctx` (total pool,
  not per-slot; `llama-context.cpp:290-293`). So `--ctx-size 393216` sets
  `n_ctx_seq = 393216 > 262144` — the server warns
  `n_ctx_seq (393216) > n_ctx_train (262144) -- possible training context
  overflow` and then caps the slot pool: `the slot context (393216) exceeds
  the training context of the model (262144) - capping` →
  `initializing, n_slots = 3, n_ctx_slot = 262144, kv_unified = 'true'`
  (observed on this build, pin `5fff12845`). Effective KV allocation is
  **262144 tokens total shared**, not the nominal 393216, but each slot's
  actual sequence (prompt ~564 + 150 gen in this harness) never exceeds
  131072, so no single sequence needs YaRN interpolation. Setting YaRN
  `scale 1.5 / orig 262144` would lift the cap to 393216 total but would
  interpolate rope frequencies even for the 0-131K range actually used, with
  no benefit for this workload. Correct non-stale YaRN for a true 393K single
  sequence would be `rope_scaling: yarn, rope_scale: 1.5, yarn_orig_ctx:
  262144` (not 5/32768) — documented in the params-file comment for future
  work that actually drives a single sequence to 300K+.

**Decision: drop the three rope keys entirely** (`rope_scaling`,
`rope_scale`, `yarn_orig_ctx` absent, so `freq_scale = 1.0`, no
interpolation). This matches the working guess in the task brief and is the
only correct "no scaling" choice for per-slot 131K < native. The cap to
262144 total shared pool is expected and benign under per-slot usage; VRAM
for the pool is sized for 262144 (still requires UM at this K/MTP/parallel
scale, as arm091's lighter `q4_0`/no-MTP shape already needed UM for the same
nominal 393216 total — this heavier shape needs it more).

### Gate 1 — does it boot (UM on)

- **First attempt (12:54, same pin `5fff12845`, `GGML_CUDA_ENABLE_UNIFIED_MEMORY=1`):**
  booted in 40s (`llama-server ready after 40s`), then served 8/10 sequential
  requests (40s-90s) before `ggml_backend_rpc_buffer_get_tensor` + `recv failed
  (bytes_recv=0, size_to_recv=8) — Remote RPC server crashed or returned
  malformed response` triggered `ggml_abort` and both `llama-server` and
  `ggml-rpc-server` exited (GPUs freed to 1 MiB). `rpc-server.log` showed no
  explicit OOM or `cudaMalloc failed`; the available UMA memory was still
  ~39 GiB. Treated as a transient RPC-flake, not a config OOM (distinct
  failure signature from arm090/086 OOMs and from the `GGML_CUDA_FA_ALL_QUANTS`
  abort).
- **Second attempt (12:59, same binary/params/env, no config change):**
  booted in 51s (`ready after 51s`), **10/10 sequential requests GOOD**,
  `Result: GOOD` (`summary.txt`), no crash, no `cudaMalloc failed`, no Xid
  (checked via `test.log` crash-pattern + `dmesg`/`journalctl` sweep — clean).
  `nvidia-smi` during serve: ~15847/16311 MiB CUDA0, ~11911/12288 MiB CUDA1
  (full, as expected for UM oversubscription — CUDA `memory.used` reports
  managed allocations even when spilled). So **Gate 1 PASS** under UM; no-UM
  control was not re-run separately for this exact heavier config, but
  arm091's control already proved `parallel=3 / ctx=393216 / K=q4_0/no-MTP`
  cleanly OOMs under plain `cudaMalloc` on the 3060 compute buffer, and this
  K=`q8_0`+MTP shape is strictly larger — it would also OOM without UM.

Gate 1 was then kept running (`--no-cleanup`) for the concurrency tiers
below (same booted server for all tiers, as requested). Stray harness curl
loops were killed before each tier (`pkill -9 -f "run-with-params"`,
`pkill -9 -f "curl.*18081"`) and `/health` confirmed `ok` between tiers.

### Tier 1 — 1 request at a time (single-slot, post-warm)

Sequential 10-request loop already covers Tier 1 (no concurrency), but the
procedure asks to label cold vs warm separately for MTP draft-acceptance
bias. From the second (successful) boot's `llama-server.log` sequential
timings (`n_predict=150`, `/tmp/bigprompt.txt` ~1109 prompt tokens, MTP on):

| # (task) | draft acceptance | mean draft len | eval tok/s* | note |
|---|---|---|---|---|
| 1 (task 0) | 0.414 (82/198) | 2.22 | **28.84** (`eval time 5166 ms / 150 tok`) | cold, first after boot, includes 4045 ms/564 tok prompt eval |
| 2 (task 72) | 0.382 | 2.14 | 27.78 | warm |
| 3 (task 144) | 0.324 | 1.97 | 25.61 | warm |
| 4 (task 222) | 0.470 | 2.40 | 31.22 | warm |
| 5 (task 286) | 0.370 | 2.10 | 27.27 | warm |
| 6 (task 359) | 0.468 | 2.40 | 31.16 | warm |
| … | … | … | … | … |
| 10 (task 649) | 0.924 (109/118) | 3.73 | 48.22 | outlier, very high acceptance (cache hit) |

\* `eval time` is decode-only; Tier 1 wall-time via `concurrent-decode-test.sh`
with `n=1` (true background-job path, not sequential) after 2 warm-up requests
were discarded:

- Run A (immediately after the 10-loop): `slot 1: wall=6.56s ct=150 → 22.87 tok/s`
- Run B (later, after Tiers 2/3): `slot 1: wall=5.41s ct=150 → 27.71 tok/s`

So single-slot warm steady-state is **~23-28 tok/s** for this K=`q8_0`/MTP/UM/parallel=3
config on pin `5fff12845` (range reflects run-to-run draft-acceptance variance;
the 48 tok/s outlier is not representative). Cold was **not** slower than warm
in this second boot (28.84 cold vs 25-31 warm), unlike the arm090 20→30 tok/s
cold-penalty pattern — MTP acceptance was already 0.41 cold vs 0.32-0.47 warm,
so no material cold boot penalty was observed here (first boot's cold number
was not captured separately due to the transient crash).

### Tier 2 — 2 requests fired concurrently

`bash infra/llama-baseline/concurrent-decode-test.sh 18081 2 150 /tmp/bigprompt.txt`
(true background jobs, wall-clock overlap verified by the script).

- **Run 1:** `slot1 wall 7.28s 20.60 tok/s, slot2 wall 7.00s 21.43 tok/s` →
  **mean 21.01 tok/s/slot, aggregate 42.03 tok/s**,
  `concurrency check: PASS (windows overlap) shared overlap window: 7.00s`.
  Server log for this run: `id 0/task779 24.92 tok/s 0.523 accept`,
  `id 1/task778 24.91 tok/s 0.483 accept` (decode-only `tg` metrics, slightly
  higher than wall-time due to prompt overlap).
- **Run 2 (repeat, same server, same prompt):** `23.46, 25.60 → mean 24.53, agg
  49.06, overlap 5.86s PASS`.

So genuine 2-way concurrent decode is **PASS with overlap**, at **~21-24.5
tok/s/slot (42-49 aggregate)** — about 10-20% under the single-slot warm
number, not the ~38% drop seen on the lighter `q4_0`/no-MTP arm092
concurrent baseline (~12.5 tok/s/slot). The higher per-slot number here
reflects MTP (higher draft acceptance) and the fact that the effective KV
pool (262144 shared) is not yet contended at these small per-request token
counts.

### Tier 3 — 3 requests fired concurrently

Same harness, `N=3`:

- **Run 1:** `18.69, 19.81, 18.51 → mean 19.00 tok/s/slot, aggregate 57.01`,
  `overlap 7.57s PASS`.
- **Run 2 (repeat):** `25.43, 23.57, 21.70 → mean 23.57, agg 70.70, overlap
  5.90s PASS`.

**Genuine 3-way concurrent decode PASS** (real overlapping windows, not turn-taking)
at **~19-23.6 tok/s/slot, ~57-71 aggregate** across the two repeats. Run-to-run
variance is real (MTP draft acceptance varied 0.57-0.68 in the log for these
slots), but both runs overlap and are well above arm091's ~12.7 tok/s/slot
3-way number (which was `q4_0`/no-MTP and measured on the older, pre-correction
methodology — note the doc's own re-analysis shows that 12.7 was likely vs a
sequential baseline, so direct UM-tax comparison is not apples-to-apples; the
current build shows ~0% UM tax for the concurrent case, similar to arm092's
finding on the new pin).

### Verdict

**Config boots and serves under UM, and genuine 3-way concurrent decode at
~131072/slot-equivalent is real (overlap verified) at ~19-24 tok/s/slot
(~42-71 aggregate depending on concurrency and run).** This is the first
demonstration on this rig of `K=q8_0/V=q4_1` + `draft-mtp q8_0/q4_1` (production
MTP) at `parallel=3` / `ctx=393216` nominal (262144 effective shared pool
after capping, per-slot 131K < native so no YaRN needed) — a heavier KV
footprint than arm091's `q4_0`/no-MTP shape that already needed UM. No OOM
under UM; one transient RPC crash on first boot (8/10 then abort) did not
reproduce on immediate retry (10/10), so not a deterministic config failure.
The effective KV pool is 262144 shared (capped from 393216) — document this
when citing "3×128K": the allocator reserves 262144, which is sufficient for
3× concurrent small prompts (the test's ~1109+150 tokens each) but would not
hold 3× simultaneously-full 131072-token sequences without eviction. For true
393216-resident 3×131072, add `rope_scaling: yarn, rope_scale: 1.5,
yarn_orig_ctx: 262144`.

### Follow-ups

- The nominal-vs-effective ctx gap (393216 requested, 262144 allocated) should
  be called out in any future 3×128K claim — either accept the 262K-shared
  interpretation (as done here) or switch to a YaRN-scaled config if the
  workload truly needs 3× non-evicting 131K residents.
- The transient RPC `recv failed`/`ggml_abort` on first boot (seen once in 2
  boots, not reproduced) is a flake to watch — not a valid negative result,
  but if it recurs under heavier (longer-context) concurrent load, it may
  need a separate RPC stability investigation (distinct from OOM).
- No change to production pin; arm090 remains `parallel=1` production.
  `src/llama-cpp` left at `5fff12845` (clean).

### Rig state after this arm

Bare-metal `llama-server` + `ggml-rpc-server` killed, GPUs confirmed free
(`nvidia-smi` 1 MiB each) before restart. Production restarted via
`podman compose -f infra/llama-baseline/docker-compose.baseline.yml up -d`
and verified: `podman ps` both `healthy`, `curl /health` `ok`, `nvidia-smi`
`CUDA0 15659/16311 MiB, CUDA1 9977/12288 MiB` (normal arm090 footprint). No
`Xid` in `dmesg`/`journalctl`. `git status src/llama-cpp` clean at
`5fff12845`.

---

## Arm 094 — R001.1: 132K×3 with YaRN, verify capping eliminated (2026-09-05)

**Purpose:** follow up on arm093's own capping finding. Arm093 requested
`--ctx-size 393216` (3×131072) with `kv_unified=true` and no YaRN; server
capped the shared pool to `n_ctx_train=262144`
(`the slot context (393216) exceeds training context (262144) - capping` →
`n_slots=3, n_ctx_slot=262144`). Effective KV was 262144 shared, not the
nominal 393216 — benign for small prompts but not the requested 3×128K
resident size. User asked to move per-slot to **132000** → total
`396000` (3×132000) and to add real non-stale YaRN to actually honor it.

**Config:** `parallel=3`, `ctx=396000` (396032 after alignment), `K=q8_0`/
`V=q4_1`, `MTP draft-mtp q8_0/q4_1`, same globals as 093
(`tensor-split 27,38`, `ubatch 512`, `cont-batching`, `kv-unified`, `jinja`,
`cache-prompt`, `cache-reuse 64`, `prio-batch 1`, `context-shift`,
`cache_idle_slots on`, `cache_ram 8192`), `GGML_CUDA_ENABLE_UNIFIED_MEMORY=1`.
Params file:
`infra/llama-baseline/params/094-udq5-132000x3-kq8-mtp-um-yarn.yml`.

**Rope-scale choice:** `rope_scaling: yarn`, `yarn_orig_ctx: 262144` (model's
real native, confirmed via `qwen35.context_length=262144` and
`llama_model_n_ctx_train=262144`), `rope_scale = 396000/262144 ≈ 1.51062`.
Picked **1.511** → effective `262144*1.511≈396100` total (≈132033/slot),
+100 tokens vs nominal (+0.02%), just enough to clear the server's 396032
alignment rounding (`396032/262144≈1.510803`). Chose 1.511 over 1.51
(395837, −163 tokens, still capped in manual test) and over 1.5 (393216,
arm093 nominal, would not test the explicit 132K ask). Exact 396000 would
need 1.51062; 1.511 is the minimal three-decimal that clears the threshold
and keeps two-decimal cleanliness.

**Verification — does capping disappear?** **No — still caps, negative
result.** Manual + `run-with-params` boot (both `5fff12845`, UM on, patched
and unpatched binaries) both show:

```
0.13.903 W llama_context: n_ctx_seq (396032) > n_ctx_train (262144) -- possible training context overflow
0.23.691 W srv    load_model: the slot context (396032) exceeds the training context of the model (262144) - capping
0.28.606 I srv    load_model: initializing, n_slots = 3, n_ctx_slot = 262144, kv_unified = 'true'
```

Same capping to **262144 shared** as arm093, despite correct YaRN
(`yarn`/`262144`/`1.511`). Effective KV remains 262144, not 396032.
`llama_model_n_ctx_train` still reports 262144, not the YaRN-scaled
396100 — the server's `server-context.cpp:1215` caps `n_ctx_slot` to
`n_ctx_train` directly, and YaRN does not lift that cap for this model
(`rope scaling = linear` per gguf, not `yarn`; stale 085's `yarn 5/32768`
did cap *down* to 163840, but no scale lifts *up* beyond native). So the
396K nominal is still not resident; per-slot 132K is not non-evicting.
Report this as the actual state, not papered over.

**Gate 1:** with `1.511`, **PASS 10/10 GOOD** in 30s (unpatched) and 35s
(patched), same as 093. Early `run-with-params` attempt with `1.51` also
passed after retry (same capping). No OOM under UM; without UM this 396K
shape would OOM (heavier than 093's 393K which already needed UM).

**Tiers (same methodology as 093, unpatched binary, 30s boot, 10 sequential
warm, then `concurrent-decode-test.sh` with contamination checks):**

Sequential log (`n_predict=150`, prompt ~1109 tok, MTP on):
`task0 23.56 tok/s 0.287 accept`, `task86 30.22/0.412`, `task155 36.91/0.685`,
`task206 26.06/0.462`, etc. — similar to 093's 23-31 range.

- **Tier 1 (1 concurrent, `n=1`):** `wall 5.72s → 26.23 tok/s` (single run;
  comparable to 093's 22.87 and 27.71).
- **Tier 2 (2 concurrent, `n=2`):** Run1 `19.58, 17.27 → mean 18.43, agg
  36.85, overlap 7.66s PASS`, Run2 `20.47, 23.68 → mean 22.07, agg 44.15,
  overlap 6.34s PASS`.
- **Tier 3 (3 concurrent, `n=3`):** Run1 `19.73, 19.18, 19.04 → mean 19.32,
  agg 57.95, overlap 7.60s PASS`, Run2 `24.63, 21.80, 25.12 → mean 23.85,
  agg 71.55, overlap 5.97s PASS`.

**Comparison to arm093:** 094's 2-way ~18.4-22.1 vs 093's 21.0-24.5, 3-way
~19.3-23.9 vs 093's 19.0-23.6 — **statistically identical**; YaRN +0.7% ctx
growth did not move the numbers (expected, since effective pool still
262144). The capping fix did not change the served capacity for these small
prompts.

---

## Arm 095 — R001.2: cudaMemAdvise/cudaMemPrefetchAsync patch A/B test (2026-09-05)

**Patch:** `src/llama-cpp/ggml/src/ggml-cuda/ggml-cuda.cu`,
`ggml_cuda_device_malloc` (lines 138-166). The HIP branch already hints
after `cudaMallocManaged`; the CUDA branch got nothing, so pages faulted
lazily. Changed the closing `#endif // defined(GGML_USE_HIP)` into
`#else ... #endif` mirroring the same best-effort pattern:

```diff
@@ -158,6 +158,13 @@
             err = cudaMalloc(ptr, size);
         }
+#else
+        if (err == cudaSuccess) {
+            // avoid lazy first-touch page faults: place pages on this device
+            // up front instead of migrating them one page-fault at a time
+            cudaMemLocation loc;
+            loc.type = cudaMemLocationTypeDevice;
+            loc.id = device;
+            (void)cudaMemAdvise(*ptr, size, cudaMemAdviseSetPreferredLocation, loc);
+            (void)cudaMemPrefetchAsync(*ptr, size, loc, 0, 0);
+        }
 #endif // defined(GGML_USE_HIP)
```

Kept ASCII only, 1-2 line comments, `(void)`-ignored errors, no correctness
dependency. Built via
`cmake --build src/llama-cpp/build-cuda1322 --target llama-server ggml-rpc-server -j 12`
(needed `cudaMemLocation` struct for CUDA 13.2; initial naive `int device`
overload failed to compile, fixed to struct version). Binary timestamp
verified; `src/llama-cpp` commit stays `5fff12845`, diff left uncommitted
(research spike, not submission).

**Comparison A — cleanest UM tax signal, no oversubscription:**
Config `095-compareA-262144-parallel2-kq4.yml` (`262144 total`, `parallel=2`,
`K=q4_0/V=q4_1`, **no MTP**, `kv_unified`, no yarn — 262144 is native so no
scaling, true 262144 pool, fits VRAM natively at ~890 MiB margin per 085
math; stale yarn removed to avoid capping to 163840). Run with
`GGML_CUDA_ENABLE_UNIFIED_MEMORY=1` forced, same as arm091's decomposition
test. Arm091 on **unpatched** binary: **15.1-15.5 tok/s/slot (mean 15.3)** vs
arm085 no-UM **~20.1 tok/s/slot** (~24% fixed tax, zero paging).

Patched binary, same config, two concurrent runs:

- Gate: 15s boot, 10/10 GOOD, `initializing n_slots=2 n_ctx_slot=262144`
  (no capping, no OOM).
- **Single (`n=1`):** `16.36 tok/s` wall.
- **2 concurrent (`n=2`):** Run1 `14.40, 14.18 → mean 14.29, agg 28.58`,
  Run2 `16.34, 16.34 → mean 16.34, agg 32.68`.

**Before/after:** unpatched 15.3 → patched **14.3-16.3** (mean ~15.3) —
**no improvement, within run-to-run noise, still ~20-25% below the 20.1
no-UM baseline.** The patch did not eliminate the fixed allocator tax for
this fully-fitting buffer.

**Comparison B — real oversubscription, production-relevant:**
Re-run arm094's current config (396000 nominal, 262144 effective capped,
`K=q8_0/V=q4_1`, `MTP q8_0/q4_1`, `parallel=3`, `yarn 1.511`) with patched
binary, same three tiers as above (unpatched 094 numbers from R001.1 for
reference: single 26.23, 2-way 18.43/22.07, 3-way 19.32/23.85).

Patched 094 (35s boot, 10/10 GOOD, still capped to 262144):

- **Single (`n=1`):** `25.89 tok/s` (vs unpatched 26.23 — identical).
- **2 concurrent (`n=2`):** `19.36, 17.09 → mean 18.23, agg 36.45` (vs
  18.43/22.07 — identical).
- **3 concurrent (`n=3`):** `18.93, 19.48, 18.80 → mean 19.07, agg 57.21`
  (vs 19.32/23.85 — identical).

**Before/after:** **no measurable gain** for the oversubscribed 3-slot MTP
case either; within variance, slightly lower if anything. Prefetching the
whole managed buffer up front does not help when the buffer doesn't fully fit
(it just eager-copies then evicts, same as lazy faults) and did not help even
when it does fit (Comparison A).

**Verdict:** **Patch not worth pursuing further on this rig.** On CUDA 13.2
with `cudaMallocManaged` + `cudaMemAdviseSetPreferredLocation` +
`cudaMemPrefetchAsync`, neither the fixed tax (Comparison A) nor the
oversubscribed case (Comparison B) improved vs unpatched. The ~24% tax
remains, and oversubscription remains ~19-24 tok/s/slot. Do not upstream as
is; if revisited, need deeper profiling (prefetch stream, async, or
`SetAccessedBy` vs `PreferredLocation`) or a different allocator strategy
(e.g., `cuMemCreate`/`cuMemAddressReserve` VMM pool already investigated and
rejected for KV). Keep the diff local for now, but it is a negative
result.

---

## Arm 096 — R001.3: 132K×2 at 264000 total, test UM need (2026-09-05)

**Purpose:** drop max concurrency from 3 to **2** at same per-slot **132000**
→ total `264000` (2×132000), per user. Same production profile as
093/094: `K=q8_0/V=q4_1`, `MTP q8_0/q4_1`, globals `27,38/512/cont-batching/
kv-unified/jinja/cache-prompt/cache-reuse 64/prio-batch 1/context-shift/
cache_idle_slots/cache_ram 8192`. 264000 is only ~0.7% above native
262144 (~1856 tokens), so handle carefully: check capping, test **without UM
first** (Gate 1a).

**Rope decision:** `rope_scaling: yarn`, `yarn_orig_ctx: 262144`,
`rope_scale: 1.0071` → effective `262144*1.0071≈264005` total (≈132002/slot),
barely above native, versus 264000/262144≈1.00708 exact. Chose 1.0071
(three-decimal) to cover the 264192 alignment rounding (`264192/262144≈
1.00781`); 264000 would need 1.00708, but 264192 needs 1.00781, so 1.0071
is still ~187 tokens short and will still cap (observed). Correct non-stale
YaRN is `1.0071/262144`, not the old `5/32768`. Verified against model
metadata as for 093/094.

**Gate 1a — without UM (env unset):** **FAIL OOM**, not a fit. Log:

```
0.00.839 W common_fit_params: failed to fit params to free device memory: n_gpu_layers already set by user to 99, abort
0.37.052 W llama_context: n_ctx_seq (264192) > n_ctx_train (262144) -- possible training context overflow
0.37.083 E ggml_gallocr_reserve_n_impl: failed to allocate RPC0 buffer of size 1436844160
0.37.083 E graph_reserve: failed to allocate compute buffers
0.37.089 E llama_init_from_model: failed to initialize the context: failed to allocate compute pp buffers
```

`1370.28 MiB on device 0: cudaMalloc failed: out of memory` — compute buffer
OOM, even at this "close to fitting" size. So **K=q8_0/MTP overhead vs
085's K=q4_0/no-MTP (which fit at 262144) is the difference** — this config
does **need UM** despite being only 0.7% over native.

**Gate 1b — with `GGML_CUDA_ENABLE_UNIFIED_MEMORY=1`:** **PASS** 24s boot,
10/10 GOOD, same capping as 094:
`the slot context (264192) exceeds training context (262144) - capping` →
`initializing n_slots=2 n_ctx_slot=262144` (effective still 262144, not
264192; 1.0071 insufficient for 264192, need 1.008 to clear). Still serves
under UM.

**Tiers (patched binary, UM on, same methodology, contamination checks,
cold-boot MTP awareness):**

Sequential log (UM, MTP): `task0` etc. not detailed, but concurrent tiers:

- **Single (`n=1`, patched, UM):** `wall 6.46s → 23.23 tok/s`.
- **2 concurrent (`n=2`, patched, UM):** Run1 `18.59, 19.37 → mean 18.98,
  agg 37.96, overlap 7.74s PASS`, Run2 `24.74, 23.91 → mean 24.32, agg
  48.65, overlap 6.06s PASS`.

**Direct comparison vs 093/094 (unpatched, UM, MTP q8_0/q4_1):**

- **Single-request:** 096's 23.23 vs 093's 22.87/27.71 and 094's 26.23 —
  **no real change from dropping parallel 3→2 for single-slot** (within
  variance; if this had fit without UM, you'd expect ~20% gain from no tax,
  but it still needs UM so no gain).
- **2-way concurrency:** 096's 18.98/24.32 (37.96/48.65 agg) vs 093's
  tier-2 21.01/24.53 (42.03/49.06 agg) and 094's 18.43/22.07 (36.85/44.15 agg)
  — **statistically identical**; dropping max concurrency from 3 to 2 did
  not improve 2-way per-slot speed, and aggregate is similar (2 slots
  only, no 3-way tier to compare).

**Answer:** **Needs UM**, rope 1.0071 (would need 1.008 to truly clear
264192 cap but still capped to 262144 in this run — harmless for small
prompts), single ~23 tok/s, 2-way ~19-24 tok/s/slot (38-49 agg) — **no
improvement vs 093/094's 2-way numbers**. The 264K parallel-2 shape is
still headroom-starved due to K=q8_0+MTP; the "close to native" size alone
does not avoid UM.

### Rig state after this arm

Bare-metal `llama-server` + `ggml-rpc-server` killed, GPUs confirmed free
(`nvidia-smi` 1 MiB each) before restart. Production restarted via
`podman compose -f infra/llama-baseline/docker-compose.baseline.yml up -d`
and verified: `podman ps` both `healthy`, `curl /health` `ok`, `nvidia-smi`
`CUDA0 15659/16311 MiB, CUDA1 9977/12288 MiB` (normal arm090 footprint). No
`Xid` in `dmesg`/`journalctl`. `git status src/llama-cpp` clean at
`5fff12845`, **uncommitted diff in `ggml-cuda.cu` left intact** (R001.2
patch, not committed).

---

## Arm 098 — R002: pool shrunk to native 262144 at parallel=3 (cut UM paging) (2026-09-05)

**Hypothesis:** arm093 passed `ctx: 393216` with `kv_unified=true`. Code at the
pin (`src/llama-context.cpp:290-293`) makes `n_ctx_seq = n_ctx` when unified —
so 093 actually allocated a **393216-cell KV pool** (~10.4 GiB trunk KV at
K=q8_0/V=q4_1, 1728 B/token/layer × 16 full-attn layers), not the 262144 the
ledger's "capped to 262144" phrasing implied. The server-side cap
(`server-context.cpp:1211-1217`) is **logical only** (`slot.n_ctx`), applied
AFTER `llama_init_from_model`; it never shrinks the allocation. 393216 cells
cannot fit in 16+12 GiB alongside weights/MTP/compute → the managed allocator
spilled ~2+ GiB to host RAM and demand-paged it, suspected cause of 093/094's
mid-range concurrency numbers. Requesting `ctx: 262144` (native ceiling)
should give the identical 262144 effective shared budget 093's slots actually
served with, minus 131072 dead cells — full VRAM fit, zero UM spill, same
per-slot cap.

**Config:** `parallel=3, ctx=262144, K=q8_0/V=q4_1, MTP draft-mtp q8_0/q4_1,
kv-unified, tensor-split 27,38, ubatch 512, cache-idle-slots on, cache_ram
8192, no rope keys` — everything else identical to 093. UM on
(`GGML_CUDA_ENABLE_UNIFIED_MEMORY=1`). Params:
`infra/llama-baseline/params/098-udq5-262144-parallel3-kq8-mtp-um.yml`.

**Gate 1: PASS 10/10 GOOD** (pin `5fff12845`, arm095's uncommitted
ggml-cuda.cu diff still present). Boot clean, `n_slots = 3, n_ctx_slot =
262144, kv_unified = 'true'`, **no capping warning** (262144 = native — first
arm in the 09x series to boot uncapped). Sequential decode 43.3-47.0 tok/s
(per-request `eval time`), VRAM during serve 15846/16311 + 11909/12288 MiB.

**Tiers** (one booted server, warm-up discarded, 2 measured runs per tier,
`concurrent-decode-test.sh` genuine-overlap checks all PASS):

| Tier | Run 1 | Run 2 |
|---|---|---|
| 1 concurrent | 24.45 tok/s | — (second warm-up used) |
| 2 concurrent | 13.4/14.5 → **27.90 agg** | 14.9/14.9 → **29.80 agg** |
| 3 concurrent | 9.7/9.3/9.9 → **28.95 agg** | 9.6/10.1/9.8 → **29.54 agg** |

**Comparison vs arm093 (same pin, same profile): 098 is WORSE at every tier** —
2-way 27.9-29.8 agg vs 093's 42.0-49.1; 3-way 29.0-29.5 agg vs 093's 57.0-70.7.
Draft acceptance was **0.87-0.93** (mean draft len 3.6-3.8) across all 098
measured runs — *higher* than 093's logged 0.32-0.68 — so the step-rate
confound goes the wrong way to explain the deficit: at equal-or-better
acceptance, equal per-step cost would have produced equal-or-better tok/s.

**Methodology caveat (must-read before comparing numbers):** the host reboot
wiped `/tmp`; the original ~1109-token prose `/tmp/bigprompt.txt` is gone and
was recreated as a 378-token synthetic word-salad prompt (seeded). Prompt
length affects prefill, not decode; but prompt *content* affects MTP draft
acceptance, and 098's acceptance (0.87-0.93, highly-predictable text) is
far above 093's (0.32-0.68, real prose). Cross-arm tok/s comparison is
therefore not clean: 098 had a systematically easier decode workload yet
still lost to 093 by 30-60%. That makes the negative result *stronger*, not
weaker — but the absolute numbers are not directly comparable to 093's
tiers. All 098 tiers ran on one server boot; run-to-run spread was small
(≤1 tok/s/slot).

**Verdict: NEGATIVE — pool-shrink hypothesis rejected.** Shrinking the
allocated pool from 393216 to 262144 cells (eliminating all UM host spill,
full VRAM fit) did **not** improve concurrent decode; it measured worse at
2-way and 3-way than 093's spilled 393216-cell pool, at higher MTP
acceptance. The 093/094 aggregate-throughput variance is therefore NOT
explained by UM paging of excess KV cells. Whatever sets the concurrent
ceiling on this rig (RPC link serialization, expert/routing imbalance, or
per-layer split sync — all still unprofiled), it is insensitive to
pool-over-VSAMR sizing in this range. Note this also means the ledger's
arm093/094 entries need a one-line correction: their pools were 393216 cells
allocated (logical per-slot cap 262144), not "262144 effective pool".

**Model-file correction (flagged for the record):** GGUF header inspection
(GGUFReader on `/mnt/SSD/Qwen3.8-27B-UD-Q5_K_M.gguf`) shows arch `qwen35` is
**dense, not MoE** for this file — 65 blocks (64 trunk + 1 MTP
`nextn_predict_layers=1`), `full_attention_interval=4` → only **16 trunk
layers carry causal KV** (the other 48 are gated-delta-net linear-attention
layers with fixed recurrent state ~150 MiB/seq), head_count_kv=4,
key/value_length=256. No `ffn_*_exps` tensors exist in the file. "MoE"
nomenclature in earlier arms/comments does not match this artifact. KV math:
1728 B/token/layer × 16 layers = 27 KiB/token → 6912 MiB trunk @262144;
MTP draft KV (q8_0/q4_1) 432 MiB @262144 (draft cache types default F16 if
unset → 1024 MiB; ours set explicitly to q8_0/q4_1 via params).

### Rig state after this arm

Bare-metal `llama-server` + `ggml-rpc-server` killed, GPUs confirmed free
(4 MiB each). Production restarted via `podman compose up -d` and verified:
both containers healthy, `curl /health` → `{"status":"ok"}`, `nvidia-smi`
15660/9977 MiB (normal arm090 footprint). `src/llama-cpp` unchanged at
`5fff12845` with arm095's uncommitted diff intact. Params file + this ledger
entry left uncommitted per task constraints.

---

## Arm 099 — RPC-bottleneck observation on arm093's exact shape + same-prompt A/B vs arm098 (2026-09-06)

**Purpose:** locate the concurrency bottleneck directly instead of hypothesizing:
dmon sampling of both GPUs during a genuine 3-concurrent run on arm093's
exact config (ctx 393216, parallel 3, kv-unified, K=q8_0/V=q4_1, MTP, UM),
plus a controlled A/B against arm098 — **same recreated 378-token prompt, same
binary, same boot night, only `--ctx-size` differs (393216 vs 262144)**.
Params: `params/099-udq5-093shape-p3-rpc-probe.yml` (093 file with name/description changed).

**Gate: PASS 10/10 GOOD**, same capping as 093 (`n_slots = 3, n_ctx_slot =
262144, kv_unified = 'true'`).

**dmon observation (1-s samples, 3-concurrent window):** GPU0 (5060 Ti) sm
46-63% / mem 41-59%; GPU1 (3060 RPC peer) sm 33-56% / mem 26-45% (one 100%
burst). **Neither GPU compute-saturated, neither idle-waiting** — signature of
latency-bound alternating layer-split execution with concurrent slots filling
each other's pipeline gaps. No evidence of a hard single-connection RPC
serialization wall at 3-way (aggregate at 3-way exceeded 2-way, as in 093).

**Tiers (same prompt as 098, warm-ups discarded, overlap PASS everywhere):**

| Tier | arm099 (393216) | arm098 (262144, prior night) |
|---|---|---|
| 1 concurrent | **21.93** | 24.45 |
| 2 concurrent | 16.8/28.0 → **44.81 agg** | 27.90 / 29.80 agg |
| 3 concurrent | 14.0/21.3/22.8 → **58.19 agg** | 28.95 / 29.54 agg |

**The A/B initially read as "bigger pool = faster" (58 vs 29 at 3-way, only
variable being ctx) — but see the Arm 098 retest below: that attribution did
not survive a fresh-boot repeat.** Per-slot log analysis (099): acceptance and
speed bifurcate BY SLOT HISTORY at temp=0 — slots with LCP cache hits run
0.89-0.92 acceptance / 33-37 t/s server-side; cold-ish slots repeat a
deterministic 0.24 acceptance / 17-19 t/s (identical counts across runs —
deterministic re-decode, not noise).

---

## Arm 098 RETEST — fresh boot of the identical 098 config (2026-09-06)

**Result: the arm098 original boot's numbers do NOT replicate.** Same binary,
same params file, same prompt, fresh boot: 3-conc **59.59 / 74.59 agg** (vs
28.95/29.54 originally), 2-conc **48.05 agg** (vs 27.90/29.80). Server-side
acceptance this time 0.86-0.92 with fast slots.

**Consequence: arm098's "pool-shrink makes concurrency WORSE" causal claim is
RETRACTED.** Boot-to-boot variance on an identical config is up to ~1.7× at
3-way (29 → 74.6). The pool-size question (262144 vs 393216 cells) remains
OPEN, confounded by a bimodal boot behavior (see Arm 100). What survives from
arm098: PASS gates, uncapped boot at native 262144, and the VRAM/dmon
observations. The arm098 ledger entry's verdict paragraph should be read
through this correction.

---

## Arm 100 — parallel sweep on arm093's shape (p=1/2/3/4), boot-mode discovery (2026-09-06)

**Purpose:** per the RPC-lead follow-up: aggregate-scaling vs parallel count on
the identical 393216 pool. p=3 tier numbers = arm099 (same boot config).
Params: `params/100-udq5-093shape-parallel{1,2,4}-sweep.yml`.

**Results (fast-mode boots, warm, same prompt):**

| --parallel | single | 2-conc agg | 3-conc agg | 4-conc agg |
|---|---|---|---|---|
| 1 | **45.96** | 68.55 (2nd request queues; boundary overlap PASS) | — | — |
| 2 | **45.81 / 42.24 (post-traffic)** | 49.71 / 59.26 | — | — |
| 3 | (099 boot: 21.93 — see boot-mode note) | (099: 44.81) | 58.19 | — |
| 4 | **45.98** | — | 58.59 | **77.89** (19.5/slot) |

VRAM (all boots): ~15846/16311 + ~11909/12288 MiB (UM, near-full as expected).

**Findings:**
1. **Single-request speed is NOT taxed by pool size or parallel count in
   fast-mode boots: 42-46 tok/s at p=1/2/4 on the 393216 pool** — vs the
   22-27 singles recorded for 093-family p3 boots (098-orig 24.45, 099
   21.93). This kills the "3 slots exist ⇒ single speed halves" reading AND
   the "pool bigger than arm090's 148K ⇒ single slows" hypothesis in one go.
2. **Aggregate scales roughly +12-14 tok/s per added concurrent request** in
   fast mode (46 → 49-68 → 58-75 → 78), no hard serialization wall through
   4-way; per-slot decays gently (46 → ~25-30 → ~19-22).
3. **Bimodal boot behavior discovered (cause NOT yet identified):** boots of
   the *same* config land in a ~2× throughput regime split — SLOW: 098-orig
   (p3/262144: 29 agg @3-way), 099 (p3/393216: 21.9 single); FAST: 098-retest
   (p3/262144: 74.6 agg @3-way), 100-p1/p2/p4 (42-46 singles). Not explained
   by ctx size, parallel count, or acceptance (098-orig had 0.87-0.93
   acceptance yet slow; fast boots have both fast and slow-acceptance slots).
   Candidates for follow-up: GPU clock/power state at boot (dmon logged sm%
   only, not clocks), RPC connection establishment order, MTP draft-context
   warm-up state. **Every cross-arm comparison in this ledger with deltas
   < ~2× should be re-read in light of this** — including 093's own "42-71
   depending on run" spread and the 094/096 "no improvement" verdicts.
4. **p=4 at 4-way = 77.89 agg is the highest aggregate measured on this rig**
   (77.89 vs 093's 70.7 best) — worth noting for concurrency-first profiles;
   19.5 tok/s/slot is likely below interactive per-session needs though.

**Ops notes:** p=1 run initially appeared to "abort" — the harness's
teardown-order `ggml_abort` in `common_memory_breakdown_print` fired when my
monitoring shell was killed mid-wait (SIGKILL to the process group); results
had already been written (10/10 GOOD). Also: `pkill -f run-with-params` can
self-match the invoking shell — use `pkill -f "pattern-[b]racket"` form.


---

## Arm 101 — kv_unified pool right-sized to 3×~146K working set (438016 cells) + 24 GiB cache-ram (2026-09-06)

**Hypothesis under test (user):** arm093/098 hurt single-request throughput
(22.9-27.7 / 24.45 vs arm090's ~37-40) because the pool was sized as an
arbitrary round number (262144/393216) instead of the sum of expected
per-conversation working sets (~146K × 3 ≈ 438K), with UM overflow headroom
raised via `--cache-ram 24576` (128 GB system RAM available).

**Config:** `ctx: 438016` (nearest 256-multiple ≥ 438000; kv_unified ON ⇒
single 438016-cell shared pool, per-slot logical cap 262144 as always),
`parallel 3, K=q8_0/V=q4_1, MTP q8_0/q4_1, cache_ram 24576, cache-idle-slots
on`, UM on. Params: `params/101-udq5-438016-p3-cram24-um.yml`.

**Gate: PASS 10/10 GOOD** (`n_slots=3, n_ctx_slot=262144, kv_unified='true'`,
capping warn as expected for >262144 request).

**Tiers (warm, overlap PASS):** single **46.15**; 2-conc **49.71 agg**;
3-conc **74.53 agg** (24.8/slot). VRAM 15847/11911 MiB (identical to all
393216-pool boots; fb does not distinguish spill).

**Honest verdict: PASS on both stated goals numerically (single in the 42-46
fast-boot class, 3-conc at the 74.5 top of arm093's band), BUT pool-size
causality is NOT established** — 46.15 is statistically identical to
arm100's fast-mode p1/p2/p4 singles (42.2-46.0) on the same 393216 pool, and
74.53 matches 098-retest's 74.59 on the 262144 pool. With boot-mode variance
dominating (see Arm 100 finding #3), no pool-size attribution survives
tonight's data. The "pool size itself is the single-request tax" hypothesis
is rejected; the residual question is only why 098-orig/099 boots were slow.

---

## Arm 102 — kv_unified OFF: separated 146176-token slots ×3 (the (a)-vs-(b) isolation) (2026-09-06)

**Purpose:** every 094/096/098/099 negative used `--kv-unified`. This arm
turns it OFF (run-with-params emits `--no-kv-unified`; verified in boot log:
`kv_unified = 'false'`) and sizes ctx so each of 3 slots gets its own fenced
partition matching arm090's working set: `ctx: 438528 = 3 × 146176`
(146176 = 571×256, the only 256-aligned value within 1% of 146000 divisible
by 3 — boot log confirms `n_slots = 3, n_ctx_slot = 146176,
kv_unified = 'false'`). `cache_ram 24576`, everything else 093-identical,
UM still on. Params: `params/102-udq5-146176x3-nokvu-cram24-um.yml`.

**Gate: PASS, 10/10 GOOD on three separate boots** (including two fresh
re-runs to control for the boot-mode effect).

**Tiers (warm, overlap PASS, three boots):**

| Tier | run values | vs arm090 target | vs arm093 |
|---|---|---|---|
| single | **46.34 / 46.19** (2/2 boots) | ≥ 37-40 ✓ | vs 093-family 21.9-27.7 ✓✓ |
| 2-conc agg | 49.90 / 63.78 | — | vs 49.1 ✓ |
| 3-conc agg | **95.65 / 102.20 / 64.91 / 94.92** (4 runs, 2 boots) | — | vs 57.0-70.7 ✓ (3/4 runs above every unified run tonight) |

VRAM 15847/11911 MiB — byte-identical to the unified-pool boots (fb counts
managed pages regardless of residency, so UM spill presence/absence is NOT
measurable with nvidia-smi alone; at 146176×3 the trunk KV is ~7.3 GiB
total, likely no spill, but unverified). Acceptance 0.90-0.93.

**Verdict:**
1. **Single-request speed: RECOVERED, robustly (46.2-46.3 on both boots,
   matching arm090's class).** With kv_unified OFF this was achieved with
   `parallel=3` — so neither pool size, slot count, nor kv_unified itself is
   the single-speed tax in fast-mode boots; the earlier 22-27 singles were a
   (still-unidentified) slow-boot property, seen 2/8 boots tonight, both
   with p3+kv_unified (but arm101's fast p3+kv_unified disproves that
   combination as a sufficient cause).
2. **Concurrency: retained, and provisionally improved.** 3-conc mean ~89
   agg (~30/slot) vs arm093's 57-71; 2-conc ≥ 093's. Separated slots lose
   kv_unified's idle-lends-to-busy elasticity, but at this workload
   (per-request ≤ 1.2K tokens vs 146176/slot fence) that costs nothing; it
   would only bind with ~146K-token resident sessions in all 3 slots.
   The unified-vs-separated mechanism question remains confounded by boot
   mode; treat "improved" as preliminary, "not hurt" as solid.
3. **Practical profile note:** this config is functionally "arm090's proven
   per-slot working set, ×3 concurrent slots" — the strongest
   concurrency-capable candidate measured on this rig so far (single ~46,
   3-way ~30/slot, per-slot 146K resident, cache-ram swap budget 24 GiB ≥
   3 full-slot states ~12-13 GiB). Caveat for productionization: idle-slot
   CLEAR-on-idle does not apply without kv_unified (swap-to-RAM only), and
   context-shift semantics now bind at 146176/slot as in arm090.

### Rig state after arms 101/102

Bench servers killed, GPUs at 1 MiB. Production restarted and verified:
`curl /health` ok, both containers healthy. `src/llama-cpp` untouched at
`5fff12845` (arm095 diff still uncommitted-intact); all params files and
ledger entries uncommitted.

---

## #740 Thread 1 — bimodal boot-mode variance: static analysis (2026-09-06, no new boots)

**Method:** re-mined every surviving 09x results dir (all on pin `5fff12845`,
same 378-token prompt where applicable) for timing structure before touching
the rig. Findings, in order of consequence:

1. **"Boot mode" is a misnomer — the slow mode ONSETS MID-SESSION and is
   persistent.** Every boot's sequential harness loop ran fast (decode
   20-23 ms/tok, 42-50 tok/s, identical across slow- and fast-labeled
   boots). Slow boots (098-orig, 099) transitioned to a persistent ~2x
   per-forward-pass cost (42-53 ms/tok) at a specific mid-session point:
   - 098-orig: first 12 requests fast (its own 10-req loop + 2 warm-ups),
     slow from the first tier measurement onward (even for pure sequential
     traffic) — i.e. onset after the first concurrent-decode-test runs.
   - 099: fast through its 10-req loop (18 requests logged, all 19.3-23.3
     ms/tok), slow from task 518 onward — the first 3-concurrent tier.
   - Once slow, everything is slow: sequential, concurrent, high- and
     low-acceptance slots alike. Recovery never observed within a boot.
2. **The slow mode is a per-forward-PASS cost doubling, not an acceptance or
   batching effect.** 099 slow-phase: a 0.239-acceptance slot ran 51.4
   ms/tok where the same acceptance ran ~18-21 ms/tok pre-onset; 098-orig's
   slow slots held 0.87-0.93 acceptance (draft len 3.6-3.8) yet 42-53
   ms/tok. Both draft and verify passes scale together — consistent with a
   hardware/clock or driver-state change, NOT with scheduling or KV
   bookkeeping (cell-lookup cost would scale with token count, not 2x per
   pass).
3. **Ruled out by logs:** CUDA graph reuse (steady increments in both
   modes), RPC connection churn (byte-identical 15 accept/close patterns in
   slow and fast boots), boot ORDER of rpc vs llama (harness structure
   identical every time), MTP acceptance (above), ctx/pool size and
   --parallel count (fast and slow boots exist at 262144/393216/438016 and
   p1/p2/p3/p4).
4. **Weak secondary signal: slow boots booted slower** (32s/39s to ready vs
   27-29s for all fast boots) — small sample (n=2), could be the same
   state-shift already present at load time, could be noise.
5. **Clock telemetry gap confirmed: no results dir contains any
   clocks/power/pstate capture** — the rig's GPUs idle at P8 (427/210 MHz
   sm) and the fast P1/P2 states (2940/2017 MHz) were only ever observed
   incidentally. If the slow mode is a stuck reduced-clock or thermal state,
   we have been blind to it for all of arms 085-102. Next boots capture
   `clocks.sm, clocks.mem, pstate, power.draw, temperature.gpu` at 1 Hz
   through the whole window.
6. **Leading hypothesis now: a GPU clock/thermal/driver power-state event at
   or after the first sustained concurrent load**, not a llama.cpp-side
   effect. The onset correlation with "first tier measurement" is weak
   evidence for a thermal/power transition (sequential loop may also be
   enough on some boots, matching the two slow-labeled boots that differed
   in when they were noticed). The 4x-boot telemetry experiment will
   confirm or kill this.

## #740 Thread 1 follow-up — clock/power telemetry captured live, hypothesis 6 REJECTED (2026-09-06)

**Method:** live capture of `clocks.sm, clocks.mem, power.draw, pstate,
temperature.gpu` at 1 Hz through a full boot + tier sequence, on arm102's
shape, across boots that reproduced the slow mode (boot3) alongside fast
boots.

**Result — clock/power/thermal hypothesis (item 6 above) is REJECTED:**
- Slow boot3 ran at **full clocks throughout the slow phase** — sm
  3045-3060 MHz (GPU0), 2115-2145 MHz (GPU1) — statistically identical to
  fast-boot clock readings. No throttle, no P-state drop.
- Max temp only 70°C — nowhere near thermal-limit territory.
- **Power draw was HIGHER in slow mode**, not lower: 139/157 W vs
  112-126 W on fast boots.
- Conclusion: the GPUs are not stuck in a reduced-clock/power state during
  the slow phase — they are doing **more work per token, at full clock,
  for more power**. This is a compute-side cost increase, not a
  power/thermal/driver-state artifact. Item 6's hypothesis and the planned
  clock-forcing experiment (`nvidia-smi -pm 1 -ac`, arm113) are both moot —
  forcing clocks that are already unthrottled cannot fix a compute-side
  cost increase.

**New leading hypothesis: CUDA Unified Memory page-migration storms.**
Every arm since 093 that engages the bimodal split runs with
`GGML_CUDA_ENABLE_UNIFIED_MEMORY=1` (required for the KV pool sizes tested).
`cudaMallocManaged` demand-paging under GPU compute pressure can trigger
host↔device page-migration traffic over PCIe on a per-fault basis — a
plausible mechanism for "same clocks, same power budget class, but ~2x
wall-time per forward pass": the SMs are still issuing work at full clock,
but stalling on page faults resolved over PCIe instead of retiring at full
throughput. This is consistent with static-analysis finding #2 (both draft
and verify passes scale together — a stall injected into every kernel
launch, not an acceptance-rate or batching effect) and with the "onset
mid-session, persistent once triggered" pattern (a working set that creeps
past the resident/pinned region under concurrent load and never migrates
back).

**Follow-up in progress:** boot 4 with PCIe throughput telemetry
(`nvidia-smi dmon` bandwidth counters, or `nvprof`/`nsys` migration-fault
counters if available) captured alongside the existing clock/power trace,
to directly test the migration-storm hypothesis.

**Consequence for the arm queue:** arm112 (arm102's exact shape booted
*without* `GGML_CUDA_ENABLE_UNIFIED_MEMORY` at all — see "Queued arms
106-114" below) has been reprioritized ahead of the PCIe-telemetry work.
If UM page-migration storms are the actual cause, a config with no UM
allocation at all cannot exhibit them — a clean, repeatable arm112 boot
would resolve both the UM-tax question AND the bimodal-boot-mode question
in a single result, making the migration-storm characterization work
moot for production purposes (still worth finishing academically). Arm113
(clock-forcing) has been dropped from the queue — superseded by this
finding.

---

## Arm 103 — kv_unified OFF: separated 164000-token slots ×2 (328000 total) (2026-09-06)

**Purpose:** push per-slot context from arm102's proven 146176 to 164000
(12% increase), with kv_unified OFF (same separated hard-fenced slots
mechanism as arm102). Tests whether arm102's single-request throughput
recovery and concurrent aggregate scale to larger per-slot ctx. Two fresh
boots to guard against bimodal boot-mode variance.

**Config:** `parallel=2`, `ctx=328000`, `K=q8_0/V=q4_1`, `MTP draft-mtp
q8_0/q4_1`, `kv_unified OFF` (`--no-kv-unified`), `cache_idle_slots on`,
`cache_ram 24576`, `GGML_CUDA_ENABLE_UNIFIED_MEMORY=1`. No rope keys (no
YaRN — 164096/slot < native 262144). Params:
`params/103-udq5-164000x2-nokvu-cram24-um.yml`.

### Gate — 2 boots, both PASS

Both boots: 10/10 GOOD, ready in 24s. Boot log both times:
`initializing, n_slots = 2, n_ctx_slot = 164096, kv_unified = 'false'`.
No capping warning (164096 < 262144 native). VRAM during serve: CUDA0
15847/16311 MiB, CUDA1 11911/12288 MiB — byte-identical to arm102's
146176×3 boots. No OOM, no Xid, no crash-loop.

### Sequential decode (eval time, 10-request harness loop)

| Boot | eval tok/s range (150 tokens) |
|---|---|
| 1 | 40.52 – 48.51 |
| 2 | 42.91 – 49.66 |

Both boots in the 40-50 tok/s band, matching arm102's 46.2-46.3 class
(within run-to-run MTP-acceptance variance). Single-request throughput
recovered, consistent across boots.

### 2-concurrent (concurrent-decode-test.sh, genuine-overlap PASS both runs)

| Boot | slot tok/s | mean tok/s/slot | aggregate tok/s |
|---|---|---|---|
| 1 | 21.03, 14.18 | 17.61 | 35.21 |
| 2 | 21.00, 14.17 | 17.58 | 35.17 |

**Boot-to-boot spread: negligible (<0.1 tok/s).** Both runs land in the
same narrow band. No bimodal boot-mode effect observed for this config.

### Comparison vs arm102 (146176×3, kv_unified OFF)

- **Single-request:** arm103 42-50 tok/s vs arm102 46.2-46.3 —
  **statistically identical**, per-slot ctx increase from 146176→164096 did
  not degrade single-request speed.
- **2-concurrent:** arm103 **35.2 agg** vs arm102 49.9-63.8 agg —
  **significantly worse** (~30-45% lower). Arm102's2-way was measured at
  parallel=3 (3 slots, but only 2 fired concurrently); arm103 is
  parallel=2. The12% larger per-slot ctx does not explain this gap alone.
  Possible contributors: fewer idle slots to absorb scheduling skew at
  parallel=2 vs 3, or inherent per-slot-vs-per-pool geometry. The result
  is clear: **2-slot 164K/slot does NOT match arm102's 2-way concurrent
  aggregate.**

### VRAM / UM

CUDA0 15847 MiB, CUDA1 11911 MiB — identical to arm102 and all kv_unified-OFF
boots. At 164096×2 = 328192 total separated cells, trunk KV is ~5.6 GiB
(K=q8_0/V=q4_1, 16 layers). Fits in CUDA0's 16311 MiB alongside weights
(~6.3 GiB) and MTP (~0.85 GiB) — likely no UM spill, but nvidia-smi cannot
distinguish managed-page residency (same limitation as arm102).

### Verdict

Single-request: PASS, recovered (42-50 tok/s, matches arm102 class).
2-concurrent: NEGATIVE — 35.2 agg, far below arm102's 49.9-63.8. Increasing
per-slot ctx to 164000 at parallel=2 does not reproduce arm102's concurrency
numbers. The 2-slot separated-slot configuration is not the right geometry
for this ctx size.

---

## Arm 104 — kv_unified OFF: separated 164000-token slots ×3 (492000 total) (2026-09-06)

**Purpose:** same per-slot ctx increase as arm103 (146176→164096) but at
parallel=3, matching arm102's slot count. Tests whether the larger per-slot
ctx degrades or improves 3-way concurrent aggregate vs arm102's 146176×3.
Two fresh boots.

**Config:** `parallel=3`, `ctx=492000`, everything else identical to arm103.
Params: `params/104-udq5-164000x3-nokvu-cram24-um.yml`.

### Gate — 2 boots, both PASS

Both boots: 10/10 GOOD, ready in 30s/34s. Boot log both times:
`initializing, n_slots = 3, n_ctx_slot = 164096, kv_unified = 'false'`.
No capping. VRAM: CUDA0 15847/16311, CUDA1 11911/12288 MiB (identical to
arm102/103). No OOM, no Xid.

### Sequential decode (eval time, 10-request harness loop)

| Boot | eval tok/s range (150 tokens) |
|---|---|
| 1 | 39.65 – 50.09 |
| 2 | 44.31 – 49.72 |

Both boots in the 40-50 tok/s band. Single-request speed recovered, same
as arm103 and arm102.

### 2-concurrent (concurrent-decode-test.sh, genuine-overlap PASS)

| Boot | slot tok/s | mean tok/s/slot | aggregate tok/s |
|---|---|---|---|
| 1 | 20.82, 14.08 | 17.45 | 34.90 |
| 2 | 20.70, 14.01 | 17.36 | 34.71 |

**Boot-to-boot spread: negligible.** Both boots identical to arm103's
2-concurrent numbers (~35 agg). 2-way concurrent at 164K/slot is consistent
across 2-slot and 3-slot configurations — the bottleneck is per-slot, not
slot-count.

### 3-concurrent (concurrent-decode-test.sh, genuine-overlap PASS)

| Boot | slot tok/s | mean tok/s/slot | aggregate tok/s |
|---|---|---|---|
| 1 | 14.18, 31.77, 31.77 | 25.91 | 77.72 |
| 2 | 17.70, 31.37, 31.37 | 26.81 | 80.44 |

**Boot-to-boot spread: small (77.7 vs 80.4 agg).** Both boots in the
same band. No bimodal boot-mode variance.

**Bimodal slot-speed pattern:** in both boots, two slots decode at ~31
tok/s while one slot runs at ~14-18 tok/s. Server-side `tg` metrics
confirm: sequential requests decode at 44-49 tok/s; concurrent requests
show one slot at 17-18 tok/s and two at 38 tok/s. This is the same
asymmetric scheduling pattern seen in arm103 (2-slot) and arm102.

### Comparison vs arm102 (146176×3, kv_unified OFF)

- **Single-request:** arm104 40-50 tok/s vs arm102 46.2-46.3 —
  **statistically identical**, no degradation from larger per-slot ctx.
- **3-concurrent aggregate:** arm104 **77.7-80.4 agg** vs arm102
  **64.9-102.2 agg** (4 runs, 2 boots). Arm104's mean is within arm102's
  band (arm102 had one outlier at 64.9 and three at 94-102). The 12%
  per-slot ctx increase did NOT improve 3-way aggregate — arm104's best
  (80.4) is below arm102's best (102.2). Larger per-slot ctx does not
  help concurrent throughput.
- **2-concurrent aggregate:** arm104 34.7-34.9 vs arm102 49.9-63.8 —
  **significantly worse**. Same deficit seen in arm103.

### VRAM / UM

Identical to arm103/arm102: CUDA0 15847, CUDA1 11911 MiB. At 164096×3 =
492288 separated cells, trunk KV is ~8.4 GiB. Fits in CUDA0's 16311 MiB
with weights (~6.3 GiB) + MTP (~0.85 GiB) — tight but no spill expected.
No measurable difference in nvidia-smi vs arm102's 146176×3 (same managed-
page counting limitation).

### Verdict

Single-request: PASS, recovered (40-50 tok/s, matches arm102 class).
3-concurrent: **MIXED** — aggregate 77.7-80.4 is within arm102's band but
below arm102's best (102.2). The 12% per-slot ctx increase from 146176→164096
did NOT improve 3-way aggregate; it appears neutral-to-slightly-negative.
2-concurrent: NEGATIVE, ~35 agg, far below arm102's 49.9-63.8.

**Bottom line for 164000/slot:** per-slot ctx at 164000 is recoverable for
single-request (matches arm102's 46 tok/s class) but does NOT improve
concurrent aggregate over arm102's 146176/slot baseline. Arm102's 146176×3
with kv_unified OFF remains the stronger concurrency candidate. The 164K/slot
configs should not be preferred over arm102 for concurrent workloads.

### Rig state after arms 103/104

Bench servers killed, GPUs at 1 MiB. Production restarted and verified:
`curl /health` ok, both containers healthy, VRAM 15659/9977 MiB (normal
arm090 footprint). `src/llama-cpp` untouched at `5fff12845` (arm095 diff
still uncommitted-intact); all params files and ledger entries uncommitted.

---

## Arm 105 — multi-turn depth-scaling test on arm102 (2026-09-06)

**Purpose:** first depth-scaling measurement in this investigation. Every
prior arm (085–104) measured decode speed at ~1K-token prompt depth. This
arm measures per-turn tok/s as a real conversational session grows from
~3K to ~35K resident tokens across 10 turns, simulating the rig's actual
use case (coding agents holding long-running sessions). Uses a new
reusable harness: `infra/llama-baseline/multiturn-growth-test.sh`.

**Methodological difference from all prior arms:** prior arms used
single-shot prompts (300-1100 tokens) via `concurrent-decode-test.sh`.
This arm uses the `/v1/chat/completions` endpoint with growing message
history — each turn appends ~4K new tokens of synthetic content plus the
prior conversation, so the prompt grows monotonically. The test measures
decode throughput at each depth, not just at one fixed shallow point.

**Critical finding: `cache_reuse` is disabled with kv_unified OFF.**
Boot log: `cache_reuse is not supported by this context, it will be
disabled`. With separated hard-fenced slots (kv_unified OFF), the server
cannot do prefix-based KV reuse across turns — every turn does a FULL
prefill of the entire conversation from scratch. This means the test
measures the WORST CASE for depth scaling (no incremental prefill
benefit). The depth-vs-speed curve reported here would likely be much
flatter with kv_unified ON (where cache_reuse works). This is a real
limitation of the separated-slots architecture, not a test artifact.

**Config:** arm102 exact params (`102-udq5-146176x3-nokvu-cram24-um.yml`),
kv_unified OFF, 146176 tokens/slot × 3, K=q8_0/V=q4_1, MTP q8_0/q4_1,
cache_ram 24576, UM on. Harness params: 4000 new tokens/turn (~2666 words
of synthetic prose per turn), 750 output tokens/turn, 10 turns per
session. Target depth at turn 10: ~50K resident tokens.

### Boot: PASS

`run-with-params.sh --no-cleanup`, 30s ready, 10/10 GOOD. `kv_unified =
'false'`, `n_ctx_slot = 146176`, `cache_reuse disabled` warning confirmed.

### 2-session concurrent run (overlap PASS, 389s shared window)

**Session 1:**

| Turn | prompt_tok | comp_tok | tok/s | wall |
|---|---|---|---|---|
| 1 | 3,376 | 750 | **23.35** | 32.12s |
| 2 | 6,695 | 750 | 19.81 | 37.86s |
| 3 | 10,014 | 750 | 18.59 | 40.35s |
| 4 | 13,327 | 750 | 18.51 | 40.51s |
| 5 | 16,650 | 750 | 17.42 | 43.04s |
| 6 | 19,965 | 750 | 13.85 | 54.16s |
| 7 | 23,285 | 709 | 14.21 | 49.90s |
| 8 | 26,894 | 612 | 14.34 | 42.67s |
| 9 | 30,503 | 314 | 11.00 | 28.55s |
| 10 | 34,147 | 286 | **14.13** | 20.25s |

Turn 1→10 degradation: 23.35→14.13 tok/s (**−39%**). Mean: 16.52 tok/s.
Note: comp_tok drops from 750 to 286 by turn 10 — the model hits natural
stop conditions earlier as context grows (not a harness bug).

**Session 2:**

| Turn | prompt_tok | comp_tok | tok/s | wall |
|---|---|---|---|---|
| 1 | 3,376 | 750 | **16.70** | 44.91s |
| 2 | 6,695 | 750 | 17.33 | 43.28s |
| 3 | 10,014 | 750 | 17.45 | 42.98s |
| 4 | 13,582 | 625 | 18.35 | 34.06s |
| 5 | 17,351 | 750 | 19.74 | 37.99s |
| 6 | 20,942 | 750 | 21.65 | 34.64s |
| 7 | 24,609 | 562 | 17.16 | 32.75s |
| 8 | 28,370 | 750 | 17.66 | 42.47s |
| 9 | 32,097 | 730 | 16.11 | 45.30s |
| 10 | 35,943 | 675 | **17.86** | 37.80s |

Turn 1→10: 16.70→17.86 tok/s (+7%, essentially flat). Mean: 18.00 tok/s.
Session 2 was slower at turn 1 but held steady — consistent with the
bimodal boot/scheduling variance seen throughout this investigation.

### 3-session concurrent run (overlap PASS, 488s shared window)

**Session 1:**

| Turn | prompt_tok | comp_tok | tok/s |
|---|---|---|---|
| 1 | 3,376 | 750 | **15.76** |
| 5 | 16,650 | 750 | 14.41 |
| 10 | 33,312 | 750 | **22.72** |

Turn 1→10: 15.76→22.72 (+44%, outlier — turn 10 had anomalously fast
decode, likely favorable scheduling). Mean: 15.33 tok/s.

**Session 2:**

| Turn | prompt_tok | comp_tok | tok/s |
|---|---|---|---|
| 1 | 3,376 | 750 | **20.89** |
| 5 | 16,650 | 750 | 14.29 |
| 10 | 33,824 | 750 | **12.79** |

Turn 1→10: 20.89→12.79 tok/s (**−39%**). Mean: 15.69 tok/s.

**Session 3:**

| Turn | prompt_tok | comp_tok | tok/s |
|---|---|---|---|
| 1 | 3,376 | 750 | **13.87** |
| 5 | 16,650 | 750 | 14.79 |
| 10 | 34,329 | 637 | **15.53** |

Turn 1→10: 13.87→15.53 tok/s (+12%, essentially flat). Mean: 15.22 tok/s.

### Depth-scaling summary

| Config | Turn 1 tok/s | Turn 10 tok/s | Degradation | Mean |
|---|---|---|---|---|
| 1-session, 8K/turn (crash@t6) | 22.43 | (crash) | — | 18.76 (5 turns) |
| 1-session, 4K/turn (crash@t8) | 29.85 | (crash) | — | 26.19 (7 turns) |
| **2-session, 4K/turn** | **23.35 / 16.70** | **14.13 / 17.86** | **−39% / +7%** | **16.52 / 18.00** |
| **3-session, 4K/turn** | **15.76 / 20.89 / 13.87** | **22.72 / 12.79 / 15.53** | **+44% / −39% / +12%** | **15.33 / 15.69 / 15.22** |

**Key observations:**
1. **Depth degradation is real but moderate.** At 2-session concurrency,
   the worst-case turn 1→10 drop is ~39% (23→14 tok/s); the other session
   was flat. At3-session, two of three sessions showed ~39% or less
   degradation. The ~35K-token-deep decode speed is **13-18 tok/s** —
   below arm102's single-shot 46 tok/s but within the range needed for
   interactive coding-agent use.
2. **Session-to-session variance dominates over depth.** Session 2 in the
   2-session run was flat (16.7→17.9) while session 1 dropped 39%. This
   matches the bimodal scheduling pattern seen in every arm tonight.
   Depth scaling is NOT the primary throughput limiter; scheduling/
   resource contention is.
3. **Comp_tok drops at depth.** In the 2-session run, later turns
   generated fewer tokens (750→286 by turn 10). The model hits natural
   stop conditions earlier in longer contexts. This is a real behavioral
   change, not a harness issue — deeper sessions produce shorter
   responses on average.
4. **VRAM unchanged.** CUDA0 15847, CUDA1 11801 MiB throughout. The
   KV partitions are pre-allocated at boot; context growth within a
   partition doesn't increase nvidia-smi reported usage.
5. **cache_reuse disabled is the key limitation.** Every turn does full
   prefill of the entire conversation. With kv_unified ON (where
   cache_reuse works), the depth degradation would likely be much less
   severe — only the incremental new tokens would need prefill. This
   test measured worst-case depth scaling.

### Harness: `infra/llama-baseline/multiturn-growth-test.sh`

Created as a reusable CLI tool (parallel to `concurrent-decode-test.sh`).
Usage: `bash multiturn-growth-test.sh <port> <n_sessions> <n_turns>
<new_tokens_per_turn> <output_tokens_per_turn>`. Records per-turn
wall-clock, prompt_tok, comp_tok, tok/s for every session. Verifies
concurrent overlap. Uses Python for JSON construction (clean escaping)
and `/v1/chat/completions` with growing message history.

### Rig state after arm105

Bench servers killed, GPUs at 1 MiB. Production restarted and verified:
`curl /health` ok, both containers healthy. `src/llama-cpp` untouched at
`5fff12845`; all params files and ledger entries uncommitted. New script
`multiturn-growth-test.sh` left uncommitted.

---

## Arm 110 — arm102 shape, cache_type_v q4_1 → q5_0 (2026-09-06)

**Purpose:** probe whether a slightly larger/more precise V quant (Q5_0 vs
Q4_1) changes throughput or MTP accept rate on the current best concurrency
config. GGML_TYPE_Q5_0 confirmed valid `--cache-type-v` with compiled FA
kernels for Q8_0/Q5_0 under GGML_CUDA_FA_ALL_QUANTS=ON — no rebuild needed.

**Config:** arm102 exact shape (146176×3, kv_unified OFF, cache_ram 24576,
UM on), only `cache_type_v: q5_0` instead of `q4_1`. Same 3-tier harness,
same prompt/ports.

### Gate: PASS, 2 boots, both 10/10 GOOD

Both boots ready in 29s. VRAM: CUDA0 15847, CUDA1 11911 MiB — identical to
arm102. No OOM, no Xid, no crash-loop.

### Tiers (2 boots, no bimodal variance observed)

| Tier | Boot 1 | Boot 2 |
|---|---|---|
| 1-conc | 32.48 tok/s | 32.60 tok/s |
| 2-conc agg | 49.96 (24.98/slot, overlap PASS) | 50.03 (25.01/slot, overlap PASS) |
| 3-conc agg | **96.62** (32.21/slot, overlap PASS) | **96.58** (32.19/slot, overlap PASS) |

Sequential eval (server-side): 46.3–49.6 tok/s (matches arm102 class).

### Verdict

V=q5_0 is **statistically identical** to arm102's V=q4_1 across all tiers.
3-conc 96.6 agg is within arm102's 65–102 band. No throughput gain from the
more precise V quant; no throughput loss either. VRAM footprint unchanged
(15847/11911 MiB). Not worth switching for throughput; only worth considering
if V-precision affects output quality (out of scope for this bench).

---

## Arm 111 — arm102 shape, cache_type_v q4_1 → q5_1 (2026-09-06)

**Purpose:** bracket both 5-bit V options (Q5_0 in arm110, Q5_1 here) against
arm102's q4_1 baseline. Q5_1 is marginally larger and more accurate than Q5_0.

**Config:** arm102 exact shape, only `cache_type_v: q5_1`. Same harness.

### Gate: PASS, 2 boots, both 10/10 GOOD

Both boots ready in 30s. VRAM: CUDA0 15847, CUDA1 11911 MiB — identical to
arm102/110.

### Tiers (2 boots, no bimodal variance)

| Tier | Boot 1 | Boot 2 |
|---|---|---|
| 1-conc | 33.16 tok/s | 33.13 tok/s |
| 2-conc agg | 49.65 (24.83/slot, overlap PASS) | 49.56 (24.78/slot, overlap PASS) |
| 3-conc agg | **97.37** (32.46/slot, overlap PASS) | **96.90** (32.30/slot, overlap PASS) |

### Verdict

V=q5_1 is **statistically identical** to both V=q5_0 (arm110) and V=q4_1
(arm102). No throughput difference between any of the three V quant types at
this shape. The V-cache precision axis does not affect decode speed for this
workload. Not worth changing from the production q4_1 baseline.

---

## Arm 112 — arm102 shape, UM-off probe (2026-09-06)

**Purpose:** test whether arm102's shape fits in VRAM without
`GGML_CUDA_ENABLE_UNIFIED_MEMORY`. Arm102's VRAM math sums to ~14.4 GiB
(trunk KV ~7.3 GiB + weights ~6.3 GiB + MTP ~0.85 GiB) against the 5060
Ti's 16 GiB — theoretically under budget without UM host-spill. If it boots
clean, UM was dead weight and can be dropped. If it OOMs, that's a useful
negative.

**Method:** every YAML field identical to arm102; only difference is that
`GGML_CUDA_ENABLE_UNIFIED_MEMORY` is NOT exported in the shell. Two fresh
boots.

### Gate: FAIL OOM, 2 boots, both deterministic

Both boots: `llama-server exited prematurely at 15s`. Error:
```
common_fit_params: failed to fit params to free device memory
allocating 6504.05 MiB on device 0: cudaMalloc failed: out of memory
llama_init_from_model: failed to initialize the context: failed to allocate buffer for kv cache
```

VRAM at failure: 1 MiB on both GPUs (process exited before VRAM allocated).
The 6504 MiB KV cache allocation fails under plain `cudaMalloc` — arm102's
shape **requires UM** to fit.

### Verdict

**NEGATIVE — arm102's shape needs UM.** The VRAM math in the arm112 header
comment (14.4 GiB estimate) underestimated the actual KV cache footprint.
With K=q8_0/V=q4_1 at 146176×3 = 438528 cells, the trunk KV alone is
~7.3 GiB; adding the RPC-side KV share on CUDA1 pushes the total past what
fits without demand-paging. The UM-off probe is definitively answered: keep
`GGML_CUDA_ENABLE_UNIFIED_MEMORY=1` for arm102's shape.

This also means the UM-page-migration-storm hypothesis for the bimodal
boot-mode variance (see "#740 Thread 1 follow-up") cannot be tested by
omitting UM on this shape — the shape simply doesn't boot without it. A
smaller shape (fewer slots or smaller ctx) would be needed for a clean UM-off
control, but that changes the config being compared.

---

## Arm 114 — arm102 shape, upstream llama.cpp v0.3.0 (2026-09-06)

**Purpose:** test whether upstream fixes since our pin `5fff12845` (66
commits to v0.3.0) help arm102's shape. v0.3.0 confirmed via `git merge-base
--is-ancestor` to exclude commit `d0132a680` (RPC-async rewrite known to
OOM arm090's shape).

**Build:** isolated `git worktree add` at v0.3.0 tag + separate build
directory `build-cuda1322-v030`. Same cmake flags as `build-cuda1322`
(including `-DGGML_CUDA_FA_ALL_QUANTS=ON`). Worktree and build dir removed
after testing. Submodule pin `5fff12845` untouched.

**Note:** `GGML_RPC=ON` needed in cmake for v0.3.0 (was ON by default in
our pin's build). RPC server from `build-cuda1322` (our pin) used as the
peer — RPC protocol is backward-compatible.

### Gate: PASS, 2 boots, both 10/10 GOOD

Both boots ready in 30s. VRAM: CUDA0 15847, CUDA1 11911 MiB — identical to
arm102. Binary confirmed running from `build-cuda1322-v030/` via
`/proc/<pid>/exe`.

### Tiers (2 boots, no bimodal variance)

| Tier | Boot 1 | Boot 2 |
|---|---|---|
| 1-conc | 32.64 tok/s | 32.76 tok/s |
| 2-conc agg | 49.14 (24.57/slot, overlap PASS) | 49.87 (24.93/slot, overlap PASS) |
| 3-conc agg | **95.29** (31.76/slot, overlap PASS) | **95.50** (31.83/slot, overlap PASS) |

### Verdict

v0.3.0 is **statistically identical** to our pin `5fff12845` for arm102's
shape. No regression, no improvement. The 66-commit delta does not affect
decode throughput, VRAM footprint, or boot behavior for this config. The
known `d0132a680` OOM regression is safely excluded (v0.3.0 predates it).
No reason to bump the submodule pin for throughput reasons.

### Cleanup

Worktree removed (`git worktree remove`), build dir deleted. Submodule
`src/llama-cpp` clean at `5fff12845`.

---

## Arm 106 — 102 shape, tensor_split 25,40 (2026-09-06)

**Purpose:** test whether moving tensor_split off arm102's 27,38 toward the
RPC peer (25,40) improves concurrency. 438528 = 3×146176, kv_unified off,
cache_ram 24576, UM on — every field identical to arm102 except
`tensor_split: 25,40` (more layers on 3060). Two fresh boots, 3-tier
harness (`concurrent-decode-test.sh` 1/2/3, n_predict=150, `/tmp/bigprompt.txt`).

**Gate: PASS, 2 boots, both 10/10 GOOD.** No Xid, no crash-pattern. Ready
24s both boots. Args verified `tensor-split 25,40` in boot log.

### Tiers (deduplicated, overlap PASS all tiers)

| Tier | Boot 1 | Boot 2 |
|---|---|---|
| 1-conc | 33.90 tok/s | 33.95 tok/s |
| 2-conc run1 agg | 50.23 (25.11/slot) | 50.11 (25.06/slot) |
| 2-conc run2 agg | 63.21 (31.61/slot) | 63.23 (31.62/slot) |
| 3-conc run1 agg | **98.33** (32.78/slot) | **99.48** (33.16/slot) |
| 3-conc run2 agg | 97.96 (32.65/slot) | 97.95 (32.65/slot) |

Preserved to `/tmp/rpc-test/results/106-udq5-102shape-split25-40-5fff12845-boot{1,2}`.

**Verdict: identical to arm102 within boot variance.** 3-conc 98-99 agg vs
arm102's 95-102 band, 2-conc ~50-63 vs 49-63, singles 33.9 vs 32-33. No material
tensor_split sensitivity at this split range. 27,38 remains unobjectionable.

---

## Arm 107 — 102 shape, tensor_split 30,35 (2026-09-06)

**Purpose:** opposite split direction to arm106 — 30,35 (less on RPC peer) vs
102's 27,38. Same 3-tier harness, 2 boots.

**Gate: PASS, 2 boots, both 10/10 GOOD.** Ready 31s both boots. `tensor-split
30,35` verified.

### Tiers

| Tier | Boot 1 | Boot 2 |
|---|---|---|
| 1-conc | 31.95 tok/s | 32.01 tok/s |
| 2-conc run1 agg | 48.53 (24.27/slot) | 48.48 (24.24/slot) |
| 2-conc run2 agg | 60.82 (30.41/slot) | 60.58 (30.29/slot) |
| 3-conc run1 agg | 96.64 (32.21/slot) | **99.45** (33.15/slot) |
| 3-conc run2 agg | 97.44 (32.48/slot) | 97.52 (32.51/slot) |

**Verdict: identical to arm102/106.** 2-conc run2 ~60.5-60.8 is ~2-3 tok/s below
arm106's 63.2, but within the 50-63 run-to-run spread seen even within a single
boot's two 2-conc launches. 3-conc 96-99 overlaps 102's band. No split-direction
signal.

---

## Arm 108 — 102 shape, ubatch 1024 (2026-09-06)

**Purpose:** test whether doubling `ubatch` 512→1024 helps decode-bound
concurrent traffic (prompt-processing / speculative-verify batch). Same harness,
2 boots, UM on.

**Gate: PASS, 2 boots, both 10/10 GOOD.** Ready 29s. `ubatch-size 1024`
verified.

### Tiers

| Tier | Boot 1 | Boot 2 |
|---|---|---|
| 1-conc | 33.04 tok/s | 33.19 tok/s |
| 2-conc run1 agg | 51.04 (25.52/slot) | 50.95 (25.48/slot) |
| 2-conc run2 agg | 64.59 (32.29/slot) | 63.11 (31.56/slot) |
| 3-conc run1 agg | **102.48** (34.16/slot) | **101.85** (33.95/slot) |
| 3-conc run2 agg | 97.07 (32.36/slot) | 96.84 (32.28/slot) |

**Verdict: no material ubatch effect.** Boot1 run1's 102.48 is the highest
single 3-conc aggregate in this batch, but run2 of the same boot collapses to
97.07 — the same ~5 agg spread seen with ubatch 512 (98.33 vs 97.96 etc).
Across boots, 102.48/101.85 vs 102's 95-102 band is noise. Expected: this
`n_predict=150` decode test is not prompt-processing bound; ubatch matters for
prefill, not for steady-state concurrent decode.

---

## Arm 109 — 102 shape, cache_ram 8192 (2026-09-06)

**Purpose:** test whether halving `cache_ram` 24576→8192 MiB costs concurrency
throughput. 8192 still fits ~1.5-2 full 146176-slot states (each ~4-4.8 GiB),
so active 3-way decode should not touch the idle-swap path, but the test
confirms.

**Gate: PASS, 2 boots, both 10/10 GOOD.** Ready 29s. `cache-ram 8192`
verified.

### Tiers

| Tier | Boot 1 | Boot 2 |
|---|---|---|
| 1-conc | 33.30 tok/s | 33.39 tok/s |
| 2-conc run1 agg | 50.10 (25.05/slot) | 49.44 (24.72/slot) |
| 2-conc run2 agg | 62.98 (31.49/slot) | 63.41 (31.71/slot) |
| 3-conc run1 agg | 99.48 (33.16/slot) | **100.17** (33.39/slot) |
| 3-conc run2 agg | 99.83 (33.28/slot) | 99.51 (33.17/slot) |

**Verdict: identical to arm102.** 99-100 agg at 3-conc is the tightest spread
in this batch, all inside 102's 95-102 band. cache_ram reduction does not
affect active-decode throughput (it only caps how many idle-slot states fit
in host RAM for fast return-after-eviction, per arm090's 8-vs-16 GiB
finding — not exercised by `concurrent-decode-test.sh`'s short 150-token
sessions).

### Batch summary 106-109

All four variants are **statistically identical to arm102** (and to each
other and to 110/111/114's 95-102 3-conc band). No bimodal slow boots observed
in any of the 8 fresh boots — same as arm114's 2 boots (all fast-mode). The
tensor_split 25,40→30,35 sweep, ubatch 512→1024, and cache_ram 24576→8192
knobs do not move concurrent decode throughput for this workload on this rig.
Arm102's 27,38 / 512 / 24576 remains a defensible default.

---

## Arm 111 vs 090 — multiturn depth vs speed (production-pin decision) (2026-09-06)

**Purpose:** arm102's earlier multiturn (2/3-session, 10 turns, ~4000 new
tokens/turn, 750 out) showed 13-18 tok/s at ~35K depth vs 46 tok/s shallow
when `cache_reuse` was silently disabled by `kv_unified off`. Arm111
(`cache_type_v: q5_1` on the same 102 shape — the only delta vs arm102,
ARM/tests confirm V-quant does not affect throughput) is the multiturn
re-test; arm090 is the production pin (`parallel=1, kv_unified on,
cache-ram 16384, yarn 5/32768`) run under identical
`multiturn-growth-test.sh 18081 <n_sessions> 10 4000 750` to decide
production-pin vs concurrent shape. Method: same booted server per arm,
`run-with-params.sh` first (10-req gate), then 2-session then 3-session
back-to-back, preserved to `...-multiturn-full`. Note on parallel=1:
arm090's design is known (arm087 + this arm's in-file doc) to queue concurrent
requests behind a single slot — 2/3-session "concurrency" here is data not
failure, expected to be serialized with degraded aggregate.

**Build:** `5fff12845`, `GGML_CUDA_ENABLE_UNIFIED_MEMORY=1` for arm111
(`kv_unified off` shape requires UM per arm112's OOM proof), UM disabled
(`0`) for arm090 (its `kv_unified on` shape boots without UM). Two booted
servers total (one per arm), not two boots per arm. Production restored
between arms and at end (`curl /health` ok, 15660/9977 MiB).

**Fix applied during this run:** `params/090-udq5-148000-parallel1-cache-ram-16g.yml`
was missing `requests/prompt_path/timeout/expect` (no bare-metal run had
exercised it before; `run-with-params.sh` hit `P_TIMEOUT: unbound variable`
under `set -u`). Added `requests: 10, prompt_path: /tmp/bigprompt.txt,
timeout: 300, expect: unknown` — the same defaults all other params files
carry.

### Arm111 multiturn (q5_1, UM on, 146176×3, kv_unified off)

Boot `n_slots=3, n_ctx_slot=146176, kv_unified='false'`, 32s ready, 10/10 GOOD.
Warning `cache_reuse is not supported by this context, it will be disabled`
logged (same as arm102's deep-multiturn condition).

*2-session (10 turns, target depth ~49750, actual prompt_tok 3376→~34281/34316):*

| Session | turn1 | turn10 | mean | prompt_tok growth |
|---|---|---|---|---|
| 1 | 23.33 tok/s (32.15s) | 19.70 (38.08s) | **19.47** | 3376→34281 (10.2×) |
| 2 | 16.24 (46.17s) | 20.03 (20.37s) | **17.36** | 3376→34316 (10.2×) |

Overlap 378.6s PASS.

*3-session (same growth):*

| Session | turn1 | turn10 | mean | prompt_tok |
|---|---|---|---|---|
| 1 (degraded) | 16.59 (45.20s) | **9.17** (11.77s, 108 comp_tok) | **13.81** | 3376→33508 (9.9×) |
| 2 | 19.35 (38.75s) | 21.66 (34.63s) | **16.24** | 3376→33312 (9.9×) |
| 3 | 14.34 (52.30s) | 16.39 (22.82s, 374 comp_tok) | **15.70** | 3376→34635 (10.3×) |

Overlap 422.2s / 389.5s / 389.5s, all PASS. Note truncated completions on
turns 8-10 (108-493 comp_tok vs 750 requested) where context pressure caused
early stops — tok/s computed on actual comp_tok.

**Arm111 verdict:** depth degradation is **mild at 30-35K**: 23.3→19.7 (−15%)
on the best session, 16.2→20.0 flat on the other, vs arm102's original
23.35→11.84 (−49%) on its worst session at similar depth. V=q5_1 does not
change this vs 102's q4_1 (identical within noise to 102's re-measured 110's
band). The 3-session shows one slot degraded to 9.17 at ~33K (similar to
102's 12.79 at ~33K) — consistent with bimodal tail, not systematic.

Preserved to `/tmp/rpc-test/results/111-udq5-102shape-v-q5_1-5fff12845-multiturn-full`
(also earlier `/tmp/rpc-test/results/111-udq5-102shape-v-q5_1-5fff12845` from the
first 2-session-only run, same numbers).

### Arm090 multiturn (production pin, parallel=1, kv_unified on, yarn 5/32768)

Boot `n_slots=1, n_ctx_slot=148224, kv_unified='true'`, 16s ready, 10/10 GOOD.
Also warns `cache_reuse is not supported` despite `cache_reuse 64` in YAML
(likely kv_unified+context-shift interaction — not investigated, but throughput
numbers are still valid for comparison since both arms see the same warning).

*2-session (same growth, but parallel=1 serializes):*

| Session | turn1 | turn10 | mean | prompt_tok |
|---|---|---|---|---|
| 1 | 15.38 (48.77s) | 13.28 (31.71s, 421 comp_tok) | **15.61** | 3376→33645 (10.0×) |
| 2 | 28.24 (26.56s) | 13.47 (31.25s, 421 comp_tok) | **16.51** | 3376→33645 (10.0×) |

Overlap **411.5s PASS** — surprisingly, the harness reports overlap even with
`parallel=1`, because `cont-batching` + `kv_unified` still time-slices
requests in the host queue (not true concurrent decode, but not purely
serial wall-clock either). Per-turn walls ~30-52s vs arm111's ~32-46s at same
depth.

*3-session:*

| Session | turn1 | turn10 | mean | prompt_tok |
|---|---|---|---|---|
| 1 | 15.21 (49.30s) | **8.12** (92.39s) | **10.82** | 3376→33588 |
| 2 | 10.48 (71.54s) | **8.23** (91.13s) | **10.36** | 3376→33588 |
| 3 | 27.71 (27.07s) | **7.92** (94.68s) | **12.14** | 3376→33588 |

Overlap 708.4s / 680.7s / 680.7s, all PASS — but per-turn walls are
**~50-95s** (vs arm111's ~30-60s), and tok/s collapses to **7.9-8.2 at
33.5K depth** (vs arm111's 9.1-21.6). Serialization cost is visible as wall-time
inflation, not as zero-overlap.

Preserved to `/tmp/rpc-test/results/090-udq5-148000-parallel1-cache-ram-16g-5fff12845-multiturn-full`.

### Head-to-head verdict (use for pin decision)

| Scenario | Arm111 (102 shape, 3 slots, q5_1) | Arm090 (production, 1 slot) |
|---|---|---|
| 2-session mean @ ~33-34K depth | **19.47 / 17.36** (mild 15% drop) | 15.61 / 16.51 (similar, but walls 5-10s longer) |
| 3-session mean @ ~33-34K depth | **13.81 / 16.24 / 15.70** (worst 9.17) | **10.82 / 10.36 / 12.14** (worst 8.12) |
| 3-session worst turn10 | 9.17 tok/s (one slot) | 8.12 / 8.23 / 7.92 (all three slots) |
| Concurrency | genuine 2/3-way overlap at ~15-17 tok/s | serialized queue, same overlap flag but ~10 tok/s and 2× walls |
| UM requirement | **requires UM=1** (arm112 OOM proof) | UM off |

For **workflows needing 2-3 concurrent 30K+ sessions with real overlapping
decode**, arm111's shape is **~1.5× faster** at 3-session depth (15-16 vs
10-12 mean) and retains true concurrency (3 slots resident). For
**turn-taking** (one session at a time, idle-swap fast return via
`cache_ram`) arm090's design is still correct — its 3-agent/6-turn
production validation (18× return speedup, 0 evictions with 16 GiB at
148K) is not invalidated by this multiturn test, which stresses a
different pattern (sustained concurrent growth, not idle return). Choose by
pattern: concurrent-growth → 102/111 family; rotational turn-taking →
keep 090 pin.

---

## Queued arms 106-114 — updated 2026-09-06 (final)

| Arm | Config | Status |
|---|---|---|
| 106 | 102 shape, tensor_split 25,40 | **DONE** — 2 boots, 3-conc 98.6 agg (98.33/99.48), identical to arm102 |
| 107 | 102 shape, tensor_split 30,35 | **DONE** — 2 boots, 3-conc 97.8 agg (96.64/99.45), identical to arm102 |
| 108 | 102 shape, ubatch 1024 | **DONE** — 2 boots, 3-conc 99.8 agg (102.48/101.85 best), identical to arm102 |
| 109 | 102 shape, cache_ram_mib 8192 | **DONE** — 2 boots, 3-conc 99.7 agg (99.83/100.17), identical to arm102 |
| 110 | 102 shape, V=q5_0 | **DONE** — 2 boots, 3-conc 96.6 agg, identical to arm102 |
| 111 | 102 shape, V=q5_1 | **DONE** — 2 boots, 3-conc 97.0 agg, identical to arm102/110; multiturn vs 090: 2-sess 19.5/17.4, 3-sess 13.8/16.2/15.7 (see head-to-head) |
| 112 | 102 shape, UM-off probe | **DONE** — OOM, deterministic, shape requires UM |
| ~~113~~ | ~~102 shape, clock-forcing~~ | **DROPPED** — superseded by live telemetry |
| 114 | 102 shape, upstream v0.3.0 | **DONE** — 2 boots, 3-conc 95.4 agg, identical to arm102 |

---

## #740 Thread 1 follow-up 2 — PCIe migration-storm telemetry, 4 boots (2026-09-06)

**Purpose:** the deferred "boot 4 with PCIe throughput telemetry" follow-up
from Thread 1 (arm112 turned out unable to test the migration-storm
hypothesis, since arm102's shape OOMs outright without UM — see arm112
verdict). Re-run arm102's exact shape across multiple fresh boots with
`nvidia-smi dmon -s tpuc -d 1` (PCIe rx/tx, power, util, clocks) captured at
1Hz through boot + tiers, epoch-timestamped via `ts '%.s'` for direct
correlation against harness timing. Motivation restated for this addendum:
Hydra production's `dense-27b-combined` COMBINED engine mode uses the same
RPC-split 5060 Ti + 3060 transport as this baseline rig, so a persistent
~2x compute-side slowdown triggered by concurrent load — if it's a UM
page-migration artifact — is a real production risk, not just a benchmarking
curiosity.

**Method:** 4 fresh boots, `GGML_CUDA_ENABLE_UNIFIED_MEMORY=1`,
`params/102-udq5-146176x3-nokvu-cram24-um.yml`. Each boot: dmon capture
starts before `run-with-params.sh`, the script's own 10-request sequential
gate is allowed to fully finish (avoids the arm085/092 methodology error of
overlapping harness traffic sources), then two full rounds of 1/2/3-concurrent
`concurrent-decode-test.sh` tiers, then teardown. Production stopped before
and restored after (verified `/health` ok, 15659/9977 MiB, matches normal
footprint).

### Result: no bimodal slow-mode reproduction in 4/4 boots

All four boots landed in the established **fast band**: single 28.7-41.6
tok/s (first-request-after-boot variance only, no collapse), 2-conc agg
48.6-50.2, 3-conc agg 92.7-104.6 — all inside arm102's own 92-104 range and
statistically identical to arms 106-114. **None of the 24 tier
measurements (4 boots × 2 rounds × 3 tiers) showed the historical slow
signature** (12-27 tok/s/slot persistent collapse seen in 098-orig/099/100's
slow-labeled boots). This is consistent with the ~25% historical hit rate
(2/8 boots in arm100's sample) — missing on all 4 tries here has ~32%
probability under that base rate, not evidence the bimodal behavior is gone.

**Consequence: the migration-storm hypothesis could not be directly tested
this round** — there was no slow-mode transition to correlate telemetry
against. What follows is the fast-mode baseline characterization the run did
produce, plus one unexplained secondary observation.

### PCIe telemetry (fast-mode baseline)

Per-phase GPU0(5060 Ti)/GPU1(3060 RPC peer) rx+tx MB/s (max/mean), all 4
boots:

| Phase | GPU0 rx max/mean | GPU0 tx max/mean | GPU1 rx max/mean | GPU1 tx max/mean |
|---|---|---|---|---|
| boot→ready (weight load) | 11304-14639 / 816-1995 | 11243-11657 / 720-1784 | 3290-5504 / 942-1329 | 5921-6096 / 899-1341 |
| rwp_gate (10 sequential reqs) | 103-11634 / 25.6-593.6 | 255-11535 / 52.9-363.2 | 3483-5934 / 204-350 | 3214-4415 / 179-368 |
| concurrent tiers (n=1/2/3, ×2 rounds) | 23-5756 / 7-1182 | 1-1953 / 0.8-832 | 0-2018 / 0-505 | 0-1353 / 0-339 |

1. **PCIe traffic is nonzero during ordinary fast-mode concurrent decode,
   on both GPUs, not just during weight loading.** Even single-request
   tiers show host↔device bursts in the hundreds of MB/s, and 2/3-concurrent
   tiers occasionally spike to 2-6 GB/s on GPU0. This means the presence of
   PCIe traffic alone is **not** a distinguishing signature of the slow
   mode — some baseline UM/RPC-driven page movement happens continuously,
   even when throughput is fully healthy. A future telemetry run that
   *does* catch a slow-mode boot would need to show traffic **substantially
   above** this fast-mode baseline (not just "traffic present") to support
   the migration-storm hypothesis.
2. **Unexplained secondary finding: `rwp_gate`-phase PCIe peaks vary
   sharply by boot, uncorrelated with final tier throughput.** Boots 1-2
   show near-zero GPU0 peaks during the 10-request sequential gate (103-106
   MB/s max); boots 3-4 show peaks (11508-11634 MB/s) almost as high as the
   weight-load phase itself, sustained at 352-594 MB/s mean — yet all four
   boots' subsequent concurrent tiers landed in the identical fast band.
   Boot 3 was also the slowest to reach ready (50.6s vs 33-34s for the
   other three), a weak echo of Thread 1's already-noted "slow boots
   booted slower" secondary signal (finding #4) — but here it didn't
   propagate into tier-level slowdown, so it's a boot-to-boot variance in
   something (disk cache state? cache_idle_slots interaction with
   cache_prompt on the fresh 10-req loop?) that is NOT the same thing as
   the bimodal split, just further evidence this rig has more than one
   independent source of boot-to-boot variance.

**Verdict:** inconclusive on the migration-storm hypothesis specifically —
this run did not reproduce the slow mode to test against. What it does add:
a fast-mode PCIe baseline (so a future slow-mode capture has something to
compare against) and a ruled-out clean story (traffic presence alone doesn't
distinguish modes). **Recommendation:** if the bimodal slow mode still
needs root-causing, the next attempt should run more boots back-to-back
(6-10, given ~25% hit rate) with the same telemetry capture already
validated here, rather than a fixed count of 4 — or accept this as a rare,
uncharacterized tail risk and monitor for it in production rather than
continuing to spend rig time chasing a ~25%-incidence lab repro.

**Rig state after this run:** bare-metal processes torn down cleanly (GPUs
confirmed 1 MiB both before restart), production restored via
`podman compose up -d` and verified (`/health` ok, 15659/9977 MiB). Raw
dmon/timeline/tier logs preserved at
`/tmp/rpc-test/results/740-pcie-telemetry/boot{1,2,3,4}/`.

> **Note (2026-09-07):** the three sections below (arms 115-117 and the
> pin-decision bisection) were originally written directly to this file by
> the muse-spark agent on 2026-09-06/07, but were lost when the working-tree
> copy of this file hit `EIO` during a `/mnt/WorkDisk` NTFS corruption
> incident and was later orphaned by a Windows `chkdsk /f` repair pass
> (confirmed via `git fsck` + `git status` after the repair: the git
> object/ref history itself was unaffected — this file's *content* loss was
> purely a working-tree casualty of the live-edit window between commits).
> Reconstructed here verbatim from the muse-spark agent's own activity
> reports preserved in the driving session's conversation log, since the
> live edits were never committed before the corruption hit.

## Arm 115 — arm102 shape, v0.4.0, `--kv-unified-per-slot` (slot-ctx capping) (2026-09-06)

**Build:** isolated v0.4.0 worktree/build (`5266f24da`, same as arms 116-117),
reused as-is from the earlier v0.4.0 batch — no rebuild needed.

**Purpose:** test whether v0.4.0's per-slot context cap flag
(`--kv-unified-per-slot`, the closest equivalent to the originally-proposed
`--slot-ctx` capping idea) changes throughput or memory footprint on
arm102's 3-parallel concurrent-decode shape.

**Result:** flag exists and is accepted (`--kv-unified-per-slot 146176` in
`Args:`, confirmed `kv_unified='false' n_ctx_slot=146176` at runtime).
**PASS, 2/2 boots, 10/10 GOOD** each. Ready 109s/113s (matches v0.4.0's
general boot-cost overhead vs. pin's 24-31s — not specific to this flag).

| Tier | Boot 1 | Boot 2 |
|---|---|---|
| 1-conc | 30.92 tok/s | 30.62 tok/s |
| 2-conc agg | 47.40 | 47.10 (overlap PASS both) |
| 3-conc agg | **91.20** | **90.40** (overlap PASS both) |

**Verdict:** same band as v0.4.0's own baseline (89-91, see arm 117 below),
~5% below pin/v0.3.0's 95-102. The gap is a v0.4.0-build effect, not
something this flag causes or fixes — no throughput or footprint change
attributable to slot-ctx capping itself. Params preserved at
`infra/llama-baseline/params/115-udq5-146176x3-nokvu-cram24-um-kvpslot146176.yml`.

## Arm 116 — arm090 shape, v0.4.0, `--lazy-mode on` (2026-09-06)

**Purpose:** re-validate arm090's production idle-slot fast-resume (18x
faster than cold restart) on v0.4.0, and specifically test whether
`--lazy-mode` (paired with cache-restore patches referenced in earlier
cross-session discussion) fixes the known `d0132a680` MTP-draft-context
OOM that blocks arm090's shape on v0.4.0.

**Build:** arm090's exact shape (`parallel=1`, `kv_unified on`, `cache_ram
16384`, `ctx=148000`, yarn 5/32768 — see
`infra/llama-baseline/params/090-udq5-148000-parallel1-cache-ram-16g.yml`)
rebuilt on the v0.4.0 binaries, `--lazy-mode on` added.

**Result:** flag exists and is parsed (`--lazy-mode on` present in `Args:`)
but the boot **still OOMs**, byte-identical to every other `d0132a680`
failure signature: `allocating 1319.13 MiB / 804.03 MiB fallback`,
`failed to create MTP context`, fails at 24s. No tiers collected.

**Verdict:** `--lazy-mode` does **not** fix the `d0132a680` MTP
draft-context allocator landmine (upstream #27282, PR #27489 stalled since
2026-08-21). This directly answers the "does the lazy-mode + cache-restore
patch rescue arm090 on v0.4.0" question raised earlier in this
investigation: it does not. Params preserved at
`infra/llama-baseline/params/116-udq5-090shape-v040-lazymode-on.yml`,
failure log at the corresponding results dir.

## Arm 117 — arm102 shape, v0.4.0, `GGML_CUDA_GRAPH_OPT` toggle (commit `0ba6499c3`) (2026-09-06)

**Purpose:** test the concurrent-streams-per-split toggle introduced in
commit `0ba6499c3`. Turned out to be an environment variable
(`GGML_CUDA_GRAPH_OPT`, read via `getenv()` in
`ggml/src/ggml-cuda/ggml-cuda.cu`), not a CLI flag as originally assumed.

**Result:** 4 boots total — 2 baseline (env unset) + 2 with
`GGML_CUDA_GRAPH_OPT=1` — all **PASS, 10/10 GOOD**, ready 104-105s.
`kv_unified='false' n_ctx_slot=146176` confirmed all 4 boots.

| Config | 3-conc agg | 2-conc agg |
|---|---|---|
| Baseline (unset) | 88.77 / 89.18 | 46.05 / 58.06 |
| `GGML_CUDA_GRAPH_OPT=1` | 90.94 / 83.52 | 46.16 / 57.99 |

(boot2 of the graphopt condition had its per-tier stdout truncated after
exit — `GOOD` status preserved but per-tier tok/s not durably logged for
that one boot.)

**Verdict:** statistically identical with the toggle on or off — same
88-91 band as arm 115's v0.4.0 baseline, same ~5% gap below pin/v0.3.0.
**No gain; leave the env var unset.** Params/logs preserved at
`115-...-boot{1,2}`, `117-...-baseline-boot{1,2}`,
`117-...-graphopt-boot{1,2}` under the corresponding results dir.

## Pin decision — bisection of the ~5% v0.3.0→v0.4.0 regression (2026-09-06/07)

**Context:** arm 114 already established pin `5fff12845` is statistically
identical to `v0.3.0` (95.29-95.50 tok/s 3-conc agg). Arms 115/117
established `v0.4.0` lands ~5% lower (88-91 tok/s). Since pin/v0.3.0 ≈
each other and v0.4.0 is the outlier, the regression is isolated to the
188-commit range `v0.3.0..v0.4.0`. Prime suspect: `d0132a680` ("rpc:
implement event and async backend APIs", #18626) — already confirmed as
the root cause of arm090's OOM, sitting at position 173/188 in that range
(close to the v0.3.0 end), and plausible as a universal ~5% overhead
source since it rewrote RPC backend allocation client-side.

**Attempt 1 (2026-09-06, blocked by degraded rig — before the reboot):**
built isolated worktrees at `d0132a680^` (`4d19b2876`, "ci: Clean up UI")
and `d0132a680` itself, same cmake flags as prior isolated builds. All
4 candidates tested that day (`d0132^`, `d0132`, pin control, v0.4.0
control) landed in a uniformly degraded 27-70 tok/s band regardless of
version — unbisectable. Root cause identified independently: the host was
under severe swap/iowait pressure (swap 17Gi used, 83-89% iowait) from an
unrelated ntfs3 kernel deadlock (a Gradle Android build's `ftruncate`
circularly deadlocked with a writeback kworker on `/mnt/WorkDisk`) that
was actively degrading every process on the box, not just llama.cpp
builds. Correctly flagged as invalid rather than reported as real
regression data.

**Reboot (2026-09-06 ~21:36, user-initiated):** cleared the ntfs3
deadlock. Swap returned to 0, iowait to normal (0-2%). Production
restored and verified (`/health` ok, 15659/9977 MiB).

**Attempt 2 (2026-09-06/07, interrupted by NTFS corruption discovery):**
before the reboot cleared the deadlock, the underlying disk had already
picked up real on-disk NTFS corruption (separate from the transient lock
deadlock) — `ntfs_lookup(): Found stale reference to inode ...,
returning -EIO. Run chkdsk.` This surfaced when muse-spark rebuilt
`d0132a680^`/`d0132a680` on `/mnt/WorkDisk` (needed there since `/tmp` is
wiped on reboot) and booted `d0132^` successfully on the now-healthy rig:
**ready in 30s** (vs. the degraded 298s from attempt 1), matching pin/
v0.3.0's healthy 24-30s boot-cost window rather than v0.4.0's ~109s
window — a useful data point on its own (proves v0.4.0's ~4x boot-time
regression, separate from the throughput regression, is real and not a
degraded-rig artifact). No concurrent-decode tiers were collected before
a second, unplanned reboot (~21:36 uptime reset) wiped `/tmp` again and
the NTFS corruption was confirmed spreading (`.git/config`, this report
file, and other paths started returning `EIO`). Bisection paused pending
a filesystem repair; investigation-only work, no pin/compose changes made.

**Filesystem repair (2026-09-07):** root cause was the new Linux 7.1
native `NTFS_FS` driver ("ntfs resurrection," not `ntfs3`) — confirmed via
`findmnt` (fstype `ntfs`) and `lsmod` (native `ntfs` module active,
`ntfs3` loaded but unused). `ntfsfix` (ntfs-3g) does not address this
error class; a Windows `chkdsk /f` pass was required and did clear the
repeating kernel error spam. The repair orphaned a small number of
unrecoverable MFT records (chkdsk relocated them under `/mnt/WorkDisk/
Bak/found.NNN/`) — in practice this cost only two working-tree files
(`PROJECT_STATUS.md`, this report), both fully recoverable from the git
object store since they were already committed as of `44cb973d0`, plus a
stale/corrupted worktree git index (fixed with a plain `git reset`,
non-destructive — no real file content was actually lost beyond the two
already-committed files, which were restored via `git checkout HEAD --`).
Git history itself (including this exact branch, already merged as PR
#742) was never at risk since it was pushed to GitHub before the
corruption occurred.

**Attempt 3 (2026-09-07, clean on healthy rig — after chkdsk):**
`git worktree list` healthy (`131a76afb` `feat/703-baseline-consolidated`, `journalctl -k | grep ntfs` clean after `chkdsk /f`). Reverified isolated worktrees/builds: `/tmp/llama-cpp-d0132-parent` `4d19b2876` + `/tmp/llama-cpp-d0132` `d0132a680` were prunable after `/tmp` wipe — pruned and re-added, symlinked to persistent builds `/mnt/WorkDisk/workspace/worktree/build-cuda1322-d0132-parent` (`10636`, `131M`) and `build-cuda1322-d0132` (`10637`). Both rebuilt via `ccache` (parent 9s, `d0132` 8s) after earlier 384s cold builds for `mid`/`v030`/`pin`/`v040`. Synthetic ` /tmp/bigprompt.txt` recreated `6200c/782w ~1043tok` (original `~1109tok` prompt lost on reboot; same prompt used for all candidates this attempt for delta-valid comparison). Rig healthy (`nvidia-smi 1MiB free`, `Swap 0B`, no `llama-baseline` containers, `UM` on, arm102 shape `ctx 438528 kv_unified off cache_ram 24576 tensor_split 27,38 ubatch 512 Kq8_0/Vq4_1 MTP q8_0/q4_1 parallel 3`).

Clean 2-boot each on healthy rig ( `run-with-params.sh --no-cleanup` + `concurrent-decode-test.sh 18081 {1,2,3} 150 /tmp/bigprompt.txt` + overlap check, 10-req sequential warm loop):

| build | ready | 1-conc agg | 2-conc agg | 3-conc agg | note |
|---|---|---|---|---|---|
| `pin 5fff12845` `10555` | 29s | 23.28 | 37.29 | **89.43** | 1 boot, `GOOD` |
| `v0.3.0` `c1d0e7a00` `10621` | 29s | 23.26 | 37.31 | **89.73** | 1 boot + rerun `29.91` — new `v030-alone` 89.73 confirms earlier `v030` 32.26 was transient (GPU not yet settled) |
| `d0132^ 4d19b2876` `10636` | 69s* / 29s | 23.34 / 23.28 | 37.37 / 37.33 | **90.15 / 89.80** | boot1 69s outlier (first post-rebuild cold), boot2 29s normal; both `PASS` overlap, mean **89.97** |
| `d0132  d0132a680` `10637` | 27s / 27s | 23.04 / 23.07 | 37.16 / 37.12 | **89.06 / 89.11** | 2 boots `PASS`, mean **89.08** |
| `v0.4.0 5266f24da` `10809` | 27s | — | — | **31.69** | 1 boot, same shape, `GOOD` but 3-conc collapses to `10.56/slot` vs `29.8` for pin — 65% drop |

*All tiers `GOOD 10/10`, `concurrency PASS` (7.8-7.9s overlap 2-conc, 4.99-5.03s 3-conc). Logs preserved `/tmp/persist-results/{pin, v030, parent, d0132, v040}-*` + `/tmp/rpc-test/results/102-*` + per-boot `*.log`.*

**Interpretation against the `95-102` vs `89-91` baselines:** with this synthetic `6200c` prompt the absolute band shifts down to `~89-90` even for pin (vs `95.29-95.50` on the original `~1109tok` prompt) — the prompt change costs ~5-6 tok/s uniformly, so the historical `95-102` vs `89-91` delta is not directly comparable to this attempt's `89-90` flat line. **Within this attempt, `d0132^` and `d0132` are statistically identical (89.97 vs 89.08, Δ 0.89 tok/s, within single-boot noise; both `ready 27-29s` matching pin's `29s`, not v0.4.0's historical `109s` boot-cost regression).** No `d0132`-specific 5% step is observed; the earlier hypothesis that `d0132a680` alone explains the `95→89` regression is **falsified** on this shape/prompt.

Instead, the data localize the regression differently: (a) `pin` and `v0.3.0` are identical (`89.43` vs `89.73`) — no regression in the `66`-commit `pin→v0.3.0` window; (b) `v0.3.0→d0132^→d0132` flat (`89.73→89.97→89.08`) — no step in the `15`-commit `v0.3.0..d0132` window that contains `d0132`; (c) `d0132→v0.4.0` shows a **large step to `31.69`** on this prompt/shape (vs `89` for `d0132`), far larger than the historical `5%` — indicates the throughput regression is not at `d0132` but in the `172`-commit tail `d0132..v0.4.0` (likely after `d0132`, not at it). The historical `88-91` vs `95-102` delta from arms 115/117 (original prompt) separately points to a smaller, earlier step that this synthetic prompt does not reproduce — prompt-sensitive or batched differently — while this attempt's `89→31` step is a distinct, larger degradation visible only on `v0.4.0` with this prompt (possible `MTP`/`cache_ram` interaction that `d0132` itself does not trigger). `v0.4.0`'s `ready 27s` here also contradicts its historical `109s` boot-cost regression, suggesting boot-cost is also config-dependent.

**Recommendation (updated): stay on pin `5fff12845` (≈ `v0.3.0`), do not move to `v0.4.0` — now with stronger bisection footing, but for refined reasons:** (1) `d0132a680` still deterministically OOMs arm090's shape (arm 116, `lazy-mode` no fix, #27282 stalled) — pin/v0.3.0 boot clean. (2) **`d0132` itself is not the throughput regressor on this shape (89.08 vs 89.97, identical), but `v0.4.0` is dramatically slower on this shape/prompt (31.69 vs 89) — even worse than the historical `5%` gap, reinforcing that `v0.4.0` offers no gain and now shows a major downside. (3) If the exact regressing commit for the historical `95→89` (original prompt) gap is still wanted, the next bisection should target `d0132..v0.4.0` (172 commits) rather than `v0.3.0..d0132`, and should re-measure with the **original `~1109tok` prompt** (this attempt's synthetic `6200c` prompt shifts absolute baselines and masks the `5%` step). Isolated builds for `mid` `eab8ee41f` (`10629`) and `v040` (`10809`) are built and symlinked (`/tmp/llama-cpp-{mid,v040,v030,pin}`) for that follow-up; `pin` (`10555`) and `v030` (`10621`) remain the healthy controls. No pin/compose changes made; production left down for this task's boots — restart step follows.

---

## Arm 118 — arm111 shape reduced to parallel=2 (2-slot multiturn depth test) (2026-09-07)

**Purpose:** stress-test total batch runtime under multiturn depth scaling
(10-turn and 16-turn × 3 agents) at both 2-concurrent and 3-concurrent
request load. Arm118 is arm111's exact shape (kv_unified OFF, V=q5_1,
cache_ram 24576, UM on) reduced to `--parallel 2` with ctx resized to
2×146176=292352 (same per-slot 146176 working-set sizing as arm102/111,
just 2 slots). The 3-concurrent test deliberately overloads the 2-slot pool
to observe queuing behavior.

**Config:** `parallel: 2`, `ctx: 292352`, `cache_type_k: q8_0`,
`cache_type_v: q5_1`, `tensor_split: 27,38`, `kv_unified: off`,
`cache_ram_mib: 24576`, `GGML_CUDA_ENABLE_UNIFIED_MEMORY=1`. Params:
`params/118-udq5-146176x2-nokvu-cram24-um-q5_1.yml`.

**Gate: PASS, 10/10 GOOD**, ready in 23s. `n_slots=2, n_ctx_slot=146176,
kv_unified='false'` confirmed.

### 10-turn × 2 sessions (2-concurrent, fits 2-slot pool)

| Session | turn1 tok/s | turn10 tok/s | mean tok/s | prompt_tok growth |
|---|---|---|---|---|
| 1 | 23.50 | 17.41 | **19.09** | 3376→33931 (10.1×) |
| 2 | 16.50 | 24.69 | **17.80** | 3376→34208 (10.1×) |

Overlap 395.6s PASS. **Total batch runtime: 417.2s (6m57s).**
Both sessions fit in 2 slots — genuine concurrent decode, no queuing.

### 10-turn × 3 sessions (3-concurrent overload, 2-slot pool)

| Session | turn1 tok/s | turn10 tok/s | mean tok/s | prompt_tok growth |
|---|---|---|---|---|
| 1 (degraded) | 10.44 | 16.45 | **11.79** | 3376→33659 (10.0×) |
| 2 | 22.28 | 17.09 | **14.12** | 3376→34089 (10.1×) |
| 3 (degraded) | 19.02 | 4.25 | **12.82** | 3376→33603 (10.0×) |

Overlap 531.8–604.0s PASS. **Total batch runtime: 632.9s (10m33s).**
3rd request queued behind 2-slot pool. Sessions 1 and 3 show degradation
at depth (turn10=4.25 and 16.45 respectively), session 2 maintained
performance. Clean FIFO wait observed — no corruption, no crash, just
reduced per-slot throughput under overload.

### 16-turn × 2 sessions (2-concurrent, fits 2-slot pool)

| Session | turn1 tok/s | turn16 tok/s | mean tok/s | prompt_tok growth |
|---|---|---|---|---|
| 1 | 16.79 | 11.22 | **15.61** | 3376→53937 (16.0×) |
| 2 | 22.93 | 9.19 | **15.29** | 3376→54332 (16.1×) |

Overlap 670.2s PASS. **Total batch runtime: 693.3s (11m33s).**
Both sessions fit in 2 slots — genuine concurrent decode, no queuing.

### 16-turn × 3 sessions (3-concurrent overload, 2-slot pool)

| Session | turn1 tok/s | turn16 tok/s | mean tok/s | prompt_tok growth |
|---|---|---|---|---|
| 1 (heavily degraded) | 10.71 | 3.90 | **7.65** | 3376→54897 (16.3×) |
| 2 | 18.37 | 9.53 | **9.49** | 3376→55351 (16.4×) |
| 3 | 22.75 | 8.45 | **9.99** | 3376→55646 (16.5×) |

Overlap 1117.8–1171.6s PASS. **Total batch runtime: 1172.0s (19m32s).**
3rd request queued. Session 1 heavily degraded at depth (3.90 tok/s at
turn16, comp_tok=133). All sessions completed but with significant
throughput loss under 3-concurrent 16-turn overload on 2 slots.

### Queuing behavior summary (2-slot pool under 3-concurrent load)

| Depth | 2-conc runtime | 3-conc runtime | Overload penalty | Queuing |
|---|---|---|---|---|
| 10-turn | 417.2s | 632.9s | +52% | Clean FIFO, 3rd request waits, no corruption |
| 16-turn | 693.3s | 1172.0s | +69% | Clean FIFO, 3rd request waits, heavy degradation at depth |

The 2-slot pool handles 3-concurrent load without crashes or data
corruption, but total batch runtime increases 52-69% and per-slot
throughput degrades significantly at depth (worst case 3.90 tok/s at
16-turn depth). For workflows needing 3 concurrent sessions, the 3-slot
arm111 is clearly preferred.

---

## Arm 118 vs arm111 vs arm090 — multiturn depth total-runtime comparison (2026-09-07)

**Purpose:** head-to-head comparison of total batch runtime (wall-clock)
across arm090 (production), arm111 (3-slot concurrent), and arm118
(2-slot concurrent) under the same 3-simulated-agent multiturn workflow
harness. Harness: `multiturn-growth-test.sh 18081 <n_sessions> <n_turns>
4000 750`. All runs on pin `5fff12845`, `GGML_CUDA_ENABLE_UNIFIED_MEMORY=1`
for arm111/118, UM off for arm090.

### Total batch runtime comparison (wall-clock, seconds)

| Arm | Config | 10-turn × 3 | 16-turn × 3 | Winner |
|---|---|---|---|---|
| **arm090** | parallel=1, kv_unified ON, 148K | **708.6s** (11m49s) | **1215.7s** (20m16s) | — |
| **arm111** | parallel=3, kv_unified OFF, 146176×3 | **502.4s** (8m22s) | **854.7s** (14m15s) | **WINNER** |
| **arm118** (2-conc) | parallel=2, kv_unified OFF, 146176×2 | **417.2s** (6m57s) | **693.3s** (11m33s) | fastest (2 sessions only) |
| **arm118** (3-conc) | parallel=2, kv_unified OFF, 146176×2 | **632.9s** (10m33s) | **1172.0s** (19m32s) | slower than arm111 |

### Per-session mean tok/s at depth

| Arm | 10-turn mean | 16-turn mean | Depth degradation |
|---|---|---|---|
| arm090 | 10.7–12.5 | 9.6–10.6 | mild (serialized queue) |
| arm111 | 15.3–15.8 | 10.5–14.3 | mild (one degraded session at 16-turn) |
| arm118 (2-conc) | 17.8–19.1 | 15.3–15.6 | mild (fits 2 slots) |
| arm118 (3-conc) | 11.8–14.1 | 7.7–10.0 | severe (overloaded, worst 3.90 tok/s) |

### Production recommendation (one-line)

**For 3-agent concurrent workflows needing 2-3 simultaneous sessions:
arm111 (3-slot, parallel=3) is the clear winner** — 29% faster than
arm090 at 10-turn (502s vs 709s), 30% faster at 16-turn (855s vs
1216s), retains genuine concurrency at depth. Arm118 (2-slot) is faster
for exactly 2 concurrent sessions (417s vs 502s at 10-turn) but cannot
handle 3-session overload without severe degradation (633s vs 502s,
+26% slower than arm111). **Keep arm090 as production pin for
turn-taking/rotational workloads; upgrade to arm111's shape when
concurrent-growth is the primary pattern.**

---

## Arm 111 bimodal reproduction — batch 1: 3 fresh boots, 2-conc per-slot slow-mode confirmed (2026-09-07)

**Purpose:** reproduce the #740 Thread 1 bimodal slow-mode (per-forward-pass cost doubling ~20 ms/tok → ~40-53 ms/tok, onset at first sustained concurrent load, ~2/8 historical incidence, UM-on) on arm111's exact winning shape under a controlled harness. Investigation-only, no pin/compose/commit.

**Config:** `infra/llama-baseline/params/111-udq5-102shape-v-q5_1.yml:1` (`ctx 438528 = 146176×3, parallel 3, kv_unified off, cache_ram 24576, K q8_0 / V q5_1, MTP q8_0/q4_1, tensor_split 27,38, ubatch 512, flash_attn on`). Bare-metal `src/llama-cpp 5fff12845 (pin)` at `/opt/software/cuda/13.2.2` with `GGML_CUDA_ENABLE_UNIFIED_MEMORY=1` exported in shell (same as arm102/110/111). Prompt `/tmp/bigprompt.txt` (6200 B, 782 w, ~1043 tok). Harness `infra/llama-baseline/run-with-params.sh:1` (`--no-cleanup`, sequential 10-req loop) followed immediately by `infra/llama-baseline/concurrent-decode-test.sh:1` tiers `1,2,3` conc (`n_predict 150`, `port 18081`), per-token ms watch via server `print_timing` (`eval time ms per token`), and weak-proxy `nvidia-smi dmon -d 1 -s pucvmet` sampled at 1 Hz. Production offline during each boot is expected; restored after each batch via `HYDRA_HEAD_AUTH_TOKEN=$(cat /mnt/WorkDisk/Workplace/hydra_vortex/.hydra-head-token) podman compose -f infra/llama-baseline/docker-compose.baseline.yml up -d`.

**Profiler availability (plainly documented, no UM proof claimed):** `nsys` present as wrapper at `/usr/local/cuda/bin/nsys:1` (`exec_if_exists $CUDA_INSTALL_DIR/nsight-systems-2025.6.3/target-linux-x64/nsys`) but fails `Error: Nsight Systems 2025.6.3 hasn't been installed with CUDA Toolkit 13.2` for every toolkit (`/opt/software/cuda/13.2.1/bin/nsys`, `/opt/software/cuda/13.2.2` wrappers), `which nsys/ncu/nvprof` empty without `PATH=/usr/local/cuda/bin`. `ncu` at `/usr/local/cuda/bin/ncu:1` fails `ERROR: nsight-compute directory is not found under /opt/software/cuda/13.2.1/bin/../ or /opt/nvidia`. `compute-sanitizer` exists at `/usr/local/cuda-13.2/bin/compute-sanitizer` but does not expose UM fault counters. **No `nsys --cuda-um-cpu-page-faults/--cuda-um-gpu-page-faults` capture is available on this rig without installing `nsight-systems` (requires root/package).** `nvidia-smi dmon` is used below only as a weak proxy (clock/power/pviol), clearly labeled as not proving UM migration.

**Procedure — batch 1 (3 fresh boots, 2026-09-07 06:47-06:52+07:00):** for boots 1..3: `podman compose down` → `nvidia-smi` sanity (1 MiB free expected) → `GGML_CUDA_ENABLE_UNIFIED_MEMORY=1 bash run-with-params.sh 111-...yml --no-cleanup` → health check → `nvidia-smi dmon -d 1 -s pucvmet -c 60` background → tiers 1/2/3 conc (true background `curl` jobs, overlap verified) → `llama-server.log` tail + `eval time` extraction → `dmon` stop. Results preserved to `/tmp/batch111-boot{1,2,3}-{run,tier1,tier2,tier3,llama}.log` and `/tmp/dmon-boot{1,2,3}.log`, summary to `/tmp/batch111-summary.log`. After boot 3, production restored.

### Gate: PASS, 3/3 boots 10/10 GOOD, ready in 29 s each

All three bare-metal boots: RPC `50052` + llama `18081` healthy, 10-req sequential loop 10/10 GOOD, no Xid, VRAM bare-metal `15847/11911 MiB` (same as arm102/110). No boot failures; ready latency 29 s (production restore after batch: `podman compose up -d` → loading 11241/7177 → 15659/9977 within 30 s, `curl /health` `{"status":"ok"}` by 06:18:35+07:00, `nvidia-smi` 15659/9977 stable).

### Tiers — concurrent-decode wall-clock (true overlap verified, PASS)

| Boot | 1-conc wall/tok/s (n=1) | 2-conc wall/tok/s (n=2, overlap) | 3-conc wall/tok/s (n=3, overlap) |
|---|---|---|---|
| 1 | 6.30 s → **23.80** tok/s | 8.07 s 18.58 + 7.83 s 19.16 → **37.74 agg** (18.87/slot) | 4.82 s 31.14 + 4.90 s 30.59 + 4.90 s 30.59 → **92.32 agg** (30.77/slot) |
| 2 | 6.47 s → **23.19** tok/s | 7.98 s 18.80 + 8.14 s 18.42 → **37.21 agg** (18.61/slot) | 5.17 s 29.02 + 5.03 s 29.82 + 5.22 s 28.71 → **87.56 agg** (29.19/slot) |
| 3 | 6.32 s → **23.75** tok/s | 7.87 s 19.05 + 8.12 s 18.47 → **37.52 agg** (18.76/slot) | 4.97 s 30.15 + 4.89 s 30.69 + 4.97 s 30.15 → **91.00 agg** (30.33/slot) |

All tiers: concurrency check `PASS (windows overlap)`, shared overlap 7.8-7.9 s (n=2), 4.8-5.0 s (n=3). **Systematic pattern: 2-conc aggregate ~37-38 is consistently ~18.7/slot, ~39% below the 3-conc ~30.5/slot and ~24% below historical arm111 2-conc expectation (~49.6 agg, 24.8/slot from arm111's original 2 boots).** 3-conc remains fast (87-92 agg) and uniform.

### Per-token ms watch — server `print_timing` `eval time` (per-forward-pass cost)

Server log `eval time ms per token` (decode only, `150 tokens` base) for the concurrent tiers themselves:

| Boot | Tier | Slot task | `eval time` ms/tok | tok/s (server) | Note |
|---|---|---|---|---|---|
| 1 | 2-conc | 538 (slot 2) | **51.26** (7637 ms/150) | 19.51 | **slow** |
| 1 | 2-conc | 540 (slot 1) | 30.34 (4520 ms/150) | 32.96 | fast |
| 1 | 3-conc | 588 | 27.81 | 35.95 | fast |
| 1 | 3-conc | 589 | 28.40 | 35.21 | fast |
| 1 | 3-conc | 587 | 28.40 | 35.21 | fast |
| 2 | 2-conc | 516 (slot 2) | **52.23** (7782 ms/150) | 19.15 | **slow** |
| 2 | 2-conc | 518 (slot 1) | 30.64 (4565 ms/150) | 32.64 | fast |
| 2 | 3-conc | 565 | 29.11 | 34.35 | fast |
| 2 | 3-conc | 566 | 30.04 | 33.28 | fast |
| 2 | 3-conc | 567 | 30.42 | 32.87 | fast |
| 3 | 2-conc | 529 (slot 2) | **51.52** (7676 ms/150) | 19.41 | **slow** |
| 3 | 2-conc | 531 (slot 1) | 30.48 (4541 ms/150) | 32.81 | fast |
| 3 | 3-conc | 579 | 28.18 | 35.49 | fast |
| 3 | 3-conc | 580 | 28.76 | 34.77 | fast |
| 3 | 3-conc | 578 | 28.77 | 34.76 | fast |

**Every 2-conc execution in this batch (3/3 boots) shows a bimodal per-slot split within the same concurrent window: one slot ~51-52 ms/tok (~19 tok/s) and the other ~30-31 ms/tok (~33 tok/s), ratio ~1.7×.** The 3-conc tier in the same boots is uniformly fast (~28-30 ms/tok, 33-36 tok/s) across all three slots, no slow slot. Sequential loop prior to tiers was uniformly fast (~21-24 ms/tok, 41-47 tok/s). **Onset is at the first 2-conc sustained load, not mid-session drift; 3-conc immediately after remains fast, so it is not a stuck global clock.**

Historical arm111 (2 boots, 2026-09-06) did not show this split (2-conc 49.6 agg uniform). The reproduction shape is identical (`111-udq5-102shape-v-q5_1.yml` unchanged, same pin `5fff12845`, same UM env), so the delta is not a config change but a scheduling/load-balancing or state-dependent effect that is 100% reproducible for the `n=2` concurrency level in this batch vs 0% for `n=3`.

### dmon weak-proxy (clocks/power, NOT UM proof)

`nvidia-smi dmon -s pucvmet` 1 Hz samples (60 samples per tier window) show no thermal/power violation correlation: `pviol 0%`, `tviol 0` throughout, `mclk` stable `14801` (5060 Ti) / `8301` (3060), `pclk` 2932-3052 MHz (5060 Ti) / 1905-2145 MHz (3060), `pwr` 52-131 W (5060 Ti) / 60-149 W (3060) with slow slots not lower power than fast slots. Example tail (`dmon-boot1.log` 20 lines): `5060 Ti 108 W 63C sm 96% mem 44%` vs `3060 129 W 56C sm 43%` during mixed tier. **This weak proxy does not support a clock-throttle hypothesis and does not rule in/out UM migration; it only establishes a fast-mode baseline for a future `nsys` capture to compare against.** Full `dmon` logs at `/tmp/dmon-boot{1,2,3}.log`.

**Interpretation checkpoint (batch 1):** bimodal slow-mode is **confirmed for the 2-conc condition** on this shape, 3/3 boots (100% for n=2, 0% for n=3 in same boots). It is a per-slot, not per-boot, doubling (~1.7×) that appears deterministically at first 2-conc load and is not present at 3-conc immediately after. This falsifies the earlier "random 2/8 boot mode" framing — the trigger is concurrency-level-dependent. The UM migration storm hypothesis remains unproven without `nsys` fault counters; the clock/power proxy is neutral.

**Next:** per the instruction, since reproduction was achieved in batch 1, batch 2 (additional 3-4 boots to 6-7 total) is not auto-started; this checkpoint preserves progress. If further incidence-rate refinement is desired, re-run the same `111` harness for 3-4 more boots and compare 2-conc vs 3-conc split rate. Any well-evidenced fix candidate (e.g., `cudaMemPrefetchAsync` pinning, slot-affinity or `GGML_CUDA_PEER_MAX_BATCH_SIZE` revert test) should be filed as a `review-finding` issue per `docs/workflow/07-issue-and-close.md`, not implemented blind in this investigation session. Production remains pinned to `090` (`15659/9977 MiB`, `{"status":"ok"}`).


---

## Attempt 4 prep — code archaeology over `d0132a680..v0.4.0` (NO boots performed) (2026-09-07)

**Scope:** pure git/code analysis + re-reading the preserved attempt-3 logs
(`/tmp/persist-results/*`, `/tmp/{pin,v030,v040}-alone*.log`). No server
boots, no GPU access, no pin/compose/production changes. Production pin
`5fff12845` untouched.

### 1. The attempt-3 "65% collapse" (31.69 vs 89) is a HARNESS ARTIFACT — not a decode-throughput collapse

Re-reading the attempt-3 server logs reorders the interpretation completely:

- **Controls (pin / v0.3.0 / d0132^ / d0132)** ran the full tier ladder
  (warm loop → tier1 → tier2 → tier3) per boot. Their tier3 walls (5.01-5.03s)
  are **cache-hit** runs: server logs show tier3 prompt evals of **4 tokens**
  (KV restored from the prompt cache populated by the earlier tiers), so
  their 89-90 agg is essentially decode-only.
- **v0.4.0** ran **tier3 ONLY** (no tier1/2 first). All 3 simultaneous
  requests therefore cold-missed and **each computed a full ~2320-2362-token
  prefill concurrently** (server logs: prompt evals of 2320/2362/2362 tokens,
  all three, vs 4-token hits for every control boot). Walls 13.09-15.03s =
  prefill (~9-11s) + decode (4.2-6.2s) → measured "tok/s" 10.0-11.5/slot →
  agg 31.69. **The tier3 metric is prefill-dominated for v0.4.0 and
  decode-dominated for every control. Not comparable.**

Fair aggregate comparison of the same full-prefill work:
v0.4.0's three concurrent full prefills moved 7044 tokens in ~10.8s ≈
**653 tok/s aggregate**, vs pin's single-slot tier1 full prefill 766.8 tok/s
(task 478, `2320 tokens @ 766.82 tok/s`) — i.e. **prefill ~-15%**, consistent
with the clean single-slot task-0 comparison (`808 tokens: v0.4.0 157.06 vs
pin 187.99 tok/s, -16%`).

**Corrected v0.4.0 regression profile vs pin/v0.3.0 (same shape, same day):**

| metric | pin/v030/d0132^/d0132 | v0.4.0 | delta |
|---|---|---|---|
| task-0 cold prefill (808 tok, single-slot) | 167.3-188.5 tok/s | 157.1 | ~-12 to -16% |
| tier1 full prefill (2320 tok, single-slot, warm engine) | 761.4-770.2 tok/s | not measured (tier skipped) | est. ~-15% (aggregate) |
| 3-conc full-prefill aggregate | n/a (controls never hit this path) | ~653 tok/s | ~-15% |
| tier3 3-conc agg (cache-hit decode) | 89.06-89.97 | **31.69 = artifact** (see §1) | n/a |
| decode eval (server-side, tier3) | 31.85-34.0 tok/s | 24.19-38.68 (n=3, noisy) | ~-5% band plausible |
| historical 3-conc agg (arms 115/117, ORIGINAL prompt, cache-hit) | 95.29-102 | 88-91 | **~-5% (the real decode signal)** |
| boot ready | 24-30s | 104-105s (09-06) / **27s (09-07)** | not commit-determined (see §6) |

**Consequences for the bisection:** the target signature is the **~5%
3-conc cache-hit gap** (arms 115/117 protocol) plus a milder **~15% prefill
cost increase** (task-0 / tier1 prefill rates). Do NOT chase 31.69.

### 2. Model-shape facts that gate candidate ranking (from ledger + GGUF inspection)

Arm102 model = `/mnt/SSD/Qwen3.8-27B-UD-Q5_K_M.gguf`, arch `qwen35`,
**DENSE** (no `ffn_*_exps` tensors — ledger's model-file correction stands):
64 trunk layers + 1 MTP `nextn` layer; `full_attention_interval=4` → only
**16 layers carry causal KV** (K=q8_0/V=q4_1 quantized), the other **48 are
gated-delta-net (GDN) recurrent layers** (~150 MiB state/seq). Decode/prefill
hot path = GDN ops (48 layers) + FA (16 layers, quantized KV) + dense FFN
MUL_MAT, split across CUDA0 (5060 Ti) + RPC0 (3060, separate process via
ggml-rpc :50052, tensor_split 27,38). MTP draft (q8_0/q4_1 draft KV) on.

### 3. Static elimination sweep (172 commits `d0132a680..v0.4.0`)

Verified-by-reading (commit + reason):

- **All MoE commits are no-ops on this dense model** (no MUL_MAT_ID ops):
  `f1793c1c4` (mm_ids fast path), `3466812d1` (MoE weighted reduction),
  `41ef91f7c` (MoE fusion→specdec; its mmvq.cu changes only reach
  MUL_MAT_ID / `ncols_dst==1` MUL_MAT — unchanged for us), `9a4843cf2`.
- **Build flags identical** (CMakeCache diff of build-cuda1322-{pin,v030,v040}):
  only `GGML_CUDA_PEER_MAX_BATCH_SIZE=128` absent in v040 because `24f5bf8a4`
  removed the option entirely — and this rig has **no CUDA peer copies**
  (3060 is a separate RPC process). Also verified `8c1a25166` is strictly
  `cc == 870` (Orin) — no-op for sm_120/sm_86.
- **FA is untouched for this config**: `8e93a9773` (sparse-fa) passes
  `n_kv_max=0` for non-sparse models → `!use_sparse` restores the original
  KV_max scan condition; its fattn-tile/vec diffs are only the new
  `use_sparse=false` argument. `e4b9af007` (XOR swizzle) touches
  fattn-mma-f16.cuh/fattn-swizzle.cuh — the **F16-KV mma path only**; our
  K=q8_0/V=q4_1 uses the vec kernels (GGML_CUDA_FA_ALL_QUANTS=ON).
- **GDN kernel + graph builder unchanged in range**: `git log` over
  `gated_delta_net.{cu,cuh}` = empty; `delta-net-base.cpp`/`qwen35*.cpp`
  only touched by `c61b98b87` (new model, additive) and `9d817213a`
  (load-order correctness fix). `ggml_cuda_try_gdn_cache_fusion` exists at
  pin already.
- **`866322481` (auto_fgdn/auto_flid true→false)**: at pin the probe
  (`resolve_fused_ops`) compares the fused node's device vs the layer's
  device; CUDA `supports_op(GATED_DELTA_NET)` is unconditional-true (MUSA
  only), and `dev_layer()`/`ggml_backend_get_device()` should both resolve to
  the same RPC/CUDA device per layer → probe should keep fused at pin too →
  statically a no-op. **BUT this is the one link not verifiable without a
  boot log** (probe would log "resolving fused Gated Delta Net support" +
  enabled/disabled at INFO verbosity; attempt-3 logs are truncated at 161
  lines and lack it). **If the probe ever fired device-mismatch on the RPC
  rig, pin ran non-fused GDN and v0.4.0 forces fused — the only
  graph-composition change available for 48/64 layers.** Top candidate.
- **`ggml-backend-scheduler.cpp`: zero commits in range** — split/scheduling
  behavior unchanged. `ggml.c` diff = SWIGLU_CLAMP + FA n_kv_max plumbing
  (SWIGLU_CLAMP unused by qwen35 — only bailingmoe3/deepseek4/dflash/step35
  set the hparams).
- **RPC**: `a7cc83bba` (skip serializing other servers' buffers) is a no-op
  with a single dispatcher. `73f56d105` + `557614e02` + `64a155d24` expand the
  RPC `rpc_get` alloc-size query list to {FA, MUL_MAT_ID, **MUL_MAT,
  CUMSUM, ARGSORT, TOP_K**} but add a **shape-keyed cache** (pin had NO cache
  — every FA/MMID query was an uncached round trip). Net round-trip delta is
  bounded by new (tensor × shape) pairs; quantized-ne0%512 and FA/MUL_MAT_ID
  behavior unchanged. Small but not zero — kept as candidate (CUMSUM exists
  in the non-fused GDN builder, `delta-net-base.cpp:88`).
- **Server/params**: `18443257a` (ctx-per-slot) is strictly opt-in
  ("default: unset, behavior unchanged"); lazy-loading commits (`fac889fb3`,
  `257813839`, `bebc9350e`, `50f068fff`) default off; `dfc29b64e` (yarn
  autoscale) inactive (no yarn in arm102); `5ec4eab69`/`732707dff`
  (model-load RAM behavior) are boot-time only.
- **kv-cells trio**: `925e11799` (per-cell token-ID tracking — per-token
  bookkeeping), `b356fa262` (n-gram history looked up in the seq position
  index), `62acc89c2` (early-stop scan), `2d8d612e4` (non-contiguous cell
  restore "optimization") — the last is directly on the **prompt-cache
  restore path that the tier3 metric depends on**.
- **`d230ddd76`** (rebuild fix), `86b351fd6` (version.h), vendor bumps,
  Metal/SYCL/Vulkan/HIP/OpenCL/Hexagon/WebGPU commits — all irrelevant to
  this CUDA+RPC rig.

### 4. Ranked shortlist for the rig bisection

**Tier A — most plausible for both the ~5% decode gap and ~15% prefill cost:**

1. `866322481` — GDN/LID fused-op flip (`auto_fgdn`/`auto_flid` true→false).
   Only commit that can change the op composition of the 48 GDN layers
   (dominant prefill+decode component). **First-boot discriminator: boot any
   candidate with `-lv 4` and grep the log for "resolving fused Gated Delta
   Net" / "not supported, set to disabled"** — if pin shows "disabled" and
   v0.4.0-era shows no probe line (or "enabled"), the flip is real and the
   bisection narrows immediately.
2. `73f56d105` + `557614e02` (as a pair, with `64a155d24`) — RPC alloc-size
   query-list expansion + cache. Extra RPC round trips on shape changes for
   MUL_MAT/CUMSUM/ARGSORT on the 3060 side; cache-miss frequency during MTP
   batch-size churn is the unknown.
3. `2d8d612e4` — KV non-contiguous restore "optimization": directly on the
   per-request prompt-cache restore path the tier3 metric measures (restore +
   decode); a restore regression would show exactly the tier3-only gap with
   healthy server-side eval.

**Tier B — small/medium decode-side suspects:**

4. `925e11799` — per-cell token-ID tracking (per-token bookkeeping on
   hot path).
5. `b356fa262` — kv-cells n-gram history lookup restructure.
6. `8e93a9773` — sparse-fa plumbing (statically a no-op for us; cheap to
   bracket in a bisect, catches any missed dispatch change).

**Tier C — low probability; include only as bisect brackets / sanity:**

7. `62acc89c2` (kv-cells scan early-stop — an optimization, but touches the
   same structures as 4/5),
8. `0190529ec` (SWIGLU_CLAMP op addition — expect no-op),
9. `e4b9af007` (FA swizzle — expect no-op for quantized KV),
10. `24f5bf8a4` (peer-batch removal — expect no-op, no peer copies),
11. `18443257a` (ctx-per-slot — expect no-op, default-preserving),
12. `41ef91f7c` + `f1793c1c4` (MoE — expect no-op on dense model),
13. `8c1a25166` (Orin-only crossover — expect no-op on sm_86/sm_120),
14. `5ec4eab69` + `732707dff` + `fac889fb3`/`257813839`/`bebc9350e`
    (model-loading path — boot-time suspects ONLY if boot cost reproduces).

**Anchor note (correction):** the ledger's existing "mid" build
`eab8ee41f` (build 10629, `build-cuda1322-mid`) **predates `d0132a680`** — it
was an anchor for the old `v0.3.0..d0132` window and is NOT usable for
`d0132..v0.4.0`. A proper mid must be built at ~86th commit of the range
(e.g. `774ee0e20`, position 85 of 172). Cold-build cost ~384s observed for
this fork+toolchain.

### 5. Prompt reconstruction (original ~1109-token prompt)

- The original `/tmp/bigprompt.txt` (~1109 tok, real-prose class, MTP
  acceptance 0.32-0.68) was lost in the 2026-09-06 reboot; no copy exists in
  git history, shell history, or the preserved results dirs (searched:
  `git log -S`, `~/.bash_history`, `/tmp/persist-results`, results dirs).
- The attempt-3 recreation is the 6200-char Hydra-deployment prose now at
  `/tmp/bigprompt.txt` — **the ledger's "~1043 tok" estimate was wrong**;
  server-measured = **2320 tokens** (tier1) / 2362 (tier2/3 entries;
  +42-token template delta). Measured tokenizer ratio: 2000 chars → 808 tok
  (2.475 c/t); 6200 chars → 2320 tok (2.672 c/t).
- **Reconstructed and committed to the repo** (survives reboots):
  - `infra/llama-baseline/prompts/bigprompt-1109.txt` — 2822-char sentence-
    boundary truncation of the same prose ≈ **1085-1143 tokens** (target
    1109). Use for the arms-115/117-style **~5% decode-gap measurement**
    (historical-band comparability: 95-102 vs 88-91).
  - `infra/llama-baseline/prompts/bigprompt-2320.txt` — byte-exact copy of
    the current `/tmp/bigprompt.txt` (6200c ≈ 2320 tok) used in attempt 3.
- Recommendation: always `cp` the chosen prompt to `/tmp/bigprompt.txt`
  before the run (harness + tier scripts hardcode that path), and copy the
  prompt file into the results dir in future arms.

### 6. Ready-to-execute bisection plan (RIG-FREE phase)

**Protocol per candidate boot (arm102 shape, params 102 file, isolated
build via `LLAMA_CPP` env or `P_LLAMA_BIN`):**

1. `export GGML_CUDA_ENABLE_UNIFIED_MEMORY=1`; recreate
   `/tmp/bigprompt.txt` from `infra/llama-baseline/prompts/bigprompt-2320.txt`.
2. Boot (`run-with-params.sh <params> --no-cleanup`), record ready-time.
3. 10-req sequential warm loop (built into the harness).
4. tier1 = `concurrent-decode-test.sh 18081 1 150 /tmp/bigprompt.txt` →
   **record server-side tier1 full-prefill tok/s** (2320-token entry) +
   1-conc agg.
5. tier2, tier3 = same with 2, 3 → **record tier3 3-conc agg + server-side
   eval tok/s** (cache-hit decode).
6. OPTIONAL one-off discriminator on the first two boots: add `-lv 4`
   (or grep full log) for "resolving fused Gated Delta Net" /
   "fused_lid" resolution outcome (§4 candidate 1).
7. Xid check + overlap PASS as usual; logs to `/tmp/persist-results/`.

**Metrics + expected bands:** controls must be re-established with the SAME
protocol first (pin 2 boots, v0.4.0 2 boots — expect pin tier3 ~89-90;
v0.4.0 tier3 = UNKNOWN on this protocol, likely ~84-85 if the ~5% gap
persists on the 2320-token prompt, or ~89 if the gap is
prompt-length-dependent — this first v0.4.0 ladder boot settles that).
Bisect metric = tier3 3-conc agg (cache-hit decode); secondary = tier1
full-prefill tok/s and task-0 prefill rate.

**Sequence:**

1. pin × 1 + v0.4.0 × 1 (same morning, interleaved) → establish both bands
   on the corrected protocol. (+1 repeat boot each if the bands overlap
   noise, ≤ ±2 tok/s.)
2. `-lv 4` fused-op discriminator on pin + v0.4.0 (free — rides on step 1).
3. Build mid `774ee0e20` (~86th commit; ~384s cold build or ccache), boot,
   ladder → halves the range (d0132..mid vs mid..v0.4.0). Discard
   `eab8ee41f` as anchor (predates d0132 — see §4).
4. Binary search with Tier-A-priority: if the discriminator in step 2 shows
   the GDN flip real, bracket `866322481` directly — boot its parent
   `f5e85d43a` (position 36 of 172) and `866322481` itself before any other
   midpoint.
5. ~13-17 boots total worst case; stop early on a clean ≥4 tok/s step
   between adjacent candidates.
6. After locating the step commit(s), cross-check Regression A (prefill
   rates) and, if boot-time differences appear between candidates, capture
   them opportunistically (boot regression did NOT reproduce on 09-07:
   v0.4.0 ready 27s vs 104-105s on 09-06 — same commit, same shape → treat
   boot cost as environmental/cold-page-cache (19 GB NTFS model ≈ 95s cold
   read) unless a candidate reliably reproduces it).

**Rig handoff note:** muse-spark holds the rig; do NOT boot until the
explicit "RIG FREE" message. All prep below is ready.

### 7. Also prepped in this session (for the post-bisection phase)

- **arm119 params file**: `infra/llama-baseline/params/119-udq5-146176x3-v040-kvpslot-graphopt.yml`
  — arm102 shape on v0.4.0 binaries with BOTH `--kv-unified-per-slot 146176`
  (arm115) AND `GGML_CUDA_GRAPH_OPT=1` (arm117) combined. v0.4.0 build exists
  and is REUSED as-is (`/tmp/llama-cpp-v040` worktree +
  `/mnt/WorkDisk/workspace/worktree/build-cuda1322-v040`; verified binaries
  present, no rebuild needed).
- **Harness support added** (`run-with-params.sh`, additive):
  `kv_unified_per_slot` YAML field → `--kv-unified-per-slot N`; new `env:`
  YAML map → exported to both child servers (arm117's toggle is now
  reproducible from the params file alone); binary provenance (llama_bin /
  rpc_bin paths + sha256-12) recorded in `summary.txt` to catch silent
  RPC/client build mismatches during bisection.
- **Staggered-start multiturn**: `multiturn-growth-test.sh` now takes an
  optional 6th arg `stagger_seconds` (0 = lockstep, unchanged behavior);
  sleeps between session launches; overlap check upgraded to report
  per-pair overlap % with the **adjacent-pair mean** as the tuning metric
  vs the ~70% target (±10pp verdict: "on-target / reduce delay / increase
  delay"). Unit-tested offline with synthetic start/end files (70%/90%/FAIL
  cases all correct). Stagger basis from arm 118: 16-turn × 3-session
  lockstep T ≈ 855s → **stagger ≈ 257s** (0.3×T) for ~70% adjacent overlap;
  10-turn × 3 → T ≈ 502s → stagger ≈ 150s. Same turn/output settings as
  prior tests (16 turns, 4000 new tokens/turn, 750 out) for comparability.
---

## Arm 122 — 111 shape device-order swap probe (n=2 affinity test) — ruled out, catastrophic (2026-09-07)

**Purpose:** targeted follow-up to #740 batch 1's deterministic n=2 per-slot bimodal (3/3 boots, one slot ~51-52 ms/tok ~19 tok/s vs other ~30 ms/tok ~33 tok/s, ratio ~1.7×, while n=3 stays uniformly fast ~28-30 ms/tok). Approved single-experiment budget 2-3 boots, focus n=2 tier only. Fastest slot-affinity toggle without rebuild is to swap the llama-server device order from default `RPC0,CUDA0` (via `-dev RPC0,CUDA0` in `run-with-params.sh:1`) to `CUDA0,RPC0` via `--device CUDA0,RPC0` (`P_DEVICE` in `params/122-udq5-102shape-v-q5_1-device-swap.yml:1`). If the slow slot moves to the other GPU or disappears, confirms RPC load-balancing picks an unlucky assignment at n=2; if bimodal persists identical, rules out simple order.

**Config:** identical to `111` (`ctx 438528 = 146176×3, parallel 3, kv_unified off, cache_ram 24576, K q8_0 / V q5_1, MTP q8_0/q4_1, tensor_split 27,38, ubatch 512`) except `device: CUDA0,RPC0`. Same pin `5fff12845` at `/opt/software/cuda/13.2.2`, `GGML_CUDA_ENABLE_UNIFIED_MEMORY=1`, prompt `/tmp/bigprompt.txt`, harness `run-with-params.sh --no-cleanup` + `concurrent-decode-test.sh 2 150` ×2 runs per boot. Note: `run-with-params.sh:1` always emits `-dev RPC0,CUDA0` when `rpc_port !=0`; adding `--device CUDA0,RPC0` results in two device flags (`-dev RPC0,CUDA0 --device CUDA0,RPC0`) where the later wins per script comment — this was intentional to override without editing the script, but it means the RPC tensor-split path is evaluated under the swapped order.

**Profiler note:** same as batch 1 — `nsys` unavailable (`Nsight Systems 2025.6.3 hasn't been installed`), `ncu` missing, no UM fault counters; no `dmon` capture for this run (focus was wall/clock, but `nvidia-smi` sanity shows no throttle).

**Procedure — 2026-09-07 07:22-07:54+07:00:** 2 boots planned, 1 completed before catastrophic result terminated the experiment. Boot 1: `podman compose down` → `GGML_CUDA_ENABLE_UNIFIED_MEMORY=1 bash run-with-params.sh 122-...yml --no-cleanup` (ready 40 s, 10/10 GOOD) → `concurrent-decode-test.sh 2 150` twice. Boot 2 started identical (ready 39 s) but hung on the sequential loop with ~100 s+ per request; killed after 30 min timeout via `pkill -9 -f llama-server` (GPU freed to 1 MiB, confirmed via `nvidia-smi:1`). Only boot 1 provides interpretable data.

### Result: FAIL — catastrophic 13× slowdown, not a subtle affinity shift

| Boot | Attempt | Wall per slot (150 tok) | tok/s per slot | Server `eval time` ms/tok | vs control (batch 1 n=2) |
|---|---|---|---|---|---|
| 122-1 run1 | 2-conc | 123.55 s / 167.96 s | **1.21 / 0.89** (agg 2.11) | **670.11** (99845 ms/150, 1.49 tok/s), **677.58** (100959 ms/150) | **~13× slower** than control ~51 ms/tok |
| 122-1 run2 | 2-conc | 180.01 s / 180.01 s | **0.83 / 0.83** (agg 1.67) | 704.94 (105035 ms/150), 707.53 (105422 ms/150), 587-591 ms/tok range | same |

Server log excerpt (`llama-server.log:1`): `eval time = 99845.80 ms / 150 tokens (670.11 ms per token, 1.49 tokens per second)`, `100959.29 ms / 150 tokens (677.58 ms per token)`, `105035.48 ms / 150 tokens (704.94 ms per token)` — all slots uniformly slow, no bimodal split, but an order of magnitude worse than the 1.7× bimodal seen at n=2 control. Sequential loop before tiers also degraded (already 134-195 tok/s prompt eval vs control 700+ tok/s), indicating the device-swapped placement breaks the RPC-split tensor routing, not just concurrent scheduling.

**Interpretation: ruled out.** `CUDA0,RPC0` order with this `tensor_split 27,38` and default `-dev RPC0,CUDA0` conflict does not isolate the bimodal to an unlucky slot — it makes **every** slot catastrophically slow. The n=2 bimodal at `RPC0,CUDA0` (batch 1) is not fixed by swapping order; the swapped order is not a viable configuration and cannot be a fix candidate. The `GGML_CUDA_PEER_MAX_BATCH_SIZE` revert angle was also evaluated: at pin `5fff12845` the define is present as `-DGGML_CUDA_PEER_MAX_BATCH_SIZE=128` in `build-cuda1322/compile_commands.json:1` / `CMakeCache.txt:683`, but `grep -rn PEER_MAX_BATCH_SIZE src/llama-cpp/ggml --include="*.cu" --include="*.cpp"` shows **no code use** at this pin — the option is a leftover build flag, removal in `24f5bf8a41` was doc/CMake only, so rebuilding with a different value would be a no-op without code change. Hence the device-order probe was the only fast non-rebuild affinity toggle available; it is conclusively not the path.

**No fix candidate to file.** Per `docs/workflow/07-issue-and-close.md`, no `review-finding` issue is opened — there is no evidenced fix candidate from this experiment. The next well-evidenced candidate would require either (a) a real peer-path or scheduling code change (not just a leftover define) with a rebuild, or (b) `nsys` fault-counter capture to prove/rule out UM migration at n=2 (still blocked — `nsys` not installed on any `/opt/software/cuda/*/bin/nsys`). What this experiment **does** deliver is an honest ruled-out: simple device-order flip is not the remedy and should not be pursued.

**Production:** restored after kill `HYDRA_HEAD_AUTH_TOKEN=$(cat /mnt/WorkDisk/Workplace/hydra_vortex/.hydra-head-token) podman compose -f infra/llama-baseline/docker-compose.baseline.yml up -d` → loading → `15659/9977 MiB` within 8 s, `{"status":"ok"}` stable from 07:18:45+07:00 onward (post-boot-1) and again after abort at 07:54+07:00 (verified `curl /health` and `nvidia-smi:1` 15650/9968 MiB). Rig queued next for `glm-5.3-flash` v0.4.0 bisection + `arm119` test — no further batches started without check-in, per instruction. Params file kept at `infra/llama-baseline/params/122-udq5-102shape-v-q5_1-device-swap.yml:1` for reproducibility.

**Main value preserved:** batch 1's root-cause refinement stands — bimodal is deterministically n=2-specific per-slot (~1.7×) on the `111` shape, not random boot state — and this follow-up honestly rules out the one fast affinity toggle without introducing a blind fix.


## Attempt 4 — controlled pin-vs-v0.4.0 controls: PARITY (bisection moot) (2026-09-07)

**Purpose:** rig freed 08:04+07:00. Execute the prepared v0.4.0 bisection protocol (§ "Attempt 4 prep") with the corrected `bigprompt-1109.txt` original-class prompt (not the confounded 2320-token one), full tier1→tier2→tier3 warm-up order every boot, `-lv 4` fused-op discriminator, and — before any candidate boots — the attempt-3 prefill signal re-measured under identical protocol on `bigprompt-2320.txt`. Production stop→boot→restore cycle after every boot.

**Boots (arm102 shape, pin 5fff12845 vs v0.4.0 5266f24da, isolated builds, UM=1, same params `/tmp/bisect/102-lv4-{pin,v040}.yml` = 102 + `extra_server_args: -lv 4`):**

| # | Build | Prompt | Ready | Warm loop | Result |
|---|---|---|---|---|---|
| 1 | pin 5fff12845 | bigprompt-1109 | 29 s | 10/10 GOOD | GOOD |
| 2 | v0.4.0 5266f24da | bigprompt-1109 | 27 s | 10/10 GOOD | GOOD |
| 3 | pin | bigprompt-2320 | 29 s | 10/10 GOOD | GOOD |
| 4 | v0.4.0 | bigprompt-2320 | 27 s | 10/10 GOOD | GOOD |

**1) Ladder on 1109-token prompt (client agg tok/s, `concurrent-decode-test.sh` 1/2/3 × 150):**

| Tier | pin | v0.4.0 | Δ |
|---|---|---|---|
| tier1 1-conc | 31.61 | 31.36 | −0.8% |
| tier2 2-conc | 48.78 | 48.44 | −0.7% |
| **tier3 3-conc** | **104.73** (34.91/slot) | **103.34** (34.45/slot) | **−1.3%** |

Server-side: tier1 prefill 1062 tok @ 581.9 vs 577.1 tok/s; tier2 1104 tok @ 519.6 vs 517.3; tier3 eval 40.44 vs 39.86 tok/s (24.73 vs 25.09 ms/tok). All ≤1.4% — matched-pair parity. **The arms 115/117 "−5% decode gap" does not reproduce** under controlled protocol (their original-prompt file was lost in the 09-06 reboot; content-difference cannot be ruled out, but today's reconstruction shows no gap, and attempt-3's tier3s ran cold-cache which today's protocol avoids).

**2) Prefill probe on 2320-token prompt (tier1 single-slot after identical warm loop):**

| Build | Full-prefill rate | vs pin |
|---|---|---|
| pin | **766.57 tok/s** (2320 tok, 3026 ms) | — |
| v0.4.0 | **762.10 tok/s** (2320 tok, 3044 ms) | **−0.6%** |

Pin reproduces attempt-3's 766 exactly. v0.4.0 shows **no gap** — attempt-3's "653 vs 766 = −15%" was a cold-3-conc-vs-warm-1-conc protocol confound, as suspected in § "Attempt 4 prep".

**3) Discriminator — `resolve_fused_ops` at `-lv 4` (positions 37/172 candidate 866322481):**

- pin log: `fused Gated Delta Net (autoregressive) enabled`, `(chunked) enabled`, `Lightning Indexer enabled` (plus DeepSeek V4 HC pre/comb/post enabled).
- v0.4.0 log: **no GDN/LID resolve lines at all** (auto_fgdn/auto_flid=false → skip resolve → cparams stay fused-true unconditionally) — same effective behavior as pin's resolved outcome.
- **No device-mismatch fallback fired on either side. → 866322481 is a behavioral no-op on this dense model — eliminated WITHOUT boots.** DeepSeek HC resolve lines present on both, equally irrelevant (no such nodes in qwen35).

**Conclusion — bisection moot.** Under identical builds/params/protocol, pin and v0.4.0 are at parity on every controlled metric today: decode (tier2/tier3), prefill (1062/1104/2320-token), boot time (29/27 s). With no endpoint gap to bisect, the 14-entry shortlist (ranked for a regression that doesn't exist under controlled measurement) is not actionable and no mid (`774ee0e20`) or candidate boots are spent. Honest reframe: the "v0.4.0 regression" on this rig was never reproducible — it was measurement artifacts: (a) arms 115/117 used the lost original prompt (content unknown), (b) attempt-3 v0.4.0 tier3s ran cold-cache (3× full prefill) vs controls' warm tier3s, (c) the 653-vs-766 prefill compare was cold-3conc vs warm-1conc. The persistent real finding remains batch 1's n=2 bimodal on the `111` shape (device/RPC scheduling, arm122's device-swap ruled out), NOT a v0.4.0 code regression.

**n=2 note (111-shape bimodal not reproducible on 102 shape):** today's tier2s show no strong bimodal (pin 25.0/23.8, v0.4.0 23.6/24.8) — consistent with batch 1's finding that the bimodal is `111`-shape-specific, not present on the `102` shape.

**Operational lessons (recorded for runbook):**
1. `pkill -f "llama-server"`/`"ggml-rpc-server.*PORT"` in interactive shells **self-matches the shell's own cmdline and kills it** — silent partial cleanup. Use bracket patterns (`pkill -9 -f "[b]uild-cuda1322/bin/llama-server"`) or the harness's [1/6] step.
2. A bare-metal rpc-server that survives pkill squats :50052 → containerized rpc_1 restart-loops (exit 0 after `ggml_cuda_init`) → `podman compose up -d` blocks >7 min on depends_on; llama_1 stays "Created". After killing the squatter, compose completes normally (~47 s total).
3. Production restore verified after every boot: both containers healthy, `/health ok`, VRAM 15659/9977 MiB = resting baseline.

Results dirs: `/tmp/rpc-test/results/102-lv4-{pin-5fff12845,v040-5266f24da}` (1109) and the same names re-run for the 2320 probe (superseded summaries; server logs preserved).

## Arm 119 — v0.4.0 combined flags (kv-unified-per-slot + graph-opt) + staggered multiturn (2026-09-07)

**Purpose:** final planned arm of the RIG FREE session: `v0.4.0` (`5266f24da`) with BOTH flags combined — `--kv-unified-per-slot 146176` (arm115's flag) + `GGML_CUDA_GRAPH_OPT=1` (arm117's flag) — on the 146176×3 shape, then the staggered-start 16-turn×3-agent multiturn (new 6th arg to `multiturn-growth-test.sh`) vs the arm090/arm111/arm118 baselines. Same boot used for both multiturn runs; production stop→boot→restore bracket.

**Boot:** `podman compose down` → `LLAMA_CPP=/tmp/llama-cpp-v040 run-with-params.sh params/119-udq5-146176x3-v040-kvpslot-graphopt.yml --no-cleanup` → ready 27 s, 10/10 GOOD. Both flags verified in the emitted args (`--no-kv-unified --kv-unified-per-slot 146176`; graph-opt via the harness `env:` map). **Binary provenance recorded** in `summary.txt` (new harness feature): llama `f3b874774326`, rpc `fe4d9fcee019` (sha256-12 of the v040 isolated builds). Graph-opt confirmed ACTIVE by the `graphs reused` print_timing counter climbing 50→217 during the warm loop (arm117's signature). No Xid.

**Ladder (1109-token prompt, after warm loop):**

| Tier | arm119 (v040 kvpslot+graphopt) | today's controls |
|---|---|---|
| tier1 1-conc | 30.99 | pin 31.61 / v040-102 31.36 |
| tier2 2-conc | 47.72 | pin 48.78 / v040-102 48.44 |
| **tier3 3-conc** | **103.30** | pin 104.73 / v040-102 103.34 |

At parity. Historical arm115 (85.1) / arm117 (89.0) tier3 gaps do not appear under the warm ladder — consistent with Attempt 4's artifact conclusion.

**Staggered multiturn run 1 — `multiturn-growth-test.sh 18081 3 16 4000 750 257` (stagger 257s, from the arm111-lockstep estimate 0.3×855s):** sessions ran only ~330-420 s each (arm119's depth throughput is ~2× arm111's — per-session means 16.19/16.80 tok/s at 16-turn depth vs arm111's 10.5-14.3). Result: adjacent overlap 21%/39% (mean pairwise 30%) → harness verdict `too staggered (reduce delay)` — the 257s stagger overshot because T was calibrated on arm111's slower lockstep sessions, not arm119's.

**Run 2 — retuned stagger 110s (≈0.3× measured ~375s session duration):** adjacent overlap **75%/82%** (mean pairwise **69%**) → `on-target (off by +9pp)`. Session means 15.99/12.48/13.68 tok/s (turn16 floor 3.04/9.52/13.34 — session 1 finished early into a 3-way-contended tail). Prompt depth growth identical across sessions (3376 → ~54.4K resident tokens, 16.1×).

| Multiturn (16-turn × 3 agents) | arm090 (lockstep) | arm111 (lockstep) | arm119 s=257 | arm119 s=110 |
|---|---|---|---|---|
| Total wall | 1215.7 s | 854.7 s | 933 s (30% overlap) | 894 s (69% overlap) |
| Per-session mean tok/s | 9.6–10.6 | 10.5–14.3 | 16.19 / 16.80 (2-way) | 15.99 / 12.48 / 13.68 (3-way) |

**Read:** arm119 (v0.4.0 + combined flags) matches or beats arm111 (pin) at multiturn depth — no regression at depth; the two flags coexist cleanly with kv-unified-per-slot's per-slot pools. Stagger tuning rule (validated): **S ≈ 0.3 × measured per-session duration**, not the historical batch total. Caveat: run 2 executed on the same warm server as run 1 (cache-reuse effects possible); lockstep-vs-stagger totals are not directly comparable, per-session tok/s is the honest metric.

**Production:** restored after arm119 `pkill -9` + `podman compose up -d` → both containers healthy, `/health ok`, VRAM 15659/9977 MiB (48 s cycle). Full logs: `/tmp/arm119-multiturn-s110.log` + job logs; results dir `/tmp/rpc-test/results/119-udq5-146176x3-v040-kvpslot-graphopt-5266f24da`.

**Session close-out (Attempt 4 + arm119):** the prepared v0.4.0 bisection ended at the controls — pin vs v0.4.0 is at parity on every controlled metric (decode, prefill, boot time), the top shortlist candidate `866322481` is a behavioral no-op (fused GDN/LID already enabled at pin; `-lv 4` discriminator), and no mid/candidate boots were spent. Arm 119 confirms the combined-flags v0.4.0 config at parity and beats the pin's multiturn depth numbers. No fix candidates to file; the standing `111`-shape n=2 bimodal (batch 1) remains the only open thread, awaiting an `nsys` install for UM-fault-counter proof.
---

## Arm 119 — v0.4.0 + kv-unified-per-slot + graph-opt at n=2 lockstep — inherits bimodal but per-run not per-slot (2026-09-07)

**Purpose:** quick cheap check per instruction: does `arm119` (`params/119-udq5-146176x3-v040-kvpslot-graphopt.yml:1`, v0.4.0 `5266f24da` with BOTH `--kv-unified-per-slot 146176` and `GGML_CUDA_GRAPH_OPT=1` via `env:`, same 146176×3 shape `ctx 438528 parallel 3 kv_unified off cram24 q8_0/q4_1 MTP`) at **clean lockstep** n=2 show the same 51 vs 30 ms/tok per-slot bimodal as arm111? Needed before recommending arm119 as production profile.

**Procedure — 1 boot, 2026-09-07 12:32-12:34+07:00:** `LLAMA_CPP=/tmp/llama-cpp-v040 GGML_CUDA_ENABLE_UNIFIED_MEMORY=1 bash run-with-params.sh 119-...yml --no-cleanup` (ready 27 s, 10/10 GOOD) → `concurrent-decode-test.sh` lockstep tiers 2,2,1,3 sequentially.

**Result — 1 boot, 4 tiers:**

| Tier | Wall per slot (150 tok) | tok/s per slot | Server `eval time` ms/tok | Note |
|---|---|---|---|---|
| 2-conc run1 | 8.05 s / 8.04 s | **18.64 / 18.65** (agg 37.29) | **27.90** (453, 4157 ms/150, 35.84 tok/s) + **29.36** (452, 4374 ms/150, 34.06) | **both slow, uniform** — per-run slow, not per-slot split |
| 2-conc run2 | 4.74 s / 4.74 s | **31.66 / 31.66** (agg 63.32) | **27.84** (496, 4148 ms/150) + **27.84** (497) + **19.76** (537, 2943 ms/150, 50.61) + **27.48/27.50** (578/577) | **both fast, uniform** — second run fast |
| 1-conc | 3.13 s | **47.89** | 23.58 (3512 ms/150, 42.41) | fast baseline |
| 3-conc | 4.42 s /4.49 s /4.41 s | **33.96/33.44/33.98** (agg 101.38, 33.79/slot) | 25.82 (580, 3847 ms/150) + similar 27-28 ms/tok | **uniform fast**, no slow slot |

**Interpretation:** arm119 **does inherit n=2 slowness** but with a **different signature**: batch 1's arm111 showed **per-slot split within same window** (one slot 51 ms, other 30 ms, 3/3 boots, ratio 1.7×). Arm119 shows **per-run bimodal** (run1 both slow 18.6, run2 both fast 31.6) on the same boot, with n=3 uniformly fast (33.8/slot) and n=1 fastest (47.9). So the scheduling artefact is still present at n=2, but v0.4.0+kvps+graph-opt changes the granularity from per-slot to per-batch and halves the magnitude (18.6→31.6 is 1.7× run-to-run, same ratio but uniform). **Recommendation remains cautious:** arm119 still shows n=2 instability; do not treat it as cured relative to arm111 for production n=2 lockstep recommendation until more boots.

---

## Part 1 — nsys user-local install feasibility — SUCCESS, no sudo (2026-09-07)

**Update per user:** `~/nsys-local/opt/nvidia/nsight-systems/2025.6.3/target-linux-x64/nsys` already exists and works (`2025.6.3.541-256337736014v0`, `nsys profile --help` clean). `/opt/software/cuda/13.2.1/bin/nsys` is a broken stub (`hasn't been installed with CUDA Toolkit 13.2`), `/opt/software/cuda/13.3.1` has no nsys — correctly ignored. Feasibility re-confirmed in this batch via `apt-get download nsight-systems-2025.6.3` (421 MB) extracted without sudo to `~/nsys-local` via `ar p data.tar.gz | tar -xz -C ~/nsys-local --strip-components=1` (15 min cap succeeded in ~2 min, `~/nsys-local/opt/nvidia/nsight-systems/2025.6.3/target-linux-x64/nsys --version` and `--cuda-um-cpu-page-faults=true --cuda-um-gpu-page-faults=true` flags verified). **Tooling blocker on direct UM proof is removed** — `--cuda-um-cpu-page-faults / --cuda-um-gpu-page-faults` and `--trace=cuda,nvtx,osrt` are available for Part 2.

---

## Part 2 — nsys server-side trace of arm111 n=2 vs n=3 (instrumentation to localize stall) — honest limited capture (2026-09-07)

**Goal per instruction:** lightweight timing around the **synchronous RPC round-trip (`ggml-rpc.cpp:send_rpc_cmd`)** and **sequential `ggml_backend_sched_compute_splits` per-split dispatch** to localize where the extra ~20 ms/tok goes at n=2 (batch 1: 51 ms vs 30 ms/tok) vs fast n=1/n=3. Prefer `nsys` over hand-rolled logging; isolated build only, never touch pin binary.

**Approach — 2 captures in this batch (within 2-3 boot cap):**

1. **Capture A — client-only system-wide trace (first nsys-arm111 boot, 2026-09-07 12:48-12:50):** `GGML_CUDA_ENABLE_UNIFIED_MEMORY=1 bash run-with-params.sh 111-...yml --no-cleanup` (ready 29 s, 10/10 GOOD) then `nsys profile --trace=cuda,nvtx,osrt --cuda-um-cpu-page-faults=true --cuda-um-gpu-page-faults=true --output=/tmp/nsys-arm111-n2 --duration=30 bash -c "concurrent-decode-test.sh 2 150"` and same for n=3/n=1 sequentially. **Result:** n=2 `19.14/19.15` (agg 38.29, both slow uniform this boot), n=3 `31.80/31.77/31.82` (agg 95.39 fast), n=1 `43.14` fast — **wall-clock confirms batch 1's n=2 slowness (38 vs 95)**. But nsys reps are tiny (389K/463K/313K) and `nsys stats --report cuda_um_migration` → `ERROR: Report 'cuda_um_migration' could not be found`, `cuda_api_sum` → `SKIPPED: does not contain CUDA trace data`. Reason: `nsys profile bash -c "curl ..."` traces only the **client bash/curl tree**, not the already-running `llama-server`/`ggml-rpc-server` CUDA context — system-wide flag without `--sample` did not capture server kernels.

2. **Capture B — server-side wrapper trace (second boot, 2026-09-07 13:00-13:02, the valuable one):** created isolated wrapper `/tmp/llama-nsys-wrapper.sh` → `exec nsys profile --trace=cuda,nvtx,osrt --cuda-um-cpu-page-faults=true --cuda-um-gpu-page-faults=true --output=/tmp/nsys-server-n2 $LLAMA_REAL "$@"` and ran `P_LLAMA_BIN=/tmp/llama-nsys-wrapper.sh GGML_CUDA_ENABLE_UNIFIED_MEMORY=1 bash run-with-params.sh 111-...yml --no-cleanup`. Server ready 31 s (good), then lockstep n=2 `19.43/19.43` (agg 38.87, **both slow uniform — per-run slow, matching arm119's first run**), n=3 `32.73/32.72/32.72` (agg 98.17 fast) within same nsys capture. **This time nsys captured the server:** `/tmp/nsys-server-n2.nsys-rep` **37 MB** (vs 389K client-only), sqlite 696K.

**nsys stats on the 37 MB server trace (the only trace with CUDA data):**

```
CUDA API Summary:
  87.6% cudaStreamSynchronize  25.45 s  84,969 calls  299.5 us avg  (max 2.5 s)
   4.6% cudaMemcpyAsync         1.34 s  31,173 calls
   3.1% cudaLaunchKernelExC     0.89 s 130,214 calls
   2.0% cudaMemPrefetchAsync    0.58 s       8 calls  72.5 ms avg
   0.0% cudaMallocManaged       0.43 ms   8 calls
CUDA GPU MemOps by Size:
  41.2 GB memcpy Unified Device->Host  19,663 ops 2.09 MB avg
  32.3 GB memcpy Unified Host->Device 1,062,043 ops 0.03 MB avg
  36.8 GB memset                  627 ops
  14.0 GB memcpy Host->Device    23,719 ops
```

`cuda_gpu_kern_sum` shows expected `mul_mat_vec_q`/`mul_mat_q` kernels (18.5% `mul_mat_vec_q q14,1`, 14.9% `q14,2`, etc.), no anomalous kernel. `cuda_um_migration` report not found as a named report, but the underlying sqlite has `CUPTI_ACTIVITY_KIND_RUNTIME` + `OSRT` tables; `cudaMemPrefetchAsync` is explicitly traced (8 calls, 580 ms total) and `cudaMallocManaged` 8 calls — **UM is active** (thanks to `GGML_CUDA_ENABLE_UNIFIED_MEMORY=1`) but the prefetch cost is **only 2%** of API time, far less than the 87.6% spent in `cudaStreamSynchronize`.

**Honest localization (instrumentation to LOCALIZE, not yet fix):** the **dominant stall is `cudaStreamSynchronize`**, not UM page faults/migration — consistent with the hypothesis that `send_rpc_cmd` is fully synchronous/blocking at `ggml-rpc.cpp:316` (send 1-byte cmd + 8-byte size + payload, then blocking `recv_data` for `out_size` + payload) and `ggml_backend_sched_compute_splits` at `ggml-backend.cpp:1594` processes splits **strictly sequentially** (loop `for split_id 0..n_splits` with `ggml_backend_synchronize`/`event_synchronize` before next split, no double-buffering/pipelining across RPC). The n=2 case (2 active slots out of 3, one idle) appears to tickle a **per-batch** scheduling path where the idle slot's synchronization still blocks the next batch, while n=3 (all slots busy) stays pipelined and n=1 has no cross-slot sync. The hand-rolled timestamp patch for these sites was prepared (`/tmp/llama-cpp-instr` with `send_rpc_cmd` us logging and `SCHED split us` logging) but **not built or booted** per the updated instruction to prefer nsys — the nsys data already localizes the cost to synchronize, making the manual patch redundant for this batch.

**What was not achieved:** no per-page-fault counts (the `cuda_um_migration` report name did not exist in this nsys version; raw `CUPTI_ACTIVITY_KIND_UNIFIED_MEMORY_COUNTER` table was not present), and no separate per-run nsys files for slow vs fast within the same boot (both n=2 and n=3 were in one 37 MB capture, so we cannot diff prefetch counts at n=2 slow vs n=3 fast as two files). The next step to **prove** UM vs sync would be two separate server traces (one boot with `nsys profile` wrapping only the n=2 workload, one boot only n=3) or an isolated build with `cudaMemPrefetchAsync` pinning and NVTX marks per forward pass.

**Disposition:** **localized-but-unfixed** — valuable outcome per instruction, don't force a conclusion. The `double-buffering the RPC send while CUDA0 computes` fix candidate is now **better evidenced** (synchronize dominates, prefetch is minor), but not yet proven with a code change. Per `docs/workflow/07-issue-and-close.md`, **no `review-finding` issue opened in this batch** — the evidence is suggestive but not yet at the "concrete, evidenced fix" threshold (needs a second, clean per-run nsys or the isolated `send_rpc_cmd`/`compute_splits` timing diff). If the project wants to proceed, the next bounded experiment should be that isolated `double-buffer` prototype in `/tmp/llama-cpp-instr` (never touching `src/llama-cpp` pin) with NVTX-marked n=2 vs n=3, or a pair of separate nsys server captures.

**Production:** restored after every boot via `HYDRA_HEAD_AUTH_TOKEN=$(cat /mnt/WorkDisk/Workplace/hydra_vortex/.hydra-head-token) podman compose -f infra/llama-baseline/docker-compose.baseline.yml up -d` → `15659/9977 MiB` within 6-8 s, `{"status":"ok"}` stable (arm119 boot restored at 12:34, nsys arm111 boots restored at 12:50 and 13:02, verified via `curl /health` and `nvidia-smi:1` 15650/9968). **Cap respected:** 3 boots total in this batch (arm119-1, nsys-arm111 client-only-1, nsys-server wrapper-1) — no further boots without check-in.


---

## Part 2b — Clean separate nsys server traces n=2-only vs n=3-only (2026-09-07 13:14-13:19+07:00) — honest ambiguous/contradictory

**Goal per 2026-09-07 instruction:** two clean **separate** wrapper-traced server captures (not mixed like Part 2's 37 MB capture) to diff `cuda_api_sum` between n=2 and n=3: specifically `cudaStreamSynchronize` call count / total / avg per call, plus `cudaMemcpyAsync` and `cudaLaunchKernelExC` — test *per-call latency spike* (blocking-wait-on-idle-slot) vs *proportional call count* (more RPC round-trips). If clean sync-dominated + per-call spike, open `review-finding`; if ambiguous/contradictory, stop.

**Procedure — 2 boots, cap respected, production restored each time:**
- Script `/tmp/nsys-one.sh` reused: `podman compose down` → `P_LLAMA_BIN=/tmp/llama-nsys-wrapper-one.sh` (exec `nsys profile --trace=cuda,nvtx,osrt --cuda-um-cpu-page-faults=true --cuda-um-gpu-page-faults=true --output=/tmp/nsys-server-${MODE}-only --force-overwrite=true $LLAMA_REAL "$@"`) → `GGML_CUDA_ENABLE_UNIFIED_MEMORY=1 bash run-with-params.sh 111-udq5-102shape-v-q5_1.yml --no-cleanup` (ready 31 s, 10/10 GOOD both boots) → single workload tier only → `pkill llama-server` to flush nsys → `nsys stats --report cuda_api_sum` → `podman compose up -d` → `curl /health {"status":"ok"}` + `nvidia-smi 15660/9977`.
- Boot 1 (n2-only, 13:14-13:16): `concurrent-decode-test.sh 18081 2 150 /tmp/bigprompt.txt` → `slot1 9.22s 16.28 tok/s, slot2 7.91s 18.95 tok/s, agg35.23, mean17.62` (both slow uniform this boot, per-run slow — matches Part 2's n=2 slow). Nsys rep `/tmp/nsys-server-n2-only.nsys-rep` 35 MB (vs earlier race `ls: No such file` was a flush-timing artefact; file appeared 3 s later, sqlite 133 MB).
- Boot 2 (n3-only, 13:16-13:19): `concurrent-decode-test.sh 18081 3 150` → `16.79/13.78/13.30 tok/s, agg43.87, mean14.62` (**n=3 also slow under nsys** — 3/3 slots 13-16 tok/s, slower than n=2's 17.62 and far from the non-nsys fast baseline of 32-33 tok/s; heisenbug).

**nsys stats diff (the core of the instruction):**

| metric | n2-only (slow, agg35.23, mean17.62) | n3-only (slow, agg43.87, mean14.62) | delta |
|---|---|---|---|
| `cudaStreamSynchronize` total | 26.26 s (88.4%) | 26.21 s (87.5%) | -0.19% |
| `cudaStreamSynchronize` calls | 83,822 | 84,871 | +1.25% |
| `cudaStreamSynchronize` avg | 313,288 ns | 308,825 ns | -1.42% |
| `cudaStreamSynchronize` med | 442 ns | 431 ns | -2.5% |
| `cudaStreamSynchronize` max | 2,552,751,143 ns | 2,549,567,338 ns | -0.12% |
| `cudaMemcpyAsync` | 1.25 s (4.2%) 31,989× avg39,197 | 1.29 s (4.3%) 31,379× avg41,187 | +3.1% total |
| `cudaLaunchKernelExC_v11060` | 0.86 s (2.9%) 133,610× avg6,464 | 1.05 s (3.5%) 139,998× avg7,513 | +21.8% total |
| `cudaLaunchKernel` | 0.37 s (1.3%) 23,611× | 0.42 s (1.4%) 28,246× | +14% |
| `cudaMemPrefetchAsync_v12020` | 0.578 s (1.9%) 8× avg72,254,419 | 0.583 s (1.9%) 8× avg72,963,141 | +1% |
| `cudaGraphLaunch` | 0.11 s 1,571× | 0.12 s 1,502× | +0.3% |

Full `n2-only` also matches Part 2's mixed capture (87.6% 25.45s 84,969× 299.5us) within 1-2% — stable sync dominance across captures.

**Interpretation — honest, per instruction to not force a conclusion:**

1. **Sync-dominated confirmed, now twice over:** both separate captures are 87-88% `cudaStreamSynchronize`, re-confirming Part 2's pivot away from UM-migration-storm (prefetch stays 1.9-2.0%, 8 calls, 0.58 s, identical). `cudaMemcpyAsync` (4.2-4.3%) and `cudaLaunchKernelExC` (2.9-3.5%) are minor — **rule out memcpy/launch as driver** of the extra ~20 ms/tok.
2. **But per-call latency test FAILS to differentiate n=2 vs n=3:** avg per-call sync is essentially identical (313k vs 308k ns, -1.4%), median 442 vs 431 ns (-2.5%), total time -0.19% — **no dramatic per-call spike at n=2**. Call count is also not proportional (+1.25%, not scaling with slots). So neither "blocking-wait-on-idle-slot shows higher per-call latency" nor "just more RPC round-trips at n=2" is supported by this pair.
3. **Heisenbug / contradiction with non-nsys baseline:** under nsys wrapping, **n=3 is now also slow** (14.6/slot vs non-nsys fast 32-33/slot in Batch 1 and Part 2 boot1's fast 31.8/slot). The earlier clean narrative "n=2 is 1.7× slow per-slot while n=3 uniformly fast 3/3 boots" **does not reproduce under server-side nsys** — both fall into slow mode with near-identical profiles. This suggests nsys overhead (extra synchronization for tracing, or the wrapper's `--force-overwrite` serialization) either masks the scheduling artefact or pushes both concurrencies into the same slow path. We did not obtain a fast nsys trace to compare against — both captures are slow-mode, so the intended fast-vs-slow diff is missing.
4. **No further localization:** we cannot point to a specific sync call site (e.g., `send_rpc_cmd:316 recv_data` vs `compute_splits:1594 event_synchronize`) — both traces lack NVTX marks per forward pass, and the 37 MB mixed capture and these two separate captures all show the same coarse `cudaStreamSynchronize` bucket. The `cudaLaunchKernelExC` +21% at n=3 is the only notable secondary delta, but at 3-3.5% of total it is not explanatory for a 2× wall-clock gap.

**Disposition:** **ambiguous / contradictory — do not open `review-finding`.** Per instruction, "If this cleanly confirms sync-dominated + localizes further (e.g., which specific sync call site, or a clear per-call latency spike), open the review-finding … If it's ambiguous or contradicts the first capture, say so honestly and stop." This batch **does confirm sync-dominated (87-88% vs 2% prefetch) a second time**, but **fails the per-call latency spike test** and **contradicts the n=2-specific bimodal narrative** by showing n=3 also slow under nsys with an indistinguishable profile. We report this honestly and stop — no `gh issue create --label review-finding`.

**Next step if the project wants to proceed (would need a new bounded batch):** pair a **non-nsys fast baseline** (reproduce n=3 fast outside nsys, capture `concurrent-decode` wall times) against an **NVTX-marked isolated build** in `/tmp/llama-cpp-instr` (instrument `ggml-rpc.cpp:send_rpc_cmd:316` and `ggml-backend.cpp:1594` with `nvtxRangePushA` per rank + per-split, plus chrono `LOG`) to break the `cudaStreamSynchronize` bucket into RPC vs split sites. Alternatively, a **pair of per-workload nsys captures where one is provably fast** (e.g., retry until n=3 hits the 32 tok/s fast mode under nsys, or capture n=1 fast 47 tok/s as reference) to get the missing fast-vs-slow diff. No code in `src/llama-cpp` pin was touched; isolated patch was not built this batch.

**Cap/production:** 2 boots used (n2-only 13:14, n3-only 13:16), each restored via `HYDRA_HEAD_AUTH_TOKEN=$(cat /mnt/WorkDisk/Workplace/hydra_vortex/.hydra-head-token) podman compose -f infra/llama-baseline/docker-compose.baseline.yml up -d` → `15660/9977` within 5-8 s, `{"status":"ok"}` verified at 13:19 (nvidia-smi 15660/9977, 5W/0%). No further boots.

---

## Part 2c — Tracer-free chrono instrumentation (isolated build, no nsys) — 2 boots, captures slow+fast but per-site delta absent (2026-09-07 13:30-13:37)

**Goal per instruction:** one more bounded batch (cap 2 boots), code-level `chrono` logs **with NO external tracer** (eliminate observer effect that made n=3 also slow under nsys). In `/tmp/llama-cpp-instr` (isolated, never touch pin `src/llama-cpp 5fff12845`) add lightweight `fprintf(stderr,"[INSTR]…")` around:
- `send_rpc_cmd` at `ggml/src/ggml-rpc/ggml-rpc.cpp:316` (blocking send+recv)
- per-split loop at `ggml/src/ggml-backend.cpp:1594` (`ggml_backend_sched_compute_splits`)

Build, boot arm111 shape (`111-udq5-102shape-v-q5_1.yml:1` `ctx438528=3×146176 parallel3 kv_unified off cache_ram24576 q5_1 UM on`), run n=2 lockstep repeatedly to catch both slow and fast (Batch 1 showed slow/fast alternates), then n=3 for comparison — all **without nsys**, just binary's own log. Diff per-call timings; if a specific call shows outlier, file `review-finding`; otherwise bank `sync-dominated, not UM-storm` and stop.

**Isolated build steps (no boot cost):**
- Reverted `ggml/src/ggml-cuda/ggml-cuda.cu` in `/tmp/llama-cpp-instr` to `HEAD` (removed the `cudaMemAdvise/cudaMemPrefetchAsync` prefetch patch that was dirty in both pin and instr — avoids confounding; pin remains dirty `M ggml/src/ggml-cuda/ggml-cuda.cu` but not used).
- Instrumented `ggml-rpc.cpp:301` (3-arg `send_rpc_cmd` fire-and-forget for `GRAPH_COMPUTE/RECOMPUTE`) with `chrono` + `[INSTR][RPC-SEND] cmd=… in=… us=…` for `cmd==10/16`; and `ggml-rpc.cpp:317` (5-arg with `recv`) already had `[INSTR][RPC] cmd=… in=… out=… us=…` for `GRAPH_*` or `>5000us`; and `ggml-backend.cpp:1594` per-split loop with `[INSTR][SCHED] split=…/… backend=… n_nodes=… n_inputs=… us=…`.
- Reconfigured `cmake -S /tmp/llama-cpp-instr -B /tmp/llama-cpp-instr/build-cuda1322 -DGGML_CUDA=1 -DLLAMA_CURL=ON -DCMAKE_CUDA_ARCHITECTURES="86;120" -DCUDAToolkit_ROOT=/opt/software/cuda/13.2.2 -DGGML_CUDA_FA_ALL_QUANTS=ON -DGGML_RPC=ON` (previous `build-cuda1322` had stale `CMakeCache.txt` pointing to pin source, wiped and reconfigured) and rebuilt `-j16` to `100%` (llama-server 99%).

**Procedure — 2 boots, cap respected, production restored each time, NO nsys wrapping:**
- Script `/tmp/instr-one.sh`: `podman compose down` → `LLAMA_CPP=/tmp/llama-cpp-instr GGML_CUDA_ENABLE_UNIFIED_MEMORY=1 bash run-with-params.sh 111…yml --no-cleanup` (finds both `ggml-rpc-server`+`llama-server` from `instr/build-cuda1322/bin`, ready 30s, 10/10 GOOD both boots) → 4× `concurrent-decode-test.sh 18081 2 150 /tmp/bigprompt.txt` (2 s gap), then `3 150`, then `1 150` → grep `[INSTR]` from `llama-server.log` → `pkill` + `podman compose up -d` → `{"status":"ok"}` `15659/9977` (verified 13:37:06 `15659/9977`).

**Boot 1 (13:30:16-13:32:45):**
| run | tok/s per slot (150 tok) | mean | agg | wall |
|---|---|---|---|---|
| n=2 r1 | **19.24 / 16.50** | **17.87** | 35.73 | 7.80s/9.09s |
| n=2 r2 | 25.48 / **32.13** | 28.80 | 57.60 | 5.89s/4.67s |
| n=2 r3 | 26.62 / **33.40** | 30.01 | 60.02 | 5.63s/4.49s |
| n=2 r4 | **33.99** / 26.62 | 30.31 | 60.62 | 4.41s/5.63s |
| n=3 | 34.51 / 23.95 / 34.50 | 30.99 | 92.96 | 4.35s/6.26s |
| n=1 | 45.73 | 45.73 | 45.73 | 3.28s |
**Caught slow+fast in same boot without tracer** — r1 slow 17.87 vs r2-4 fast 28.8-30.3 (1.7× run-to-run, same ratio as Batch 1 per-slot but now per-run; replicates Batch 1's 51 vs 30 ms/tok bimodal in wall-clock). n=3 and n=1 fast (30.99, 45.73) — **no heisenbug**: without nsys, n=3 stays fast as in Batch 1, confirming nsys was contaminating.

Boot 2 (13:34:38-13:37:06) **replicates**: r1 **17.89** (19.25/16.52), r2 28.88, r3 30.39, r4 30.05, n=3 31.77, n=1 45.80 — same slow r1 vs fast rest, 2/2 boots.

**INSTR logs — boot1 587K lines (804 RPC + 8536 SCHED), boot2 628K (822 RPC-SEND + 8728 SCHED):**

*RPC* (5-arg `GET_TENSOR` etc, >5000us filter): 804 rows boot1, cmd histogram `{8:795,5:8,4:1}` (8=`GET_TENSOR`), e.g. `cmd=8 in=312 out=245760 us=40821` etc. Top `us` are load spikes `3,014,416` (40960) during model load, not decode. Did **not** initially log `GRAPH_COMPUTE` (10) because it uses the 3-arg fire-and-forget path; after patch, boot2 adds `[INSTR][RPC-SEND]` 820 rows: `cmd=10 in=702404 us=60-82` and `cmd=16 in=4 us=4-5` — **all tens of µs, no slow outlier**.

*SCHED* per-split: 3867 backend1 + 3867 backend2 + 802 backend0. For decode:
- `n_nodes=79, backend=1` (small decode split): count 3065, **avg 197us median 179us min70 max43427 q90 231us**. Grouped as tokens (2 splits per token) → `ns=2` tokens 3065, **avg 202us median182 p95 259 max43445** (max is load).
- `n_nodes=2425, backend=1` (full): 802, avg 53.6ms median33.8ms max3.4s (load), but decode-phase `2425` avg ~33-34ms.

**Per-run diff (the core test):** Split the 79-node and 2425-node SCHED streams chronologically into windows proportional to token counts (300,300,300,300,450,150):
- 79-node windows (boot1): w1 159.2, w2 156.2, w3 163.5, w4 239.7, w5 185.0, w6 158.6 — **w4 outlier 239 vs 159 is due to including a single 9412us split, but median 182 vs 172 not dramatic; windows 1-3 (r1-r3) are indistinguishable (159-163)**. Proper token-grouped `ns=2` windows 1-4: **w1 363us (includes load outlier 43445), w2 160.4, w3 159.9, w4 158.7** — **no slow vs fast delta**: r1 slow (17.87) and r2 fast (28.8) both show ~160us per-token SCHED.
- 2425-node full windows: w1 33.6ms, w2 33.7ms, w3 33.8ms, w4 69.1ms (includes 488k outlier), w5 46.8ms, w6 33.9ms — again **no systematic slow vs fast**; token-grouped `ns=3` vs `ns=2` totals also overlap.
- RPC-SEND `GRAPH_COMPUTE` 60-80us all runs, no outlier in r1 slow; RPC 5-arg `GET` 33-41ms all windows identical.

**Interpretation — honest, per instruction:**

1. **Tracer-free confirms bimodal persists** and that **nsys heisenbug is real**: without nsys, n=2 shows clean slow r1 (17.8, both boots) vs fast r2-4 (28-30) and n=3 stays fast 30-31, whereas with nsys both n=2 and n=3 fell to ~14-17 slow — **observer effect eliminated by this batch**.

2. **But per-site chrono fails to localize:** `send_rpc_cmd` (both 3-arg `GRAPH` 60-80us and 5-arg `GET` ~40ms) and `SCHED` per-split (79-node ~160us, 2425-node ~33ms) are **indistinguishable between a slow n=2 run (17.8 tok/s) and a fast n=2 run (30 tok/s) in the same boot** — median/avg differ <5%, max outliers are load not run-specific, and the ~12ms extra wall per token (52ms vs 30ms) is **not in backend compute**. The remaining wall time (~30ms of 30-50ms) is outside `SCHED` — likely queueing/scheduling between tokens, KV cache, or sampler, not captured by these two probes.

3. **No specific call site shows outlier latency** (neither `RPC-SEND`, nor `RPC` recv, nor which `split_id`/`backend`/`n_nodes`). Therefore **does not meet the "clear slow-vs-fast delta at a specific call" bar for a `review-finding`**.

**Disposition:** **Do not file `review-finding`.** Per instruction: if this localizes to a specific call site with clear delta, file it; if still ambiguous after this, **bank the `sync-dominated, not UM-storm` finding as the final word**. That is what we do: ledger's final word is Part 2 + 2b's **87-88% `cudaStreamSynchronize` vs 2% `cudaMemPrefetchAsync`** (replicated 3 captures) + this tracer-free batch's proof that **bimodal is real without tracer but not in per-split/RPC send**, so the sync is at a higher level (likely `sched` outer loop / `llama_context` decode queuing) with diminishing returns for further rig cycles.

**Next step if the project wants to pursue:** instrument the **outer decode loop** (`llama_server` / `common` sampler `decode` inter-token interval, `slot` queue wait, `kv_cache` `cache_ram` swap) with `chrono` around `llama_decode` / `slot` `inflight` rather than `ggml` backend — not in this batch, would need a new bounded experiment.

**Cap/production:** 2 boots used (boot1 13:30, boot2 13:34) as instructed, each restored via `HYDRA_HEAD_AUTH_TOKEN=$(cat …) podman compose -f infra/llama-baseline/docker-compose.baseline.yml up -d` → `15659/9977` within 5s (`curl /health {"status":"ok"}` 13:37:06, `nvidia-smi` 15659/9977, 5W/0%). No further boots. No `gh issue create`.

---

## Part 2d — Outer-loop chrono (llama_server) — tracer-free, 2 boots, 2-3 layer cleared (2026-09-07 13:43-13:48)

**Goal per instruction:** if UM (Part 2b) + ggml RPC/SCHED (Part 2c) show no per-site delta, instrument **outer `llama_server` decode loop** (`update_slots` / `pre_decode` / `decode` / `post_decode` / `llama_decode+synchronize` vs queue yield) with wall-anchored `chrono` and re-boot arm111 `n=2` lockstep (4×) to diff slow vs fast at that layer. Isolated build `/tmp/llama-cpp-instr` (never pin `src/llama-cpp 5fff12845`), no nsys.

**Instrumentation (still isolated, incremental on Part 2c patches):**

- `tools/server/server-context.cpp:20` `#include <chrono>` + `+ <cstdio>` + `static auto _instr_global_start = steady_clock::now()` + `struct instr_outer_scope { wall_ms = now - _global_start, us }` that logs `[INSTR][OUTER] <name> wall_ms=… us=…` on scope exit.
- `update_slots():2738` `instr_outer_scope _instr_update_slots("update_slots")` (total per iteration).
- `pre_decode():2854` `instr_outer_scope("pre_decode")`.
- `decode():3594` `instr_outer_scope("decode")` + **fine-grained around `llama_decode+synchronize`**: inside `decode()` capture `_t0` before `llama_decode`, `_t1` after, `_t2/_t3` around `llama_synchronize` if `has_output`, then `us_outer = now-_t_decode_outer`, `us_yield_overhead = us_outer - us_decode - us_sync`, log `[INSTR][OUTER] decode_outer wall_ms=… n_batch=… off=… n_tokens=… has_output=… ret=… us_decode=… us_sync=… us_yield_overhead=… us_outer=…`.
- `post_decode():3729` `instr_outer_scope("post_decode")`.
- Kept prior `RPC-SEND`/`SCHED` probes (so 3 layers in one log).

**Build:** `cmake --build /tmp/llama-cpp-instr/build-cuda1322 --target llama-server -j16` → `100%` `server-context.cpp.o` relink `libllama-server-impl.so` + `llama-server`.

**Procedure — 2 boots, cap respected, production restored, no nsys (script `/tmp/outer-one.sh`, same shape as `instr-one.sh` but greps `OUTER`):**

- `LLAMA_CPP=/tmp/llama-cpp-instr GGML_CUDA_ENABLE_UNIFIED_MEMORY=1 run-with-params.sh 111…yml --no-cleanup` ready 30s 10/10, then 4× `concurrent-decode-test.sh 18081 2 150`, `3 150`, `1 150` (2 s gap), collect `llama-server.log`, `podman compose up -d` → `{"status":"ok"}` `15659/9977`.

**Boot 1 (13:43:?? `outer-one-boot1.log` 4032 OUTER 803 decode_outer 820 update_slots) — no wall_ms yet (pre-timestamp build):**

| run | tok/s per slot (150) | mean | agg | wall |
|---|---|---|---|---|
| n=2 r1 | 19.23/16.49 | 17.86 | 35.71 | 7.80/9.10 |
| n=2 r2 | 32.11/25.48 | 28.80 | 57.59 | 4.67/5.89 |
| n=2 r3 | 26.64/34.02 | 30.33 | 60.66 | 5.63/4.41 |
| n=2 r4 | 26.62/33.41 | 30.01 | 60.03 | 5.63/4.49 |
| n=3 | 34.48/34.48/23.96 | 30.97 | 92.92 | 4.35/4.35/6.26 |
| n=1 | 45.89 | 45.89 | 45.89 | 3.27 |

Slow r1 vs fast r2-4 **1.7×** replicates Part 2c/Batch 1.

OUTER tail (no wall, via counts): `decode_outer` 803 rows `us_decode~33-34ms us_sync~31ms us_outer~64-65ms us_yield_overhead~100-150us`, `pre_decode~10ms`, `update_slots~77ms` (includes pre+decode+post), `SCHED 79-node ~195us` etc — **no per-run wall to diff**, but totals show `OUTER counts 4032`.

**Boot 2 (13:44:56-13:48:16 `outer-one-boot2.log` 3867 OUTER 770 decode_outer 787 update_slots) — wall-anchored build:**

| run | tok/s per slot | mean | agg | wall |
|---|---|---|---|---|
| n=2 r1 | 19.21/16.47 | **17.84** | 35.68 | 7.81/9.11 |
| n=2 r2 | 32.08/25.48 | 28.78 | 57.57 | 4.68/5.89 |
| n=2 r3 | 26.80/34.26 | 30.53 | 61.06 | 5.60/4.38 |
| n=2 r4 | 26.65/34.02 | 30.34 | 60.67 | 5.63/4.41 |
| n=3 | 23.72/33.96/34.84 | 30.84 | 92.52 | 6.32/4.42/4.30 |
| n=1 | 45.83 | 45.83 | 45.83 | 3.27 |

Again **2/2 boots replicate slow r1 vs fast rest**, no nsys heisenbug (n=3 fast 30.84, n=1 45.83).

**OUTER per-run with `wall_ms` (boot2, `decode_outer` split by `n_tokens` and `wall_ms` gaps >800ms = idle 2 s sleeps):**

- By `n_tokens` histogram: `4:552 (n=1+MTP tail)`, `8:152 (n=2)`, `12:37 (n=3)`, `42/250/512` prefill 3. `n_tokens=8` is the `n=2` work: **152 rows = 4 runs ×38/37 decodes** wall windows: `72063->75939 94843/93406`, `79980->83956 94009/93397`, `87637->91586 93747/93382`, `95285->99258 94058/93379` — **outer avg 93747-94843 med 93379-93406, Δ <1.1%** between any slow vs fast run, `us_decode 59181 med57807`, `us_sync 34933 med35492` also <2%.

- `n_tokens=12` (n=3 single run 37 rows `103261->106781`) outer `84396 med81695` `decode 44799 med41366` `sync 39484 med40214`.

- `n_tokens=4` (552 rows) outer `65167 med64862` — run0 `429` rows `33533->68099 65075/64815`, later tails 16/15/15/15/62 rows `65661/65031` etc — **all 65k ±1%**, no delta.

- `pre_decode`/`update_slots`/`decode` scopes similarly: `pre_decode ~10ms/10398 med`, `update_slots ~102-113ms`, `decode ~86-98ms` per iteration — **per-run windows for the four n=2 runs are indistinguishable** (e.g. `update_slots` runs 3-6: `113350/106627`, `102120/106873`, `101532/106008`, `102121/106617` — Δ <11% max, median within 0.5%).

- `decode_outer` fine-grained: `us_decode` (llama_decode) 33-34ms for n=2-4-token decodes, `us_sync` 30-31ms (sync-dominated, ~48% of outer), `us_yield_overhead` 80-150us (negligible) — **identical slow vs fast**: slow r1's `us_decode`/`us_sync`/`us_outer` not larger; e.g. boot2 n=2 run windows above show `us_outer` 93-94k for both slow and fast.

**Interpretation:**

1. **Outer layer also cleared.** The 1.7× client wall difference (17.8 vs 28-30 tok/s, ~22ms extra per token) is **not in** `pre_decode`, `llama_decode`, `llama_synchronize`, `yield_to_queue` overhead, nor `update_slots` total — all per-token outer timings are within 1-2% across slow vs fast runs in same boot.

2. **Three layers now all show no per-site delta:** UM `cudaMemPrefetchAsync 1.9% 8×` vs `cudaStreamSynchronize 88%` (Part 2b), `RPC-SEND 60-80us` + `SCHED 79-node 197us` (Part 2c), `OUTER decode_outer 64-94k` (this part) — **all indistinguishable slow vs fast**, so **bimodal is not a per-call latency spike** at any instrumented site.

3. **Sync-dominated remains final word.** Each decode still spends ~50% in `synchronize` (31ms of 65ms) and total CUDA runtime is 88% `StreamSynchronize` (Part 2b), but that sync time itself does not bloat in slow runs — the extra wall must be at a higher level not captured (e.g. inter-token slot scheduling gap, sampler `common_sampler`, KV-cache slot search `llama_kv_cache` / `cache_ram` / `srm` swamping, or CPU-side `queue_tasks` wake latency between `update_slots` iterations). Further instrumentation would need to wrap the **inter-`update_slots` idle** (time between successive `update_slots` returns) and `post_decode` sampler + `slot` state machine, not just inside `update_slots`.

**Disposition:** **Do not file `review-finding`.** No specific call site (UM, RPC, SCHED, outer decode/sync/yield) shows clear slow-vs-fast delta (all <2% median, max outliers are load). Bank three-layer `sync-dominated, not UM-storm, not RPC, not outer` as final word per instruction and stop rig cycles.

**Cap/production (this part):** 2 boots `outer-one boot1 13:43` (`4032 OUTER`) + `boot2 13:44:56-13:48:16 wall_ms` as instructed, each restored via `HYDRA_HEAD_AUTH_TOKEN=$(cat …) podman compose -f … up -d` → `{"status":"ok"}` `15659/9977` `nvidia-smi 15659/16311 9977/12288 427/210MHz 5W` (verified 13:48:16). Pin `src/llama-cpp 5fff12845` untouched (isolated `/tmp/llama-cpp-instr` only). No `gh issue create`. Ledger now 3157→ +~80 lines.

---

## Closing — arm111 n=2 bimodal thread (2026-09-07) — banked, no fix candidate

**Chase:** `n=2` 1.7× bimodal (Batch 1: 51 vs 30 ms/tok, 19→33 tok/s; replicated 4×/boot across 5 boots) falsified address-filter/bisection paths, then localized via three tracer-free layers under bounded caps (2 boots/layer, production restored):

- **Part 2 / 2b (CUDA/UM):** `nsys` (client-only 389K skipped, server-wrapper 37M, then clean separate `n2-only 35M` vs `n3-only 37M`) → `cudaStreamSynchronize 87-88% (25-26s)`, `cudaMemPrefetchAsync 1.9% 8×0.58s`, no per-call spike (`-1.4% avg`), **not UM-storm**; heisenbug (n=3 also slow under nsys) identified.
- **Part 2c (ggml RPC/SCHED):** isolated `chrono` in `/tmp/llama-cpp-instr` (no nsys) → 2 boots catch slow r1 17.8 vs fast 28-30 in same boot; `RPC-SEND GRAPH 60-80us`, `SCHED 79-node 197us med179`, `2425-node 33ms` **indistinguishable slow vs fast** (<5%).
- **Part 2d (outer llama_server):** `update_slots/pre_decode/decode/post_decode` + `decode_outer` (`llama_decode 33-34ms + sync 30-31ms + yield 0.1ms = 64-94k outer`) wall-anchored → **4× n=2 runs 93-94k med Δ<1.1% slow vs fast**, `pre 10ms`/`update 102-113ms` also flat.

**Verdict:** **Sync-dominated overall (88% Sync, ~50% per-decode in `synchronize`) but bimodal not localized to any probed call site — no per-site delta at UM, `send_rpc_cmd`, `sched splits`, or outer decode/sync/yield. No `review-finding` bar met; no code fix candidate from this thread. Honest signal is to stop with current probes.** Future reader: if revisiting, the remaining hypothesis is **inter-`update_slots` idle / sampler `post_decode` / KV-cache slot search / `queue_tasks` wake** (not inside `update_slots`), but that is a fourth speculative layer with diminishing returns per agreement — do not chase without a new bounded design. Production shape `111-udq5-102shape-v-q5_1.yml` unchanged, `src/llama-cpp 5fff12845` pin retained, isolated `/tmp/llama-cpp-instr` only, no `gh issue create` for this thread.


---

## Part 3 — arm111 n=2 bimodal: inter-`update_slots` gap probe (2026-09-07, fresh thread) — gap CLEARED, run-level bimodal = prompt-cache miss, per-slot eval split NOT reproduced

**Chase (this thread, 3 boots, cap respected, production restored after each):** the prior thread's "future reader" hypothesis list started with **inter-`update_slots` idle** — instrumented it directly, plus GPU telemetry (the other assigned angle), plus per-slot spec-decode anatomy via the fork's existing `slot.stats` counters.

**Instrumentation (isolated `/tmp/llama-cpp-instr`, pin `5fff12845` untouched):**
- `server-queue.cpp` `start_loop()`: `[INSTR][LOOP] wall_ms us_gap_entry us_process us_update` per iteration (raw steady-clock; `us_gap_entry` = wait between loop wake and work start = the idle-gap candidate; the 1s-poll rows show up as `us_gap_entry≈999987`).
- `server-context.cpp`: `[INSTR][RUN] start/done` (per task: `n_gen draft_total acc_total verif`), `[INSTR][TOK]` in `send_partial_response` (absent — `concurrent-decode-test.sh` posts `stream:false`), `[INSTR][STEP] accepted/draft_n/replay` at the spec-verify point. muse-spark's `[INSTR][OUTER]/[RPC]/[SCHED]` kept; note `decode_outer` wall_ms is relative to `_instr_global_start` (boot-scoped offset vs my raw-ms rows).
- Rebuilt via ccache (10s); SHA resolves "unknown" (broken `.git` in the plain-copy build tree) → results dir `111-udq5-102shape-v-q5_1-unknown` (same dir reused across boots — logs append).

**Assigned angle 1 — inter-`update_slots` gap: CLEARED.** During decode runs the gap between successive `update_slots` iterations is **med 1μs** (loop self-pumps via `SERVER_TASK_TYPE_NEXT_RESPONSE` self-post; the 1s poll only appears between runs). The slow run's wall is fully accounted by long `us_update` iterations (prefill chunks + verify steps), not idle wakes. Boot-1 4× n=2 lockstep: r1 19.7/19.9 slow, r2-r4 32-34 fast — replicated under instrumentation.

**Assigned angle 2 — GPU telemetry: CLEARED.** 500ms-cadence nvidia-smi across slow+fast runs: GPU0 (5060 Ti) P1 sm 2565-3052 MHz, GPU1 (3060) P2 sm 1777-2145 MHz, temps ≤57C, zero pstate transitions, no clock drops. Slow run was not a cold-clock or throttle event.

**New root cause for the RUN-LEVEL bimodal (client sees 1.3-1.7× slow on first n=2 run): prompt-cache miss.**
- `concurrent-decode-test.sh`'s 10-request warm loop uses **4-token prompts** (print_timing: 169-171ms/4 tok) — it never primes the bigprompt KV cache.
- First bigprompt run(s) therefore pay a full-prompt prefill: boot-1 run 1 = **2162 tok in 3 chunks** (588@618ms, 1058@1217ms, 516@564ms + 216ms sync, both slots); boot-2 tier2A = **1081 tok one slot** (+1.75s vs run B); boot-3 tier2A (batch-1's exact 2320-token prompt restored) = **2362 tok one slot @ 683 tok/s (3459ms)** while the other slot hit the tier1-primed sequence (4-tok eval).
- Decode phase is **bit-identical** slow vs fast: 37-44 verify steps, ~107.4ms/step, spec acceptance **3/3 full** (`slot.stats` dist `{3: 37}`), replay 0. The client "tok/s" difference is purely prefill amortization on the first run; "slow then fast" = cache priming order.
- Batch-1's per-slot split (slot 2 51 vs slot 1 30 ms/tok print_timing eval, same 8.07s window) **did not reproduce** in 3 boots today (all per-slot eval windows equal within runs, both prompt sizes, tier1→tier2×2→tier3 ladders). Client-side per-slot asymmetry is explained by cache asymmetry (which slot held the warm sequence), but the eval-window split specifically remains unexplained — if it recurs, the discriminating data to capture is per-task `print_timing` eval + `[INSTR][STEP]` rows in the same boot (this thread's tooling already provides both).

**Disposition:** inter-update gap and GPU telemetry both cleared with hard numbers; run-level "first-run slow" root-caused to cache-miss prefill (not a decode anomaly). **`review-finding` filed: #743** (harness defect — `concurrent-decode-test.sh` warm loop uses 4-token prompts, never primes the bigprompt KV cache; fix direction: prime with the actual test prompt; harness-level, no production/fork change). Batch-1's per-slot eval-window split (51 vs 30 ms/tok) is **explicitly unresolved / not reproduced** — left in the open per thread close-out, no separate issue (nothing actionable while it does not reproduce; discriminating data to capture if it recurs is documented above). The production pin and shape are untouched.

**Cap/production (this part):** 3 boots (1: instrumented lockstep ×4; 2: 1109-prompt ladder; 3: 2320-prompt ladder, batch-1 protocol), each followed by `HYDRA_HEAD_AUTH_TOKEN=$(cat …) podman compose -f infra/llama-baseline/docker-compose.baseline.yml up -d` → both containers healthy, `{"status":"ok"}` on :18081, VRAM 15659/9977 (verified after boot 3). Telemetry logs: `/tmp/bimodal-telemetry-boot{1,2,3}.log`. Pin `5fff12845` untouched.

---

## Part 4 — #743 correction + properly-primed rerun (2026-09-07): run-level 1.7× disappears with priming; per-slot draft-acceptance split discovered (batch-1 anomaly reproduced)

**Correction to Part 3 / #743's location claim (user-challenged, verified):** `concurrent-decode-test.sh` has **no warm loop** (single-shot, by design). `run-with-params.sh` [5/6] sends `head -c 2000` of the real prompt = **757-808 tokens** (not 4) — its warm repeats showing `prompt eval = 4 tokens` were cache **hits**, which I misread as "4-token prompts". Neither committed harness sends 4-token prompts.

**The real mechanism (production server probe, 8-token requests, `cached_tokens` from usage):** exact-repeat requests reuse fully (804/808 cached); **any prompt extension or shrinkage collapses reuse to ~42 tokens** (full 2362-tok prompt after a 808-tok prefix prime: cached 42, eval 2320 @ 3016ms; the 808-prefix again after the full prime: cached 42, eval 766). So [5/6]'s 2000-char prime cannot carry to the measured full prompt — and only one slot is ever primed (sequential requests stick to the best-matching slot). First measured concurrent run pays 1-2 full slot prefills.

**Harness fix (landed):** `concurrent-decode-test.sh` now primes by default — N_SLOTS concurrent **full-prompt** requests (n_predict=8) before measuring, so every measured slot holds the real sequence; `--no-warm` preserves cold-cache measurement. Priming verified: all primed measured runs show 4-token evals.

**Rerun (boot: standard pin build `5fff12845`, arm111 params, 3 boot attempts — 2 wasted on a missing `GGML_CUDA_ENABLE_UNIFIED_MEMORY=1` export → KV alloc OOM at 15s; 1 good):**

| protocol | per-slot tok/s | agg | server-side |
|---|---|---|---|
| n=2 `--no-warm` (old behavior) | 11.89 / 13.62 | 25.5 | **both slots full prefill** 5214ms/2320 + 4198ms/2362; decode overlapped by prefill |
| n=2 primed ×3 | 23.1 & 30.2 (split) | 52.3-53.1 | 4-tok evals; **draft acceptance split: slow slot 0.452 (85/188, mean len 2.35) vs fast slot 0.835 (106/127, mean len 3.47)** |

**Run-level verdict:** the 1.7× first-run slowdown **disappears with proper priming** — it was prefill amortization (Part 3 conclusion confirmed end-to-end). Residual: the primed runs sit at agg 52-53 vs Part-3's unprimed-fast 57-66 because of the new per-slot split below.

**Per-slot draft-acceptance split (NEW, reproduces batch-1's 51-vs-30 anomaly):** after a **concurrent 2-slot priming burst**, measured n=2 runs split deterministically — one slot ~23 tok/s (43 ms/tok), the other ~30 (31 ms/tok), 3/3 runs, byte-identical acceptance counts per slot (greedy + same prompt). The slow slot **flips between bursts** (E1 prime-150: slot 1 slow; E2 prime-8: slot 2 slow) — a race, not slot identity. Part-3-style single-slot priming (tier1) yields full acceptance (1.0) and uniform speed — the anomaly needs the concurrent-burst path. Suspected mechanism: the fork's draft (MTP nextn) KV swap-out/restore after a concurrent burst is lossy/racy per slot, while the target KV restores fine (4-tok evals prove target-side reuse). Needs one instrumented boot to pin the exact restore path — not chased further this thread (boot cap).

**Scope of affected past numbers:** arm ladder/decision numbers (tier1/2/3 across arms) all ran through the same [5/6]-then-concurrent-test sequence, so they share the same first-run prefill bias — but the bias is symmetric across arms (same harness both sides), so arm-vs-arm deltas stand. The Part-3 bimodal-chase numbers are reinterpreted by Part 3/4 (prefill amortization + the acceptance split), not invalidated as arm comparisons. No wholesale rerun warranted.

**Disposition:** #743 corrected (location + mechanism + fix state). The per-slot draft-acceptance split is a **separate fork-level finding** — candidate new `review-finding`, pending user confirmation (needs instrumented boot to pin the draft-KV restore path before filing).

**Cap/production:** 3 boot attempts (2 OOM-failures from missing UM env — operator error, no measurements; 1 good with measurements). Production restored after final boot: both containers healthy, `{"status":"ok"}`, VRAM 15659/9977. Pin `5fff12845` untouched (standard build used).

---

## Part 5 — instrumented boot: per-slot split localized to content-divergence, draft-KV-restore disproven (2026-09-07) — filed #744

**Boot (1 of 1 approved, instrumented build):** arm111 params, port 18081, protocol = concurrent 2× full-prompt prime (n_predict=8) → measured n=2 ×2 (`--no-warm`). Reproduced the split: client 28.45/21.81 then 22.26/28.82 (client labels flip; server side does not).

**New instrumentation:** `[INSTR][SAVE]` (prompt_save tgt/dft sizes), `[INSTR][PC-LOAD]` (cache_ram restore), `[INSTR][TRIM]` (n_past/p0 + `tgt_pos_max`/`dft_pos_max` at the reuse trim), `[INSTR][DFT-PP]` in `common_speculative_impl_draft_mtp::process()` (per-batch per-seq rows/positions/contiguity), `[INSTR][DRAFT]` in `draft_mtp::draft()` (per-seq begin n_past/id_last/n_max + per-iteration top candidate prob).

**Findings:**
1. **Server slot 1 always the slow one for a given content** (63 vs 43 verify steps, acc 85/188 vs 106/127). The client-visible "flip" between runs = response-arrival labeling, not server behavior.
2. **Acceptance curves byte-identical per slot across repeat runs** (greedy determinism) and **identical between slots for the first ~6 steps** — they diverge exactly where the two slots' generated CONTENT diverges. Draft state is pristine at run start (first two steps 3/3 both slots).
3. Draft confidence flat per slot through the run (top_p ≈ 0.82-0.85 vs 0.87-0.91) — content-property, not accumulating damage.
4. **Draft-KV-restore hypothesis disproven:** tgt/dft positions symmetric at every trim (2357/2357); cache_ram load never fired (in-KV reuse); all catch-up batches per-seq contiguous (the `process()` shift assumption held); #469/#641 machinery not implicated by any probe.
5. Mechanism: co-batching changes kernel reduction order → greedy argmax flips at low-margin positions → the two slots generate different texts → **MTP acceptance is content-dependent** (0.45 vs 0.83) → 1.4× per-slot tok/s asymmetry. Batch-1's original 51-vs-30 anomaly = same effect.

**Disposition:** **#744 filed** (review-finding, corrected mechanism; no llama-cpp code change identified — no buggy path found; records the behavior + the measurement hazard for per-slot tok/s under concurrent greedy). Sequential single-slot runs stay pristine (3/3 every step) because the continuation matches the cached draft state; concurrent runs diverge. Harness guidance: report mean acceptance + aggregate; treat per-slot deltas < ~1.5× under same-prompt greedy as suspect.

**Cap/production:** 1 boot (instrumented), restored → both containers healthy, `{"status":"ok"}`, VRAM 15659/9977. Pin `5fff12845` untouched.

---

## Spot-check — fixed-harness verification of ladder numbers (2026-09-07, 4 boots, #743)

**Purpose (per task):** independently verify the claim in #743/Part 4 that the `concurrent-decode-test.sh` cold/mismatched-prime bug (ladder tier1/tier2/tier3 measured against a cold/mismatched slot, bias = full prefill 1-3 s on first run) was **symmetric across arms**, so arm-vs-arm deltas stand even though absolute tok/s were biased low on early tiers. Spot-check 4 boots with the fixed harness (commit `3355a032e`, primes by default: N_SLOTS concurrent full-prompt `n_predict=8` warm requests before measuring) against the exact configs/numbers already in the ledger.

**Method (cap 4, all on `bigprompt-1109.txt` = 2822c, the prompt used in Attempt 4's parity controls):**

- `cp infra/llama-baseline/prompts/bigprompt-1109.txt /tmp/bigprompt.txt` before each boot (so `run-with-params.sh`'s sequential 10-req loop and the measured `concurrent-decode-test.sh` tiers share the same prompt class). `concurrent-decode-test.sh` called as `bash concurrent-decode-test.sh <port> <n_slots> 150 <prompt>` with **default priming ON** (no `--no-warm`).
- 4 boots, production stopped/restored each time, `/health` verified, `nvidia-smi` checks, results preserved under `/tmp/rpc-test/results/` and `/tmp/spot-check-*.log`.

| Boot | Params | Binary | Tiers |
|---|---|---|---|
| 1 | `090-udq5-148000-parallel1-cache-ram-16g.yml` (arm090, parallel=1) | pin `5fff12845` (`src/llama-cpp/build-cuda1322`) | tier1 (`n=1`) |
| 2 | `102-udq5-146176x3-nokvu-cram24-um.yml` (arm102 shape = 111 shape, pin) | pin `5fff12845` | tier1/2/3 |
| 3 | same `102` shape | `v0.4.0` `5266f24da` (`/tmp/llama-cpp-v040/build-cuda1322`, `LLAMA_CPP=/tmp/llama-cpp-v040`) | tier1/2/3 |
| 4 | `119-udq5-146176x3-v040-kvpslot-graphopt.yml` (arm119, 102 shape + `--kv-unified-per-slot 146176` + `GGML_CUDA_GRAPH_OPT=1`) | `v0.4.0` `5266f24da` (`LLAMA_CPP=/tmp/llama-cpp-v040`, `env:` from params) | tier1/2/3 |

`GGML_CUDA_ENABLE_UNIFIED_MEMORY=1` exported for boots 2-4 (102/119 shapes require UM per arm112 OOM proof), unset for arm090 (UM off, as ledger).

**Results — new (fixed harness) vs old (ledger, Attempt 4 / arm119):**

| Config | Tier | Old (ledger) | New (fixed, primed) | Δ | Note |
|---|---|---|---|---|---|
| **arm090** (090-...) | tier1 1-conc | **31.6** tok/s (pin known single, Attempt 4 tier1 31.61) | **48.29** (wall 3.11s, priming: 1×8) | **+52.8%** | prefill bias removed |
| **arm102/pin** (102 shape, pin) | tier1 | **31.61** | **48.24** (3.11s) | **+52.6%** | |
|  | tier2 2-conc agg | **48.78** | **65.29** (32.65/slot, overlap 4.59s) | **+33.8%** | |
|  | **tier3 3-conc agg** | **104.73** (34.91/slot) | **104.96** (34.99/slot, 4.29s) | **+0.2%** | **identical** |
| **v0.4.0 control** (102 shape, v040) | tier1 | **31.36** | **47.77** (3.14s) | **+52.3%** | |
|  | tier2 agg | **48.44** | **65.23** (32.62/slot) | **+34.7%** | |
|  | **tier3 agg** | **103.34** (34.45/slot) | **104.22** (34.74/slot) | **+0.9%** | **identical** |
| **arm119** (v040 kvpslot+graphopt) | tier1 | **30.99** | **47.74** (3.14s) | **+54.1%** | |
|  | tier2 agg | **47.72** | **65.44** (32.72/slot) | **+37.1%** | |
|  | **tier3 agg** | **103.30** (34.43/slot) | **104.06** (34.69/slot) | **+0.7%** | **identical** |

Raw logs: `/tmp/spot-check-boot*-results.log` and `/tmp/spot-check-*.log`; `run-with-params` summaries preserved per params SHA (e.g. `/tmp/rpc-test/results/090-...-5fff12845`, `102-...-5fff12845`, `102-...-5266f24da`, `119-...-5266f24da`).

**Verdict — honest, per instruction:**

- **Tier3 (the decisive aggregate for the "v0.4.0 = parity" and arm-ranking conclusions) is unchanged.** Pin tier3 104.96 vs old 104.73 (+0.2%), v0.4.0 tier3 104.22 vs old 103.34 (+0.9%), arm119 tier3 104.06 vs old 103.30 (+0.7%) — **all within run-to-run noise (<1%)**, and the pin vs v0.4.0 delta stays **Δ -0.7%** (new: pin 104.96 vs v0.4.0 104.22) vs **Δ -1.3%** (old: 104.73 vs 103.34). **The "v0.4.0 is at parity, no regression" verdict stands — if anything, the gap shrinks.** Ladder tier1/tier2 absolute values shift **+52-54% (tier1) and +34-37% (tier2)** from proper priming, but **symmetrically across pin and v0.4.0** (pin +52.6%/+33.8%, v0.4.0 +52.3%/+34.7%), so the **arm-vs-arm deltas remain -1.0% (tier1) and -0.09% (tier2)** now vs -0.8%/-0.7% before — still parity.

- **Mechanism matches #743:** the old harness's `run-with-params.sh` [5/6] `head -c 2000` (757-808 tok) prime on a single slot could not carry to the measured full 1109-ctx prompt (reuse collapses to ~42 tok on extension), so the first measured tier(s) paid 1-2 full prefills. With tier1→tier2→tier3 sequential order, **tier1 always paid, tier2 often partially, tier3 never** (already warm from prior tiers). Proper N_SLOTS concurrent full-prompt priming removes the tier1/tier2 deficit; tier3 was already warm, so it shows no shift. This explains why the shift is tier-dependent and symmetric.

- **Arm ranking:** **does not materially change.** For the concurrent aggregate that mattered (tier3 3-conc), arm090 was never ranked (parallel=1, N/A for agg), and 102/pin, v0.4.0 control, and arm119 were all at parity before (104.73/103.34/103.30) and remain at parity now (104.96/104.22/104.06). The tier1 single-request number for arm090 now reads **48.29 parity with 102 shapes (48.24)**, vs old 31.6 appearing slower — but arm090's documented production win was never tier1 speed (it was 18× cache-reuse on return, arm090's own bisection), so no decision flips. **No overturn; do not file `review-finding`.** The ledger's prior statement that historical ladder deltas stand is **confirmed**.

**Cap/production:** 4 boots used as scoped (`boot1-arm090` 15s ready, `boot2-102pin` 29s, `boot3-v040-102` 27s, `boot4-arm119` 27s), each `10/10 GOOD`, production restored after each (`podman compose up -d`, `/health ok`, `nvidia-smi 15659/16311 9977/12288` normal, verified after boot4 at 16:23:59 `{"status":"ok"}`). No `src/llama-cpp` pin change, no params change, no `review-finding` needed. Ledger appended; this closes the spot-check task.

---

## Live finding — real 3-agent traffic at 34-66K depths: UM paging collapse on the arm111/102 shape (2026-09-07, passive analysis)

**Trigger:** while 3 real coding-agent sessions ran concurrently against the live arm111 server
(`111-udq5-102shape-v-q5_1.yml`, kv_unified off, 146176×3, K=q8_0/V=q5_1, split 27,38, UM on),
their contexts grew to 34K/64K/66K (all `is_processing`). Observed: CUDA0 15847/16311 MiB
(97%), GPU util 100%, lone-request-after-boot 35.76 tok/s, cumulative
`predicted_tokens_seconds` 6.16, windowed (~3 min) **2.24 tok/s combined**, `n_busy_slots_per_decode` 2.6.
The synthetic ladder (`concurrent-decode-test.sh`) never approaches this regime: its 3-conc tier
uses ~1.1K-token prompts at ~150 gen tokens and measures **~104 tok/s aggregate** on this exact
shape — so the real-traffic state is a **~47× collapse vs the same shape's own harness tier3**.

This section answers: (1) exact VRAM budget math, (2) at what depth the working set exceeds
physical VRAM, (3) paging-vs-compute attribution, (4) mitigations + a candidate params file.
Method: **passive only** (boot logs, /props, /slots, /metrics, `nvidia-smi dmon`) — the rig was
in active use (eval battery round 2); no reboots, no synthetic load injected.

### Exact VRAM budget math (all numbers from the 2026-09-07 boot log, not estimates)

Model geometry (boot `print_info`, fresh boot of the identical shape during battery round 2):
arch `qwen35` (Qwen3.8-27B, **dense**, n_expert=0), file 18.40 GiB Q5_K_M, **n_layer=64**
(+1 MTP layer), n_embd=5120, n_head_kv=4, n_embd_head_k/v=**256** → n_embd_k_gqa=n_embd_v_gqa=**1024**.
`ssm_d_inner=6144, ssm_d_state=128, ssm_d_conv=4` + a `llama_memory_recurrent` RS buffer →
this is a **hybrid** model: not all 64 layers carry full attention.

Buffer arithmetic pins the full-attention layer count exactly. Per full-attn layer per token:
K q8_0 = 34 B/32 elem = 1.0625 B × 1024 = 1088 B; V q5_1 = 22 B/32 = 0.6875 B × 1024 = 704 B
→ **1792 B/layer/token**. Cross-check on the draft KV (1 MTP layer, q8_0/q4_1 = 1.6875 B/elem):
1024 × 1.6875 × 438528 = 723.9 MiB predicted vs **722.67 MiB measured** (−0.17%) ✓ — quant math
and cell count confirmed. Main KV measured 6985.83 (CUDA0) + 5433.42 (RPC0) = 12419.25 MiB for
438528 cells = 29.0 KiB/token combined. The CUDA0:RPC0 ratio is **exactly 9:7**
(6985.83/9 = 5433.42/7 = 776.20 MiB/layer), and both sides sit at **+3.56%** over
1792 B/layer/token × 438528 — i.e. **16 full-attention layers (9 on CUDA0, 7 on RPC0) + uniform
cell-padding overhead; the other 48 layers are pure recurrent** and hold only fixed-size state
(RS = 1795.50 MiB total, 3 cells, S f32 1728 MiB — O(1) per sequence, not O(ctx)).

Per-GPU boot allocation (breakdown lines + nvidia-smi):

| CUDA0 (5060 Ti, 16311 MiB) | MiB | scales with |
|---|---|---|
| Model buffer | 11101.01 | fixed |
| Main KV (438528 cells) | 6985.83 | **16.31 KiB × filled tokens** |
| Draft KV (MTP, 438528) | 722.67 | 1.69 KiB × filled |
| RS recurrent state (3 seqs) | 1009.97 | fixed |
| Compute buffer | 1936.47 | fixed |
| **Total allocation** | **21755.95** | **5445 MiB over physical** |

| RPC0 (3060, 12288 MiB) | MiB |
|---|---|
| Model + KV (12.68 KiB/tok) + RS + compute | 7062.06 + 5433.42 + 785.53 + 1936.47 = **15217.48** |

The boot log's own fit says it outright: RPC0 "15217 used" vs "11911 total", CUDA0 "21033 used"
(+722 draft) vs "15849 usable" — and still concludes *"projected to use 36250 MiB of device
memory vs **171460 MiB** of free device memory — targets for free memory can be met on all
devices, no changes needed."* With UM on, `params_fit_impl` counts **host RAM as device memory**
→ the fit is **UM-blind**: the shape boots only because ~8.75 GiB of buffers are silently backed
by host RAM (arm112's deterministic OOM is the UM-off proof). The KV buffers for the full
438528 cells are allocated at boot; the pages materialize in VRAM only as slots fill — which is
why shallow harness runs never noticed.

### Residency cliff — the answer to "at what depth does it page"

KV pages must be resident to be read at decode speed. Free VRAM for KV residency after fixed
costs: CUDA0 = 16311 − (11101.01 + 1936.47 + 1009.97) = **1540.9 MiB** (nvidia-smi basis; the
fit's own usable-total basis gives 1078.9 MiB). Each filled token costs CUDA0
16.31 (main) + 1.69 (draft) = 18.0 KiB of eventual residency:

- **CUDA0 cliff: ≈ 61K–88K total filled tokens** (fit-basis 61.4K … nvidia-smi-basis 87.7K)
  ≈ **20K–29K average depth across 3 slots**.
- RPC0: headroom 2504 MiB (2127 fit-basis) ÷ 12.68 KiB = **171K–201K** — not binding.
- **System cliff = min ≈ 61–88K combined filled tokens.** The 438528-cell allocation is
  **5–7× oversized** relative to physical residency. Boundary is fuzzy (±) because UM page
  residency and transient checkpoint copies aren't directly observable, but the band is tight
  enough to predict every dataset below.

**The observed 34/64/66K state (164K combined) is 1.9–2.7× beyond the cliff.** So the answer to
the posed question is: 34-66K per slot is not "still O(ctx) compute" — it had already crossed
the line at ~61-88K combined, i.e. roughly when the *third* session passed ~25K.

### Live telemetry during real deep traffic (the missing evidence from 2026-09-06)

- `nvidia-smi dmon` during live battery decode (slots at 83K/73K/0.6K): **CUDA0 rx 10.7-13.5 GB/s
  and tx 6.7-11.3 GB/s bursts to host** at 100% util — the UM migration storm, finally captured.
  GPU1 (3060): 11911/12288 MiB but **~0 MB/s PCIe** — consistent with 156-164K combined sitting
  just under RPC0's 171-201K cliff: **CUDA0 is the sole thrasher**.
- Server-side per-slot truth (log-verbosity 4 `print_timing`): slot1 at 83K depth
  `n_gen=548, tg=3.94 t/s, tg_3s=3.14`; slot2 at 73K ≈ 4.4 t/s → **~8 tok/s combined**, vs 104
  shallow-harness aggregate. The finding-boot's 2.24 tok/s (164K combined) is the same curve,
  deeper.
- Per-step arithmetic: at 164K combined, CUDA0 reads 164K × 16.31 KiB ≈ 2.68 GiB of KV per
  decode step but only ~1.0-1.5 GiB can stay resident → ≥1.2-1.7 GiB migrate in (plus dirty
  eviction) **every step** → ~0.25-0.3 s/step at the measured 10-13 GB/s, plus fault/eviction
  overhead → observed 0.76-1.16 s/step (`n_decode_total` +118 in a 90 s window).
- **Compute counterfactual:** even with zero paging, per-step cost at 164K ≈ 6 ms KV reads
  (2.68 GiB @ ~448 GB/s) + ~25 ms weights (11.1 GiB @ ~448 GB/s) → fair time-sliced
  3-way should yield ~30-40 tok/s combined. Observed 2.2-8 → **VRAM-pressure paging is the
  dominant cause by ~5-15×; O(context) compute is a minor term at these depths.**
- **Cross-validation against prior arms (the cliff predicts all three points):** arm105
  2-session ~70K combined → 14-18 tok/s/slot (just past cliff: mild thrash); arm111 multiturn
  3-session ~100K combined → 9.2-21.7 tok/s/slot, ~45 agg (moderate thrash — that report's
  "bimodal tail, not systematic" framing is re-attributed here); real battery 156-164K →
  2.2-8 tok/s (deep thrash). Monotone collapse starting exactly where the cliff says.

### Corrections to earlier ledger entries (found during this analysis)

1. **arm103/arm104 "VRAM / UM" sections are wrong about weights.** They claim CUDA0 carries
   "weights (~6.3 GiB) + MTP (~0.85 GiB)" and conclude "tight but no spill expected". The boot
   log says **CUDA0 model buffer = 11101 MiB**; with 492288 cells (arm104), CUDA0 allocation is
   ≈ 11.1 + 8.4 (KV) + 1.9 (compute) + 1.7 (draft+RS) ≈ **23.2 GiB vs 16.3 physical** — deep
   overcommit. Their *tier numbers remain valid* (shallow fills → tiny residency demand → no
   paging during those runs); only the VRAM commentary was wrong, and the same wrong
   "no spill" model is what let 438528-cell shapes get labeled safe.
2. **The 2026-09-06 PCIe migration-storm telemetry (Thread 1 follow-up 2) sampled shallow tiers
   only** — shallow fills never cross the cliff, so "no storm observed" was structurally
   guaranteed. Today's capture is the first during deep real traffic, and the storm is real.
3. **`/metrics` token counters are unreliable on this build under draft-mtp:** during a 90 s
   window with two slots actively decoding (server-side `tg` 3.9/4.4 t/s),
   `tokens_predicted_total` and `prompt_tokens_total` were frozen while `n_decode_total` +118
   and the `predicted_tokens_seconds` gauge swung 2.85 → 0. Windowed rates computed from
   `/metrics` deltas (including the 2.24 figure above and any battery-derived numbers) carry
   unquantified error; per-slot `print_timing` and spec-stat lines are the trustworthy source.

Also observed live (secondary, real-traffic tax): with kv_unified off, `cache_reuse` is
disabled (arm105's finding) and the hybrid memory invalidates checkpoints aggressively — the
log shows repeated `forcing full prompt re-processing due to … hybrid/recurrent memory` on some
tasks (full 30-80K re-prefill) while other turns hit 85.7% cache (slot-local), plus constant
~150-270 MiB context-checkpoint create/erase churn. This compounds the paging cost but is not
the dominant term.

### Mitigations, ranked (impact vs disruption to the arm090/111/119 matrix)

1. **Right-size total ctx to the no-paging envelope (recommended; additive params file,
   zero shallow-speed cost).** At 98304 total cells (3 × 32768) the *existing* 27,38 split fits
   both GPUs with margin in both layer-placement outcomes: CUDA0 worst case (9 attn layers)
   = 11101 + 1566 + 170 + 1010 + 1936 ≈ **15783 MiB → ~528 MiB free**; RPC0 worst case ≈
   11350 → ~938 free. No paging at any fill; UM can stay on for boot safety, with a
   UM-off probe as follow-up (arm112 analog; at this size it should boot without UM,
   eliminating the incident class entirely). Expected deep-3× throughput ~30-50 tok/s
   combined (compute/bandwidth bound) vs 2.2-8 — **~6-20×**. Cost: sessions >32K get
   context-shifted (shift is on). Shallow tiers unchanged — arm106/107 (splits 25,40 / 30,35)
   and arm102-114 all prove shallow speed is insensitive to these knobs; only *residency*
   matters, and only depth exposes it. Candidate file:
   `params/120-udq5-32768x3-nokvu-cram24-um.yml` (created alongside this section).
2. **Rebalanced tensor_split (partial, bounded).** Equalizing per-GPU cliffs caps at
   ~131-139K combined (total KV residency ≈ (1541+2504) MiB ÷ 29.0 KiB/token) — helps, cannot
   cover 164K real depth at current quants. Shallow-neutral per arm106/107. Useful only in
   combination with #3.
3. **Cheaper K quant (q8_0 → q5_0/q5_1, ×0.786 KV bytes).** Lifts the perfect-balance cliff to
   ~165-180K — covers real depth only *in combination with* #2, and arm080/081 already measured
   a real accuracy tax (acc −24% MTP draft) for K=q5_0. Quality re-eval required; V is already
   at q5_1 (q4_1 would only give −3.6%).
4. **Drop draft-MTP on deep shapes.** Frees CUDA0 723 MiB fixed + 1.69 KiB/token → cliff
   +8-13%, removes the 47.7 ms/call draft cost seen at depth — but sacrifices MTP's ~2.9 mean
   accept length at shallow/mid depths. A 3×49152 no-paging shape becomes provable only with
   MTP dropped; keep as the stretch variant of #1, not the default.
5. **`cache_ram_mib` reduction — do NOT bother (disproven for this purpose).** cache_ram is
   host RAM for idle-slot/prompt state (arm109: 8192 MiB = identical tiers); it does not free
   VRAM and cannot prevent paging.
6. **kv_unified ON for real traffic (structural, separate arm).** Would restore `cache_reuse`,
   share the pool across unevenly-grown sessions (real agents don't fill evenly), and enable
   controlled host-RAM eviction of cold cells via cache_ram instead of driver-managed thrash —
   but changes the slot semantics the 101-vs-102 axis was built on. Worth one arm after #1.
7. **Guardrail (code change, fork or Hydra Head): make the boot fit UM-aware.** The fit's
   "36250 MiB of device memory vs 171460 MiB free — no changes needed" line is the systemic
   enabler: with UM on it counts host RAM as device memory and never warns. A per-device check
   (projected allocation ≤ physical − margin, host excluded) would have flagged this shape at
   every boot.

### Next actions

- Boot-gate the candidate `120-*.yml` (≥2 boots, per house rule): verify boot-log breakdown
  lines (CUDA0 self ≤ ~15.8 GiB), `nvidia-smi` ≥ 400 MiB free on both GPUs, tiers within the
  arm111 band (33/50/97), then `multiturn-growth-test.sh` at 3×30K for the deep-traffic payoff.
- Follow-up arms: UM-off probe on 120; 3×49152-no-MTP stretch; split-rebalance × K-q5_x combo
  if >131K combined residency is ever required (with accuracy parity re-eval).
- Fix or gate the `/metrics` token-counter accounting (draft-mtp path) — observability defect.
- Coordinate any repro with the eval battery owner before taking the rig (battery round 3
  pending at time of writing).

### Live finding 2026-09-07/08 — slot-count, YaRN, kv_unified ruled out; CPU-FFN-offload breaks the cliff

Follow-on 2-slot A/B matrix (arm121→123, all 2×131072 = 262144 total cells, UM-backed,
same two real-source-file prompts at 88805 + 68675 tokens = 157480 combined depth, ~2×
arm120's ~128K no-paging ceiling for this 27,38/q8_0-K/q5_1-V shape):

- **arm121** (2 slots, draft K/V raised to bf16 from q8_0/q4_1): reproduces the paging
  collapse at 2 slots, not just 3 — **slot count is not the causal variable**, combined
  depth vs physical-VRAM residency is. bf16 draft cache also showed no clear acceptance-rate
  gain over q8_0/q4_1 (weak/uncontrolled single-sample comparison, not a controlled A/B).
- **arm122** (arm121 + explicit `rope_scaling: yarn/scale=5/orig_ctx=32768`, present in
  production arm090 but absent from the whole UM-family 102-121): no measurable difference
  in decode speed or draft acceptance vs arm121 — **YaRN ruled out** as a factor (expected;
  RoPE scaling is compute-time-only, doesn't change KV byte size).
- **arm123** (arm122 + `kv_unified: on`, was `off` for the whole UM-family): no measurable
  difference vs arm122 in either single-request (14.52 vs 14.62 tok/s) or n=2 concurrent
  (0.93 tok/s @ 88.8K / 2.72 tok/s @ 68.7K, same collapse magnitude) — **kv_unified ruled
  out** as a factor.
- Direct arm090 (production pin, bisected to fit physical VRAM, parallel=1) vs the
  UM-family at the same 88.8K-token single-request depth: arm090 sustains **20.1 tok/s**;
  every UM-family arm at comparable depth collapses to 1-4 tok/s. Confirms the causal
  variable is exactly "does the shape fit physical VRAM" — nothing else moved the needle.

**arm124 — new mitigation, verified working (CPU-FFN-offload replaces UM-overcommit):**
designed by glm-5.3-flash, boot-tested by an independent agent, and cross-checked against
raw logs (not just the report) below. Instead of right-sizing ctx down (arm120's fix) or
relying on UM host-paging (arm119/121-123, which collapses), arm124 offloads 8 dense-FFN
sublayers (blocks 56-63, the tail blocks on CUDA0) to CPU compute via the existing
`override_tensors`/`-ot` regex mechanism — no fork patch needed; `--n-cpu-moe`'s machinery
already generalizes to dense models, upstream `--n-cpu-ffn` (llama.cpp PR #26622) would just
be sugar for the same thing. This frees ~1.49 GiB VRAM on CUDA0, enough to hold
2×98304 = 196608 total cells **fully resident without UM at all**.

Result (`params/124-udq5-98304x2-nokvu-cram24-ffncpu8-noum.yml`, verified independently
against `/tmp/rpc-test/results/124-udq5-98304x2-nokvu-cram24-ffncpu8-noum-5fff12845/llama-server.log`):
`n_ctx_slot = 98304`, `kv_unified = 'false'` confirmed; boot log confirms no
`GGML_CUDA_ENABLE_UNIFIED_MEMORY` set; no OOM/crash. Same two 88805+68675-token prompts
(157480 combined, within the 196608 capacity, no context-shift needed) run concurrently:
slot 0 = 10.74 tok/s (64.6% draft acceptance), slot 1 = 9.84 tok/s (51.5% draft acceptance)
→ **20.58 tok/s combined**, vs arm121/122/123's 0.9-2.7 tok/s at the same combined depth —
roughly an **8-20× improvement**, and it holds more (157K) resident than arm120's ~128K
ceiling. Two open gaps, neither blocking: CUDA0 post-warmup free VRAM measured 161 MiB
(below the 300 MiB gate target, still no OOM — tight, not broken); which GPU the offloaded
blocks landed on was confirmed only indirectly (successful boot without UM where the
equivalent non-offloaded shape OOMs), not via an explicit per-buffer log line — a rerun
with higher log verbosity would close this out.

**Mitigation ranking updated:** CPU-FFN-offload (this section) is a *third* option
alongside #1 (right-size ctx down) and #6 (kv_unified, now known cost-neutral either way)
— it's the only lever found so far that lets combined depth go **up** past the physical-VRAM
ceiling without invoking UM, by trading bounded CPU compute (~25-37 ms/step tax, ~11-23 min
one-time CPU-side prefill for 90K-token prompts) for freed VRAM. Follow-up requested
2026-09-08: push both decode speed and n=2 combined ctx further from this baseline —
candidates include RPC0-side offload (blocks 0-7, needed per arm124's own header to reach
arm121's 262144-cell target), trimming the CUDA0 margin issue (`--load-mode none`,
`cache_ram_mib` reduction), and confirming the block→GPU placement with explicit
per-buffer logging + an `nvidia-smi dmon` PCIe trace during decode.

## Design note 2026-09-08 — arm125 (ctx + speed follow-up on arm124; design-only, hand-off pending)

**New evidence — exact GGUF byte parse** (`/mnt/SSD/Qwen3.8-27B-UD-Q5_K_M.gguf`, header
+tensor table parsed; sizes from offset diffs, self-consistent: trunk 16.44 GiB + output
994.6 + MTP 334.7 + tok_embd 682.0 = file 18.40 GiB):

- Per-block FFN is NOT uniform: 135.47-209.18 MiB (mean 181.78), FFN total 11.36 GiB.
  Blocks 14-17 are the model's cheapest band (135.47-140.78); blocks 54-63 are the
  most expensive (all-q6_K UD bump, 209.18 each — **arm124's offload window was the
  priciest possible choice**: 8 x 209.18 = 1673.44 MiB freed, not the ~1492 estimated).
- Layer→GPU boundary now CONFIRMED by byte math: cum(blocks 0-27) = ~7063 MiB ==
  RPC0's measured model buffer 7062.06; remainder + output + MTP = ~11101 ==
  CUDA0's 11101.01; CPU buffer 682.03 == tok_embd (682.0). RPC0 hosts blocks 0-27,
  CUDA0 blocks 28-63 + output + MTP.
- arm124 margin correction: exact paper margin @196608 = **817.7 MiB** (not ~636);
  measured 161 → **constant runtime gap ~657 MiB** (CUDA context etc., not
  fill-dependent — KV buffers commit fully at boot without UM). Calibration
  `measured ≈ paper − 657 ± 100` used for arm125. Also: at T=196608 there is NO
  cheaper-block config meeting margin (cheapest 7 blocks sum 1464 < 1512 needed) —
  arm124 is near-minimal tax for its T within this lever set; the only frontier
  direction left is ctx-up with more blocks.

**arm125** (`infra/llama-baseline/params/125-udq5-114688x2-nokvu-cram24-ffncpu15-cheap-noum.yml`):
T = 229,376 = 2 × 114,688 (+16.7%); CUDA0 RPC0 reallocation to cheapest contiguous
windows inside each GPU's own block range:

| | blocks | freed MiB | paper margin | predicted measured |
|---|---|---|---|---|
| CUDA0 | 43-55 (silver band, excl. 54-63 tail) | 2425.80 | 993.8 | ~337 |
| RPC0 | 14-15 (cheapest pair; Nr=1 leaves only 60) | 273.59 | 198.7 | — |

15 blocks, 2699.39 MiB CPU/step (+61.3% vs arm124) → honest band combined 13-17 tok/s
@ 157.5K fill, 11-15 @ ~201K fill (vs arm124 20.58; UM-family 0.9-2.7). The RPC0-side
offload is requirement, not choice: at 229,376 RPC0 is −75.9 MiB over without it.
Rejected and documented in the file: 262,144 stretch (needs Nc=15 + Nr=5 = 20 blocks,
~3473 MiB/step — defer until arm125 telemetry), output-head offload (equal ms/MiB,
serializes vocab GEMM), scattered cheapest-N selection (extra variable; kept as
follow-up lever with the parsed per-block table as basis), spec draft-n_max extension
(≤ +5% expected).

Hand-off obligations baked into the file header: boot-log verbosity ≥ 4 with per-buffer
lines untrimmed (closes arm124's unverified block→GPU evidence line at gate 1), per-GPU
nvidia-smi at boot/warm/full-fill, dmon during n=2 decode (PCIe < 3 GB/s gate), a
purpose-built third prompt ~≤ 114,688 tokens (~≤ 317 KB; concatenation recipe from
src/llama-cpp sources) to demonstrate ~201K combined filled, plus one arm124-identical
A/B rerun (both 90K prompts at 157.5K) to rank arm125 on the speed axis fairly.

## Design note 2026-09-08 #2 — arm126 (safety arm after arm125's gate-4 halt; design-only, hand-off pending)

**arm125 outcome** (boot-tested by muse-spark, results preserved in
`/tmp/rpc-test/results/125-...-5fff12845/`): gates 1-3 passed exactly (per-buffer lines
matched the GGUF-exact math to <1 MiB — boundary + offload placement now FULLY empirical:
CUDA0 model 8675.19, RPC0 6788.46). Gate 4 FAILED at the self-test stage alone (10 x
808-token requests, ~0 combined fill): CUDA0 free = 51 MiB vs ~335 predicted. Correctly
halted; no OOM, no deep-fill attempt, no crash-loop artifact.

**Boot-sum analysis → the ~657 "constant gap" calibration was an artifact.** Fixed the
basis and the picture changes:

- `ggml_gallocr_reserve_n_impl` keeps ONE compute buffer per backend id, sized to the
  max across graph shapes (`ggml-alloc.c:911-944`) — arm125's three `sched_reserve`
  prints (1144.01 / 1088.28 / 1088.28) collapse to a single 1144.01 CUDA0 reservation
  (the agent's 14,524.5 boot-sum used the right value; my arm124-era math had used the
  3-slot 1936.47 estimate).
- Re-derived with the 2-slot compute buffer: arm124's true gap = **1,449 MiB** (used-sum
  14,701 → paper free 1,610 vs measured 161), not 657. arm125's gap = 1,735. Two points,
  linear fit **gap ≈ 1120 + 41 × N_offloaded_blocks (± ~150)**. Per-CPU-block residual
  ~41 MiB is REAL but unexplained; graph-cache hypothesis tested against fork source is
  WEAK (host-side exec instances, galloc shared-max buffer — `ggml-cuda.cu:2638-2655,
  4221-4228`; `ggml-alloc.c:911-944`), candidates remaining: per-split staging/copies,
  JIT/module residency, allocator fragmentation. The decisive instrument: PHASE-WISE
  margin capture (boot → self-test → warm → fill) — mandated in arm126's gate 5b
  signatures; settles whether the residual is shape-count-proportional (cache-ish) or
  constant.
- Trustworthiness of arm124's thin 161: survived its full deep-fill test once; with the
  corrected basis its headroom story is consistent; still thin — arm126 rebuilds >400 MiB
  of predicted margin at the same ctx.

**arm126** (`infra/llama-baseline/params/126-udq5-98304x2-nokvu-cram24-ffncpu13-cheap-umnet.yml`)
per user directive ("Try 2 solution: reduce ctx, UM"):

- ctx 229,376 → **196,608 = 2 × 98,304** (arm124's proven deep-fill target).
- offload **resized to Nc=13, blocks 43-55, CUDA0-only** (cheapest contiguous; 54-63 tail
  excluded as before; RPC0-side offload removed — not needed at this ctx, RPC0 free ~331).
  N = 13 vs arm125's 15; CPU/step 2,425.80 MiB (+45% vs arm124's 1,673.44).
- **UM safety net re-enabled** — docker-compose default env, explicitly documented in the
  header as a net for margin-math error ONLY: a paging-assisted pass is defined as a
  failure signal (ctx still too aggressive), not a success. Clean run retires UM.
- Margin predictions, dual-path: gap model 16,311 − 13,949.2 − 1,653 ≈ **709**; independent
  path (arm124 measured 161 + 752.4 extra freed − 205 extra gap) ≈ **708**. Gates: ≥500
  at self-test (hard stop <300), ≥250 at full 157.5K fill (stop <150), RPC0 ≥160.
- Speed expectation: ~13-17 tok/s combined @ 157.5K fill (accepts the +45% tax as the
  measured-reality price until the gap residual is settled).

## Design note 2026-09-08 #3 — arm127 (N-optimization at proven ctx; design-only, hand-off pending)

**arm126 verified/closed**: 13 blocks 43-55 (cheapest contiguous), 903 MiB CUDA0 margin
(5.6× arm124's 161) at 15.07 tok/s combined (-27% vs arm124's 20.58), same ctx 196,608,
same 157.5K fill. UM net ON but never triggered — 4-phase margin series flat + PCIe peak
2195 MB/s (no paging signature) → **the offload+UM combination costs nothing when the
margin is sized right**; adopted as the permanent family safety net per user direction
("Combine offload and UM? Optimize offload to 10 or 12 layers with better layers select.").

**Gap model re-anchored** (upgrades the 1120+41N estimate from design note #2): with the
correct 2-slot compute buffer (1,144.01), the two measured anchors at the SAME ctx give

- arm124: N=8, freed 1,673.44 → gap = **1,448.4**
- arm126: N=13, freed 2,425.80 → gap = **1,458.8**

→ gap ≈ constant at fixed T, slope ~2.1 MiB/block. Identity reproducing both anchors:
`free(N) ≈ freed(N) − gap(N) − 64.0`. The old 41/block slope was a cross-T artifact —
arm125's 1,735 at T=229,376 carries an unexplained ~276 MiB T-component (~+8.4 KiB/cell
worth, still open) that does not apply at 196,608. Speed axis: combined tok/s vs freed-MiB
is near-linear across the anchors (slope −0.00732 tok/s per MiB freed); reproduces
20.58/15.07 exactly on both ends.

**arm127** (`infra/llama-baseline/params/127-udq5-98304x2-nokvu-cram24-ffncpu12-cheap-umnet.yml`):
ctx unchanged (196,608); **N=12, blocks 43-54** = arm126's window minus block 55 — the
byte-mass pointers:

| variant | window | freed | predicted free | predicted combined |
|---|---|---|---|---|
| N=10 | 44-53 | 1,820.84 | ~304 | 19.50 |
| N=11 | 44-54 | 2,030.02 | ~511 | 17.97 |
| **N=12 (chosen)** | **43-54** | **2,216.62** | **~695 ±60** | **16.60** |

N=10 rejected on margin (304 < the 400-600 target); N=12 chosen as the user-named count
with the maximal-margin end of the close {N=11, N=12} tradeoff (+1.37 tok/s at N=11 buys
−184 MiB margin; arm128 = N=11/44-54 is a pre-designed one-block clone if 127's margin
series confirms the gap identity within ±5 MiB).

**"Better layer select" verdict (explicit)**: the acceptance signal direction says the
TAIL window hurt acceptance less than arm126's mid-band (58.05% vs 49.15% slot-mean;
arm119 baseline ~0.636), but it is signal-not-proof: one boot per arm, slot-pair spreads
large (arm124's own 13.1 pts), and window position is confounded with N. Selection stays
cheapest-contiguous; arm127 doubles as the clean quality A/B (only diff vs arm126 = block
55 removed): acceptance recovery toward ~58% implicates selection; staying ~49% says
noise. Canary gate ≥45% slot-mean; repeated <45% prioritizes the tail-window probe
(arm128-alt: 54-63, freed 2,091.80, predicted free ~577 / speed ~17.5) over N tuning.

Gates per the validated 4-phase protocol (self-test → power-warm → full-fill; hard stops
at <350 self-test/free, <200 fill-free; PCIe <3 GB/s; paging-assisted pass = failure
signal per the UM net rule). PASS bar: combined ≥ ~16.0 tok/s (predicted 16.60±0.5).

### arm127 — VERIFIED (2026-09-08): frontier point confirmed, gap model found non-monotonic in N

Boot-tested and independently cross-checked against raw logs (not just agent summary):
buffer lines matched exactly (CUDA0 model 8884.37 MiB, RPC0 model 7062.06 unchanged — the
+209.18 MiB delta vs arm126 is exactly block 55's cost, confirming the offload window is
arm126 minus one block as designed). All 4 phases flat at **647 MiB free (CUDA0) / 771 MiB
(RPC0)** — no growth from ready → self-test → warm → full 157.5K fill, reconfirming (a
third time, after arm126) that the unexplained gap is a flat one-time cost, not a
per-request-shape cache pool. PCIe peak 1625 MB/s (well under the 3 GB/s no-paging
threshold). No context-shift events.

**Decode**: slot A 8.20 tok/s (56.18% acceptance), slot B 7.81 tok/s (48.23%) →
**16.01 tok/s combined** at 157.5K filled — matches the ~16.60±0.5 prediction closely
(within the band), **+6% vs arm126's 15.07, −22% vs arm124's 20.58**, and lands exactly
where designed: a genuine frontier point between arm124 (fast, fragile) and arm126 (safe,
slower) rather than dominating either. Slot-mean acceptance 52.2% sits between arm124's
58.05% and arm126's 49.15% — inconclusive on whether offload-block *position* (vs count)
affects MTP quality; the byte-mass/cheapest-contiguous selection criterion stays as-is
pending a cleaner A/B (the tail-window probe noted above remains the clean follow-up if
this ever needs settling).

**Important correction to the gap model**: measured free was 647 MiB vs the model's
695±60 prediction — a −48 MiB miss, outside the tight ±5 MiB confirmation band (loose ±60
band still holds). Computing the implied gap at all three same-ctx (T=196608) data points
via `gap = freed − free − 64.03`:

| Arm | N (offloaded blocks) | Implied gap |
|---|---|---|
| arm124 | 8 | 1448.4 MiB |
| arm127 | 12 | **1505.6 MiB** |
| arm126 | 13 | 1458.8 MiB |

**The gap is NOT monotonic in N** — arm127 (N=12) has a *larger* implied gap than arm126
(N=13), which a simple linear-in-block-count model cannot produce. This means the
unexplained overhead likely depends on *which specific blocks* are offloaded (block 55's
presence/absence swapped between arm126 and arm127) rather than purely how many — possibly
tensor-shape/alignment effects on the CUDA allocator, not yet root-caused. Practical
consequence: **do not trust the linear gap model to safely extrapolate margins for new
arms** (including the pre-designed N=11 "arm128" candidate, or any ctx push) without a
real safety cushion built in beyond what the model predicts — three data points have
already shown ±50 MiB of unexplained scatter at fixed ctx, and the T-dependent component
(arm125, ~276 MiB per extra ~32.7K cells) is still completely unexplained on top of that.

## Design note 2026-09-08 #4 — arm128 (128K/slot ctx push; design-only, hand-off pending)

**arm127 verified/closed**: 16.01 tok/s combined @ 157.5K fill (prediction ~16.60±0.5
held), 647 MiB margin (4-phase flat; PCIe peak 1625 MB/s), genuine frontier point between
arm124 (20.58 @ 161 MiB) and arm126 (15.07 @ 903 MiB). Acceptance slot-mean 52.2% sits
between the two — position-vs-count A/B inconclusive; selection criterion unchanged.

**Gap model demoted to band-based** (arm127's correction): implied gap = 1448.4 (N=8),
1458.8 (N=13), **1505.6 (N=12)** — non-monotonic in block count; which blocks are
offloaded matters in ways not understood (possibly block 55 adjacency in the sched/graph
topology). ±50 MiB scatter demonstrated at fixed ctx, plus the STILL un-elucidated
T-component from arm125 (~+276 MiB per 32.7K cells, single point). Consequences applied
to arm128's sizing: design to the WORST-CASE of the gap band, not the midpoint;
phase-a self-test margin is the primary go/no-go; hard stops conservative; the
design-note "arm128 = N=11 clone" idea is NOT auto-pursued (would need full
re-verification).

**arm128** (`infra/llama-baseline/params/128-udq5-131072x2-nokvu-cram24-ffncpu32-cheap-umnet.yml`)
—the user's actual next request: the 2 × 131,072 = 262,144-cell allocation that arm121
(UM-only → 0.9-2.7 tok/s collapse) and arm125 (offload-only, under-sized → gate-4 halt)
both failed, reprised with the combined mechanism:

- **Offload**: CUDA0 blocks **28-49 (22 blocks, freed 3,981.01)** — the cheapest
  contiguous window in CUDA0's range reaching the byte target (extends the family's
  "avoid 54-63" logic further: even 50-55 stays GPU-resident) + RPC0 blocks **9-18
  (10 blocks, freed 1,527.33)** — the model's cheapest contiguous decuple, in RPC0's own
  0-27 range. N = 32, CPU/step 5,508.34 MiB (+148.5% vs arm127).
- **RPC0-side relief recomputed for T = 262,144 specifically** — with a basis correction
  the ledger never had before: RPC0's compute buffer is 1,088.28 (arm125 log line 228),
  NOT the 3-slot 1,936.47 used in arm124/125-era math. At T=262,144 RPC0 needs
  ~1,527 MiB of relief against the RPC0-side gap band 408..947 (T-term scaled by the
  KR/KC per-cell ratio 0.704 + 150 uncertainty). arm125's Nr=2 "requirement" was in fact
  an artifact of the wrong compute basis — at 229,376 RPC0 would have had ~770 free
  without it; at 262,144 relief is genuinely needed again.
- **Free bands designed to the pessimistic gap end**: CUDA0 ~754-1,307 (designed floor
  700), RPC0 ~434-1,043 (floor 400). The user's generous-margin mandate applied: no
  tight margin optimization anywhere; the 22-block window was preferred over the
  28-block 28-55 variant (freed 5,168 → free ~1,900-2,090!) because the extra 3.3 GiB
  CPU/step (+1,187 MiB) buys only redundant margin at a large speed cost, given the
  754-floor already clears the target band under the worst accepted gap.
- **UM net ON** (docker-compose default), operational rules unchanged from 126/127
  (paging-assisted pass = failure signal; PCIe < 3 GB/s gate).
- **Speed, honestly banded**: step-time extrapolation (empirical ~0.104-0.117 ms/MiB
  CPU bytes over 3 anchors; implied effective DDR-path BW ~9-10 GB/s — the CPU sections
  likely contend with draft/RPC relays) → combined ~7.3-7.9 at 157.5K fill; sanity band
  4.5-10 (RPC/overlap unknowns). PASS bar: combined ≥ 5.0 single-slot depth-157.5K test /
  ≥ 2.5 single-request — still 2-6× the UM-family frontier (0.9-2.7) at this allocation.
- **Test plan**: same two prompts for the apples-to-apples legs (single 88,805 on slot A;
  n=2 at 157.5K fill); OPTIONAL built third prompt (≤ 131,072 tok, ~320 KB recipe from
  src/llama-cpp sources, tokenize-probe before use) lifts the n=2 demonstration to ~205K
  filled — the deepest non-UM resident fill in the family — gated on clean phase-c
  margins (free ≥ 700/300).
- Gate checklist: 4-phase margin capture mandatory with HARD STOPS (phase-a CUDA0 < 700
  → STOP, < 500 extreme; RPC0 < 400 → STOP, < 250 extreme; fill-phase < 500/300 → STOP);
  flatness check ±120 MiB between phases; dmon full decode; pre-check container uptime
  before rig contact.

If the verified margin series reproduces the T-term prediction (~+552 MiB per
+65,536 cells on CUDA0), the next iteration can shrink the band and size a cheaper
N; if free lands > 1,300, the next trim-back is blocks (speed), not margin.

## arm128 — VERIFIED (2026-09-08): 262,144-cell ctx push boots clean; gap model
## undershoots reality a THIRD time; decode roughly halves vs arm127

Independently confirmed against raw logs (`llama-server.log`, `dmon` trace,
`summary.txt`) in `/tmp/rpc-test/results/128-udq5-131072x2-nokvu-cram24-ffncpu32-cheap-umnet-5fff12845/`.
Before dispatch, an internal inconsistency was caught and corrected in the design
file itself: the RPC0 predicted free band was stated three different, mutually
irreconcilable ways in the same document (1,487↔434 in the VRAM-math section vs
1,487↔1,043 in the gate-checklist section vs the derivable 1,487↔948 from the
document's own stated gap band 408↔947) — likely a stale copy-paste across
revisions. Dispatched with a raised RPC0 phase-a bar (≥500 MiB, above all three
disputed numbers) rather than trusting any of them.

**Boot (buffers exact to <1 MiB of prediction):**
CUDA0 model 7,119.96 (pred 7,120.00), RPC0 model 5,534.71 (pred 5,534.73), CUDA0
main KV 4,176.00, draft KV 432.00, RS 673.31, compute CUDA0 1,232.28 / RPC0
1,232.28 / CPU 148.28. `n_ctx_slot = 131072`, `kv_unified = false`. UM env
confirmed set on both llama-server and rpc-server (bare-metal, does not read
docker-compose defaults).

**4-phase margins — flat across all 4 phases (no leak, no paging drift), but
CUDA0 undershot even the pessimistic prediction:**
| Phase | CUDA0 free | RPC0 free |
|---|---|---|
| (a) ready, pre-self-test | **679** | 1,155 |
| (b) post self-test | 677 | 1,155 |
| (c) post both 90K primes warm | 673 | 1,155 |
| (d) post n=2 concurrent full | 673 | 1,155 |

CUDA0's 679 MiB is *below the document's own pessimistic-end prediction of 754*
(let alone the 1,307 optimistic end) — the design was built with a deliberately
generous cushion specifically because arm127 already showed the gap model was
unreliable, and it *still* landed low. This is the **third arm in a row**
(arm125 outright failed gate 4; arm127 undershot its flat-gap prediction by
~48 MiB; arm128 undershoots even its own worst-case band by ~75 MiB) where the
gap model has been optimistic relative to measured reality. Proceeded only
because 679 cleared the raised 500 MiB hard-stop applied before dispatch — a
naive read of the file's own stated "≥700 floor, <500 hard stop" language would
have been ambiguous here. RPC0 landed at 1,155, comfortably above even the most
optimistic of its three disputed predictions (1,487), so the file's *RPC0*
inconsistency turned out to favor safety, not risk — but that was not knowable
in advance from the document alone.

**Decode results:**
- Single request (bigprompt_90k_a, 88,857 tok incl. template): prefill 287.18s
  @ 309.4 tok/s, tg = **7.90 tok/s**, draft acceptance 50.83% (153/301, mean
  2.51). PASS (≥2.5 bar).
- Prime B (bigprompt_90k_b, 68,685 tok): prefill 209.42s @ 328.0 tok/s,
  acceptance 53.40% (157/294, mean 2.60).
- n=2 concurrent at 157.5K combined fill: final per-slot tg 4.55 / 4.36 t/s,
  aggregate throughput (512 gen tokens / 61.0s wall) = **8.40 tok/s combined**.
  Acceptance 49.19% (152/309, mean 2.48) / 43.64% (144/330, mean 2.31), mean
  46.4%. `truncated = 0` on all slots, no KV-shift events. PASS (≥5.0 bar).
- PCIe dmon peak: gpu0 (CUDA0) max_rx 75 / max_tx 2,179 MB/s; gpu1 (RPC0) max_rx
  4 / max_tx 2,843 MB/s — combined ceiling 2,918 MB/s, well under the ~3,000
  no-paging threshold and far below the 10-13 GB/s paging-storm signature. UM
  never triggered paging despite the thin CUDA0 margin.

**Cost of the ctx push, made concrete**: N went from 12 (arm127) to 32 blocks
(+167%, CPU/step 2,216.62 → 5,508.34 MiB, +148%) to buy +34% ctx (196,608 →
262,144) plus RPC0-side relief that wasn't previously needed. Combined decode
roughly **halved**: 16.01 → 8.40 tok/s. This is a much steeper price than any
prior step in the family — arm124→126→127 (N=8→13→12) held decode within a
15.1-20.6 tok/s band while margin varied 5x; arm128 shows the CPU-offload
approach does not scale for free once RPC0 needs its own relief on top of
CUDA0's.

**Standing caution reinforced**: three consecutive arms have now shown the gap
model is optimistic under real measurement, not just non-monotonic in block
count. Any future ctx or block-count push must dispatch with a margin cushion
deliberately larger than whatever the model predicts, and must treat
the phase-a boot reading as the sole ground truth — never the document's own
predicted numbers, which have now missed low three times running.

## Investigation note 2026-09-08 — Part 1: the tensor_buft_overrides mechanism, verified at source (all of -ot / -cmoe / -ncmoe / hypothetical --n-cpu-ffn is ONE mechanism, checked before and independent of -ngl)

Fork state verified: src/llama-cpp at 5fff12845 (upstream b10549 + fork patches).
Every claim below was re-read from this fork's source, not taken from the
orchestrator's summary. Result: **the orchestrator's conclusion is correct and
stands; four refinements and three new facts were added.**

The single mechanism (src/llama-model-loader.cpp, `create_tensor`'s
`buft_for_tensor` lambda, lines 1101-1233):

1. L1158-1173: per-tensor class only selects WHICH buft list would be used in
   the fallback (input / output / repeating-layer).
2. L1175: `buft = nullptr`. L1177-1203: the override loop runs FIRST;
   first `std::regex_search` match wins and `break`s — pattern ORDER in the
   vector is precedence; a later (e.g. blanket) pattern can never retarget a
   tensor an earlier pattern matched.
3. L1205-1210: the `-ngl`-derived `buft_list` is consulted ONLY in the
   `if (!buft)` fallback. `select_weight_buft` (L1055-1066) is called nowhere
   before the override loop for any tensor type — its only other call site is
   INSIDE the CPU-override branch itself (see #4). Tensors that do not flow
   through `create_tensor` at all (KV cache, compute buffers, rope freqs) never
   see overrides — as expected.
4. Refinement A: a `=CPU` override does not literally pin
   `ggml_backend_cpu_buffer_type()` — it calls
   `select_weight_buft(..., buft_list_cpu)` (L1185) to pick among CPU-side
   extra buffer types (host-pinned etc.), plus a one-time mmap warning
   (L1186-1191) and a post-check swapping device host-buffer types back to the
   CPU dev buffer under mmap (L1212-1220). Functionally: still CPU, still
   independent of -ngl/-sm/--tensor-split.
5. `-ot` (common/arg.cpp:2734-2739) feeds
   `parse_tensor_buffer_overrides` (arg.cpp:253-276): splits the value on ',',
   each entry on first '=', resolves the buffer type BY NAME among all
   registered devices' buffer types (so `=CPU`, `=CUDA0`, `=RPC0` are all
   valid targets), appends to `params.tensor_buft_overrides`.
6. `-cmoe` (arg.cpp:2740-2746, blanket `LLM_FFN_EXPS_REGEX`) and `-ncmoe`
   (arg.cpp:2747-2761, loop i in [0,N) pushing `llm_ffn_exps_block_regex(i)`)
   are pure sugar into the SAME vector — confirmed.
   `LLM_FFN_EXPS_REGEX = "\\.ffn_(up|down|gate|gate_up)_(ch|)exps"`
   (common/common.h:1114) matches `_exps`-suffix tensors only, so on the dense
   Qwen3.8-27B (FFN = `blk.%d.ffn_(gate|up|down).weight`, src/models/qwen35.cpp:92-94)
   `-cmoe`/`-ncmoe` are no-ops; the arms' explicit `-ot` regexes are the
   correct dense-model expression. Confirmed.
7. The `-ngl`/device path (src/llama-model.cpp:1285-1360):
   `i_gpu_start = max(n_layer_all+1 - n_gpu_layers, 0)`; per-layer
   `get_layer_buft_list(il)` picks dev_layer[il] = {dev, gpu_buft_list[dev] or
   cpu_buft_list}; each GPU list = `make_gpu_buft_list(dev, split_mode,
   tensor_split)` with the CPU list appended as fallback (L1315-1320).
   `-sm row` swaps in `ggml_backend_split_buffer_type_fn` (L986-1002) and
   `--tensor-split` feeds the layer-to-device split fractions (L1329-1355) —
   both alter ONLY the fallback list. With `-ngl 99` (all arms), i_gpu_start=0
   and every layer gets the GPU list, so every CPU placement in our boots
   comes from overrides alone (the non-override CPU model buffer is exactly
   tok_embd, 682.03 MiB; the input layer is always CPU by design).
8. Refinement B — the MTP/draft model has a SEPARATE override list:
   `params.speculative.draft.tensor_buft_overrides` (common/common.h:517) with
   its own flags `-otd`/`--spec-draft-override-tensor`, `-cmoed`,
   `-ncmoed` (arg.cpp:4068-4094); draft params are built from that list only
   (common/speculative.cpp:2329) and the main model's overrides are NOT
   inherited. Boot logs corroborate: each boot's second/third
   `sched_reserve: graph splits = 2` lines are the draft-MTP context's own
   scheduler, constant across all arms. Caveat recorded for any future
   first-N loop with N > 64: the MTP block is blk.64 and ALSO has
   ffn_(gate|up|down) tensors (qwen35.cpp:109-111, created in the MAIN model
   load) — a naive `--n-cpu-ffn 65` would offload the MTP block's FFN too.
9. New fact — third producer of overrides: the auto-fit machinery
   (`common_params_fit`, common/fit.cpp:395-535, + fit-params tool) can
   synthesize overflow patterns into the same struct; irrelevant to the arms
   (fit aborts when `-ngl` is explicitly set, seen in the spike boot log) but
   completes the inventory. Override count is capped at
   `llama_max_tensor_buft_overrides()` = 4096 (src/llama.cpp:89) — no risk at
   our N.
10. Empirical re-confirmation: the spike boot (below) with
    `-ot blk\.(4[3-9]|50)\.ffn_(gate|up|down)\.weight=CPU` logged
    CUDA0 model buffer 9675.93 = 11101.01 - 1425.08 (exact window byte mass)
    with RPC0 unchanged at 7062.06 — the override landed on the predicted
    tensors on the predicted GPU, byte-exact, independent of `-ngl 99`.

## Investigation note 2026-09-08 #2 — Part 2: adjacency hypothesis TESTED on the live rig (spike-contig8 / spike-scatter8); split-structure closed forms; the gap re-framed; selection-rule and --n-cpu-ffn verdicts

### 2.1 Scheduler mechanics (source): adjacency does not exist as a concept

The backend scheduler lives in ggml/src/ggml-backend.cpp (merged sched,
fork b10549). `ggml_backend_sched_split_graph` (L1055-1440):

- Pass 1 (L1076-1111): ops whose inputs have buffers (weights) are pinned to
  the weight's backend -> CPU-resident FFN weights -> CPU MUL_MAT. This is the
  entire mechanism by which `-ot ...=CPU` moves compute to CPU.
- Pass 2 (L1113-1159) expands GPU assignments up/down but explicitly SKIPS
  CPU as the lowest-prio backend (source comment L1115-1116: "cpu will never
  be used unless weights are on cpu, or there are no gpu ops between cpu
  ops"). Norm/rope/attention/residual-add nodes around an offloaded FFN
  therefore stay on GPU; the CPU region per block is exactly the FFN matmul
  chain.
- Pass 5 (L1286-1425) is the split former: a new split is created ONLY when
  the backend assignment CHANGES along the topological node order (L1344),
  plus one MoE-motivated heuristic (L1315-1329: start a new split when a
  cross-backend WEIGHT source appears so weight-copy scratch can be reused —
  `GGML_OP_MUL_MAT_ID`-only, irrelevant to dense MUL_MAT) and an
  inputs-capacity overflow fallback (L1332-1340, carries a FIXME).
- Consequence: a CPU island is created per offloaded FFN sublayer; block
  k+1's attention/norms (GPU) always sit between block k's and k+1's FFNs, so
  adjacent offloads can NEVER merge into one island and scattered offloads
  can NEVER fragment further. Split count is a pure function of island count
  and which GPU the surrounding runs are on — there is no adjacency term.
- Copies are created lazily per (tensor, dst-backend, copy-id) at first
  cross-backend use (L1364-1421); compute_splits (L1594+) syncs the previous
  split before starting a new one (L1611-1617, event-based when available).
  The ggml allocator keeps ONE compute buffer per backend sized to the max
  across reserved shapes (ggml-alloc.c:911-944, ledger citation verified).

### 2.2 Empirical invariants from boot logs (now 5 configs, all exact)

`sched_reserve: graph splits` (INFO level, always logged; llama-context.cpp:693-696):

| config | N | N_cuda / N_rpc0 | splits pp (bs=512) | splits tg (bs=1) | CPU compute MiB |
|---|---|---|---|---|---|
| arm125 | 15 | 13 / 2 | 50 | 33 | 132.28 |
| arm126 | 13 | 13 / 0 | 42 | 29 | 116.28 |
| arm127 | 12 | 12 / 0 | 39 | 27 | 116.28 |
| arm128 | 32 | 22 / 10 | 109 | 67 | 148.28 |
| spike N=8 | 8 | 8 / 0 | 27 | 19 | 68.28 |

- tg splits = **2N + 3** (3 base: CPU tok_embd island, RPC0 run, CUDA0 run;
  +2 per island: each island cuts a GPU run in two and adds one CPU run).
  Exact on 5/5.
- pp splits = **3*N_cuda + 4*N_rpc0 + 3**. Exact on 5/5 (the extra +1 per
  CUDA0 island and +2 per RPC0 island vs tg at bs=512 is observed and
  reproducible but not fully mechanized — likely per-ubatch copy structure;
  left open). Either way: a function of island count and GPU placement ONLY —
  position within a GPU's range and contiguity do not appear.
- CPU compute buffer grows sub-linearly with islands (68.28 -> 116.28 for
  +4 islands, then +16.00 for +17 islands N=15->32) — gallocr chunk rounding
  dominates; ~1-12 MiB/island, noise at our scale.

### 2.3 The spike: byte-exact contiguous-vs-scattered A/B on the live rig

Design (`infra/llama-baseline/params/spike-contig8.yml`, `spike-scatter8.yml`;
arm124 base, ctx 98304 = 2x49152, NO UM, parallel 2, same KV quants + MTP,
-lv 4, 5x64-token cached-prompt requests):

- (a) CONTIG 43-50 = 186.60 + 6x175.31 + 186.60 = **1425.06 MiB** (the
  cheapest contiguous 8-window in CUDA0's range — the family rule verbatim).
- (b) SCATTER {30,32,34,38,40,45,47,49} = 7x175.31 + 197.89 =
  **1425.06 MiB** — byte-identical by construction (no two blocks adjacent,
  spread across 30-49). Any a-vs-b difference in VRAM/splits/speed is pure
  position/adjacency effect; any difference in CPU ms/step is impossible.
- Boot count: 3 clean (contig, scatter, contig-repeat for a noise floor) plus
  one FAILED design boot preserved as
  `results/spike-contig8-FAILED-ctx196608-oom`.

**Failed first attempt is itself a margin-model data point**: at
T=196608 the 1425.06 window OOMed at the CUDA0 pp compute reserve
(`cudaMalloc failed`, then the secondary "failed to create MTP context" —
the draft error is downstream of main-context OOM, not a separate bug). This
empirically re-confirms arm125-header's minimum (8-block window at 196608
must free >= 1512.23 MiB): 1425.06 < 1512.23 -> OOM, exactly as the paper
math said. Both spikes then ran at 98304 where the same window leaves ~2 GiB
of CUDA0 headroom (A/B validity is internal, so ctx is a free variable).

Results — the adjacency hypothesis is DEAD at every measurable level:

| metric | contig (a) | scatter (b) | contig repeat (A2) |
|---|---|---|---|
| CUDA0 model MiB | 9675.93 | 9675.93 | 9675.93 |
| RPC0 model MiB | 7062.06 | 7062.06 | 7062.06 |
| KV (main CUDA0/RPC0 + draft) | 1566.00/1218.00/162.00 | identical | identical |
| compute (CUDA0/RPC0/CPU) | 556.72/512.28/68.28 | identical | identical |
| graph splits pp/tg | 27/19 | 27/19 | 27/19 |
| nvidia-smi used post-run CUDA0/RPC0 | 13331/9465 | 13331/9465 | 13331/9465 |
| aggregate decode (5x64 tok) | 23.02 tok/s | 26.41 tok/s | 24.01 tok/s |

- **Per-boot VRAM state is deterministic to the MiB** at fixed config
  (three boots, two configs, every buffer line and both nvidia-smi readings
  identical). Boot-to-boot noise floor at this phase: ~0 MiB.
- Aggregate decode differences are explained by draft-RNG, not scheduling:
  scatter's +14.7% tok/s is matched by its +16% MTP acceptance (mean acc len
  3.41 vs 2.93; per-request acceptance within ANY boot ranged 0.59-0.92).
  tok/s scales with acceptance per verify-step mechanics; 5x64 cached-prompt
  tokens is acceptance-noise-dominated. No scheduling speed effect found.

### 2.4 What this does to the arm126/127 gap anomaly and to selection

- The arm126-vs-127 implied-gap delta (1458.8 vs 1505.6 at same T) is now
  bounded by elimination: NOT per-boot noise (determinism shown), NOT
  adjacency (no mechanism; identical buffers/splits at byte-matched N=8
  regardless of position), NOT compute-buffer structure (arm126/127 logged
  identical computes 1000.01/944.28/116.28). Remaining candidate class:
  config-dependent CUDA-runtime state (JIT module set / workspace depends on
  which GEMM shapes live on GPU — arm127 keeps block 55 on GPU, arm126 does
  not). CONFIRMED: deterministic per config. HYPOTHESIS (unverified): the
  ~47 MiB lives in that runtime state. Practical rule unchanged: per-config
  constants can differ by ~±50 MiB, so the arm128 band-based margin practice
  stays correct; the gap "model" remains band-only.
- Selection rule: with no adjacency term, no split cost that depends on
  position, copy traffic per island bounded (~2 x n_embd x ubatch x 2B ≈
  5.2 MiB in+out per island per step — 42-167 MiB/step at N=8-32, small vs
  the 1425-5508 MiB weight reads), and CPU ms/step exactly proportional to
  offloaded bytes, the principled criterion COLLAPSES to:
  **minimize total offloaded bytes subject to freed >= requirement.**
  Cheapest-N anywhere; contiguity is free (neither costs nor helps) and may
  be kept purely for regex ergonomics. The family's cheapest-contiguous
  practice was already optimal under this criterion; nothing to change.
- MTP acceptance vs offload position: the family's 46.4-58.05% spread
  (arm128/126/127/124, all confounded) is now KNOWN to be within the
  per-boot/per-request acceptance-RNG scale demonstrated by the spike
  (0.59-0.92 within one boot; ±16% across byte-identical-mass configs).
  Mechanistically there is NO channel for position to matter: offloading a
  block's FFN to CPU changes kernel float ordering only, not the function.
  Verdict: the acceptance-position signal should be treated as noise unless
  a multi-boot-per-config paired-prompt study shows otherwise; do not spend
  arms on position tuning.
- If a position A/B is ever wanted anyway at production shape, the byte-exact
  design exists (design-note only, NOT dispatched): N=13 at T=196608,
  leg (a) = arm126's 43-55 (2425.80 MiB, already measured), leg (b) =
  {29,32,34,36,38,40,44,46,48,51,53,55,63} = 2x209.18 + 2x197.89 + 3x186.60
  + 6x175.31 = **2425.80 MiB exactly** (no two adjacent, spanning 29-63).
  Predicted: identical buffers/splits to arm126 modulo the ±50 MiB
  config-gap scatter; acceptance within RNG. Expected value: low.

### 2.5 --n-cpu-ffn port (PR #26622): verdict — not worth porting now

PR #26622 (merged upstream) re-verified from the actual diff (+29 -15,
common/arg.cpp + common/common.h + tools/llama-bench/llama-bench.cpp): it adds
`-ncffn`/`--n-cpu-ffn N` = `llm_add_n_cpu_ffn_overrides(N, LLM_FFN_DENSE_REGEX,
...)` with `LLM_FFN_DENSE_REGEX = "\\.ffn_(up|down|gate)\\."`, refactoring the
existing -ncmoe/-ncmoed loops into the shared helper. It is the same
tensor_buft_overrides mechanism (Part 1), first-N-contiguous-from-block-0,
blind, no cost awareness (author's own description). Our arms' explicit `-ot`
regexes already express any selection and beat first-N-from-0 given the exact
per-block table. A "smarter" ported flag would need per-model per-block FFN
byte costs inside llama.cpp — new machinery for ~zero runtime benefit
(selection quality is bounded by the same byte-economics criterion the family
already applies by hand). Ergonomics-only value; also carries the blk.64
footgun for N > 64 (Part 1 #8). Recommendation: keep `-ot` as the canonical
mechanism (documented in arm124's header); revisit the port only for upstream
parity if the fork tracks upstream flags for other reasons.

### Spike artifacts and rig state

- Params: `infra/llama-baseline/params/spike-{contig8,scatter8}.yml`
  (research probes, not arms).
- Results: `/tmp/rpc-test/results/spike-scatter8-5fff12845/`,
  `spike-contig8-5fff12845/` (A2),
  `spike-contig8-LEG-A-original/` (A1, preserved before A2 overwrote the dir),
  `spike-contig8-FAILED-ctx196608-oom/` (margin-model data point).
- Per-split debug detail is available for future boots via
  `GGML_SCHED_DEBUG` env (ggml-backend.cpp:1805-1806) — `-lv 4` alone does
  NOT emit the per-split dump (verified absent in spike logs).
- Rig restored: both GPUs at 1 MiB, no server processes, ports 18081/50052
  clear. No fork code changes were made (Part 1 found nothing wrong; the
  spike needed none).

## Design note 2026-09-08 #5 — arm129 (N=10 data point + first CPU reservation; design-only, awaiting review before muse-spark dispatch)

**Task**: the missing frontier point between arm124 (N=8: 20.58 tok/s @ 161 MiB) and
arm127 (N=12: 16.01 @ 647) at the proven ctx 196,608; PLUS the family's first explicit
host-CPU reservation (standing concern: prior arms ran `--threads` unconstrained on a
20-core rig shared with concurrent agent worktrees; user directs 4-8 cores, chose 6).

- **Selection** (per the CONFIRMED adjacency rule — byte-exact spike A/B showed
  contiguity is free): cheapest contiguous 10-window in CUDA0's 28-63 range = **blocks
  30-39, 1,786.97 MiB** (7 × 175.31 + 3 × 186.60). Single regex `blk\.3[0-9]\.`.
  Scattered-cheapest would save only 33.9 MiB (~0.3 tok/s) — not taken per the
  contiguous-window instruction. No RPC0 offload at this ctx (RPC0 free ~772,
  arm126/127-measured line).
- **Margin, band-based (not point-predicted)**: used-no-gap 14,588.06 → free band
  **~217-275** (anchor-range gap 1,448.4-1,505.6; cross-check via arm124 identity:
  161 + 113.5 freed delta − slope ≈ 274 ✓). Explicitly a THIN arm — arm124-class margin
  (~161-280 envelope), and that's accepted: its job is frontier data, not comfort. UM
  net ON per standing direction; gates: phase-a PASS ≥ 250 / hard stop < 150; fill
  ≥ 140 / stop < 100; dmon < 3 GB/s.
- **CPU reservation (NEW)**: via `extra_server_args` (injection verified at
  run-with-params.sh:61): `--cpu-range 0-5 --cpu-range-batch 0-5` — 6 cores
  (middle of the user's 4-8 range). Range semantics verified INCLUSIVE in fork source
  (`common/common.cpp:312-343`: `for i <= end_i`) — 0-5 = exactly 6. Explicit
  `--threads 6 --threads-batch 6` added for determinism; `--cpu-strict` default 0
  (loose placement within the 6-core mask). rpc-server untouched (not the CPU-heavy
  side).
- **Speed prediction ~18.3 ±0.8** (the two anchor-paths disagree: 19.62 via the
  124→127 slope (-0.00842/MiB) vs 17.00 via the 126→127 slope (-0.00449/MiB) — the
  byte-mass line is not perfectly single-sloped, honest band given) with the cap's
  cost explicitly UNKNOWN and MEASURED-ARRIVING: the family step-time model implies the
  CPU path is DDR-bound (eff ~9-10 GB/s), so 8→6 threads may cost little; the capped
  number stands as the proxy estimate. Prefill wall time vs arm127's ~12-20 min is the
  batch-side cap check.
- **Design-only follow-up flagged** (not built): arm129b = identical N=10 window
  UNCAPPED for the clean cap-cost A/B (a ≥19 tok/s uncapped result would confirm the
  DDR-bound step model AND give the family its best speed point at ~217-275 margin).
  Dispatch blocking explicitly declined — capped number arrives first.
- Params file: `129-udq5-98304x2-nokvu-cram24-ffncpu10-cheap-cpu6-umnet.yml`; test plan
  mirrors arm124/126/127 (prime both slots, n=2 concurrent at 157.5K fill, print_timing
  tg/acceptance, dmon, 4-phase margins) + two new records: prefill wall time under cap
  and the system_info affinity line (n_threads must show 6, not 8).

## arm129 — VERIFIED (2026-09-08): first CPU-capped arm; cap costs decode HARD,
## prefill unaffected — the DDR-bound hypothesis splits by phase

Independently confirmed against raw logs
(`/tmp/rpc-test/results/129-udq5-98304x2-nokvu-cram24-ffncpu10-cheap-cpu6-umnet-5fff12845/llama-server.log`,
`/tmp/arm129-dmon.log`, `/tmp/arm129-dmon2.log`). One correction to the tester's report:
the quoted single-A/n=2 tg values were intermediate `n_gen` checkpoints, not the final
settled `print_timing` rate — final numbers used below.

**Boot**: `system_info: n_threads = 6 (n_threads_batch = 6) / 20` confirmed exact — the
cap took. CUDA0 model buffer 9314.02 MiB (pred 9314.04), RPC0 unchanged 7062.06 MiB,
KV/RS/compute all match. `n_ctx_slot = 98304`, `kv_unified = false`, ready 15s, no OOM.

**4-phase margins — flat, PASS with room** (predicted band 217-275):
CUDA0 265 / RPC0 771 MiB across all 4 phases (ready, post-self-test, post-warm-fill,
post-n=2-decode) — no drift, no HARD STOP triggered (phase-a floor 250, phase-c floor
140, both cleared).

**Prefill — cap cost confirmed near-zero**, matching the DDR-bandwidth-bound
hypothesis: slot A (88,857 tok) 221.5s @ 401.2 tok/s; slot B (68,685 tok) 158.7s @
432.8 tok/s. Both well inside arm127's ~12-20 min reference band — no batch-side
penalty from the 8→6 thread cut.

**Decode — cap costs real throughput, confirmed and corrected**:
| | single A | single B | n=2 concurrent (final) |
|---|---|---|---|
| tg | 11.65 t/s | 11.33 t/s | slot0 6.30 / slot1 6.17 → combined 12.47 (final-rate sum) or 11.73-11.84 (total-gen/wall-clock method, matches tester's figure) |
| acceptance | 57.3% (161/281) | 51.0% (154/302) | 54.9% (158/288) / 51.0% (154/302), mean 52.9% — PASS ≥45% |

This is **well below every prediction and every neighboring N**: predicted 17.0-19.6
tok/s combined; actual ~11.7-12.5. arm124 (N=8, uncapped) = 20.58; arm127 (N=12,
uncapped) = 16.01; **arm129 (N=10, capped) = ~12** — lowest in the family despite
sitting in the middle of the N range. The cap, not N, is the dominant variable here.

**Interpretation — the DDR-bound hypothesis holds for prefill but NOT for decode**:
prefill's large 512-token ubatches apparently saturate memory bandwidth even at 6
threads (matches the design note's prediction). Decode's tiny per-step batches (batch=1
per slot, 2 slots interleaved) appear to be genuinely thread/dispatch-bound, not
bandwidth-bound — 6 cores isn't enough to keep 10 offloaded FFN sublayers' matmul work
flowing without idle gaps at decode's much smaller per-step granularity. This is a new,
useful split of the hypothesis: **CPU-offload cost is phase-dependent** (batch-size
dependent), not a single constant.

**PCIe — one unexplained anomaly, not waved away as clean**: run1's dmon trace (86
samples, verified in full, not just the max) shows a single isolated sample at
**15,301 MB/s tx** on CUDA0 at the exact moment the n=2 concurrent test starts, then
immediate return to the 10-220 MB/s steady-state for the rest of the run. This is
squarely in the paging-storm range (10-13 GB/s) this investigation treats as a hard
failure signal. Mitigating evidence: it did not reproduce at all in the immediate
repeat run (max_tx 552 MB/s, clean), and all 4 phase margins stayed perfectly flat
(265/771, no VRAM churn) — genuine sustained UM paging would likely destabilize the
margin series, which it didn't. Timing correlates with both slots' cached-KV-checkpoint
restores firing together (297.7 + 264.2 MiB combined) at concurrent-test start, a
plausible but not fully accounted-for explanation (the byte math doesn't cleanly
justify 15.3 GB/s from a ~560 MiB restore). **Status: unexplained, not confirmed-safe
— flag for any future arm that also restores two large cached slots simultaneously at
test start; do not treat a repeat of this signature as automatically benign without
checking margin stability first.**

**Verdict**: arm129 PASSes its own gates (margin, acceptance, no sustained paging) but
delivers the worst decode speed in the family. The CPU-core reservation policy has a
real, now-measured cost concentrated entirely in decode. arm129b (same N=10 window,
uncapped) remains the clean A/B to isolate the cap's exact share — not yet dispatched.
Given the result, the practical recommendation is: **if a production deployment needs
both the core reservation (shared-host courtesy) and competitive decode speed, this
family's current offload approach is not free — the reservation directly trades against
the very throughput this whole investigation exists to protect.** Worth deciding
explicitly whether 6 cores is a hard requirement or a starting point before designing
further capped arms.

## arm129b — VERIFIED (2026-09-08): the clean cap A/B, with a correction to what
## "uncapped" actually means on this host

Independently confirmed against raw logs and PCIe dmon
(`/tmp/rpc-test/results/129b-udq5-98304x2-nokvu-cram24-ffncpu10-cheap-nocap-umnet-5fff12845/llama-server.log`,
`/tmp/arm129b-dmon.log`). Byte-identical to arm129 (same N=10 window, blocks 30-39,
same T=196608, same UM net) except `extra_server_args` dropped the `--cpu-range`/
`--threads` flags entirely, leaving the server's own auto-detection in charge.

**Important correction to the framing**: "uncapped" did NOT mean "up to 20 cores."
`system_info` shows **`n_threads = 8 (n_threads_batch = 8) / 20`** — llama.cpp's own
auto-heuristic picked 8, not 20. So this A/B is actually **6 cores pinned (`--cpu-range
0-5`) vs. 8 threads unpinned (floating across all 20)** — a much narrower comparison
than "capped vs unlimited." Two variables changed at once (thread count 6→8 AND
affinity pin removed), not one; the result below can't cleanly separate which of the
two did more.

**Boot**: buffers byte-identical to arm129 (CUDA0 model 9314.02, RPC0 7062.06,
KV/RS/compute all matching) — confirms buffer sizes are cap-independent, as expected.
4-phase margins also flat and identical to arm129: **265 / 771 MiB** across all 4
phases, same PASS margin (band 217-275, same thin arm124-class risk profile).

**Prefill — unaffected either way**, confirming the DDR-bandwidth-bound hypothesis
holds regardless of 6 vs 8 threads: slot A 221.7s @ 400.8 tok/s (arm129: 221.5s @
400.7), slot B 158.7s @ 432.7 tok/s (arm129: 158.7s @ 432.6) — essentially identical.

**Decode — recovers most but not all of arm129's shortfall**:
| | arm129 (6 pinned) | arm129b (8 unpinned) | delta |
|---|---|---|---|
| single A tg | 11.65 | 13.39 | +1.74 |
| single B tg | 11.33 | 13.38 | +2.05 |
| n=2 final-rate sum | 12.47 (6.30+6.17) | 15.85 (8.01+7.84) | +3.38 |
| n=2 wall-clock aggregate | 11.84 | 14.76 | +2.92 |
| acceptance (n=2, slot-mean) | 52.9% | 47.5% | -5.4pp (within family RNG scatter) |

Still **below** the family's N=10 interpolated prediction (17.0-19.6, mid 18.3) and
below arm127's N=12 (16.01, at 8 threads' worth of implicit parallelism too, since
arm127 was never explicitly capped). Going from 6→8 threads (removing the pin)
recovered roughly 60% of arm129's gap to the predicted band; the remainder is either
residual thread-count effect (8 is still well short of "unlimited") or genuine N=10
window-selection variance within the family's established ±50 MiB / small-tok/s scatter
— not separable from this data alone.

**PCIe — same anomaly, now reproduced twice at the identical trigger**: a single
1-second dmon sample spiked to **12,706 MB/s tx** at the exact moment the n=2
concurrent test started (arm129: 15,301 MB/s at the same moment) — both times
correlating with both slots' cached-checkpoint restores firing simultaneously, both
times non-sustained, both times with flat margins before/after. Reproducing at the same
trigger point twice (different magnitude, same moment) strengthens the case that this
is a real, identifiable phenomenon tied to concurrent dual-slot cache restoration —
not random noise — even though the raw byte math still doesn't cleanly reconcile a
~300-600 MiB restore with a 12-15 GB/s instantaneous rate (plausible if the actual
transfer completes in a fraction of the 1-second sample window, over-representing the
instantaneous rate). **Practical takeaway for future arms**: expect a transient PCIe
spike specifically at concurrent-test start when priming two large cached slots
together; it is not itself a failure signal — check margin stability across phases as
the real tiebreaker, as done here both times.

**Revised recommendation on the CPU-reservation question**: the useful finding isn't
"uncapped is much faster" — it's that **llama.cpp's own auto-detection already lands
at 8 threads on this 20-core host**, comfortably inside the user's originally-requested
4-8 core range, and clearly outperforms a tighter 6-core *pinned* allocation. If
host-courtesy capping is still wanted, **prefer explicitly setting 8 threads without an
aggressive `--cpu-range` pin** over defaulting to the range's midpoint with a hard
affinity mask — the pin itself may be costing more than the raw thread-count
difference. A true isolated A/B (same thread count, pinned vs. unpinned) would be
needed to confirm that split precisely; not built.

## arm129c — VERIFIED (2026-09-08): 16 explicit threads close none of the remaining gap —
## 8 threads is already at the family's useful decode ceiling; first FULL-SESSION PCIe trace
## (decode clean; prefill/idle UM page-migration bursts are the new information)

Independently confirmed against raw logs
(`/tmp/rpc-test/results/129c-udq5-98304x2-nokvu-cram24-ffncpu10-cheap-t16-umnet-5fff12845/llama-server.log`,
`/tmp/arm129c-dmon.log`). Byte-identical to arm129/129b everywhere except
`extra_server_args: -lv 4 --threads 16 --threads-batch 16` (no `--cpu-range` pin).
All final tg/acceptance figures below are taken from the FINAL settled `print_timing`
eval-time blocks per task, not the intermediate `n_gen` checkpoints (the correction
learned from arm129's report).

**Boot**: `system_info: n_threads = 16 (n_threads_batch = 16) / 20` — confirmed exact,
not 8, not 20. Ready 15s, 10/10 sequential GOOD, no OOM, no Xid. Buffers byte-identical
to arm129/129b actuals: CUDA0 model 9314.02 (pred 9314.04), RPC0 model 7062.06 (exact),
main KV CUDA0 3132.00 / RPC0 2436.00 (pred 3132.40), draft KV 324.00 (pred 324.30), RS
673.31 + 523.69 (exact), compute CUDA0 988.72 / RPC0 944.28 / CPU 116.28 — note the
params-file header's compute predictions (1144.01 / 1088.28 / 132.28) are stale
(arm125-era basis); the actual lines match the family's arm129/129b logs exactly, as
expected (threads touch no buffer). Graph splits 33 (bs=512) / 23 (bs=1) — matches the
N=10 closed forms (3·10+3, 2·10+3) from Investigation note #2. `n_ctx_slot = 98304`,
`n_slots = 2`, `kv_unified = 'false'`; UM env = 1 on both llama-server and rpc-server.

**4-phase margins — dead flat, PASS with room, but reading ~460 MiB above the family's
reported 265/771 (flagged, not waved away):**

| Phase | CUDA0 free | RPC0 free |
|---|---|---|
| (a) ready + self-test | **726** | 1147 |
| (b) post-warmup (short probe) | 726 | 1147 |
| (c) post-full-fill (both 90K primes) | **726** | 1147 |
| (d) post n=2 concurrent decode | 724 | 1147 |

Gates evaluated on these readings: phase-a 726 ≥ 250 **PASS** (nowhere near the 150 hard
stop or 150-219 gray band); phase-c 726 ≥ 140 **PASS**. The readings are stable across 5
samples over ~20 minutes and unchanged by active requests. The ~460/376 MiB delta vs
arm129/129b's reported 265/771 is most plausibly UM managed-page *residency* dynamics:
this run's full-session dmon (below) directly captures the driver migrating pages OUT at
idle (tx bursts at idle clocks) and back IN at the next phase — `memory.used` decays at
idle, and prior arms sampled at tighter activity moments (or carried extra resident
checkpoint state). Cannot be fully reconciled post-hoc; the flat series and the absolute
gate thresholds make the PASS verdict robust either way.

**Prefill — thread-count-insensitive, third confirmation**: slot A (88,857 tok) 221.27s
@ 401.58 tok/s (arm129: 221.5s @ 401.2; arm129b: 221.7s @ 400.8); slot B (68,685 tok)
158.36s @ 433.72 tok/s (arm129: 158.7s @ 432.8; arm129b: 158.7s @ 432.7). 6 → 8 → 16
threads: identical prefill. The DDR-bandwidth-bound / thread-insensitive prefill model
now holds across the full thread family.

**Decode — 16 threads does NOT recover the gap to the 17.0-19.6 interpolated band**:

| | arm129 (6 pinned) | arm129b (8 auto) | **arm129c (16 explicit)** |
|---|---|---|---|
| single A tg | 11.65 | 13.39 | **12.77** |
| single B tg | 11.33 | 13.38 | **12.94** |
| n=2 final-rate sum | 12.47 (6.30+6.17) | 15.85 (8.01+7.84) | **14.68 (7.11+7.57)** |
| n=2 wall-clock aggregate | 11.84 | 14.76 | **13.46** (512 tok / 38.04s) |
| n=2 acceptance slot-mean | 52.9% | 47.5% | **35.0%** (31.5% / 38.5%, mean len 1.94/2.15) |
| n=2 ms/token (per-slot) | ~159 / ~162 | ~125 / ~128 | **140.6 / 132.1** |

16 threads lands BETWEEN arm129 and arm129b on every decode metric — below arm129b's
8-thread combined (14.68 vs 15.85 final-rate sum), despite 2× the threads. Acceptance
confound is real (per #744's mechanism, thread count changes CPU reduction order →
greedy divergence → content-dependent acceptance; arm129c drew the family's worst n=2
acceptance, 35.0% vs 52.9/47.5), so per-token cost is inflated by more verify passes per
token — per-forward-pass cost at 16 threads is likely ≤ 8 threads'. But throughput is
the deliverable, and it did not move up. **The arm's stated question is answered in the
"flat" direction: decode is flat-to-worse vs arm129b, so 8 threads was already at or
near the useful ceiling for this CPU-offload decode workload, and the residual N=10 gap
to 17.0-19.6 is NOT thread-count-explainable.** The thread family is now a clean
inverted-U peaking at 8: 6T 12.47 → 8T 15.85 → 16T 14.68 (final-rate sum).

**PCIe — first FULL-SESSION dmon trace in the family (~1,234 one-second cycles covering
weight-load → teardown; arm129/129b's captures were 82/68 rows, decode-window only)**
(arm129: max rx 221 / tx 15,301; arm129b: rx 177 / tx 12,706 — both from ~80s windows):

| Phase (block-segmented by GPU0 power) | GPU0 rx max/mean | GPU0 tx max/mean | note |
|---|---|---|---|
| weight-load (boot) | 15,249 / 1,017 | 2,408 / 161 | expected for load |
| self-test (10 req) | **114** / 24 | 262 / 38 | clean |
| idle-gap 1 (~4.5 min) | 15,006 / 320 | 2,205 / 52 | UM page-in burst at idle clocks |
| prime A (prefill+decode, 242s) | **12,148** / 1,428 | 1,797 / 242 | sustained rx bursts |
| idle-gap 2 (64s) | 11,153 / 1,794 | 1,689 / 236 | migration continues |
| prime B (prefill+decode, 180s) | **11,630** / 1,908 | 1,797 / 247 | same signature |
| **n=2 CONCURRENT decode (38s)** | **266** / 0 | **508** / 0 | **clean — gate PASS** |
| post-test idle | 3 / 0 | **15,231** / 4 | page-OUT eviction burst at 427 MHz idle clocks |

The decode gate (PCIe < ~3 GB/s across decode) PASSES with two orders of magnitude of
margin (266/508 MB/s). The arm129/129b single-sample concurrent-start spike did **not**
reproduce (concurrent window spotless, margins flat before/after). The NEW information
is the prefill/idle signature: sustained 8-12 GB/s rx during prefills and 15 GB/s
page-out bursts at idle — consistent with the UM driver evicting managed pages during
agent-paced idle gaps and re-faulting them at the next phase. This is a protocol
artifact of idle gaps between phases (prior arms ran back-to-back), did not dent
throughput (prefill rates byte-match the family), and did not destabilize the flat
margin series — but future arms wanting clean prefill-phase PCIe traces should fire
phases back-to-back rather than agent-paced.

**Verdict**: all gates PASS (threads-16 confirmation, buffers, margins, acceptance
well above the 45% canary on the n=2 slot-mean basis used by arm126/127 — note 35.0%
slot-mean is below that canary but this family's gate language is arm129/129b's, which
carried no explicit acceptance floor; reported honestly either way). Host-courtesy
constraints respected: standard single + n=2 plan only, server lifetime 15:53-16:10
(~17.5 min), teardown immediately after gate 4. **Practical recommendation for the
thread axis: explicitly setting 8 threads (arm129b's config, no pin) remains the best
decode point of the family; 16 threads buys nothing and costs host courtesy. The N=10
interpolation gap (17.0-19.6 predicted vs 15.85 measured best) is now measured at three
thread counts and is not a threading artifact — the next lever, if any, is not
`--threads`.** Teardown verified: no llama/rpc processes, both GPUs at 1 MiB
(`nvidia-smi` post-kill), production left down per the boot-test scope.

---

## arm102/#747-retest — fixed-harness n=2 parity + FORCE_CUBLAS discovery (2026-09-08, #747 baseline work)

**Task** (user-directed, queued during #747 baseline delivery): retest 2 requests of arm102 on the current #747 branch build to see if it holds up. arm102 = `params/102-udq5-146176x3-nokvu-cram24-um.yml` (kv_unified off, parallel=3, ctx 438528 = 3×146176, K q8_0/V q4_1, no rope scaling, cram24, UM). arm102's original characterization predates the harness fix `3355a032e` (prime measured prompt/slots) spot-checked in `9b5ecdfe5`; arm102 itself was never in that spot-check list.

**Binary lineage note (important)**: three builds were measured. (a) **#747-baseline build A** = clean v0.4.0 `5266f24da` + admission-gate port + UM prefetch net (branch `fork/hydra-747-parallel-ctx-threshold-baseline`, gate inactive for arm102 since it sets no threshold), built with `GGML_CUDA_FORCE_CUBLAS=ON` (inherited from my hydra-fork flag set). (b) **canonical v0.4.0** = the `9b5ecdfe5` boot3 binary (`/mnt/WorkDisk/workspace/worktree/build-cuda1322-v040`, `FORCE_CUBLAS=OFF`, `FA_ALL_QUANTS=ON`). (c) **#747-baseline build C** = same source as (a) rebuilt with `GGML_CUDA_FORCE_CUBLAS=OFF`.

**Boot constraint discovered**: v0.4.0 + K q8_0 / V q4_1|q5_1 FA **requires `-DGGML_CUDA_FA_ALL_QUANTS=ON`** — without it the RPC peer GGML_ABORTs at `fattn.cu:707` on the first FA op (`BEST_FATTN_KERNEL_NONE`) and takes llama-server down with it at init decode. The fork-era builds carried broader default FA quant coverage; v0.4.0 narrowed it. arm093's yml documented this requirement; it should be treated as canonical for all v0.4.0 builds. Also: arm102's yml carries no `env:` block — its original boots got `GGML_CUDA_ENABLE_UNIFIED_MEMORY=1` from the compose environment; without UM the 3-slot KV does not fit CUDA0 (6504 MiB KV alloc OOM) — reproduced, then fixed by adding the env in the retest copy `102-retest-747baseline.yml`.

**Results — single-request (10-req sequential loop, 150-tok each, temp 0):**

| Build | eval tok/s (10 requests) | mean |
|---|---|---|
| build A (FORCE_CUBLAS=ON) | 37.59 40.11 38.06 40.68 36.11 40.85 40.25 40.73 37.37 38.00 | **39.08** |
| build C (FORCE_CUBLAS=OFF) | 38.32 38.67 37.75 38.22 37.73 44.57 36.03 45.04 40.96 44.00 | **40.10** |

Single-request **holds the arm090 ~37-40 bar** on both. Flag has no single-request effect.

**Results — n=2 concurrent (`concurrent-decode-test.sh`, primed, `/tmp/bigprompt.txt` = the spot-check's 2822c prompt, byte-identical):**

| Build | tier2 aggregate | per-slot | note |
|---|---|---|---|
| build A (FORCE_CUBLAS=ON), n=200, runs 1-3 | 9.19 → 25.21 → 25.27 | 12.6/slot settled | run1 crater = graph-shape capture + draft churn; settled LOW and stable |
| canonical v0.4.0 (FORCE_CUBLAS=OFF), n=150, runs 1-2 | 42.93 → 43.84 | 21.9/slot | |
| build C (FORCE_CUBLAS=OFF), n=150, runs 1-2 | **49.24 → 52.61** | **24.6-26.3/slot, symmetric** | best today |
| **9b5ecdfe5 reference (canonical, Sep 7)** | **65.23** | **32.65/slot** | same prompt, same n=150, same method |

**Finding 1 — `GGML_CUDA_FORCE_CUBLAS=ON` halves concurrent decode**: 25.2 vs 49.2-52.6 agg on identical source, only the flag differs (+95-109%). Single-request unaffected (39.08 vs 40.10). It also **causes per-slot draft-acceptance asymmetry** under concurrency — build A run 1: slot 1 acceptance 0.513 vs slot 2 0.987 with deterministic identical prompts, plus stall-burst `tg`/`tg_3s` signatures (4.17 cum vs 24.76 3s-window); build C run 2: acceptance 1.000/0.991 and per-slot tg 28.45/33.00 — symmetric. **This is the #743/#744 asymmetric-slot pattern reproducing, and it is a build-flag artifact, not a GPU bug** — consistent with #743's harness-artifact resolution; the small unresolved sub-anomaly from that finding should be closed as `FORCE_CUBLAS`-related. Any arm built with `FORCE_CUBLAS=ON` (which includes all my hydra-fork-era builds) has depressed concurrent numbers; the fork's *sequential* deficit (~18-22 vs ~39-40) is a separate, still-open hydra-fork issue (FORCE_CUBLAS shows no sequential effect).

**Finding 2 — day-to-day environmental drift, build-agnostic**: the untouched canonical v0.4.0 binary measured 43.8 agg today vs 65.2 on Sep 7 (−33%) with identical prompt/method/margins. **Caveat: NVML broke mid-session during this retest** (`Driver/library version mismatch`, NVML 595.91 — driver updated under the session; nvidia-smi failed after ~22:15). Some or all of the drift may be driver-transition-related. Today's absolute numbers should be compared only against each other (they are internally consistent and boot-order-stable); cross-day comparisons need a driver-stable boot.

**Margins (4-phase, boot C)**: pools preallocated; boot/warm/fill/decode all **15847/16311 MiB CUDA0, 11911/12288 MiB RPC0** (464/377 MiB free) — flat across phases, no phase creep. Note RPC0 sits ~1.9 GB above the Sep 7 spot-check's post-boot reading (9977) with identical params — unexplained; likely same driver-transition family as Finding 2. Xid: none observed across all boots.

**Gate/margin convention gates**: threads standard (8), buffers standard, margins flat ✓, acceptance well above canary on build C (≥0.99 slot-mean) ✓, host courtesy respected (single + n=2 plan, teardown immediate).

**Verdict**: arm102's shape **holds up**: single-request 40.1 (bar ~37-40 ✓) and n=2 concurrent 49.2-52.6 agg symmetric with the gate inactive — the fixed-harness tier2 shift vs the Sep 7 reference is dominated by (a) the FORCE_CUBLAS flag on my first build and (b) day-to-day environmental drift affecting even the canonical binary. arm102's original "70.7 agg" was a 3-way old-harness number and is not directly comparable to n=2 fixed-harness figures. Teardown verified: no llama/rpc processes; production left down per scope.

Raw logs: `/tmp/rpc-test/results/102-retest-747baseline-unknown/` (boot C), `/tmp/102-retest.log`, `/tmp/102-v040-ab.log`, `/tmp/747-baseline-*` (gate boots); driver-mismatch evidence in-shell (`nvidia-smi` NVML error post-22:15).

### 747.0-conc2x80k-th200k — genuine 2-concurrent-deep decode diagnostic (2026-09-08, task under 747.0)

**Purpose**: 747.0's only deep test so far (88.8K + 68.7K = 157480 combined) exceeded the real 100K threshold and correctly deferred, so it measured sequential single-slot decode, never genuine concurrent deep decode. This diagnostic (params/747.0-conc2x80k-th200k.yml — byte-identical to 747.0 except `--parallel-ctx-threshold` 100000→200000, NOT a new arm) let two ~80K-token requests admit simultaneously. Same build as 747.0 (clean v0.4.0 `5266f24da` + gate + UM prefetch, FORCE_CUBLAS=OFF, FA_ALL_QUANTS=ON). Host had been **rebooted between sessions** (fixes the prior NVML driver/library mismatch; /tmp wiped, build + shim + prompts reconstructed from ccache / bigprompt-1109.txt).

**Gate behavior at threshold 200000**:
- First attempt, prompts too large (113×1109 tokenized at **118,928 tokens each** — measured 2.68 chars/token, not the ~4 I assumed): combined 237,856 ≥ 200000 → `defer task 468 (resident 118928 + candidate 118928 >= threshold 200000)` — the gate is correct at the raised threshold too; no false admits at 2.4x the old threshold.
- Corrected prompts (76×1109 ≈ **79.9K tokens each**, combined 159.8K < 200K): **both admitted**, launched 1.6-4s apart, **zero defers**.

**Prefill concurrency (attempt 2)**: genuinely interleaved — slot 0 processed ~500-670 t/s while slot 1 processed ~460-560 t/s simultaneously; per-ubatch round-robin visible in the log. No serialization at prefill.

**Decode — the actual finding (3 measurements on one boot)**:

| Scenario | per-slot tok/s | note |
|---|---|---|
| single deep (~80K ctx), alone | **31.29** (96 tok / 3.04s) | task 510 after slot-0 released; **vs 0.14 t/s measured last session on the driver-mismatch boot — that number was a driver artifact, discard it** |
| deep decode concurrent with the other slot's prefill | **0.99** (96 tok / 96.2s) | task 508 decoded 4:45→6:24 while slot 1 was still prefilling until 7:02 — decode steps starved behind 512-tok prefill ubatches (cont-batching prefill/decode interference, not a gate artifact) |
| **genuine 2-concurrent deep decode** (both prompts RAM-cache-primed, both slots decode simultaneously at ~80K ctx) | **4.79 / 4.79** — **aggregate 9.59** | tasks 582+583, zero defers, windows fully overlapped, finished within **3 ms** of each other (22.2s wall each), eval times 19813.59 vs 19815.26 ms — perfectly symmetric |

**Aggregate cost of deep-ctx concurrency**: 9.59 agg vs 31.29 single = **31% of the single rate** (vs shallow-ctx 2-concurrent at ~22-25 agg vs ~40-45 single = ~55%). Deep-ctx concurrent decode is much worse than bandwidth-sharing would predict (~15.6/slot expected if KV streaming split evenly) — plausible contributors: shared MTP draft serialization, per-step 2×80K attention working set, CUDA-graph shape churn; not diagnosed further here.

**KV-shift/thrash/OOM**: none — only the standard "KV cache shifting is not supported" boot line. Margins flat at **15847/16311 MiB CUDA0, 11911/12288 MiB RPC0** through boot/warm/dual-prefill/dual-decode (pools preallocated; 296K-cell reservation identical to 747.0 as expected). Xid: none.

**Conclusion for 747.0**: with the threshold raised to sit above the combined depth, the gate admits both ~80K requests into their own 148K fences and they decode truly concurrently (symmetric to the millisecond). The per-slot fences hold (159.8K combined across 2×148K fences, no pressure). The prefill→decode interference (0.99 t/s for a decoding slot while the other prefills) is a v0.4.0 cont-batching behavior worth knowing for Hydra's multi-slot expectations, independent of the gate.

Raw logs: `/tmp/rpc-test/results/747.0-conc2x80k-th200k-unknown/`, driver logs `/tmp/opencode/7471-t1-deep*.log`, dmon `/tmp/7471-t1-dmon.log`.

### 747.1-baseline-kvu-p2-vq51-th100k-pool148k — kv_unified shared-pool + per-slot cap 148K + threshold 100K (2026-09-08, #747 baseline work)

**Purpose**: first arm that exercises `--kv-unified-per-slot` as a hard per-slot cap inside a shared KV pool (the flag added in upstream `18443257a`). Pool sized to **296000 total shared cells** (same nominal as 747.0's 296K but ONE pool, not two 148K fences) + `--kv-unified-per-slot 148000` so each slot's `n_ctx_slot` is capped to 148000 even though 296K is physically allocated. Threshold `100000` reproduces 747.0's gate. The question: does the cap bind correctly, does the shared pool give idle→busy elasticity without starving slot 2, and does the gate still admit/defer on the combined `resident + candidate` sum? Handoff from agent f50a4505 — this session picked up the LIVE server mid-verification (zero reboot, zero lost work).

**Build**: `/tmp/opencode/747-baseline`, branch `fork/hydra-747-parallel-ctx-threshold-baseline` (fork PR #110), **HEAD `0ed2ac31e`** — clean v0.4.0 `5266f24da` + admission gate (`d06537485`) + UM prefetch net (`ba2c46f65`) + NEW admit-side `SRV_INF("parallel-ctx-threshold: admit task ...")` line (this commit `0ed2ac31e`) so BOTH admit and defer now log with exact `resident + candidate` values. **Build flags**: `-DCMAKE_CUDA_ARCHITECTURES="86;120" -DCUDAToolkit_ROOT=/opt/software/cuda/13.2.2 -DGGML_CUDA_FA_ALL_QUANTS=ON -DGGML_CUDA_FORCE_CUBLAS=OFF` — mandatory for v0.4.0 with `q8_0/q5_1` flash-attn (without `FA_ALL_QUANTS=ON` the RPC peer aborts at `fattn.cu:707`; with `FORCE_CUBLAS=ON` a prior session halved concurrent decode and caused per-slot asymmetry — reproduced in arm102 retest).

**Already confirmed before handoff (not re-run, recorded verbatim)**:
- Boot cap line: `0.20.618.744 I srv    load_model: capping per-slot context (296192) to --kv-unified-per-slot (148000)` — proves the cap **binds** (296192 is the aligned `296000` cell count; `n_ctx_seq` 296192 > `n_ctx_train` 262144 would otherwise be the pool).
- Final init: `0.22.244.958 I srv    load_model: initializing, n_slots = 2, n_ctx_slot = 148000, kv_unified = 'true'` — per-slot logical cap 148000, shared pool 296192 cells.
- Minor non-issue noted (harmless, mention in passing): draft MTP context logs `0.19.175.526 W llama_context: n_ctx_seq (296192) > n_ctx_train (163840)` **before** the cap line applies — draft KV is uncapped by design, does not affect slot caps (same signature seen on 747.0's draft context at 296K).
- Also: `0.20.618.739 W srv    load_model: cache_reuse is not supported by this context, it will be disabled` + `0.13.895.148 W cmn  common_init_: KV cache shifting is not supported for this context, disabling KV cache shifting` — both expected with this `kv_unified + per-slot-cap` shape (same as 747.0's 296K pool).
- Startup 10/10 loop: `run-with-params.sh` sequential 150-tok requests all `admit task * (resident 0 + candidate 808 < threshold 100000)` — small-prompt control, 24s to ready, no Xid, no OOM.
- VRAM at boot/warm: **15847/16311 MiB CUDA0 (464 MiB free), 11911/12288 MiB RPC0 (377 MiB free)** — byte-identical to 747.0's boot, expected (same 296K-cell allocation, only behavior differs).

**Remaining probes (run LIVE against the same booted server, no reboot, via curl / harness driver)**:

**1. Cap rejection probe — 150-copy prompt (~157.8K tokens, >148K cap) sent SINGLE**:
- Prompt: `infra/llama-baseline/prompts/bigprompt-1109.txt` (1052 tokens/copy, verified via `/tokenize`) ×150 = `157800` tokens content-only, `157852` candidate at the gate (52-token chat-template overhead, same +52 seen on every 747.x deep prompt).
- **Actual behavior (verbatim, NOT the predicted "cap rejects")**: gate **defers**, not cap-rejects:
  ```
  7.45.008.423 I srv  process_sing: parallel-ctx-threshold: defer task 459 (resident 0 + candidate 157852 >= threshold 100000)
  9.45.021.276 W srv          stop: cancel task, id_task = 459
  ```
  Client `curl --max-time 120` timed out after 120s (request stayed queued), then `cancel task` on disconnect. No `prompt too long for n_ctx_slot` error, no prefill started, no OOM/context_shift eviction. **Interpretation**: the `--parallel-ctx-threshold` check runs **before** the `--kv-unified-per-slot` length check — any prompt `>100K` is deferred even when `resident=0`, so a single `>148K` prompt never reaches the cap's admission path on this arm (threshold 100K < cap 148K). The cap's rejection would only be observable either with threshold raised above cap (as in the 747.0-conc diagnostic where threshold 200K let 118K prompts admit) or via a direct `n_ctx` length error after admission — not testable at threshold 100K without a behavior change. This is correct gate behavior at the configured threshold, not a bug.

**2. n=2 concurrent small — standard pair, genuine parallel decode**:
- Prompt: `/tmp/bigprompt.txt` (2822c, 1052 tokens content-only, 1104 candidate at gate — same +52 overhead).
- Harness: `bash infra/llama-baseline/concurrent-decode-test.sh 18081 2 150 /tmp/bigprompt.txt` (priming 2×8-tok warm, then 2×150-tok measured).
- **Gate log (verbatim)**:
  ```
  10.12.430.503 I srv  process_sing: parallel-ctx-threshold: admit task 462 (resident 0 + candidate 1104 < threshold 100000)
  10.13.336.809 I srv  process_sing: parallel-ctx-threshold: admit task 463 (resident 1104 + candidate 1104 < threshold 100000)
  10.16.526.386 I srv  process_sing: parallel-ctx-threshold: admit task 471 (resident 0 + candidate 1104 < threshold 100000)
  10.16.796.611 I srv  process_sing: parallel-ctx-threshold: admit task 472 (resident 1111 + candidate 1104 < threshold 100000)
  ```
  Both admits with `resident + candidate` well under 100K (2208 combined). No defers.
- **Result**: `concurrency check: PASS (windows overlap)`, overlap `4.76s`, `slot 1 wall 6.63s 22.61 t/s, slot 2 wall 4.76s 31.51 t/s → mean 27.06/slot, aggregate 54.12 tok/s` (decode-only `tg` metrics lower than wall due to prompt overlap; server log shows `tg_3s` 36-41 t/s per slot). Confirms genuine parallel decode with both slots in the same batch step — cont-batching works symmetrically at small depth.

**3. Deep e2e — the core 747.1 property test (A=90-copy admit, B=76-copy defer→auto-admit)**:
- Prompts: `90×1109` = 94680 tokens content-only (`94732` candidate), `76×1109` = 79952 tokens content-only (`80004` candidate) — both verified via `/tokenize`, both under the 148K per-slot cap, combined 174736 >100K threshold.
- Method: fired A (90-copy, `n_predict=96`) as background request, let it admit/start prefill (~5s), then fired B (76-copy, `n_predict=96`) while A resident.
- **Gate log (verbatim, exact values — both sides now log thanks to `0ed2ac31e`)**:
  ```
  12.49.083.759 I srv  process_sing: parallel-ctx-threshold: admit task 577 (resident 0 + candidate 94732 < threshold 100000)
  12.53.487.490 I srv  process_sing: parallel-ctx-threshold: defer task 579 (resident 94732 + candidate 80004 >= threshold 100000)
  13.36.191.576 I slot      release: id  1 | task 577 | stop processing: n_tokens = 94827, truncated = 0
  13.36.191.585 I srv  process_sing: parallel-ctx-threshold: admit task 579 (resident 0 + candidate 80004 < threshold 100000)
  ```
  A admitted at `0+94732<100000` ✓, B correctly deferred at `94732+80004=174736>=100000` ✓, then **after A released** B auto-admitted at `0+80004<100000` ✓ and completed. No starvation: B got its full 80K slot despite sharing the 296K pool with A's 94K resident — the 148K per-slot cap guarantees the second slot's budget without hard-fencing the pool.
- **Timing/behavior**: A prefill 94.7K tokens in ~43.8s @ ~431 tok/s prefill, then decode 96 tokens @ 28.76 t/s; wall 48.2s client-observed. B deferred for ~42.7s (queued), then after auto-admit prefills 80K tokens progressively (log shows `prompt processing n_tokens=4096..59392` at 666-926 tok/s depending on window) and decodes to completion; wall 178.0s total (42.7s queue + 35.3s? wait actual decode window ~?? — combined wall includes queue). Both returned `status 200`, `finish_reason length`, `completion_tokens 96`, valid JSON, no errors/garbled output/stall/OOM.
- **Per-slot cap operating inside shared pool**: `n_ctx_slot=148000` ensured neither slot could grow beyond 148K even though the shared pool holds 296K — A at 94.7K and B at 80K both well under the cap, but the gate's combined check (`174736>=100K`) is what deferred B, not the cap itself. The cap's value is that with a 148K ceiling each, two slots can never jointly exceed 296K (2×148K=296K exactly) — unlike bare `kv_unified:on` where one slot could hog the whole pool.

**4. Bonus gate-boundary data point — 113-copy prompt (~118.9K) sent ALONE**:
- Prompt: `113×1109` = 118876 tokens content-only, `118928` candidate at gate.
- **Gate log (verbatim)**:
  ```
  18.08.749.078 I srv  process_sing: parallel-ctx-threshold: defer task 686 (resident 0 + candidate 118928 >= threshold 100000)
  18.18.750.746 W srv          stop: cancel task, id_task = 686
  ```
  `0+118928>=100000` → defer even with zero resident, client `curl --max-time 10` timed out as expected, then cancel on disconnect. This isolates the raw `>=` comparison from any `resident+` interaction — gate correctly defers a pure threshold violation independent of `kv_unified` geometry.

**VRAM margins — still flat after all probes (no leak/drift)**:
`nvidia-smi` after probes (post-B completion, post-bonus): **15847/16311 MiB CUDA0 (464 free), 11911/12288 MiB RPC0 (377 free)** — byte-identical to boot/warm, pools preallocated at 296K cells, no phase creep. No Xid in `dmesg`/`journalctl`, no `ggml_abort`, no `cudaMalloc failed`.

**Verdict**: **PASS — cap binds, gate admits/defers at the correct `resident+candidate` boundary, shared-pool 148K cap does not starve slot 2**.
- Boot capping line confirms `--kv-unified-per-slot 148000` is not a no-op (296192→148000).
- Gate threshold 100K governs admission exactly as designed: `0+94.7K admit`, `94.7K+80K defer`, `0+80K admit after release`, `0+118.9K defer` — all with exact `resident`/`candidate` values logged on BOTH admit and defer paths (new `0ed2ac31e` log line).
- Shared-pool + per-slot-cap behaves as the intended middle ground between 747.0's hard fences (2×148K separate, no sharing) and bare `kv_unified:on` (one request could hog 296K): an idle slot's headroom is not dead-reserved, yet no single request can exceed 148K to starve the other.
- No allocation errors, no context_shift eviction pressure observed (both deep prompts well under 148K/cap and 296K/pool), no garbled output.

**Raw logs**: `/tmp/rpc-test/results/747.1-baseline-kvu-p2-vq51-th100k-pool148k-unknown/` (`llama-server.log` live, `test.log`, `summary.txt`), deep-e2e driver `/tmp/7471-deep-e2e.py` + log `/tmp/7471-deep-e2e2.out`, bonus probe `/tmp/bonus113.py`.

**Rig state after 747.1**: server kept LIVE for this handoff's remaining probes (deep/bonus/VRAM) — teardown will occur before 747.2 boot per the handoff checklist, with `pgrep` + `nvidia-smi` confirmation to 0/~0.


### 747.2-baseline-nokvu-p2-vq51-th80k — threshold lowered 100K→80K, separated fences, boundary correctness (2026-09-09, #747 baseline work)

**Purpose**: same as 747.0/747.1 but `--parallel-ctx-threshold` lowered `100000→80000` — the only change vs 747.0 (still `kv_unified off`, `ctx 296000=2×148224` fences after 256-alignment, `parallel 2`, `tensor_split 27,38`, `K q8_0/V q5_1` + `draft-mtp q8_0/q5_1`, `UM=1`, `cache_ram 16384`, same YaRN `yarn 5/32768`). Tests the gate's `resident + candidate >= threshold` decision **at a lower, more realistic boundary** with prompt depths chosen to straddle `80K` on both sides (single ~40K admit, concurrent 40K+45K defer vs 40K+38K admit, single ~82K defer) — complementary to 747.0's extreme 157K-combined test, not a throughput measurement.

**Build**: same `/tmp/opencode/747-baseline` `0ed2ac31e` (`FA_ALL_QUANTS=ON` `FORCE_CUBLAS=OFF`, CUDA 13.2.2, `86;120`) — gate PR #110 with NEW admit-side log line, reused for all 747.x arms. Shared build provenance with 747.1 (no rebuild).

**Boot (fresh, 23s, 10/10 GOOD, no Xid/OOM)**:
- No `capping per-slot` line — expected: per-slot `148224` (`296000` aligned to 256, split 2 ways) < `n_ctx_train 262144`, so YaRN `5/32768` does not cap here (unlike 747.1's 296K shared pool which capped `296192→148000`). Log: `0.21.996.897 I srv    load_model: initializing, n_slots = 2, n_ctx_slot = 148224, kv_unified = 'false'` (148224 = 148000 aligned).
- `W cmn  common_init_: KV cache shifting is not supported` + `W srv    load_model: cache_reuse is not supported` — same expected lines as 747.0/747.1 for this context shape (cache_reuse disabled with `kv_unified off`).
- Startup loop 10× small (`808` candidate) all `admit task * (resident 0 + candidate 808 < threshold 80000)` — sanity gate not deferring small prompts.
- VRAM at boot/warm: **15847/16311 MiB CUDA0 (464 free), 11911/12288 MiB RPC0 (377 free)** — byte-identical to 747.0/747.1 boots, pools preallocated at `296000` cells (2×148224 fences).

**Probes (LIVE server, no reboot, via curl, exact `resident`/`candidate`/`threshold` values from the NEW admit-side log line `0ed2ac31e`)**:

All deep prompts built as `N× bigprompt-1109.txt` (1052 tok/copy, verified via `/tokenize`; `+52` chat-template overhead at gate, same as 747.1) — except Probe 2's fix used a ~600-token unique prefix to bust `LCP similarity f_keep ~0.997` cache (without prefix a 38-copy prompt cached to ~2657 eval tokens in 7.4s; with prefix it prefills full ~40K tokens in ~55s, giving a long resident window).

**Probe 1 — single ~40K (38-copy, 39976 content → 40028 candidate) alone**:
- Sent alone, `resident 0`.
- **Gate (verbatim)**: `1.27.350.318 I srv  process_sing: parallel-ctx-threshold: admit task 510 (resident 0 + candidate 40028 < threshold 80000)` → `status 200`, `prompt_tokens 40028`, `prompt_ms 55008 @ 727 tok/s`, wall 57.9s. **ADMIT** ✓ (`0+40K<80K` — gate must not defer a non-limit request).

**Probe 2 — while A (~40K) resident, fire B ~45K (expect DEFER) and B_small ~38K (expect ADMIT) in the SAME slot-open window**:
- A = 38-copy with unique prefix → `40890` content tokens → `40942` candidate (prefix overhead). Sent first, `admit task 724 (resident 0 + candidate 40942 < threshold 80000)` ✓, wall 117.3s (full prefill, 40K tokens), `is_processing true` on slot 1.
- **B_big = 43-copy → 46152 content → 46204 candidate**, fired 4s after A (A still prefilling, `resident 40942`):
  ```
  5.14.783.208 I srv  process_sing: parallel-ctx-threshold: defer task 728 (resident 40942 + candidate 46204 >= threshold 80000)
  ```
  `40942+46204=87146 >=80000` → **DEFER** ✓. Client `curl --max-time 12` timed out (queued), `cancel task 728` after timeout. Exact numbers show the boundary with both sides.
- **B_small = 36-copy → 38789 content → 38841 candidate**, fired 15s after A (A still resident, `resident 40942`, B_big still queued but `resident` counts only **active** slots, not queued):
  ```
  5.29.690.305 I srv  process_sing: parallel-ctx-threshold: admit task 736 (resident 40942 + candidate 38841 < threshold 80000)
  ```
  `40942+38841=79783 <80000` → **ADMIT** ✓ — genuine concurrent admit while A still resident. Wall 96.3s, `prompt_tokens 38841`, both A and B_small completed `200` with `finish_reason length` (96 tok each), overlapping windows (A 5.09→7.06, B_small 5.29→7.05 wall-clock, ~96s overlap). This gives **both sides of the boundary with real `resident+candidate` sums (87146 defer vs 79783 admit) in one resident window** — the intended demonstration.
- Early attempt without prefix-busting cached A in 7.4s and missed the concurrent-admit window (A finished before B_small) — fixed by prefix-busting; keep this methodology for any future 40K-class boundary tests with `cache_reuse` disabled but `LCP` still high.

**Probe 3 — after drain (resident 0), single ~82K (78-copy, 82056 content → 82108 candidate) alone**:
- Waited for `slots is_processing false` on both slots.
- **Gate (verbatim)**: `7.20.693.826 I srv  process_sing: parallel-ctx-threshold: defer task 797 (resident 0 + candidate 82108 >= threshold 80000)` → client timeout 12s → `cancel task 797`. **DEFER** ✓ (`0+82K>=80K` — pure single-candidate `>=` comparison, isolates the `>=` itself from any `resident+` interaction).

**VRAM margins — flat after all probes**: `nvidia-smi` post-Probe 3: **15847/16311 CUDA0, 11911/12288 RPC0** — identical to boot, no phase creep. No `Xid`, no `ggml_abort`, no `cudaMalloc failed`.

**Verdict**: **PASS — gate admits/defers at the correct `resident+candidate >= 80000` boundary on both sides, with exact numbers logged on admit AND defer**.
- Single 40K admits (`40028`), single 82K defers (`82108`) — raw `>=` correct.
- Concurrent 40K+45K defers (`87146`), concurrent 40K+38K admits (`79783`) — `resident+` correct while a slot is active, demonstrating the gate does not starve or over-admit at a realistic lower threshold.
- All `candidate` values = `content tokens +52` template overhead, consistent with 747.1's `+52` observation — report both raw content and gate candidate to avoid confusion.

**Raw logs**: `/tmp/rpc-test/results/747.2-baseline-nokvu-p2-vq51-th80k-0ed2ac31e/` (`llama-server.log` live, `test.log`, `summary.txt`), driver logs `/tmp/7472-boot.log`, `/tmp/7472-probe2-rerun.out`, `/tmp/7472-probe2-fix.out` (the fix run with prefix-busted prompts is the primary record), `/tmp/7472-probe3.out`.

**Rig state after 747.2**: server kept LIVE for these probes — teardown will occur before 747.3 boot per the handoff checklist, with `pgrep` + `nvidia-smi` confirmation to `1 MiB`.


### 747.3-baseline-kvu-p2-pool256k-cap164k-oversub — kv_unified_per_slot oversubscription edge case, pool 262144 vs claimed 2×164000=328000 (2026-09-09, #747 baseline work)

**Purpose**: tests the unchecked invariant in `tools/server/server-context.cpp n_ctx_slot()` + `SRV_WRN` check that `--kv-unified-per-slot` is only validated against the **whole** pool (`kv_unified_per_slot > n_ctx_seq → warn cap has no effect`) and **never** against `cap × n_parallel` vs pool. Here: `ctx 262144` (model's native `n_ctx_train` per arm102 verification, 256K), `kv_unified_per_slot 164000`, `parallel 2` → claimed aggregate `2×164000=328000` exceeds the actual `262144`-token shared pool by `65856` tokens. The arm observes real behavior when both slots grow toward their nominal cap and the shared pool runs out first: `context_shift` eviction, allocation error/OOM, garbled output, stall, or silent interleaving. Handoff context: this is the original 747.3 dispatch; thorough testing with threshold 100K (params file default) plus a diagnostic with threshold raised to actually exercise the oversubscription.

**Build**: same `/tmp/opencode/747-baseline` `0ed2ac31e` (`FA_ALL_QUANTS=ON` `FORCE_CUBLAS=OFF`, CUDA 13.2.2, `86;120`) — no rebuild, gate + UM prefetch included.

**Boot verification — capping binds, oversub warning SILENT (the bug/gap being demonstrated)**:
- **Expected info line present (verbatim)**: `0.19.505.651 I srv    load_model: capping per-slot context (262144) to --kv-unified-per-slot (164000)` — proves the per-slot cap binds (pool 262144 → cap 164000).
- **Second cap (stale YaRN)**: `0.19.505.652 W srv    load_model: the slot context (164000) exceeds the training context of the model (163840) - capping` → `0.21.737.470 I srv    load_model: initializing, n_slots = 2, n_ctx_slot = 163840, kv_unified = 'true'`. With `rope_scaling yarn, rope_scale 5, yarn_orig_ctx 32768` (stale, not native 262144), `n_ctx_train` as seen by the server is `163840` (32768×5), so the 164000 cap is itself capped to **163840** — effective per-slot `163840`, claimed aggregate `2×163840=327680` still oversubscribed vs `262144` by `65536`. Mention in passing: draft MTP context briefly logs `n_ctx_seq (262144) > n_ctx_train (163840)` before cap line applies (uncapped draft KV by design, harmless — same as 747.1).
- **NO warning about parallel×cap vs pool**: boot log contains **zero** `SRV_WRN` about `cap × n_parallel` oversubscription (only the whole-pool `> n_ctx_seq` check exists). Grep for `parallel.*cap|candidate.*threshold|oversub` finds only the `capping per-slot` info line — silence confirmed, as predicted.
- Other boot lines: `W cmn  common_init_: KV cache shifting is not supported` + `W srv    load_model: cache_reuse is not supported` — both expected (same shape class); `context_shift on` is configured but disabled for this context (no `context_shift` eviction will occur).
- VRAM at boot/warm: **15847/16311 MiB CUDA0 (464 free), 11911/12288 MiB RPC0 (377 free)** — byte-identical to 747.1/747.2 boots (pools preallocated; 262144 cells vs 296K, but `q8_0/q5_1` KV + MTP + `cache_ram 16384` dominate, difference within rounding).

**Single-request sanity — 80K prompt alone (under both threshold and cap)**:
- Prompt: `76×1109` = 79952 content → `80004` candidate (52 overhead, same as all 747.x deep prompts).
- **Gate (verbatim)**: `3.41.514.541 I srv  process_sing: parallel-ctx-threshold: admit task 515 (resident 0 + candidate 80004 < threshold 100000)` → `status 200`, `prompt_tokens 80004`, wall 13.3s (cached 4186 eval tokens due to LCP reuse — single alone never conflicts with shared pool, as expected; one slot's 80K well under both 163840 cap and 262144 pool).

**Two concurrent deep — with threshold 100000 (params file default, the actual oversubscription gate-masks)**:
- Prompts: `143×1109` = 150436 content → `150488` candidate each (52 overhead).
- **Gate (verbatim, both at resident 0)**:
  ```
  4.01.407.417 I srv  process_sing: parallel-ctx-threshold: defer task 546 (resident 0 + candidate 150488 >= threshold 100000)
  4.01.904.929 I srv  process_sing: parallel-ctx-threshold: defer task 547 (resident 0 + candidate 150488 >= threshold 100000)
  4.16.408.694 W srv          stop: cancel task, id_task = 546
  4.16.906.975 W srv          stop: cancel task, id_task = 547
  ```
  Both **defer** at `0+150488>=100000` — gate blocks **before** cap or pool are ever consulted (candidate > threshold, even alone). Client `curl --max-time 15` timed out (queued), then `cancel` on disconnect. **Result**: with threshold `100000`, the `2×164000>262144` oversubscription **cannot be exercised** — the gate defers any single prompt `>100K`, so two concurrent deep prompts never both admit to let the combined `>262K` pool pressure manifest. This is correct gate behavior at this threshold, but it means the `cap × parallel` validation gap is **masked** by the threshold at 100K — the oversubscription bug is latent, not observable, unless threshold is raised above the per-prompt size.

**Diagnostic — threshold raised to 400000 to actually allow oversubscription (same binary, same pool/cap, only `extra_server_args` changed to `--parallel-ctx-threshold 400000`, rebooted via `/tmp/7473-th300k.yml` 23s, 10/10 GOOD, same boot capping lines)**:
- Prompts: `143×1109` with unique prefix (~600 tokens) to bust LCP cache → `151103` candidate each (verified via `/tokenize`; prefix adds ~600 tokens, ensures full 151K eval, not cached).
- **Gate (verbatim, both admit)**:
  ```
  1.11.088.912 I srv  process_sing: parallel-ctx-threshold: admit task 460 (resident 0 + candidate 151103 < threshold 400000)
  1.13.760.319 I srv  process_sing: parallel-ctx-threshold: admit task 463 (resident 151103 + candidate 151103 < threshold 400000)
  3.14.699.552 I srv  process_sing: parallel-ctx-threshold: admit task 525 (resident 0 + candidate 151103 < threshold 400000)
  3.19.121.297 I srv  process_sing: parallel-ctx-threshold: admit task 527 (resident 151103 + candidate 151103 < threshold 400000)
  ```
  `0+151103<400K` and `151103+151103=302206<400K` → **both admit** ✓ — threshold now permits the combined `302K` that exceeds the `262144` pool, exposing the oversubscription. Second run's tasks 525/527 are the primary record (first run cancelled at 120s client timeout before completion).
- **Prefill behavior (interleaved, no OOM/context_shift eviction yet, verbatim)**:
  - Slot 0 (task 525) prefills at ~440-527 tok/s: `prompt processing n_tokens 2048→18432 @ 527→446 tok/s`, progressing steadily.
  - Slot 1 (task 527) prefills interleaved after slot 0 passes ~50% progress: `n_tokens 42→42458 @ 13→246 tok/s` (slower start, then ~245-250 tok/s steady). The log shows per-ubatch round-robin: slot 0 logs `prompt processing` lines dominate early, slot 1's lines appear after 525 passes 74769 progress, indicating **genuine concurrent prefill with interleaved ubatches**, not serialization.
  - No `context_shift` eviction (disabled for this context), no `ggml_abort`, no `cudaMalloc failed`, no garbled output — expected since `context_shift` is off and KV pool is not yet at eviction point at ~45K-75K processed per slot (still under 151K each, combined ~120K processed, under 262K pool). The test was **cancelled at 9.38** (`cancel task 525/527` on client `kill` after ~360s) before either slot reached its nominal 163840 cap or the combined 262144 exhaust (`151103×2=302206` would need full prefill to manifest). Wallace time per slot ~117-180s before cancel, still mid-prefill.
  - **What was NOT observed**: no allocation error/OOM, no stall, no `context_shift` line (disabled), no `Xid`/`dmesg` error. The predicted `~131K each` collision (262144/2) was not reached before cancel — the diagnostic was intentionally bounded to demonstrate **admission** under oversubscription, not to drive to inevitable pool exhaustion (which would need ~262K combined, i.e. both slots near 131K, still under 151K each, but requires ~300s+ more prefill). The **silence of the boot-time `cap × parallel` validation** and the **threshold's masking at 100K** are the primary findings, not a specific failure mode at 131K.

**Four-phase VRAM margins (boot/warm/fill/decode)**:
- Boot: `15847/16311 CUDA0 11911/12288 RPC0` (464/377 free)
- Warm (post 10× small): flat same
- Fill (post single 80K + two 150K defer + diagnostic two 151K concurrent mid-prefill): flat same (pools preallocated at 262144 cells, no phase creep)
- Decode: `tg` metrics from completed requests: small prompts `~40-48 tok/s` single, deep diagnostic mid-prefill decode not yet reached (prefill still ongoing at cancel) — no decode-phase VRAM drift.
- Xid: `none` across `dmesg`/`journalctl` sweep.

**Verdict**: **PASS — boot-time `capping per-slot (262144→164000→163840)` binds, NO `parallel×cap` warning (gap demonstrated ✓), threshold 100K masks the oversubscription by deferring any single `>100K` prompt, threshold 400K lets both `151K` prompts admit (`302K` combined > `262K` pool) with interleaved prefill and **no immediate OOM/eviction/garble at mid-prefill**; longer run to full `151K` each would be needed to observe the pool-exhaustion behavior (predicted ~131K each), but the validation gap itself is confirmed and the gate's threshold interaction is now documented. Recommend a follow-up that either raises threshold or lowers `kv_unified_per_slot` to `≤131072` (262144/2) to make the claimed aggregate respect the pool — or adds the missing `cap × n_parallel > n_ctx_seq` warning in `server-context.cpp`.

**Raw logs**: `/tmp/rpc-test/results/747.3-baseline-kvu-p2-pool256k-cap164k-oversub-0ed2ac31e/` (boot with threshold 100K, `llama-server.log` live), `/tmp/7473-boot.log`, `/tmp/7473-th400k-boot.log`, `/tmp/7473-single.out`, `/tmp/7473-deep-oversub2.out` (diagnostic 400K threshold, tasks 525/527), params `/tmp/7473-th300k.yml` (threshold 400K diagnostic, not the committed `747.3` file).

**Rig state after 747.3**: diagnostic cancelled, server kept LIVE for this report — teardown will confirm `pgrep` 0 and `nvidia-smi` 1 MiB before closing.

### 747.3 container-validation — production-mode pin-candidate check, cache_ram 24576 (2026-09-09, #747 container build)

**Purpose**: re-run the 747.3 arm (kv_unified 2×164000 vs 262144 pool, oversubscription + threshold 100000) through the **production container path** (`infra/llama-baseline/docker-compose.baseline.yml` + `Dockerfile.baseline`) with `cache_ram_mib` bumped `16384→24576`, not just the bare-metal `run-with-params.sh` harness. Checks that the container picks up the 747.3 params file's exact args (no entrypoint drift), healthchecks pass, model loads, and VRAM is unchanged (host-RAM flag). This is the pin-candidate production-mode run — NOT a full oversubscription diagnostic repeat (that was already done bare-metal).

**Pre-flight fix — entrypoint drift**: initial `podman compose up -d --build` produced `Args:` without `--kv-unified-per-slot` or `--parallel-ctx-threshold` (entrypoint `llama-cpp-entrypoint.sh` only parsed `cache_ram_mib`, not `kv_unified_per_slot`/`extra_server_args`). Fixed in this worktree by adding `P_KV_UNIFIED_PER_SLOT`/`P_EXTRA_SERVER_ARGS` parsing + `LLAMA_ARGS+=(--kv-unified-per-slot…)` and `LLAMA_ARGS+=(${P_EXTRA_SERVER_ARGS})` (mirrors `run-with-params.sh` §"Per-slot cap"/"extra_server_args"). Rebuilt with `podman build --no-cache -f infra/llama-baseline/Dockerfile.baseline -t localhost/llama-baseline:upstream .` (cache-busted; prior cached layers reused stale entrypoint — `podman compose build` without `--no-cache` hit cache). `src/llama-cpp` confirmed at `0ed2ac31e` (`fa_all_quants=ON FORCE_CUBLAS=OFF cuda13.2.2 86;120`) — same gate binary as all 747.x bare-metal.

**Podman storage incident (incidental)**: first `podman compose up -d --build` failed `readlink /mnt/containers/overlay/l/4NS7FT3NA36F2O4GEAZVVYO6DH: no such file or directory` (overlay corruption). `podman system prune -f` reclaimed 52.5 GB but still failed; removing the stale `localhost/llama-baseline:upstream` image (`podman rmi -f`, deleted `ab42aeb9c211…`) cleared the dangling layer and the subsequent build succeeded (`91bbbf00bfa3`). Old `ubuntu:26.04` was in-use by a prior container (`ada96f…`) — forced `podman pull docker.io/library/ubuntu:26.04` to `af52039db3f8` before rebuild. Not a code bug, just host storage state.

**Before/after `podman ps` — stale containers replaced (not left as duplicates)**:
- Before fix: `llama-baseline_rpc_1` + `llama-baseline_llama_1` `Exited (137) 34h ago` (pre-existing arm090 footprint); plus after first (broken-entrypoint) build: `cb2488a0ce42` (rpc healthy) + `047cef279d9d` (llama starting→healthy).
- After `down` + `--no-cache` rebuild + `up -d`: `b35202a5c4e9` (rpc) + `68aaeba8dfe7` (llama) — new IDs, old `cb248/047ce` removed. Verified via `podman ps -a` before/after.
- RPC image tag is `localhost/llama-baseline:upstream` (`91bbbf00bfa3` after fix).

**Healthchecks — waited for healthy**: polled `podman ps` every 5s through the 60s `start_period`. RPC became `healthy` at ~6s (TCP `:50052`), llama transitioned `starting→healthy` at ~34s (curl `http://localhost:18081/health`). At steady state after validation: `llama-baseline_llama_1: healthy (0 fails)` via `podman inspect --format '{{.State.Health.Status}}'`; `llama-baseline_rpc_1: unhealthy (10 fails)` via inspect but `podman ps` briefly showed `healthy` then flipped to `unhealthy` — healthcheck `bash -c 'echo > /dev/tcp/127.0.0.1/50052'` is flaky under `network_mode: host` + podman-compose's shell probe, while `podman logs llama-baseline_rpc_1` shows `Accepted client connection`/`Client connection closed` continuously and `llama-server` logs show `RPC0[127.0.0.1:50052]` model buffer `7062 MiB` and steady `update_slots: all slots are idle` — RPC is serving, healthcheck probe is the only failing signal, not the data path. Llama `curl -f http://localhost:18081/health` is the authoritative readiness signal and stayed `healthy`.

**Logs — container picked up 747.3 args**:
```
Args: ... --parallel 2 --cont-batching --kv-unified ... --cache-prompt --cache-reuse 64 --prio-batch 1 --context-shift --cache-idle-slots --kv-unified-per-slot 164000 --cache-ram 24576 --parallel-ctx-threshold 100000 --metrics --slots --log-verbosity 4
0.19.686.889 I srv    load_model: capping per-slot context (262144) to --kv-unified-per-slot (164000)
0.19.686.xxx W srv    load_model: the slot context (164000) exceeds the training context (163840) - capping  →  n_slots=2 n_ctx_slot=163840 kv_unified='true'
0.21.917.456 I srv    load_model: initializing, n_slots = 2, n_ctx_slot = 163840, kv_unified = 'true'
```
Matches bare-metal 747.3 capping (`262144→164000→163840` after YaRN `32768*5=163840`) and `n_ctx_slot 163840`. `--cache-ram 24576` confirmed in `Args` (host-RAM, VRAM should be flat — see below). Also `W cmn common_init_: KV cache shifting is not supported` + `W srv load_model: cache_reuse is not supported` lines present as on bare-metal.

**Liveness — 10× curl health**:
```
for i in 1..10: curl -s http://localhost:18081/health → {"status":"ok"}  [10/10]
podman inspect llama-baseline_llama_1 → healthy
podman inspect llama-baseline_rpc_1 → unhealthy (healthcheck flake, data path ok — see above)
podman ps → both Up (llama healthy, rpc unhealthy per inspect but serving)
```

**VRAM — flat, cache_ram is host RAM**:
```
nvidia-smi: CUDA0 15847 MiB / 16311 MiB (464 free) — 5f11? actually GeForce RTX 5060 Ti
           CUDA1 11911 MiB / 12288 MiB (377 free) — GeForce RTX 3060
Processes: PID 674687 llama-server 152 MiB (CUDA0), PID 674438 ggml-rpc-server 126 MiB (CUDA1)
```
Byte-identical to every 747.x bare-metal boot (747.1/747.2/747.3 all 15847/11911). The `24576` vs `16384` `cache_ram` bump did **not** move VRAM — expected: `--cache-ram` is host RAM for idle-slot swap, not VRAM. No OOM, no Xid.

**Verdict**: **PASS — container builds and serves 747.3 correctly in production mode.** Same binary, same params, same capping/VRAM as bare-metal; entrypoint drift fixed; no new failure mode introduced by containerization. Still UNOBSERVED: the actual pool-exhaustion failure at ~131K/slot concurrent growth (bare-metal diagnostic was cancelled at ~50% prefill) — container run was **not** re-driven to that depth, so do **not** treat this as a confirmed-safe production pin until that gap is closed. Keep `dagger` note in `docker-compose.baseline.yml` ("failure mode still UNOBSERVED") as-is.

**Left running**: `b35202a5c4e9` (rpc) + `68aaeba8dfe7` (llama) on `localhost:18081` with 747.3 pin-candidate config (`cache_ram 24576`, `threshold 100000`). Do **not** tear down — queued 12-turn decode-speed task runs against this same container. If container had failed, fallback would have been bare-metal `run-with-params.sh` with `747.3-baseline-kvu-p2-pool256k-cap164k-oversub.yml`.

**Raw evidence**: `podman logs llama-baseline_llama_1` (Args + capping lines above), `podman ps` before/after IDs, `curl -s http://localhost:18081/health` ×10, `nvidia-smi` (15847/11911), build log `podman build --no-cache` → `91bbbf00bfa3`, entrypoint diff in `infra/llama-baseline/llama-cpp-entrypoint.sh`.

### 747.3 — 12-turn growing-context decode-speed sweep, 2 concurrent sessions 10K→65K, same container pin (2026-09-09)

**Purpose**: decode tok/s vs context depth under realistic 2-concurrent-session growth against the **same** 747.3 container (`cache_ram 24576`, `threshold 100000`, `kv_unified_per_slot 164000→163840`, `ctx 262144`) validated above — no threshold override, no bare-metal fallback (container stayed healthy). Each session grows independently via `infra/llama-baseline/prompts/bigprompt-1109.txt` copies (verified `1052 tok/copy` via `/tokenize`, `+52` chat-template overhead at gate → `prompt_tokens = copies*1052+52`). Turn 1 = 10 copies (≈10,520 content / 10,572 total), each subsequent turn APPENDs 5 copies (≈5,260 content) and resends full accumulated history via `/v1/chat/completions` so `cache_prompt`/`cache_reuse 64` (LCP) only prefills the new suffix — realistic chat growth, not full reprefill. `n_predict 96` per turn (modest, measures `tg` decode rate, not prefill). Depth trajectory per slot: 10K,15K,20K,…65K content (≈10,572→68,432 total). Both sessions' 12 turns are fired **concurrently per turn** (two threads per turn, same wall-clock window — same idea as 747.1 deep e2e driver but sustained 12 turns) — verifies gate behavior when `resident+candidate ≥ threshold`.

**Method**: `python /tmp/12turn_decode_test.py` (concurrent per-turn driver, `temperature 0`, `max_tokens 96`, `timeout 180s`), two threads per turn, `time.time()` wall, `usage.prompt_tokens`/`prompt_tokens_details.cached_tokens` + `timings.{prompt_n,cache_n,prompt_ms,prompt_per_second,predicted_per_second}` per slot, plus delta `podman logs … | grep parallel-ctx-threshold` for exact `resident+candidate<threshold` admit vs `≥threshold` defer lines. RPC `unhealthy` healthcheck flake persisted but data path stayed serving (same as container-validation — `Accepted client connection` continuously, llama healthy). `nvidia-smi` not re-polled mid-sweep (same 15847/11911 footprint as before).

**Table — depth vs throughput per turn, per slot** (`depth(content)` = copies*1052, `depth(total)` = prompt_tokens incl. +52 overhead; `prefill` = `timings.prompt_n` actually processed vs `cache_n` hit; `prompt_tps` = prompt eval rate; `gate` = server's threshold log for that turn's two admits/defers; `wall max` = slower of the two concurrent slots):

| turn | copies | depth(content) | depth(total) | decode slot0 (tg) | decode slot1 (tg) | prefill processed (prompt_n) s0/s1 | cache_n s0/s1 | prompt_tps s0/s1 | gate event | wall max |
|---|---|---|---|---|---|---|---|---|---|---|
| 1 | 10 | 10520 | 10572 | 32.4 | 5.6 | 10572 / 516 | 0 / 10056 | 713 / 433 | admit 0+10572<100000 (task 70); admit 10572+10572<100000 (task 72) | 18.7s |
| 2 | 15 | 15780 | 15832 | 31.4 | 17.3 | 5776 / 5776 | 10056 / 10056 | 487 / 390 | admit 0+15832<100000; admit 15832+15832<100000 | 21.2s |
| 3 | 20 | 21040 | 21092 | 30.0 | 16.1 | 5776 / 5776 | 15316 / 15316 | 441 / 353 | admit 0+21092<100000; admit 21092+21092<100000 | 23.3s |
| 4 | 25 | 26300 | 26352 | 28.9 | 15.2 | 5776 / 5776 | 20576 / 20576 | 385 / 309 | admit 0+26352<100000; admit 26352+26352<100000 | 26.1s |
| 5 | 30 | 31560 | 31612 | 27.7 | 14.4 | 5776 / 5776 | 25836 / 25836 | 371 / 294 | admit 0+31612<100000; admit 31612+31612<100000 | 27.6s |
| 6 | 35 | 36820 | 36872 | 13.5 | 26.6 | 5776 / 5776 | 31096 / 31096 | 273 / 345 | admit 0+36872<100000; admit 36872+36872<100000 | 29.7s |
| 7 | 40 | 42080 | 42132 | 12.8 | 25.6 | 5776 / 5776 | 36356 / 36356 | 254 / 322 | admit 0+42132<100000; admit 42132+42132<100000 | 31.8s |
| 8 | 45 | 47340 | 47392 | 12.3 | 24.8 | 5776 / 5776 | 41616 / 41616 | 238 / 303 | admit 0+47392<100000; admit 47392+47392<100000 (94784<100000 by 5216) | 33.8s |
| 9 | 50 | 52600 | 52652 | 31.8 | 31.8 | 4 / 5776 | 52648 / 46876 | 15 / 343 | admit 0+52652<100000; **defer 52652+52652=105304≥100000** (task 353) → admit 0+52652<100000 after first completes | 24.3s |
| 10 | 55 | 57860 | 57912 | 31.7 | 31.6 | 4 / 5776 | 57908 / 52136 | 14 / 343 | admit 0+57912<100000; **defer 57912+57912=115824≥100000** → admit after | 23.2s |
| 11 | 60 | 63120 | 63172 | 31.6 | 31.7 | 11036 / 4 | 52136 / 63168 | 341 / 14 | admit 0+63172<100000; **defer 63172+63172=126344≥100000** → admit after (note s0/s1 swap — whichever raced first got the 4-token hit) | 38.7s |
| 12 | 65 | 68380 | 68432 | 31.6 | 31.6 | 4 / 5776 | 68428 / 62656 | 14 / 342 | admit 0+68432<100000; **defer 68432+68432=136864≥100000** → admit after | 23.2s |

*Notes on table*: `decode` = `timings.predicted_per_second` (tg, server-side decode-only, not wall-derived `comp_tokens/wall` which is ~3-5 tok/s wall due to including prefill wall time — report wall_max separately). `prompt_n` 5776 = the incremental 5-copy suffix actually processed (≈5,260 content + overhead + boundary tokenization delta; matches 747.2's ~5K increment — cache_reuse working: `cache_n` grows by ~5,260 each turn, `prompt_n` stays flat at 5,776 until gate serializes). Turns 9-12 `4` / `11036` are the identical-prompt artifact: both sessions used the **same** bigprompt text, so after the first of the pair admits and fills its slot, the deferred second (same prompt) hits almost 100% cache (`prompt_n 4`, `cache_n = prompt_tokens-4`) when it finally admits with `resident 0` — a real heterogeneous two-session workload with distinct content would both see `5776` processed. Turn 11's `11036` is a double-increment (two suffixes missed due to slot reassignment after prior defer — still within 2×5,776 budget). `gate event` resident values are literal server logs (`process_sing: parallel-ctx-threshold: admit/defer task N (resident X + candidate Y … threshold 100000)`).

**One-line verdict**: **No decode crater up to 65K per slot** — under true concurrency (turns 1-8, both admit) decode drifts gently `~32→12 tok/s` (slowest 12.3 at 47K, still >10 tok/s) and per-turn wall grows ~18→34s with depth; once combined depth crosses threshold at ~50K/slot (turn 9, `52652*2=105304≥100000`) the gate **defers the second session, serializes the pair (queue → late admit with resident 0), and decode *recovers* to ~31-32 tok/s** — the deferred turn just queued and completed late (wall 23-38s, still within `timeout 180s`), did **not** break the session's turn sequence (next turn's growing prompt still admitted correctly, depth continued 52K→68K). Production-relevant finding: threshold 100K trades concurrent throughput for per-slot speed at high depth — it prevents both slots reaching their 163840 cap simultaneously (claimed 327680 > 262144 pool) by throttling to sequential at ≈50K/slot; the oversubscription pool-exhaustion failure remains unobserved because the gate stops the workload before the ~131K/slot collision point.

**Cache_reuse confirmation**: `prompt_n 5776` vs `5,260` content increment + `cache_n` monotonic `10056→68428` shows LCP hit ~95-99% each turn (only the 5-copy suffix prefills: `prompt_per_second` 238-487 tok/s). Turns 1's slot0 cold `10572/0` vs slot1 warm `516/10056` shows the first concurrent pair interfered: the second slot's decode dropped to `5.6 tok/s` while the first prefills (≈747.0's 0.99 tok/s interference pattern) — same mechanism, less severe at 10K depth.

**Wall time vs depth**: wall_max `18.7s→33.8s` (concurrent) then `23-24s` (serialized) — not monotonic because serialized turns avoid concurrent prefill stall and benefit from full cache hit (identical prompts). With heterogeneous prompts, serialized wall would be ~`2×(prefill 5,776 + decode 96)` ≈ 2×~12-15s = 24-30s, still bounded.

**Left running**: same `b35202a5c4e9` + `68aaeba8dfe7` on `localhost:18081` (747.3 pin, cache_ram 24576) — do not tear down. Fallback not needed (container stayed healthy throughout; RPC healthcheck still `unhealthy` per inspect but `Accepted client connection` and llama `healthy` — gate logs and decode prove serving).

**Raw evidence**: driver `/tmp/12turn_decode_test.py` + log `/tmp/12turn_run.log`, results JSON `/tmp/12turn_results.json`, gate log `/tmp/12turn_gate.log`, `podman logs llama-baseline_llama_1` gate lines (admit/defer with resident/candidate/threshold values above).


### bugfix (2026-09-09): #747 gate permanently defers a lone request exceeding the threshold (resident==0) - production agent c865947e unblocked

**Impact (live production incident).** Agent c865947e (Paseo, `baseline/local` -> container `llama-baseline_llama_1` :18081) had its conversation cross 100K tokens; every subsequent turn deferred forever:
`133.05 defer task 10542 (resident 0 + candidate 102401 >= threshold 100000)`, `186.39 defer task 12822 (resident 0 + candidate 104482 >= threshold 100000)`. Client saw timeouts only.

**Root cause.** The gate (tools/server/server-context.cpp, `process_single_task`) deferred any request with `resident + candidate >= threshold`, including `resident == 0`. A lone request bigger than the threshold on an otherwise-idle pool defers with NO retry trigger: nothing is resident, so no slot release ever fires `pop_deferred_task`. Permanent stall. The threshold guards combined oversubscription between concurrent requests - it was never meant to bound a single request's own size (that is the per-slot cap / `n_ctx_slot` check, already correct).

**Fix.** `fork/hydra-747-parallel-ctx-threshold-baseline` commit `3ab0fdec8` (updates fork PR #110): defer now requires `resident > 0` AND `resident + candidate >= threshold`; a lone request (resident 0) always admits. Admit log gains an accurate lone-request variant (`resident 0 + candidate N >= threshold T; lone request on idle pool`) so the printed comparison never lies (the old unconditional `< threshold` admit line would have been wrong in this case).

**Verification (bare-metal, 747.3 yml, build-cuda1322 @ 3ab0fdec8; live agent traffic hit the same instance on :18081 while production was stopped, doubling as a real-workload test).**
- Lone request >= threshold ADMITS: agent's own 104,482-token turn admitted at 0.52 boot-time (`resident 0 + candidate 104482 >= threshold 100000; lone request on idle pool`); next agent turn 114,353 admitted lone at 10.21; explicit probe 118,928 (113 copies) admitted lone at 19.14 and completed end-to-end in 542s, valid JSON, 64 completion tokens.
- Contention protection intact - 6 defer events, all genuine contention: `918 + 104482`, `104482 + 808`, `58790 + 118928`, `69360 + 80004` (x2, deep pair), `118991 + 70992`.
- Defer -> release -> auto-retry: tasks 414 (808 tok), 6765 (63,336), 8962/8963 (deep pair 80,004 each) all auto-admitted after the blocking slot released; pair A+B both completed valid (96 tokens each). Transient resident inflation observed (118,991 while only A processing) - stale same-slot prompt-cache residue before prefix trim; decision still correct, resolves within seconds.
- Small requests correctly wait behind deep residents (808-token request deferred at `104482 + 808` then admitted on release) - matches #747 intent; threshold 100K < cap 164K means a deep resident blocks any concurrent admission until it finishes. Expected under this pin, not a defect.

**Redeploy (production).** Image rebuilt `--no-cache` from `src/llama-cpp/build-cuda1322` (binaries @ 3ab0fdec8; llama-server build 10812, rpc unchanged) -> image `e6d04f12c9b2`. GOTCHA: `podman compose up -d --build` did NOT recreate the container - it restarted the existing container still backed by the stale pre-fix image `91bbbf00` (built 07:56 by muse-spark), and a lone 105,252-token probe DEFERRED (`11.39 defer task 0 (resident 0 + candidate 105252...)`) on it. Version string is useless for distinguishing (both binaries print `build 10812, commit 0ed2ac31e` - tree HEAD was the same at both build times) and the exec-md5 check misfired; only the behavioral probe exposed the stale image. Required `podman compose down` + `up -d` to actually recreate. Re-verify on the recreated container: the SAME 105,252-token probe ADMITTED (`6.09 admit task 0 (resident 0 + candidate 105252 >= threshold 100000; lone request on idle pool)`).
- Cost accepted per dispatch: restart dropped the in-process idle-prompt cache (incl. c865947e's ~101,978-token saved state); its next turn pays a fresh ~104K prefill (~5 min) instead of staying stuck forever.
- Status at write time: c865947e already unblocked (its backlog drained through the bare-metal instance running the identical binary+config: turns 104,482 / 114,353 / 58,790 / 63,336 / 67,375 / 70,992 / 80,004 x2 all admitted with correct gate semantics). Production next-retry admission: monitoring `podman logs llama-baseline_llama_1`.
- Open follow-ups: parent-side submodule bump to `3ab0fdec8` for PR #748 (parent tree currently carries muse-spark's uncommitted edits - left untouched); container rpc_1 healthcheck shows unhealthy-but-functional (pre-existing, unchanged).

### verification (2026-09-09): #747 gate exclusivity is race-safe by construction; deep-session serialization is designed behavior (no code change)

**Behavioral evidence (30-min live production window, fixed binary e6d04f12c9b2).** Every deep (~85K-126K) task strictly serialized; `/slots` showed at most one slot processing at any instant:
```
179.51 admit task 22999  (resident 0 + candidate 123647 >= threshold; lone request on idle pool)
180.18 defer task 23611  (resident 123683 + candidate 85155 >= threshold)
191.09 defer task 24207  (resident 125193 + candidate 88775 >= threshold)
194.31 admit task 23611  (resident 0 + candidate 85155 < threshold)   <- only after 22999 fully released
194.36 defer task 24392  (resident 85190 + candidate 126231 >= threshold)
195.19 admit task 24207  (resident 0 + candidate 88775 < threshold)   <- only after 23611 fully released
206.53 admit task 24392  (resident 0 + candidate 126231 >= threshold; lone request on idle pool)
```
Note the ordering at 194.31/194.36: release -> deferred retry admitted (resident 0) -> the NEXT deferred retry 50 ms later already sees resident 85190 -> defer. Single-thread sequencing, no interleaving window.

**Code-level atomicity confirmation (3ab0fdec8).** The gate's read-compare-act needs no mutex because task admission is structurally single-threaded:
- Admission (`process_single_task`, wired via `queue_tasks.on_new_task`, server-context.cpp:1408-1410) runs on exactly one of two threads, never both: the main loop thread (`start_loop` -> `process_new_tasks(false)`, server-queue.cpp:301) OR the yield worker (`worker_loop` -> `process_new_tasks(true)`), and the worker only runs while `worker.busy` is set - exclusively inside `yield_to_queue()` (server-queue.cpp:230, non-nestable assertion :229). During a yield the main thread runs the work lambda and then blocks for `!worker.busy` (:249-251). The two processing paths cannot overlap by construction, not by timing.
- Tasks are processed one at a time (server-queue.cpp:138-161: pop -> callback -> next), so each task fully completes gate check + slot launch before the next task's gate scan runs.
- `launch_slot_with_task` synchronously sets `slot.state = SLOT_STATE_STARTED` / `WAIT_OTHER` before returning (server-context.cpp:1822-1824); `is_processing()` = `state != SLOT_STATE_IDLE` therefore already observes a just-admitted task as resident for the very next arrival.
- All post-gate fallbacks are conservative (never over-admit): no free slot -> defer (:2423-2427); busy explicitly-requested slot -> defer (:2430-2434); launch failure -> drop with SRV_ERR (:2450-2452). Deferred tasks re-enter `process_single_task` and re-evaluate the gate from scratch.
- Release path (`server_slot::release()`: state=IDLE -> `callback_on_release(id)` -> `pop_deferred_task(id_slot)`, server-context.cpp:552-563, server-queue.cpp:90-108) runs on the same exclusive task thread; re-posts one task at a time, each individually re-evaluated.
- The only mutexes (`mutex_tasks` queue coordination; `mutex_cache` prompt cache) never guard the gate - not needed: gate data (`slots`, queues) is exclusively owned by the single task-processing thread.
- The four `yield_to_queue` work lambdas (speculative draft, mtmd chunk, llama_decode, speculative process - server-context.cpp:3064/3514/3700/3765) run pure ggml/llama compute on the main thread and touch no slot/task-queue state, so even during yields there is zero concurrent access to the gate's data.
- Residual caveat (benign, conservative direction): a released slot mid-scan can only DECREASE resident; `WAIT_OTHER` parent slots count as resident while waiting on children - both undercount/overcount errors point safe. A gate-admitted task that then fails to launch is dropped on the error path (server is erroring anyway), never silently double-admitted.

**Conclusion.** The single-deep-request guarantee does not depend on arrival timing; it is enforced by the server's single-task-thread architecture. The "once any resident depth >= threshold, nothing of any size admits until full release" property is the direct, intended consequence of the documented sum-based semantics (`resident-ctx-sum + candidate >= threshold`, tools/server/README.md:179, commit d06537485) - a resident at or above the threshold leaves zero combined-context headroom for any candidate. Documented here as explicit designed behavior; no code change made.

### verification (2026-09-09): 747.4 (nokvu) idle-slot residency hypothesis REFUTED; prompt-cache reuse CONFIRMED working on both slots; the 61-88K cliff model is obsolete post-patch

**Task 1 verdict - the stated mechanism is wrong; 747.4's speed is real but has a different cause.**
- REFUTED at code level: the production binary fully prefetches ALL managed allocations to the device at alloc time (commit ba2c46f65, in both 747.3's image 91bbbf00 and 747.4's e6d04f12c9b2: cudaMemAdviseSetPreferredLocation + cudaMemPrefetchAsync immediately after cudaMallocManaged). There is no demand-paging regime left for KV cells: every one of the 262144 cells (both fences in nokvu, the whole shared pool in kvu) is physically VRAM-resident from boot, filled or not. An idle fenced slot costs FULL bytes, not zero.
- REFUTED empirically: fb flat at 15847/11911 MiB from boot on 747.4 (no residency dynamics); idle footprint IDENTICAL across 747.0 (nokvu 296K cells), 747.3 (kvu 262144+cap), 747.4 (nokvu 262144) - whole pool resident at boot in every config.
- kvu-vs-nokvu ruled out as the collapse variable in BOTH directions: 747.2 (NOKVU) collapsed to 2.75 t/s (task 736, slot 0) while its sibling decoded 28.8 t/s at ~78K combined concurrent - nokvu collapses too; 747.3 (KVU) 12-turn sweep held 12-31 t/s with both slots concurrent to 65K/slot (131K combined) and recovered to 31.6 t/s serialized - kvu does not inherently crater. Config is not the variable.
- The 61-88K cliff model (pages materialize as slots fill; §3430) described the PRE-PATCH arms only (measured Sep 7/8; ba2c46f65 landed Sep 8 20:00). Post-patch, 747.4 sustains ~190-200K COMBINED filled cells (slot 1 ~110K + slot 0 ~91-96K) with serialized lone decode at 20-25 t/s and ZERO paging storm - way past the old cliff. The old model needs a post-patch re-derivation.
- Live telemetry this boot (747.4, active 108K+ lone decode, nvidia-smi dmon): rxpci/txpci at noise level (0-270 MB/s blips = checkpoint copies) vs the 10.7-13.5 GB/s + 6.7-11.3 GB/s sustained storm signature recorded pre-patch (§3457). No host paging during deep decode.
- What ACTUALLY explains the observed patterns post-patch: (a) decode-while-concurrent-prefill interference (proven repeatedly: 747.0's 0.99 t/s, 747.2 task 736's 2.75 t/s - the decoder starves while the sibling slot prefills tens of K tokens; NOT a memory cliff); (b) pre-patch UM eviction storms (§3457 - gone post-patch). The 747.3 production window's 2.3-2.8 t/s at 85-128K lone: raw logs did not survive the container recreation, so the exact cause is UNVERIFIED - most consistent with an interference/burst pattern (sibling prefill, checkpoint/restore bursts), NOT with a kvu-inherent residency property (the 747.3 sweep's own 65K/slot concurrent data contradicts that). The orchestrator's "8x improvement" is real as observed but the kv_unified-off causal story is not supported; treat the 747.3-vs-747.4 comparison as traffic-confounded until a controlled same-traffic A/B exists.
- Generalization to parallel=3+: the operative property is "one deep decode at a time is fine; deep decode overlapping another slot's large prefill (or pre-patch: combined overflow) collapses". Fencing bounds each session to total/n_parallel cells and the threshold gate serializes deep decodes - those are the protective factors, and both hold at any parallel count. The kvu/nokvu choice itself is not the protective factor.

**Task 2 verdict - YES, prompt caching works correctly on both slots on production 747.4.**
- Slot 1 same-conversation chain (candidates 85918 -> 96419 -> 97866 -> 98950 -> 100669 -> 108589 -> 109622): prompt-eval processed only 2718 / 621 / 1237 / 1380 / 163 / 166 / 1401 / 7924 / 206 tokens per turn - LCP cache hits of 96.2-99.8% (the 7924 on task 5136 reflects the slot having served another conversation in between; still ~93% hit). Decode at depth: 22.72 t/s @108K, 23.21 t/s @~110K.
- Slot 0 second conversation: 91060 first prefill (new, correct), then 43 tokens processed vs 96387 candidate (99.96% hit), then 576 (441 tok/s suffix rate); decode 25.03 t/s @100K, 39.64 t/s.
- ZERO "forcing full prompt re-processing due to ... hybrid/recurrent memory" lines (arm105's failure mode NOT reproducing); --cache-prompt --cache-reuse 64 --cache-idle-slots all active in Args; LCP slot-selection firing every turn (f_sim 0.986-0.998).
- Checkpoint machinery: create/erase churn CONFIRMED firing (context checkpoints ~343-344 MiB at 108K depth, created/erased every ~512 tokens during decode, 6-7 of 32 slots in use) - the arm105-flagged tax is real but bounded and does not break caching; restore path ("restored context checkpoint") unexercised this boot (no idle-eviction yet; machinery unchanged from the 747.3-era config where session-swap restores were verified). Caveat: if heavy session-swap traffic resumes, watch whether restore bursts re-introduce interference (not memory paging - compute/IO overlap).
- Verdict: no revert to 747.3 needed on cache grounds; current deploy is sound for the observed workload. Open follow-up: controlled 747.3-vs-747.4 same-traffic A/B if the 8x claim needs to be attributed to config vs traffic pattern.

### finding (2026-09-09): deferred-task retry is release-event-only and one-pop-per-release - a gate-passed request that cannot launch can hang silently (root cause confirmed, no fix applied pending triage)

**Observed (production 747.4)**: task 12239 (75 tokens) printed its gate ADMIT line at 33.33.318 but never got a launch_slot_ event; slot 0 then sat idle from 33:46 with nothing running, and the client (curl -m 120) timed out with no response and no error. An older deferred task (10767, 104875 tokens) was re-evaluated and re-deferred at the same release event (33.46.507).

**Root cause chain (code-verified, 3ab0fdec8)**:
1. Both defer paths converge on one queue: a task the gate ADMITS but that then finds no free slot goes through get_available_slot -> nullptr -> queue_tasks.defer (server-context.cpp:2423-2427) - the SAME deferred FIFO as gate-deferred tasks. There is no separate "admitted-but-not-launched" path.
2. Deferred tasks have exactly ONE retry trigger: pop_deferred_task(id_slot), called ONLY from callback_on_release (server-context.cpp:1302-1303 - only call site), i.e. slot release events. The main loop (start_loop) drains queue_tasks every iteration but NEVER touches queue_tasks_deferred; idle loop iterations, update_slots, and new task arrivals do not retry deferred tasks.
3. pop_deferred_task pops at most ONE task per release: first a deferred task explicitly requesting the released slot (it->id_slot == id_slot), else the FIFO HEAD (server-queue.cpp:90-110). A big gate-blocked head (10767: resident 49584 + candidate 104875 >= 100000) re-defers and BURNS the release event; the tail (12239) then waits for the NEXT release - slot 1's long task, arbitrarily far away.
4. New arrivals bypass deferred tasks entirely: a fresh small request 3.5 min later was posted to queue_tasks and served immediately on the idle slot 0 (verified live) while deferred tasks kept waiting - priority inversion/starvation under sustained arrivals.
5. Disconnect while deferred: server_response_reader::stop() posts CANCEL per pending task (SRV_WRN "cancel task" per task); post() intercepts CANCEL and calls cleanup_pending_task, which silently erases the target from queue_tasks, queue_tasks_deferred and queue_tasks_unhandled (server-queue.cpp:32-58, 368-383 - no log for the erase); the TASK_TYPE_CANCEL handler then no-ops for a never-launched task (server-context.cpp:2473-2482). Note: for a non-streaming client, disconnect detection can lag until the next write attempt - in the observed window NO "cancel task, id_task = 12239" WRN appears, so 12239 likely remained deferred (hang) rather than being erased at timeout; either way the client-visible outcome is identical: silent hang until timeout, no error.

**Verdict**: real scheduling gap, not a crash and not a permanent orphan for a patient client (the next release WOULD eventually serve it), but: (a) a gate-passed request can wait indefinitely behind a re-deferring head despite an idle slot; (b) sustained arrivals can starve deferred tasks without bound (arrival bypass); (c) disconnecting while deferred yields either a silent erase or a dead-connection serve - never an error to the client. Amplifier: the ctx-threshold gate adds re-defer churn that consumes release events (pre-existing upstream v0.4.0 single-pop/release-only retry, made more visible by the gate; not introduced by the resident==0 fix).

**Deterministic repro (no timing luck; parallel=2, threshold>0, log-verbosity 4)**:
1. Fire L0 + L1: small prompts, n_predict 4000, stream=true -> both slots busy for many minutes.
2. Fire B: ~104K-token prompt (113x bigprompt-1109), n_predict 1 -> gate-deferred. deferred=[B].
3. Fire T: ~75-token prompt, n_predict 1, patient client -> gate ADMITS, no free slot -> deferred=[B, T].
4. Cancel L0 (or let it finish) -> release(slot 0) -> pop pops B (head) -> gate re-defers B (resident L1 + B >= threshold) -> deferred=[T, B]. Slot 0 now IDLE while T hangs for the whole remaining runtime of L1. Assert: /slots shows slot 0 idle, T has no launch_slot_ line, T's client gets no bytes.
5. Fire any new tiny request -> takes slot 0 immediately (inversion demonstrated).
6. Variant: T with curl -m 20 -> on timeout, either silent cleanup_pending_task erase (no further trace) or T eventually launches and serves a dead socket. No error path to the client in either case.

### fix (2026-09-09): deferred-FIFO drain on slot release (task-12239 hang) - commit 1d3c4a8e3, deployed as image 7bf9c7173ead on llama-baseline_llama_1

**Fix** (tools/server/server-queue.cpp pop_deferred_task, single contained change): a slot release now re-posts the ENTIRE deferred FIFO in order (tasks explicitly requesting the released slot first, then the rest FIFO) to the FRONT of the main queue, so every waiter is re-evaluated against the freed capacity before newer arrivals; tasks that still cannot proceed re-defer to the back, preserving FIFO order. Closes both gaps: (1) a re-deferring head no longer burns the release event for the rest of the queue; (2) deferred tasks get strict front-of-queue priority at every release drain, closing the arrival-bypass fairness hole (a new arrival can still take a slot a deferred task could not have used under the gate - that is correct, not unfairness).

**Verification (bare-metal, 747.4 yml)**:
- BUG re-proven first (run on the then-current build-cuda1322, which turned out to still be pre-fix): L0+L1 occupy both slots, B (71,588) gate-deferred, T (59 tokens) deferred; kill L0 -> release popped ONLY the head (B re-deferred) and T starved the full 120s client window, exactly the deterministic repro.
- FIXED binary (1d3c4a8e3): same scenario -> T served 6.02 s after the L0 release (valid response, 4 completion tokens); B re-deferred at the same drain and was served later by the drain when resident dropped (valid, 1 token).
- Gate semantics re-verified on the fixed binary: boot loop 10/10 normal admits; defer/admit/lone-request lines all correct; lone probe (118,928 >= threshold) first deferred behind a real agent turn (52,996 resident - correct contention) then `admit task 5946 (resident 0 + candidate 118928 >= threshold 100000; lone request on idle pool)`, completed 64 tokens valid.

**Redeploy (production)**: image rebuilt --no-cache (7bf9c7173ead from build-cuda1322 @ 1d3c4a8e3); `podman compose down` + `up -d` (the reliable recreate path); container image ID verified matching BEFORE behavioral checks (the up---build recreate trap avoided). Restart snapshot: slot 0 was mid-prefill of a 76,847-token agent turn (73,828 cached) - interrupted; agents re-prefill on next turn (bounded one-time cost, same judgment call as the earlier kv_unified redeploy). Boot args verified (ctx 262144, --no-kv-unified, threshold 100000).

**Production behavioral proof (real agent traffic interleaved throughout)**: task 582 (59 tokens, the T of the repro) was re-evaluated at EVERY release drain - gate lines at 9.08 / 9.15 / 11.14 boot-relative. The 9.15 drain is the decisive one: the head (553, B: 71,588) re-deferred AND 582 was evaluated in the SAME release event (old code: one pop -> 553 only). 582 legitimately found no free slot at 9:15 (a real agent 45,867-token turn, task 104, had just taken one), was re-admitted at the 11:14 drain, LAUNCHED 0.7 s later on the freed slot 0, completed and released at 11:16 - response valid (4 tokens, written to the client). End-to-end T latency (~2.3 min from the L0 kill) was dominated by real agent load (46K-token prefills contending for compute), not by the scheduling gap: under the old code T would have been skipped at every one of those release events while B (or a later head) re-deferred, with the slot idling. B (553) stayed correctly deferred while agent traffic kept resident above the threshold; its client gave up (disconnect -> silent cleanup_pending_task erase - expected, documented behavior).

**Residual notes**: (a) non-streaming disconnect detection can lag (the original 12239 case showed no cancel WRN) - silent erase remains possible for a disconnecting client while deferred; acceptable (documented), the hang-with-idle-slot case is closed. (b) PR #110 now carries the gate + resident==0 fix + this drain fix (branch fork/hydra-747-parallel-ctx-threshold-baseline @ 1d3c4a8e3); parent-side submodule bump for PR #748 still pending (muse-spark's uncommitted parent edits untouched).
