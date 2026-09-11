# cold_atomic self-lease decode deadlock — fix design

**Date:** 2026-08-29
**Live incident:** PR #709 comment 5452376844 (P100 T2 rig, 2026-08-28)
**Repro test:** `src/core/Tests.Core/Integration/ColdAtomicSelfLeaseTests.cs`
**Scope:** coordinator routing/dispatch only — no engine changes

---

## 1. Root-cause chain

### 1.1 Up-front decode lease in ColdRouteAsync

`WorkerSchedulerService.cs` ~L1131-1139:

```csharp
if (aw != null && _tracker.TryAcquireSlot(aw.Name, out var slot, "decode"))
{
    item.RouteType = "cold_atomic";
    ...
    item.DecodeWorker = aw;
    item.DecodeSlot = slot;
    item.DecodeLease = new SlotLease(aw.Name, slot, item.SessionId,
        LeaseLifetime.Long, _tracker);
```

ColdRouteAsync acquires the decode slot **up-front** and holds it via `item.DecodeLease` for the entire cold_atomic lifecycle (prefill → save → pick-decode → decode → finalize).

### 1.2 Prefill→Decode handoff re-enqueues as Decode

`WorkerSchedulerService.cs` ~L758-764 (RunItemPipeline):

```csharp
if (prev is WorkItemState.SaveDone or WorkItemState.MarkEvicted
    && next == WorkItemState.PickDecode)
{
    item.RequestType = RequestType.Decode;
    EnqueueRequest(item, RequestType.Decode);
    return;
}
```

After SaveDone→PickDecode, the pipeline sets `RequestType=Decode` and re-enqueues the item into the evaluator queue.

### 1.3 Evaluator admission gate uses worker-level IsFree

`WorkerSchedulerService.cs` ~L663-664 (CanServeRequest):

```csharp
RequestType.Decode or RequestType.Solo =>
    _cfg.Workers.Any(w => w.CanDecode && _tracker.IsFree(w.Name) && _health.IsHealthy(w.Name)),
```

`WorkerTracker.IsFree(workerName)` returns true only when the worker has **at least one free slot**.  With 2 concurrent cold_atomic requests on a 2-slot worker, both slots are occupied by the items' own `DecodeLease`s → `IsFree("rtx") == false` → neither item's re-enqueued PickDecode dispatches → both stall until client timeout (499).

### 1.4 PickDecodeAsync reuse-lease branch

`WorkerSchedulerService.cs` ~L3276-3302:

When PickDecodeAsync **does** run (single-request case), it reuses the existing DecodeLease when the route type is `cold_atomic` and the lease targets the prefill worker.  The branch does **not** try to re-acquire the slot (which would fail against its own lease).  This is correct — but it only runs when the evaluator actually dispatches the item.

### 1.5 The invariant

`One GPU = one compute task`.  A slot held by a DecodeLease is "busy" for **other** items, but the item that owns the lease should still be dispatchable for its own PickDecode phase.  The current code violates this: it gates decode dispatch on **any** free slot, which is too strict.

**Safe concurrency for same-worker cold_atomic = slots - 1** (one slot consumed by each in-flight DecodeLease; one slot must remain free for the evaluator to admit the next PickDecode).

---

## 2. Candidate fixes

### Fix A: self-lease exemption in CanServeRequest

**Change:** In `CanServeRequest`, for `RequestType.Decode`, additionally allow dispatch when the worker has a slot held by **this session's own DecodeLease** — treating it as dispatchable.

```csharp
RequestType.Decode or RequestType.Solo =>
    _cfg.Workers.Any(w => w.CanDecode && _health.IsHealthy(w.Name)
        && (_tracker.IsFree(w.Name)
            || qi.WorkItem.DecodeLease?.WorkerName == w.Name)),
```

**Pros:**
- Minimal blast radius (one condition change in the admission gate)
- Touches the hot path but adds only a null-check + string comparison
- Does not change the lease lifecycle or slot accounting
- Reversible: revert the single line

