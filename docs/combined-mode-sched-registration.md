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

## Fix: peer backend must be a first-class scheduler backend

llama.cpp already has the mechanism — it just runs too late for COMBINED. RPC
servers passed at startup are folded into `model.devices` by
`llama_prepare_model_devices()` (`src/llama.cpp:159-201`), and those devices
become real sched backends at context construction. The fix is to make the
COMBINED peer go through that same path:

**Register the COMBINED peer as an RPC device before context/sched
construction**, so it is present in `model.devices` and therefore in the
`ggml_backend_sched_new` backend list at `llama-context.cpp:439`. Then the
dual-loaded `_rpc` expert tensors live on a backend the scheduler owns, and
`split_graph` places the routed-expert ops on the peer instead of asserting.

Sequencing inside `llama_engine()`:

1. Resolve `--combined-ot-pattern` / `--rpc-engine` peer **before** model load.
2. If the peer is reachable, add it as an RPC device so it joins
   `model.devices` (reuse `ggml_backend_rpc_add_server` + the device path that
   `llama_prepare_model_devices` already consumes — do **not** invent a parallel
   registration).
3. Load the model / build the context: sched now includes the peer backend.
4. Dual-load the expert weights onto that *same* peer backend (the existing
   copy logic, but the buffer now belongs to a sched-known backend).
5. `SET_EXPERT_MODE("combined")` becomes a pure pointer swap between local and
   `_rpc` tensors — both already schedulable. No sched mutation at runtime.

This generalizes the existing device mechanism rather than bolting a special
case onto a frozen scheduler. It also removes the need for the post-hoc
`ggml_backend_register` in `load_combined_experts`.

### Why not rebuild the sched after dual-load?

`ggml_backend_sched` has no public "add a backend" API; it is fixed at
`ggml_backend_sched_new`. Recreating the sched mid-life would have to rebuild
graph state and re-reserve buffers while a model is resident — invasive and
easy to get wrong. Constructing the sched correctly the first time (peer
included) is the smaller, safer change.

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

- [ ] COMBINED: first PREFILL + DECODE complete with no `ggml-backend.cpp:898`
      assert; expert ops scheduled on the peer (RTX precise prefill).
- [ ] SOLO regression: RTX prefill → P100 decode unchanged; KV migrate + restore
      round-trip byte-identical.
- [ ] P/D mix-quant: precise prefill (RTX) + quant decode (P100) across the
      COMBINED topology, KV restore intact.
- [ ] `hydra_compute_lock` (fork PR #13) still serializes head-local decode vs
      inbound RPC compute on the shared device, and is a no-op in SOLO (no new
      deadlock).
- [ ] Clean model load (no warmup hang, see #8) and clean shutdown (no
      `std::terminate`, fork PR #18).

## Open questions

- Does adding the peer to `model.devices` change tensor *placement* of
  non-expert layers (i.e. does the head try to offload other tensors to the
  peer)? The `--combined-ot-pattern` / override-tensor pattern must keep all
  non-matched tensors local — confirm the device addition doesn't broaden
  offload beyond the dual-loaded experts.
- Interaction with the always-on dual-role design (#348): the peer may be
  running its own resident SOLO model; confirm the head adding it as a *compute*
  device doesn't disturb the peer's own scheduler.
