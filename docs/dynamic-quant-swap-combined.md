# Dynamic quant swap × COMBINED zero-copy — reconciliation design

How to make **dynamic quant swap** (`#263`/E3, SWAP_QUANT `0x45`) coexist with
**COMBINED zero-copy expert tensors** (`llama.cpp#20`). Both are deliverables of
the **Llama-Engine — P/D split mix-quant** milestone, and as currently built
they conflict. Designing this **now**, before SWAP_QUANT is implemented
(`HydraEngineClient.EngineSwapQuantAsync` is stubbed → `NOT_IMPLEMENTED`), lets
us make the binding swap-safe by construction instead of retrofitting.

## The conflict

COMBINED zero-copy (merged in `llama.cpp#20`) works like this:

- The peer registers its resident model tensors **once at startup** by raw
  pointer: `llama_hydra_register_local_tensors_for_rpc` →
  `ggml_backend_rpc_register_local_tensor(name, tensor)` stores
  `{buffer, data, type, ne, nb}` in a process-wide `g_hydra_local_tensors`.
- The COMBINED head **binds** to those exact addresses
  (`ggml_backend_rpc_bind_remote_tensor`): `tensor->data` = the peer's absolute
  pointer, `tensor->buffer` = a synthetic buffer over the peer's `buffer` handle,
  `type`/`ne`/`nb` copied from the registry.

Dynamic quant swap **frees the peer's current model tensors and loads a
different quant**. After a swap:

1. `g_hydra_local_tensors` + the server's `foreign_buffers` point at **freed
   memory** → any later RESOLVE/compute is a use-after-free.
2. Already-bound heads hold **dangling** `data`/`buffer` pointers.
3. The new quant has **different `type` and `nb`** (block size/strides) for the
   same tensor name — so even fresh metadata isn't optional; stale metadata is
   silently wrong.

There is no invalidation or re-registration path today. **As built, a node
cannot be both a COMBINED peer and a quant-swap target.**

## Principles applied (see `architecture-principles.md` P1–P3)

- One GPU, one task at a time; Core orchestrates exclusivity.
- A swap is a Core-scheduled GPU state transition, like SOLO or COMBINED-serving.
- Therefore a peer **never swaps while serving COMBINED**, and is **never
  reserved for COMBINED mid-swap** — removing the UAF-*during-compute* case
  entirely. What remains is **staleness *between* COMBINED windows**, which the
  binding lifecycle must handle.

## Reconciliation — five mechanisms

### 1. Mutual exclusion via Core reservation (free from P3.0)
SWAP_QUANT and COMBINED reservation of the same peer are mutually exclusive in
the Core scheduler. Core only issues SWAP_QUANT to a peer that is `IDLE`
(not `COMBINED_SERVING`, not `SOLO_BUSY`), and marks it `SWAPPING` for the
duration so nothing reserves it. This reuses the GPU state machine from
`combined-reservation-design.md`; add `SWAPPING` as a fourth exclusive state.

### 2. Bind-on-activation (ephemeral bindings) — also fixes #357
Stop binding once at startup. Instead, the head **(re)resolves + (re)binds the
peer's expert tensors when it enters COMBINED for a reserved window** (driven by
Core's `SET_EXPERT_MODE("combined")`). The binding then always reflects the
peer's *current* model. This single change:

- makes swap-staleness impossible (binding is never older than the current
  COMBINED window),
