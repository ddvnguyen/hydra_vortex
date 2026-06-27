# COMBINED mode: register the peer backend with the scheduler

Design for the fix to **ddvnguyen/llama.cpp#12** — COMBINED expert-split mode
crashes on the first real PREFILL/DECODE. This is the P0 blocker for the
**Llama-Engine — P/D split mix-quant** milestone (mix-quant P/D depends on
COMBINED working end-to-end).

Related: hydra issue #287/#260 (COMBINED spike), #348 (shared-backend unify),
llama.cpp fork PR #13.

## Symptom

With COMBINED configured (`--rpc-engine --combined-ot-pattern …`, peer
reachable, dual-load reports `combined_head_attached: true`) and
`SET_EXPERT_MODE("combined")` accepted, the first PREFILL aborts:

```
ggml/src/ggml-backend.cpp:898: pre-allocated tensor (blk.0.ffn_down_exps.weight)
in a buffer (RPC0[:]) that cannot run the operation (NONE)
```

The hydra-head supervisor auto-restarts within ~1s, so it presents as a
restart loop on the first request, not a clean error.

## Root cause

`llama_hydra_load_combined_experts()` (`src/llama-hydra.cpp`) runs **after**
`load_model()` / context construction. It:

1. registers the peer as an RPC backend globally
   (`ggml_backend_register(peer_reg)`, line 106),
2. allocates the dual-resident expert tensors on the peer's buffer type
   (`ggml_backend_alloc_ctx_tensors_from_buft(meta_ctx, peer_buft)`, line 206),
3. stores them in `layer.ffn_*_exps_rpc`.

When `cparams.hydra_expert_mode == 1`, `qwen35moe.cpp:507-513` selects the
`_rpc` tensor pointers for the routed-expert matmuls, so the compute graph now
references tensors that live on the `RPC0` buffer.

But the context's `ggml_backend_sched_t` is built **once**, at context
construction (`src/llama-context.cpp:439`,
`ggml_backend_sched_new(backend_ptrs …)`), from the fixed backend list derived
from `model.devices`. The peer RPC backend is **never in that list**. At
`ggml_backend_sched_alloc_graph` / `split_graph`, the scheduler looks for a
backend in its list that owns the `RPC0[:]` buffer, finds none, and asserts.

The global `ggml_backend_register` makes the backend *exist*; it does **not**
add it to *this context's* scheduler. That is the gap.

## Fix (implemented): append the peer backend post-load and re-reserve

Two approaches were considered:

- **(A) Pre-load device registration** — fold the peer into `model.devices`
  before construction (via `llama_prepare_model_devices`, `src/llama.cpp:159-201`)
  so it joins the sched at `ggml_backend_sched_new` (`llama-context.cpp:439`).
- **(B) Post-load sched rebuild** — load the model normally (all base weights
  placed locally), then append a backend for the peer device and re-run the
  scheduler reserve.

**(B) was chosen.** Reading the code, `llama_context::sched_reserve()` is
already a re-runnable method (guarded by `sched_need_reserve`) that builds a
fresh `ggml_backend_sched` and re-reserves the worst-case graphs — so a
post-load rebuild is neither exotic nor "mid-graph surgery." More importantly,
(B) **sidesteps the tensor-placement problem entirely**: base weights are placed
by the normal SOLO load and never move, and only the manually dual-loaded
`ffn_*_exps_rpc` tensors live on the peer. The scheduler assigns ops by tensor
location, so the routed-expert matmuls go to the peer automatically and
everything else stays local — no override-tensor gymnastics, no risk of the
peer being used for general offload. (A) would have required constraining
placement so the peer didn't attract non-expert layers.

### Implementation (landed on `claude/pr-360-review-nh5a5a`)

`src/llama-context.{h,cpp}`:
- Extract the constructor's backend-vector build loop into
  `build_backend_buffer_vectors()` (single source of truth for
  `backend_ptrs` / `backend_buft` / `backend_buf_exp_size`).
