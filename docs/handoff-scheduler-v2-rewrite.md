# Hydra v2 Scheduler Rewrite — Epic #591 Handoff

**Status:** CODE COMPLETE on `epic/591-rewrite-worker-scheduler` — **NOT yet merged to `main`**,
**NOT yet the default scheduler**. See the open PR (epic → `main`) and the "Decision needed"
section before flipping the default.

**Branch:** `epic/591-rewrite-worker-scheduler`
**Issues:** #591 (epic), #307, #279, #470, #336, #469 (references)
**Open PR:** epic → `main` (held for explicit approval before merge)

---

## TL;DR

`WorkerSchedulerService` (legacy, ~4800-line event-loop) was rewritten as `WorkerSchedulerV2`
in `src/core/Hydra.Core/Services/SchedulerV2/` — a SOLID, event-driven, slot-bounded state
machine with a **differential parity gate** that byte-compares v2's RPC trace against the
legacy scheduler's golden traces. The v2 scheduler is **feature-complete and 13 scenarios
byte-match the legacy goldens**. It ships behind an A/B toggle (`HYDRA_SCHEDULER_IMPL`,
**default `legacy`**) so the two run side-by-side with zero risk to production.

## Why this exists

The legacy scheduler grew organically: a single `Channel<WorkItem>` dispatcher with 4 routing
paths, a hand-rolled state machine, implicit slot leases, and duplicated engine-wire logic.
The v2 rewrite (epic #591) replaces it with:

- A **fluent-DSL state machine** (`Hydra.StateMachine`) — typed states, events, guards, hooks.
- **One `WorkerStateRunner` per state** (`PlanRunner`, `PrefixRestoreRunner`, `PrefillRunner`,
  `SaveKvRunner`, `RestoreRunner`, `DecodeRunner`, `BgSaveRunner`) — single responsibility.
- A **simple `SchedulerRequest` model** + **typed `ICompletionResult` submit** (no `Task<object>`).
- A **differential parity harness** (`src/core/Tests.Core/Harness/`) that pins legacy behavior
  so the rewrite cannot silently drift.

## What was built (WP0 → feature follow-ups)

| Component | Status | Location |
|-----------|--------|----------|
| `Hydra.StateMachine` DSL | ✅ | `src/core/Hydra.StateMachine/` |
| Differential/contract harness (WP0) | ✅ | `src/core/Tests.Core/Harness/` |
| `Hydra.Core.Scheduling` executor core (WP1) | ✅ | `src/core/Hydra.Core.Scheduling/` |
| `WorkerSchedulerV2` (WP2, SOLID) | ✅ | `src/core/Hydra.Core/Services/SchedulerV2/` |
| v2 two-phase P/D split | ✅ | `SchedulerV2/RoutePlanner.cs` |
| v2 session ledger (C1) | ✅ | `SchedulerV2/StateRunners.cs` |
| v2 warm-lease stash (C2) | ✅ | `SchedulerV2/LeaseManager.cs` + `WorkerSchedulerV2.cs` |
| v2 C4 decode + guards + resilience | ✅ (reviewed) | `SchedulerV2/` |
| Feature follow-ups (prefix, chunked, COMBINED, cross-node) | ✅ (reviewed) | `SchedulerV2/` |
| v2 hydra-model rule evaluation | ✅ | `Tests.Core/SchedulerV2Tests/V2HydraModelRuleTests.cs` |
| Differential gate (WP3) | ✅ | `Harness/DifferentialGateTests.cs` |

## Differential parity — the acceptance gate

`DifferentialGateTests` runs the scenario catalog against v2 via `V2ScenarioDriver` and
byte-compares the produced RPC trace (`Op`/`Key`/`Len`/`Status`), Proxy calls, final state,
ledger, and `BusySeconds` against the legacy golden captured in `Harness/Goldens/*.json`.

**13 scenarios are asserted byte-identical** (in `ExpectedParity`):

`cold_atomic_engine`, `cold_concurrency_pd`, `streaming_cold_atomic`, `busy_retry_then_success`,
`busy_exhausted`, `merged_decode_accept`, `merged_decode_gate_a_reject`, `prefix_hit`,
`prefix_miss`, `chunked_save`, `chunked_save_with_pushes`, `combined`, `cross_node_fallback`.

**Not in `ExpectedParity` (still `match=False`, tracked as follow-ups):**
`warm_affinity_on`, `warm_affinity_verify_on`, `migration`. These are "runs to Done" but not yet
byte-identical (see "Remaining work").

**Excluded by contract:** `cold_atomic_http` (legacy-mode `UseLlamaEngine=false` — v2 is
engine-mode/hydra-model by design).

## Feature follow-ups landed (each reviewed before merge)

1. **Warm-slot verification** — `WarmSlotVerificationEnabled` verifies the warm slot before a
   warm route; on dead node, evict + re-route cold (#469 guard).
2. **Prefix-checkpoint restore** — `prefix_hit`/`prefix_miss` byte-match.
3. **Chunked save** — `SyncMissing`/`PushChunks`/`PutManifest` with explicit chunk size
   (no global-static race); `chunked_save`/`chunked_save_with_pushes` byte-match.
4. **COMBINED multi-engine** — head slot + peer exclusive reservation (one GPU = one task),
   `hydra_config` in the 0x42 prefill, skip SaveKv → decode on head, BgSave direct-Puts the
   surviving in-memory KvBlob; `combined` byte-matches.
5. **Cross-node warm fallback** — a warm turn whose affinity node's only slot is warm-held
   falls back to an alternate worker and restores the KV from Store; `cross_node_fallback`
   byte-matches.

## Verification status

- `dotnet build src/core/Tests.Core/Tests.Core.csproj` → **0 errors**
- `dotnet test src/core/Tests.Core` (full) → **559 passed / 0 failed**
- Differential gate → **13/13 asserted scenarios byte-match**
- Each feature slice went through **independent lead re-verification** before merge (build +
  full suite + gate), not just the implementing agent's report.

## Reviews performed

- **Claude Sonnet 5 (High)** review fixes applied (eviction save-before-erase, resume error
  surfacing, caller-token threading, slot-identity same-node skip, on-demand warm eviction).
- **DeepSeek V4 Flash (max)** independent review of the COMBINED slice → findings #2–#5 fixed
  (retry re-plan peer reconciliation, force-mode threshold bypass, peer-reserve-failure
  degrades to solo, BgSave gated on `HydraConfigDelivered`). Finding #1 was **kept as
  deliberate parity** (see below).

## Known parity decision (review finding #1)

The `combined` golden pins `Ledger.SlotFreed=false` with the slot actually released
(`rtx busy=0`) after Done. This looks like a warm-resident state but is not — it is the
**legacy behavior, byte-pinned by the golden** (legacy's combined decode lease is
`Short`-lifetime → disposed, not warm-stashed). v2 reproduces it for byte-parity. The
default-on warm-slot verifier catches any follow-up turn that would otherwise decode over an
empty slot. **Deliberate divergence decision open** — see "Decision needed".

## Build / run / test

```
export PATH=$HOME/go-sdk/go/bin:$PATH          # go is not on default PATH
dotnet test src/core/Tests.Core/Tests.Core.csproj
dotnet test src/core/Tests.Core/Tests.Core.csproj --filter "FullyQualifiedName~DifferentialGateTests"
dotnet test src/core/Tests.StateMachine/ && dotnet test src/core/Tests.Core.Scheduling/
```

## Decision needed (before merge / default flip)

1. **Merge the epic → `main` PR?** This is the single integration PR. It is CI-gated but NOT
   E2E/live-GPU verified — the convention is to deploy + live-verify before the final merge.
2. **Flip the default to v2?** Currently `HYDRA_SCHEDULER_IMPL=legacy`. A fresh independent
   review (e.g. Claude) is recommended before flipping, plus a live-rig A/B run.
3. **`warm_affinity_on`/`warm_affinity_verify_on`/`migration` parity** — decide whether to
   chase byte-match (fresh-slot affinity key + `LastIdSlot` BgSave key) or accept current
   `match=False` and close the epic.

## Remaining work (after this PR)

- `warm_affinity_on` parity: legacy decodes on a FRESH slot (key "1") while BgSave StateGets
  key "0" (from the proxy `id_slot`); v2 reuses the warm slot (key "0").
- Merged-decode-on-warm + `warm_affinity_verify_on` parity.
- Fresh Claude re-review → v2 default flip decision.
- E2E live-GPU verification + deploy (sm_120/sm_60 build + fork bump) if this becomes default.