- **fixes `#357`** (startup race: dual-load is no longer a one-shot at boot that
  strands the head in solo if the peer wasn't up),
- naturally carries the peer's current quant (`type`/`nb`).

The startup `llama_hydra_load_combined_experts` becomes a *bind-now-or-defer*;
the binding is (re)established on demand.

### 3. Registry epoch + re-registration on swap (defense-in-depth)
On the peer, SWAP_QUANT must, atomically w.r.t. the RPC server's compute lock:
clear `g_hydra_local_tensors` and `foreign_buffers`, free the old model, load the
new, **re-register** the new tensors, and **bump a `registry_epoch`** counter.
`RPC_CMD_RESOLVE_TENSOR`'s response carries `registry_epoch`; a binding records
the epoch it was made under. Any compute/resolve against a mismatched epoch is
rejected → head re-binds. Even if Core sequencing is perfect, the epoch makes a
stale binding **fail loud instead of reading freed memory**.

### 4. Head-side binding cleanup (fix the now-real leak)
Today the bound tensors' synthetic client buffers + `meta_ctx` are intentionally
leaked (acceptable for a one-shot startup bind). With bind-on-activation, that
leaks **per activation**. The rebind path must **free the previous binding's
synthetic buffers + `ggml_context`** before establishing the new one. Track the
current binding generation per peer and release the prior one.

### 5. Quant-shape safety + KV identity
- **Shape guard:** the bound expert tensor's **`ne` (shape) must match** the
  head's expectation (same `qwen35moe` arch); only **`type`/`nb` (quant) may
  differ**. Reject the bind (→ stay solo) if `ne` mismatches — that means the
  peer is running a different *model*, not just a different quant.
- **Re-reserve on quant change:** different quant → different compute-buffer
  sizing. The existing `hydra_add_combined_rpc_backend` re-reserve must run when
  a (re)bind changes the bound metadata. (The peer backend itself stays in the
  sched; only tensor pointers + a re-reserve change.)
- **Cross-model KV identity:** a session whose KV was produced with COMBINED
  active depends on *both* the head quant *and the peer quant*. Extend the
  existing cross-model KV-safety identity (the `RestoreKvAsync` model-swap
  detection, `StoreServer.cs:476`, M-Perf.9) so a peer quant swap invalidates
  reuse of KV produced under the old (head-quant, peer-quant) pair.

## Protocol (Core-driven)

**Swap a peer's quant:**
```
Core: ensure peer state ∈ {IDLE}            (P3 single-authority)
Core → peer: SWAP_QUANT(new_quant)
peer: lock compute; free old model; load new; clear+re-register registry;
      bump registry_epoch; unlock
peer → Core: {ok, new_quant, registry_epoch}
Core: update peer model state; mark any COMBINED head bound to this peer STALE
```

**Run a COMBINED request (head A, peer B):**
```
Core: reserve B exclusively (P3.0); B must not be SWAPPING
Core → A: SET_EXPERT_MODE("combined")
A: (re)bind B's expert tensors at B's current registry_epoch
   (free prior binding; ne-guard; re-reserve if metadata changed)
A: prefill + decode (expert ops run on B)
Core: release B
```

## Engine API changes (concrete)

- `ggml-rpc.cpp`: add `registry_epoch` to `rpc_msg_resolve_tensor_rsp`; a
  `ggml_backend_rpc_clear_local_tensors()` (clear map + `foreign_buffers`,
  bump epoch) called by the swap path before re-register.
- `llama-hydra.cpp`: `llama_hydra_register_local_tensors_for_rpc` becomes
  re-callable (clears prior epoch first). New
  `llama_hydra_rebind_combined_experts(ctx, peer, pattern)` = current
  load_combined_experts logic, but **frees the previous binding first**, carries
  the epoch, and applies the `ne`-guard.
- `llama-engine.cpp`: SWAP_QUANT (`0x45`) handler — gated to run only when the
  engine is not serving COMBINED (Core guarantees this, engine asserts as
  backstop); frees/reloads model; re-registers; replies with quant + epoch.
- `SET_EXPERT_MODE("combined")` handler: trigger `rebind_combined_experts`
  instead of assuming a startup binding exists.
- Hydra.Core `WorkerSchedulerService`: add `SWAPPING` to the GPU state machine;
  serialize SWAP_QUANT vs COMBINED reservation; mark head bindings stale on swap.

## Failure handling
- **Swap fails (peer left without a valid model):** peer reports error; Core
  keeps it out of rotation (neither SOLO nor COMBINED) until a successful reload;
  COMBINED heads degrade to solo.
- **Stale binding slips through:** epoch mismatch on resolve/compute → head
  re-binds (or degrades to solo for that request). Never reads freed memory.
- **Rebind fails (peer unreachable, `ne` mismatch):** fail-open to solo (the
  existing COMBINED philosophy), do not abort — note this also requires fixing
  the `bind_remote_tensor` `RPC_STATUS_ASSERT`→abort path (review finding #1).

## What can be built now (forward-compatible, before SWAP_QUANT lands)
Even with SWAP_QUANT stubbed, these make COMBINED swap-ready and pay off
immediately (they also fix #357):
1. `registry_epoch` field + re-callable register + `clear_local_tensors`.
2. `rebind_combined_experts` with prior-binding cleanup + `ne`-guard, invoked on
   `SET_EXPERT_MODE("combined")` (bind-on-activation). **Fixes #357 today.**
3. `SWAPPING` state + swap⊥reserve serialization in Core.
The actual SWAP_QUANT model free/reload (3 of the engine changes) lands with
#263 and slots into this contract.

## Tests
- Engine pure-protocol (`test-hydra-rpc-bind.cpp` extension): re-register bumps
  epoch; resolve after clear returns the new epoch; a bind at epoch N is rejected
  after a clear→re-register to N+1.
- Rebind frees prior synthetic buffers (no leak across N rebinds — assert buffer
  count steady).
- `ne`-guard: bind rejects a same-name tensor with mismatched shape.
- Core: SWAP_QUANT refused while peer `COMBINED_SERVING`; reservation refused
  while `SWAPPING`; head binding marked stale after a peer swap.
- KV identity: KV produced under COMBINED(head=mini, peer=balanced) is not reused
  after peer swaps to a different quant.

## Open questions
- **Rebind cost vs caching:** re-resolving every COMBINED activation is N small
  RPCs (one per matched expert tensor). If COMBINED activations are frequent,
  cache the binding keyed by `registry_epoch` and skip re-resolve when the epoch
  is unchanged (the common no-swap case) — re-bind only on epoch change.
- **Granularity:** does SWAP_QUANT swap the *whole* model or only the expert
  tensors COMBINED cares about? If only experts, the registry clear can be scoped
  to the matched pattern.
- **Who owns peer-quant in KV identity** — confirm the M-Perf.9 model-identity
  field can carry a (head, peer) quant pair for COMBINED sessions.