- Add `bool hydra_add_combined_rpc_backend(ggml_backend_dev_t peer_dev)`:
  `ggml_backend_dev_init` the peer, insert it into `backends` **before** the CPU
  backend (preserving GPU/RPC-before-CPU order), rebuild the derived vectors,
  set `sched_need_reserve` and call `sched_reserve()`. Context takes ownership
  of the peer backend.

`src/llama-hydra.cpp` (`llama_hydra_load_combined_experts`):
- After the dual-load copy, call `ctx->hydra_add_combined_rpc_backend(peer_dev)`.
  On failure, clear the `_rpc` tensors and return solo-only (fail-open preserved).

The whole new path is reachable **only** when a COMBINED peer is configured and
reachable, so SOLO is provably unaffected (it never calls the new method).

### Known follow-up (worst-case reserve vs. mode switch) — became real, then got superseded

The original concern: `load_combined_experts` runs at startup while
`hydra_expert_mode` is still `0`, so the peer's compute buffer is reserved at
~0 and the first COMBINED decode would need a ggml-alloc grow. **On real
hardware (2026-06-27, RTX 5060 Ti + Tesla P100) this wasn't a benign realloc —
it was the actual crash.** The peer's RPC buffer for the dual-loaded experts
(sized near-zero at reserve time) failed its server-side bounds check on the
first real `SET_TENSOR`/`COPY_TENSOR`, and the connection was silently
dropped: `ggml-rpc.cpp:498: Remote RPC server crashed or returned malformed
response`. The scheduler-registration fix above is real and necessary (the
`ggml-backend.cpp:898` assert is gone), but COMBINED still didn't work
end-to-end until the copy itself was eliminated — see the next section.

### Superseding fix: zero-copy expert tensors (llama.cpp#20, 2026-06-27)

Rather than re-reserving on `SET_EXPERT_MODE` (the originally proposed
optimization for the follow-up above), the dual-load **copy** step was
replaced entirely: P100 already has the full model resident in VRAM for its
own SOLO duty — at a *different* quant ("Balanced") than RTX's ("Mini"), which
is the literal "mix-quant" theme of this milestone. Instead of RTX copying its
own tensor bytes to P100, P100 now exposes tensors it has already loaded by
name, and RTX binds directly to that memory — zero bytes of weight data cross
the wire, ever.

