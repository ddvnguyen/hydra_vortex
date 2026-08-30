# Issue #712 — Solo/Cold Route Never Reuses Prefix KV — Trace Findings

## Summary

The `force_mode="solo"` request path bypasses all prefix/ledger lookup machinery,
causing full re-prefill (0x42) every turn. The root cause is a chain of three gates:

1. **`ClassifyRequestType` maps "solo" to `RequestType.Atomic`** (not `RequestType.Solo`)
2. **`RouteAsync` skips warm affinity when ForceMode is set** (forces cold route)
3. **`ColdRouteAsync` cold_atomic path returns `WorkItemState.Prefill`** (bypasses `PrefixRestoreAsync`)

## Exact Code Path (legacy scheduler)

```
SubmitAsync (line 199)
  → item.ForceMode = "solo"
  → ClassifyRequestType (line 522-529):
      ForceMode="solo" → switch default → RequestType.Atomic (priority 30)
      ⚠ Does NOT reach the ledger.Lookup() warm check at line 532-534

RouteAsync (line 808-813):
  → ForceMode is set → skip warm affinity entirely
  → EvictWarmAndColdRouteAsync → ColdRouteAsync

ColdRouteAsync (line 1063-):
  → ForceMode is set → ForceMultiEnginePlan returns null for "solo" (line 1260)
  → Falls to atomic check (line 1121):
      estimatedTokens <= AtomicThreshold → atomic=true
  → cold_atomic path (line 1123-1193):
      Picks worker → sets DecodeWorker/DecodeSlot
      Returns WorkItemState.Prefill (line 1189) ← BUG: should be PrefixRestore

  → cold_concurrency path (line 1195-1237):
      Returns WorkItemState.PrefixRestore (line 1237) ← this path DOES prefix restore
```

## Why the Bug Exists

### Gate 1: `ClassifyRequestType` (line 520-540)

```csharp
if (!string.IsNullOrWhiteSpace(item.ForceMode))
{
    return item.ForceMode.ToLowerInvariant() switch
    {
        "combined" => RequestType.Combined,
        "pd" => RequestType.Prefill,
        _ => RequestType.Atomic,   // ← "solo" falls here
    };
}
// Only warm sessions reach this:
var entry = _ledger.Lookup(item.SessionId);
if (entry != null && entry.HasStoreState && !entry.SlotFreed)
    return RequestType.Solo;       // ← unreachable for force_mode="solo"
```

`force_mode="solo"` forces `RequestType.Atomic`, which has lower priority (30)
than `RequestType.Solo` (10). The ledger lookup at line 532-534 is unreachable
when ForceMode is set.

### Gate 2: `RouteAsync` warm bypass (line 810-813)

```csharp
if (!string.IsNullOrWhiteSpace(item.ForceMode))
    return await EvictWarmAndColdRouteAsync(item);
```

When ForceMode is set, warm affinity is skipped entirely — even if the session
has a resident KV slot. This is intentional for debug/testing purposes but
means `force_mode="solo"` never uses warm routes.

### Gate 3: `ColdRouteAsync` cold_atomic returns Prefill (line 1189)

```csharp
if (_cfg.UseLlamaEngine)
{
    // ... model swap check ...
    return WorkItemState.Prefill;  // ← FULL PREFILL, no prefix restore
}
```

The cold_concurrency path (line 1237) returns `WorkItemState.PrefixRestore`,
but the cold_atomic path returns `WorkItemState.Prefill` directly — bypassing
`PrefixRestoreAsync` entirely.

## Why Turn 6 Worked

At turn 6, the engine's shared-prefix detection engaged because:
- The engine received a PREFILL (0x42) with a warm slot
- The engine detected that a prefix of the new prompt matched the existing KV
- The engine only prefilled the delta

This is engine-side behavior that works when the KV is already resident in the
slot. The coordinator never explicitly "asked" for prefix restore — the engine
detected it autonomously.

## Existing Machinery That DOES Work

1. **`PrefixRestoreAsync`** (line 1780-1882): Restores `prefix/{hash}.kv` (system
   prompt prefix) before PREFILL. Only reached via `WorkItemState.PrefixRestore`.
2. **`RestoreKvAsync`** (line 3505-): Full session KV restore (`{sessionId}.kv`).
   Used by warm/migration paths, not cold_atomic.
3. **Ledger `HasStoreState`** (line 36-37): Set by `MarkStoreState()` after BgSave.
   Available for lookup but never checked during cold_atomic routing.
4. **Prefix checkpoint save** (line 4828-4868): Saves `prefix/{hash}.kv` after
   successful SaveKv. This works but is useless if the restore path is never reached.

## Design: Minimal Fix

### Change 1: `ColdRouteAsync` — route cold_atomic to PrefixRestore

When `force_mode="solo"` and `UseLlamaEngine` and the session has `HasStoreState`
in the ledger, return `WorkItemState.PrefixRestore` instead of `WorkItemState.Prefill`.

This engages the existing `PrefixRestoreAsync` machinery.

### Change 2: `PrefixRestoreAsync` — fallback to full session KV

When the prefix checkpoint (`prefix/{hash}.kv`) misses, check if the session has
a full KV checkpoint (`{sessionId}.kv`) in the Store. If yes, restore it via
StatePut and set `PrefixCacheHit = true`.

This engages the existing Store + StatePut machinery without inventing new paths.

### Change 3: Config flag `SoloPrefixReuseEnabled`

Gated on `HYDRA_COORD_SOLO_PREFIX_REUSE_ENABLED` (default: `true` when
`UseLlamaEngine` is true). Allows disabling the new behavior if regressions surface.

### n_tokens > n_past Guard

The restored KV has `n_past = entry.NPast`. The new request has
`estimatedTokens > n_past` (turn-over-turn growth). The delta prefill produces
`n_past_new = n_past + delta_tokens`, satisfying the invariant.

Additional guard: skip restore if `estimatedTokens <= entry.NPast` (truncated
history = prefix mismatch). This mirrors the existing guard in `RouteAsync` warm
path (line 832-838).

## Rejected Alternatives

1. **Modify `ClassifyRequestType` to map "solo" to `RequestType.Solo`**: Would
   break the warm-path behavior — `RequestType.Solo` assumes KV is already resident,
   not that it needs to be restored from Store.

2. **Add a new `WorkItemState.SoloPrefixRestore`**: Unnecessary complexity. The
   existing `PrefixRestoreAsync` can handle both system prompt and full session KV
   with a fallback.

3. **Modify `RouteAsync` to not skip warm affinity for ForceMode="solo"**: Would
   break the debug/testing purpose of ForceMode — the whole point is to force a
   specific route for A/B testing.

4. **Query the Store during route selection**: Overkill for this fix. The ledger's
   `HasStoreState` flag is sufficient to decide whether to attempt a restore.
