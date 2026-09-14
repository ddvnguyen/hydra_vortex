# Hydra Differential/Contract Harness (epic #591, WP0)

This directory is the **golden-event-trace harness** for the `WorkerSchedulerService`
rewrite (epic #591). It runs the **legacy** scheduler through its real evaluator
loop, captures a normalized execution trace for every scenario, and pins those
traces as golden JSON. When the v2 event-driven scheduler lands (WP3+), the
*differential gate* re-runs this exact catalog against v2 and requires
**byte-identical traces** before any strangler swap can merge.

```
Harness/
  ScenarioRpcClient.cs        single recording + fault-injecting RPC fake
  SchedulerScenarioRunner.cs  per-scenario fixture + normalized trace capture
  ScenarioCatalog.cs          21 scenario specs (route × shape × failure)
  GoldenTraceTests.cs         golden compare / regenerate gate
  LeaseInvariantTests.cs      lease/leak invariants after EVERY scenario
  RouteMatrixTests.cs         route-matrix table + WorkItemState coverage sweep
  Goldens/*.json              checked-in golden traces (byte-stable)
  README.md                   this file
```

## Running

```bash
# fast compile feedback
dotnet build src/core/Tests.Core

# just the harness
dotnet test src/core/Tests.Core --filter "FullyQualifiedName~Harness"

# the whole suite (must stay green)
dotnet test src/core/Tests.Core
```

All harness tests share the non-parallelized `HydraHarnessTests` collection:
scenarios mutate process-wide statics (`ModelRegistry`, `ChunkEngine.CHUNK_SIZE`)
and must never overlap each other.

## Regenerating goldens

The golden gate compares byte-for-byte and fails on ANY drift. To re-baseline
(after a deliberate, reviewed behavior change — never to hide a regression):

```bash
HYDRA_HARNESS_REGEN=1 dotnet test src/core/Tests.Core --filter "FullyQualifiedName~GoldenTraceTests"
```

Then re-run without the env var and diff the changed goldens. Commit goldens
alongside the code. If a trace drifts *without* you having touched the scheduler,
that IS the finding the harness exists to surface.

## Trace normalization

The trace strips everything time-varying and keeps only the contract:

- `Rpc[]` — ordered binary RPC calls `(op, key, payloadLen, status)` from the
  single `ScenarioRpcClient` wired into BOTH the Store slot and
  `AgentClientFactory` (same pattern as the existing integration fixtures, e.g.
  `EngineFixture`). One instance ⇒ naturally ordered, deterministic interleave
  of Store ops (`Put/Get/Stat/SyncMissing/…`) and engine ops
  (`EnginePrefill/StateGet/StatePut/EngineDecode/…`). `status` is the
  `StatusCode` name, or `Throw` when the call raised — this is what lets the
  gate distinguish a BUSY retry from a cross-model re-prefill.
- `MergedDecode[]` — framed DECODE 0x43 calls `(slotKey, model, stream)`.
- `Proxy[]` — HTTP chat-completion proxy calls `(url, stream, model, maxTokens,
  nPredict)`; `nPredict == 0` marks the #279 prefill fallback.
- `FinalState` — terminal `WorkItemState` family (Done/Failed/Cancelled).
- `Ledger` — `(nodeName, slotId, nPast, hasStoreState, slotFreed)` snapshot.
- `BusySeconds` — **normalized to 0/1** (idle/busy). Wall-clock seconds are
  nondeterministic between runs (a warm lease held 0.352s vs 0.351s); the
  semantic signal — is any claim outstanding after settle — is the contract.
  `LeaseInvariantTests` disambiguate sanctioned warm leases from leaks.

Trace ids, timestamps, warm-lease `CreatedAt`, and busy-since instants are
stripped by construction.

## What the gate proves

1. **Golden parity** (`GoldenTraceTests`) — every scenario's ordered RPC/proxy
   stream, terminal state, and ledger outcome are byte-identical to the legacy
   goldens. v2 must reproduce them exactly.
2. **Lease/leak invariants** (`LeaseInvariantTests`) — after EVERY scenario:
   - every busy slot is backed by a warm lease (no stray claims); when no warm
     lease survives, `GetElapsedSeconds == 0` for all workers;
   - `_peerLeases` is empty and no worker stays exclusively reserved;
   - `_warmLeases` is bounded (≤ total slots, ≤ 1 per session, in-range slots);
   - completion resolves exactly once (terminal outcome, settled future);
   - the ledger is consistent (Done sessions are either live or evicted *with*
     a Store copy; slots in range; n_past ≥ 0).
   - plus the cancel-mid-flight leak regressions (1-phase and 2-phase).
3. **Route matrix** (`RouteMatrixTests`) — route × request-shape ×
   failure-injection → terminal outcome class + state-path markers.
4. **Coverage sweep** — every `WorkItemState` enum value is exercised at least
   once (as a dispatch target or a returned next-state); the gate fails on any
   never-exercised state so v2 cannot silently break a contract the harness
   never proved.

## Known legacy behaviors the harness pins (and why)

- **Cold-atomic sessions end `SlotFreed=true`** (with `HasStoreState=true`):
  the save→decode handoff calls `MarkEvicted`, and the atomic path never
  re-registers (it reuses the lease). The next turn migrates from Store. This is
  designed behavior, not a leak.
- **Warm affinity needs a P/D session**: only sessions that went through
  `RestoreKvAsync` (which re-registers with `SlotFreed=false`) hit the
  `RouteType=affinity` fast path. `warm_affinity_on` therefore uses a P/D turn 1
  with `P100Slots=2` (a 1-slot decode node starves the follow-up turn because
  the warm lease holds the only slot — that starvation is the
  `cross_node_fallback` scenario).
- **`WorkerTracker.BusySince` is a coarse per-worker flag**: `ReleaseSlot`
  clears it even while other slots stay held. The pool-level equality
  (`busy slots == warm leases`) is the authoritative claim check; the busy
  flag is only a leak detector when no warm lease survives.
- **KNOWN LEGACY DEFECT — cross-model abort leaks the decode-fallback slot**:
  when `StatePut` returns `model_match=false` (or `CrossModelGuard` aborts), the
  restore path acquires a decode-fallback lease on the prefill node; the
  subsequent re-prefill→`PickDecode` re-acquires a fresh decode lease **without
  disposing the fallback one**, orphaning it (`state_put_mismatch` golden shows
  `rtx busy=1` with no warm lease). The invariant gate pins this EXACT leak
  shape via `ScenarioSpec.HasKnownLegacySlotLeak` instead of failing; the
  differential gate will show whether v2 reproduces it (parity) or fixes it
  (deliberate divergence to review). Tracked for the rewrite: the v2 scheduler
  should dispose any existing `DecodeLease` before re-acquiring in
  `PickDecodeAsync`.

## Adding a scenario

1. Add a `ScenarioSpec` to `ScenarioCatalog.BuildCatalog()` (options + run
   script + expected outcome).
2. Regenerate goldens (`HYDRA_HARNESS_REGEN=1`), review the new trace.
3. The golden test, the invariant test (it iterates the catalog), and the
   coverage sweep pick it up automatically.
4. If the scenario exercises a new route, add a `RouteMatrixTests` row with
   explicit outcome + state-path markers.