New primitive in `ggml/src/ggml-rpc/ggml-rpc.cpp` (`RPC_CMD_RESOLVE_TENSOR` +
`ggml_backend_rpc_register_local_tensor`/`ggml_backend_rpc_bind_remote_tensor`):
a worker registers its own resident tensors by name once at model-load time
(`llama_hydra_register_local_tensors_for_rpc`, called from `llama-engine.cpp`
right after `start_shared_backend_rpc_server`); any peer can then resolve +
bind to that memory directly, skipping `RPC_CMD_ALLOC_BUFFER` and
`SET_TENSOR`/`COPY_TENSOR` entirely. `rpc_server` tracks these under a second
set (`foreign_buffers`, distinct from `buffers`) so `deserialize_tensor`/
`graph_compute` accept them but `free_buffer`/`buffer_clear` — which must stay
restricted to buffers the server actually allocated — never touch live model
weights. `llama_hydra_load_combined_experts` now binds per matched tensor name
instead of allocating + copying; the VRAM-headroom precheck is gone (nothing
new is allocated on the peer). `hydra_add_combined_rpc_backend`'s post-load
scheduler re-reserve (this doc's original fix) is unchanged and still
required — the scheduler still needs to know a backend owns these tensors,
regardless of whether the tensor data arrived by copy or by reference.

This is the "future zero-copy COMBINED variant" the RPC spec's PIPELINE
section and `docs/spike-engine-mode-switch.md` had flagged and deliberately
scoped out — distinct from `PIPELINE_ATTACH` (`0x46`, tracked separately under
hydra_vortex#287/#161, the full prima.cpp-style layer-split epic with live
activation-passing). This fix is narrower: COMBINED's existing scheduler
dispatch already moves op activations over RPC once a tensor's `buffer`
correctly points at the RPC backend, so no new activation-passing machinery
was needed — only how the *weight* tensor gets bound.

Tracked as `ddvnguyen/llama.cpp#20`. Pure-protocol test (no GPU):
`tests/test-hydra-rpc-bind.cpp`.

### Preserved behavior

- **SOLO unaffected.** When no peer is configured/reachable, `model.devices`
  is unchanged and the sched is identical to today's SOLO sched.
- **Fail-open stays.** Keep the "peer unreachable → log + stay solo, never
  abort" philosophy: if peer registration fails before load, fall back to a
  SOLO context (no peer device), exactly as `load_combined_experts` does today.
- **VRAM headroom check** moves to (or stays adjacent to) the pre-load peer
  decision, before committing the dual-load copy.

## KV-cache restore safety (must not regress)

Mix-quant P/D relies on the STATE_GET / restore path. This change touches the
backend list / scheduler, not the state serialization, but the topology change
makes restore part of the acceptance gate:

- STATE_GET / STATE_PUT round-trip must still produce byte-identical KV on a
  COMBINED-configured head.
- Hybrid/recurrent **context checkpoints must still be created** — the
  checkpoint-creation gate (`server_should_create_checkpoint`, pinned by
  `tests/test-hydra-checkpoint-policy.cpp`) is independent of this change and
  stays on. See ddvnguyen/llama.cpp#8 for why the recurrent/hybrid term must
  not be dropped.

## Verification

Run on real hardware (RTX 5060 Ti + Tesla P100) 2026-06-27, after the
zero-copy fix landed:

- [x] COMBINED: first PREFILL + DECODE complete with no `ggml-backend.cpp:898`
      assert; expert ops scheduled on the peer (RTX precise prefill).
- [x] SOLO regression: RTX prefill → P100 decode unchanged.
- [x] KV `STATE_GET`/`STATE_PUT` round-trip on a COMBINED-configured head:
      byte-identical, `n_past` matches.
- [ ] P/D mix-quant: precise prefill (RTX) + quant decode (P100) across the
      COMBINED topology — bind confirmed (P100's Balanced-quant weights are
      what RTX now references for the matched experts), full perf/quality
      characterization not yet done.
- [ ] `hydra_compute_lock` (fork PR #13) still serializes head-local decode vs
      inbound RPC compute on the shared device, and is a no-op in SOLO (no new
      deadlock). **Found broken under sustained concurrent load** — see below.
- [ ] Clean model load (no warmup hang, see #8) and clean shutdown (no
      `std::terminate`, fork PR #18).

### New finding: compute-lock stall under sustained concurrent load

Running P100's own SOLO PREFILL/DECODE cycles concurrently with RTX driving
COMBINED traffic into P100's shared backend — the exact follow-up
`docs/spike-engine-mode-switch.md` called out once COMBINED itself worked —
RTX goes quiet for ~52s then dies; P100's in-flight `send()` breaks
(`bytes_sent=28931040, size_to_send=191269920`) right as RTX's health check
times out and the supervisor kills it. This is a `#348` shared-backend
compute-lock issue (`llama_hydra_lock_compute`/`unlock_compute`,
`ggml_backend_rpc_server_compute_lock`), not introduced by the
scheduler-registration fix or #20's zero-copy bind — this scenario literally
couldn't be exercised before today, since COMBINED compute itself was broken
until both fixes landed. Tracked as a follow-up; not fixed by this doc's
changes.

## Open questions

- Does adding the peer to `model.devices` change tensor *placement* of
  non-expert layers (i.e. does the head try to offload other tensors to the
  peer)? The `--combined-ot-pattern` / override-tensor pattern must keep all
  non-matched tensors local — confirm the device addition doesn't broaden
  offload beyond the dual-loaded experts.
- Interaction with the always-on dual-role design (#348): the peer may be
  running its own resident SOLO model; confirm the head adding it as a *compute*
  device doesn't disturb the peer's own scheduler.