**Cons:**
- The exemption is implicit — the gate no longer mirrors the router's accept set exactly
- A future change to lease ownership could weaken the gate unintentionally
- Does not fix the conceptual issue: DecodeLease is acquired before prefill even starts

### Fix B: defer decode lease to PickDecode (stop up-front acquisition)

**Change:** In `ColdRouteAsync`, do **not** acquire `item.DecodeLease` up-front for `cold_atomic`.  Instead, acquire it in `PickDecodeAsync` when the reuse-lease branch fires (or via `TryAcquireSlot` if the slot was freed during prefill).

**Pros:**
- Cleaner state machine: decode lease is acquired when decode is actually needed
- Removes the self-lease deadlock by construction
- Aligns with the P/D split pattern (prefill lease acquired at cold route, decode lease at PickDecode)

**Cons:**
- Prefill currently relies on the up-front DecodeLease to hold the slot during model-swap + prefill (inline swap can take 60-120s).  Without the lease, another request could steal the slot while prefill is in flight → race condition
- Requires re-proving the model-swap race: the inline swap path (n_predict=0 → swap → re-prefill) must not lose its slot
- Larger diff (ColdRouteAsync + PickDecodeAsync + possibly PrefillAsync cleanup)
- Harder to review and roll back

---

## 3. Recommendation

**Fix A (self-lease exemption)** is recommended.

Rationale:
1. The blast radius is minimal — a single condition in `CanServeRequest`.
2. The fix is correct by construction: the DecodeLease **already** holds the slot, so the item is not competing for capacity — it's reusing its own reservation.
3. Fix B is cleaner conceptually but introduces a race window during inline model swap that would require additional synchronization (e.g. a separate prefill-slot lease), increasing complexity.
4. Fix A is immediately reversible.

---

## 4. Invariants to preserve

| # | Invariant | How preserved |
|---|-----------|---------------|
| 1 | One GPU = one compute task | DecodeLease still holds one slot per item; the exemption only affects the evaluator's admission decision, not slot accounting |
| 2 | slots - 1 safe concurrency | After fix, CanServeRequest allows N items with DecodeLease on the same worker only if N ≤ slots (each holds one slot).  The evaluator's semaphore (`_cfg.Workers.Count`) limits parallel pipelines |
| 3 | No slot leak on cancel | FinalizeAsync disposes DecodeLease regardless of how the item entered PickDecode |
| 4 | No slot stealing during prefill | DecodeLease is still acquired up-front in ColdRouteAsync (Fix A keeps this) |
| 5 | Existing tests unchanged | The repro test (failing) + safe-path test (passing) + full Tests.Core suite must all pass post-fix |

---

## 5. Rollback plan

1. Revert the `CanServeRequest` change (single line).
2. The failing repro test re-appears (red), confirming rollback.
3. No schema or API changes to roll back.
4. No deploy required (coordinator-only, restart picks up the revert).

---

## 6. Required green tests

| Test | Expected | Notes |
|------|----------|-------|
| `TwoColdAtomicRequests_BothReachDecode_ExpectFailsDueToSelfLeaseDeadlock` | **FAIL → PASS** after fix | Repro: both requests must reach Decode |
| `SingleColdAtomicRequest_ReachesDecode_WithSlotsTwo` | PASS (unchanged) | Safe-path: single request works today |
| `dotnet test src/core/Tests.Core` (full suite) | PASS | No regressions |
| `dotnet build src/core/Tests.Core` | 0 errors | Build clean |

---

## 7. Follow-up items

- [ ] Run the live-rig concurrency tier (`Tests.LiveRig`) after merge to verify no engine-side regression
- [ ] Consider adding a metric: `hydra_decode_self_lease_exemption_total` for observability
- [ ] Document the slots - 1 rule in `docs/architecture.md` under "same-worker concurrency"
