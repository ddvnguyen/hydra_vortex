# Hydra Architecture Principles & Planning Guidelines

Foundational, cross-cutting decisions that govern how Hydra is designed and how
we plan work. These are **load-bearing invariants** — code, scheduling, and the
roadmap are expected to uphold them. Treat them like the "Key Design Decisions"
in `CLAUDE.md`: do not relitigate casually; change them only deliberately, in
this document, with the reasoning recorded.

---

## P1 — One GPU does exactly one compute task at a time

A physical GPU executes **a single compute task at any instant**. We do **not**
run two independent inference workloads concurrently on one device. This is a
hard invariant, not an optimization target.

Rationale: a single GPU is one compute resource. Two actors issuing work to it
concurrently (e.g. two CUDA streams sharing memory, or two processes sharing one
context) is a correctness minefield — races, illegal memory access, and
non-deterministic state. We refuse that whole class of bug by construction.

## P2 — "Dual-role" is a capability, not simultaneous execution

The Hydra engine is **dual-capable**: the same binary can serve in two modes —

- **SOLO**: run its own resident model end-to-end (prefill + decode).
- **COMBINED-peer**: lend its GPU's compute (e.g. quant expert layers) to a
  *different* model running on another node.

"Two roles" means **the engine can do either**, selected per moment — **not**
that one GPU does both at the same time. At any instant a GPU is in exactly one
state:

```
IDLE  →  SOLO_BUSY  →  IDLE
IDLE  →  COMBINED_SERVING (lending compute to a peer)  →  IDLE
IDLE  →  COMBINED_DRIVING (head, dispatching to a peer) →  IDLE
```

The earlier framing of "both nodes doing their own SOLO traffic *and* mix-quant
COMBINED simultaneously" is **superseded** by P1/P2: COMBINED borrows a peer's
GPU **only while that peer is free**, exclusively, for the duration.

## P3 — Exclusivity is enforced by Hydra Core scheduling, not by low-level locks

The concurrency contract lives at the **orchestration layer**, where there is a
global view of every GPU's state — not in a per-device mutex that only sees
local collisions after they happen.

**Hydra Core owns GPU state and guarantees single-actor-per-GPU.** It only
dispatches COMBINED expert-compute to a peer it has **marked free**, and holds
that peer reserved until the work completes.

### Scheduling flow (COMBINED on a free GPU)

1. A request needs COMBINED (e.g. precise prefill on GPU-A + quant experts on
   GPU-B).
2. Core checks GPU-B's state. It proceeds **only if `GPU-B == IDLE`**.
3. Core atomically transitions `GPU-B: IDLE → COMBINED_SERVING` and **stops
   routing GPU-B's own SOLO queue** for the reservation window.
4. GPU-A (head) drives the COMBINED graph; GPU-B serves the expert ops. No other
   actor touches GPU-B.
5. On completion, Core transitions `GPU-B → IDLE`; its SOLO queue resumes.
6. If no peer is free, the request **waits** or **falls back to SOLO on the
   head** — never forces concurrent demand onto a busy GPU.

The engine-level compute lock (#348) is **demoted to a defense-in-depth
assertion / backstop**, not the primary mechanism. Primary correctness comes
from Core never creating concurrent demand on one GPU. If the lock ever actually
contends, that's a **scheduler bug** to surface, not a race to absorb silently.

### Consequence: this dissolves the #21 crash class

`ddvnguyen/llama.cpp#21` (CUDA illegal-memory-access from local decode racing
inbound RPC compute on one shared stream) **cannot occur** under P1–P3: Core
guarantees the peer is idle before borrowing it, so there is never a second
actor on that GPU's stream. The shared-backend design (one backend instance,
minimal VRAM) stays — we don't need separate streams / MPS, because we removed
the concurrency, not just guarded it. Completing the engine lock's coverage
remains worthwhile as a backstop, but it is no longer the load-bearing fix.

### Accepted trade-off

COMBINED is **opportunistic, exclusive borrowing**: a peer lends its GPU only
while idle, not time-sliced under its own load. COMBINED throughput therefore
depends on peer idleness. We accept this in exchange for eliminating the
single-GPU multi-actor bug class. Higher utilization, if ever needed, is a
deliberate future decision (e.g. hardware-isolated concurrency via separate
CUDA contexts + MPS) — not something to bolt on with a mutex.

---

## P4 — Plan bold: solve at the right altitude, optimize for long-term gain

Hydra is an ambitious project. When choosing a solution:

- **Prefer architecture-level solutions over local patches** when they yield
  long-term gains. The obvious local fix (a mutex, a special case, a bandaid) is
  often the wrong altitude — solve at the layer that has the right information
  (#21 belongs in the scheduler, not a lock).
- **We accept architectural change** — even changes that touch the project's
  shape — when the long-term net is positive. Don't anchor on the current design
  if a bolder one pays off.
- **Think out of the box.** Survey how the problem is solved elsewhere (upstream
  llama.cpp rpc-server, exo, prima.cpp), then decide deliberately whether to
  adopt, adapt, or diverge — with the reasoning recorded.
- **Every design decision is judged against the roadmap**, not just today's
  task: does it scale to layer-swap, single-GPU P/D mix-quant, and beyond?

This is a guideline for *how we decide*, paired with the discipline of recording
the decision (here or in `CLAUDE.md`) so it isn't silently relitigated.

---

## P5 — Roadmap directions this unlocks

P1–P3 (one task at a time, time-multiplexed roles, Core-scheduled exclusivity)
are the foundation for the ambitious single-device work ahead:

- **Layer swap** — time-multiplex a single GPU's resident weights (swap layer
  sets in/out) rather than holding everything resident. Fits P1: the GPU does
  one task at a time; swapping is sequencing, not concurrency.
- **Single-GPU P/D mix-quant** — run precise-prefill and quant-decode phases on
  one GPU by **time-multiplexing** between weight sets / quants, scheduled by
  Core, instead of two concurrent contexts. Same invariant: one task at a time,
  sequenced by the orchestrator.

These are explicitly **sequencing / scheduling** problems under P1 — which is
why getting the Core-level GPU state machine (P3) right now pays off repeatedly
later.

---

## Status

- **Recorded:** these principles are adopted as project guidelines.
- **Open implementation work:** the Core-side GPU state machine + COMBINED
  reservation flow (P3) is the concrete next step; it supersedes the
  engine-lock-coverage approach as the primary resolution for #21 (the lock work
  is retained only as a backstop). Tracked alongside the Llama-Engine — P/D split
  mix-quant milestone.
