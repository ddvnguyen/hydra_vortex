# Decision 003 — KV store on tmpfs with content-addressed chunking; no shared filesystem

## Problem

The coordinator must persist and transfer ~800 MB of KV cache state per
60–80 K-token session across heterogeneous GPU nodes (RTX 5060 Ti + RTX 3060
on the host, Tesla P100 on a separate KVM VM), and restore it fast enough
that cross-GPU decode migration beats re-prefill. The storage substrate had
to be chosen before M2 (chunked dedup) shipped, because every routing mode
(P/D split, COMBINED, warm affinity, migration) reads and writes through it.

## Decision

- **Store lives on host tmpfs** (`/mnt/llm-ram/store/`), owned by Hydra.Core.
  GETs use `Socket.SendFileAsync` — zero-copy from the page cache.
- **Content-addressed chunking at the Store level** (M2): KV state is split
  into 8 MiB chunks (`HYDRA_STORE_CHUNK_SIZE` is in KB, default 8192 →
  8192 × 1024 = 8 MiB; see `Hydra.Core/Program.cs`), SHA-256 hashed, stored
  by hash. Repeated saves write only the delta; a restore whose chunks are
  all known is a no-op.
- **Full KV state only** — no speculative/partial KV formats.
- **No shared filesystem between nodes.** Cross-node transfer is always an
  explicit StateGet/StatePut over the hydra RPC wire. The two nodes that
  share the host tmpfs are in the same OS; the P100 VM never sees the store
  mount.
- **No Ray** (or any distributed-compute framework): two GPU nodes do not
  justify it; Hydra.Core's scheduler owns placement.

## Alternatives considered

- **Shared filesystem (NFS/NAS) for the store.** Rejected: it couples both
  nodes to a network FS for the hottest path, defeats the sendfile zero-copy
  design, and makes the P100 VM a store participant — violating the explicit
  "transfer is an RPC call" boundary.
- **Ray / distributed-compute framework.** Rejected: at 2 nodes it is
  orchestration overhead with no payoff; the one-GPU-one-task invariant and
  P/D placement are already owned by the Hydra.Core scheduler.
- **On-disk (NVMe) store from day one.** Deferred to M3 (write-behind
  persistence is the C# re-spec milestone). tmpfs keeps the MVP fast and the
  eviction model simple (LRU sweep + evict-on-ENOSPC, #615).
- **Partial/streaming KV formats instead of full KV.** Rejected for now:
  full KV makes restore semantics deterministic and byte-verifiable
  (differential-parity harness relies on exact state).

## Consequences

- Restores are bounded by RAM: tmpfs is finite, so the Store runs an LRU
  sweep and evicts on ENOSPC; a session whose KV is evicted re-prefills.
- Every cross-node transfer is an explicit, instrumented RPC (trace_id
  end-to-end) — observable, but it means no "just mount it" shortcut for new
  nodes; a new node is a new hydra-head deploy (see ADR 0001).
- Content addressing makes dedup exact but requires the hash pre-pass in the
  engine fork — a sm_60 build with the broken pre-pass (`234083a45`–
  `3206b13b6` window) fails PREFILL M2 entirely (hit live on the test lane,
  2026-08-28).
- M3 persistence must layer *under* the content-addressed chunk model
  (write-behind to SSD), not replace it.

Ref: #750
