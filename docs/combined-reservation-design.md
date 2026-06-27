# COMBINED-reservation design (Hydra Core)

Concrete design for the Core-side mechanism that operationalizes
`docs/architecture-principles.md` **P1–P3**: a GPU does one task at a time, and
Hydra Core — not a low-level lock — guarantees it by **reserving** a peer GPU
before lending it to a COMBINED head.

This is the **primary** resolution for `ddvnguyen/llama.cpp#21` (the engine
compute-lock is a backstop, not the load-bearing fix).

## What a "reservation" is

A **reservation** is an exclusive, time-bounded lease on a GPU resource, held by
Hydra Core's worker scheduler. While a peer is reserved for COMBINED, **nothing
else issues compute to it** — not its own SOLO queue, not another COMBINED head.
It is a lock, but owned by the orchestrator (global view) with **admission
control**, rather than an in-engine mutex that only sees local collisions.

Why request-granularity: COMBINED expert dispatch happens transparently inside
the head's decode loop (the scheduler routes expert ops to the peer over RPC
once expert-mode is on). Core can't safely interleave the peer's own SOLO work
in the unpredictable sub-ms gaps between those dispatches, so a peer is reserved
for the **entire COMBINED request** (prefill + decode). Coarse, but it is
exactly P1.

## GPU / worker state model

Core tracks a state per GPU worker (single source of truth in the worker
scheduler):

```
IDLE                – no compute in flight; available
SOLO_BUSY           – running its own resident-model request
COMBINED_DRIVING    – acting as a COMBINED head (dispatching to a reserved peer)
COMBINED_SERVING    – reserved + lent out: a remote head is using this GPU
```

Legal transitions (all mediated by Core):

```
IDLE → SOLO_BUSY → IDLE
IDLE → COMBINED_DRIVING → IDLE        (head side)
IDLE → COMBINED_SERVING → IDLE        (peer side, reserved)
```

A worker is never in two states at once. `COMBINED_SERVING` and `SOLO_BUSY` are
mutually exclusive on the same GPU — that exclusion *is* the fix.

## Reservation protocol (happy path)

For a request that should run COMBINED (head = GPU-A, peer = GPU-B):

1. **Admission.** Core's scheduler checks `GPU-B`. Proceed only if
   `GPU-B == IDLE`. (If not, see *Admission & fallback* below.)
2. **Reserve (atomic).** In one critical section, Core transitions
   `GPU-B: IDLE → COMBINED_SERVING` and `GPU-A: IDLE → COMBINED_DRIVING`, and
   **stops routing GPU-B's SOLO queue** for the window. Record a lease
   `{head, peer, request_id, deadline}`.
3. **Dispatch.** Core sends the request to GPU-A with expert-mode COMBINED
   active (peer already bound). GPU-A drives; GPU-B serves expert ops. No other
   actor touches GPU-B.
4. **Complete.** On the head's response (or terminal failure), Core releases:
   `GPU-B → IDLE`, `GPU-A → IDLE`, drop the lease, resume GPU-B's SOLO queue.

The reservation lifecycle is bound to the head request's lifecycle.

## Admission & fallback

When `GPU-B` is not `IDLE` at admission time, Core does **not** force concurrent
demand. It chooses, by policy:

- **Queue** the COMBINED request until the peer frees (bounded wait), or
- **Fall back to SOLO** on the head (run the request without the peer), or
- **Pick a different free peer** if more than one exists.

This is ordinary admission control / queueing — the latency question for #21
becomes "how long until a free peer," not "stream contention."

## Atomicity & the single-authority rule

Reserve/release must be atomic against all other scheduling decisions.
Mechanism: **one scheduling authority** owns the GPU state map (Core's worker
scheduler), and all transitions go through a single critical section (one lock
around the state map, or a single-threaded scheduler actor). No engine ever
self-promotes to COMBINED without a Core-granted reservation.

### Deadlock avoidance (mutual borrow)

If GPU-A wants to borrow GPU-B *and* GPU-B wants to borrow GPU-A at the same
time, naive per-peer reservation could deadlock. Because Core is the single
authority, it prevents this by construction: reservations are granted under the
one scheduler lock, and Core refuses to put a worker into `COMBINED_DRIVING`
while it is already `COMBINED_SERVING` (and vice-versa). A worker that is lent
out cannot simultaneously be a head. (Total ordering of workers for any
multi-peer reservation is the general rule if N>2 nodes ever land.)

## Engine contract (what changes on the llama-engine side)

Minimal. The engine **trusts Core's reservation**:

- The engine does not need to detect or arbitrate concurrency; Core guarantees a
  reserved peer has no other compute in flight.
- The `#348` compute-lock stays as a **defense-in-depth backstop / assertion** —
  if it ever actually contends, that's a Core scheduling bug to surface (log +
  alert), not a race to absorb silently. Completing its coverage (state
  get/set + `synchronize`, with a `recursive_mutex`) is optional hardening, not
  required for correctness.
- `SET_EXPERT_MODE` / dispatch is driven by Core within the reserved window.

## Failure handling

- **Lease deadline / head crash:** every reservation has a `deadline`. If the
  head doesn't report completion (crash, hang, supervisor restart), Core
  **expires the lease**, returns `GPU-B → IDLE`, and resumes its SOLO queue —
  so a dead head can't strand a peer forever.
- **Peer crash while reserved:** the head's COMBINED request fails fast (RPC
  error); Core releases and may retry SOLO on the head.
- **Restart races (cf. the `combined_head_attached: false` false-negative in
  #21):** Core must confirm the peer's RPC backend is actually up *and* the
  head reports `combined_head_attached: true` before counting a request as
  COMBINED; otherwise treat as SOLO. Reservation state must reflect reality, not
  intent.

## Observability

- Per-worker state + current lease exported as metrics (Prometheus): time in
  each state, reservation count, reservation wait time, lease expiries.
- Alert if the engine compute-lock ever contends (backstop tripped ⇒ scheduler
  invariant violated).
- Dashboard panel: GPU state timeline + COMBINED reservation occupancy.

## Phasing

1. **P3.0** — GPU state map + atomic reserve/release + admission (queue or SOLO
   fallback). Single-authority scheduler. This alone closes #21.
2. **P3.1** — lease deadlines + failure/expiry handling + restart-race guard.
3. **P3.2** — metrics/alerts (incl. backstop-contention alert).
4. **Later** — multi-peer ordering (only if N>2 nodes), and the roadmap's
   single-GPU time-multiplexing (layer swap, P/D mix-quant) which reuses the
   same state machine + reservation primitive.

## Open questions

- **Reservation granularity:** whole-request (this design) vs. phase-level
  (reserve only for prefill, release for decode). Whole-request is the safe v1;
  phase-level is a later optimization if peer idle time during a COMBINED
  request proves significant.
- **Where the state map lives:** `WorkerSchedulerService` (C#, Hydra.Core) is
  the natural home — confirm it already serializes worker dispatch decisions, or
  add the single critical section.
- **Fairness:** if SOLO and COMBINED both contend for a peer, what's the
  admission policy (FIFO, priority, starvation guards)?
