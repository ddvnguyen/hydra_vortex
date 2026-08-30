# Trace: Issue #718 Warm-Slot Fast Path

## Problem Statement

6-turn same-session solo A/B shows warm turns pay +36→+152 s TTFT vs baseline flat ~25 s.
The cycle per warm turn: post-decode SaveKv pushes 198-600 MB to Store (async), then the
NEXT turn's PrefixRestore pulls the SAME bytes back to the SAME engine (Store Get + StatePut,
2-3 s) inside a 17-29 s route+restore cycle — while the engine often still held the KV
resident (DECODE_APPLY restore=0.0ms with n_past matching). The Store round-trip is pure
waste when the slot is warm. Cold turn 1 (no restore) costs only +2.8 s — that's the target.

## Code Path Trace

### 1. Request Classification — `ClassifyRequestType` (WorkerSchedulerService.cs:520-540)

```csharp
// L532-534: warm-residency gate
var entry = _ledger.Lookup(item.SessionId);
if (entry != null && entry.HasStoreState && !entry.SlotFreed)
    return RequestType.Solo;  // priority 10
```

**Key:** When session has `HasStoreState=true && SlotFreed=false`, the request is classified
as `Solo` (priority 10). This is the existing warm-residency check pattern.

### 2. Route Decision — `RouteAsync` (WorkerSchedulerService.cs:808-1037)

```
RouteAsync:
  if ForceMode → EvictWarmAndColdRouteAsync
  if entry.SlotId && !SlotFreed → WARM AFFINITY (L819)
    → n_past_guard check → evict if shrinking
    → WarmThreshold check → evict if large delta
    → AcquireSlot(decode) → VerifyWarmSlot
      → success: Decode/ModelLoadDecode (NO Store roundtrip)
      → failure: evict → PickDecode → RestoreKvAsync (FULL Store Get+StatePut)
    → n_past_guard fires: evict → PickDecode → RestoreKvAsync
    → Alt worker (cross_node): RestoreKvAsync
  if entry.HasStoreState (SlotFreed=true) → MIGRATION
    → PickDecode → RestoreKvAsync
  else → COLD ROUTE
    → ColdRouteAsync → PrefixRestore → Prefill → SaveKv → Decode
```

**The bug:** When warm affinity succeeds at L819 (`entry.SlotId.HasValue && !entry.SlotFreed`)
BUT the slot verification fails (L866-888) or n-past guard fires (L893-917), the item falls
to `PickDecodeAsync` → `RestoreKvAsync`, which does a full Store Get + StatePut even though
the engine STILL has the KV resident (restore=0.0ms).

### 3. Existing Warm-Residency Check (RouteAsync L819-852)

```csharp
// L819: warm affinity — session already has a slot on a node
if (entry != null && entry.SlotId.HasValue && !entry.SlotFreed)
{
    // L831-838: n_tokens guard (truncated history → evict)
    // L843-849: WarmThreshold cap (large incremental prefill → evict)
    // L852: AcquireSlot(decode) → if fails, try cross-node
    // L866-889: VerifyWarmSlot → if fails, evict → PickDecode
    // L893-918: N-past guard → if fires, evict → PickDecode
    // L920-923: Happy path → Decode
}
```

### 4. PrefixRestoreAsync (WorkerSchedulerService.cs:1780-1882)

```csharp
// L1780-1783: early exit when prefix disabled or no hash
if (!_cfg.PrefixCheckpointEnabled || item.PrefixHash == null || item.PrefillWorker == null)
    return WorkItemState.Prefill;

// L1789-1791: Store Get prefix/{hash}.kv
// L1819-1834: GetManifest for n_past
// L1837-1845: n_past guard (skip restore if prefix covers ≥85% of estimated tokens)
// L1847-1850: StatePut into prefill slot
```

**Key:** PrefixRestore is for system-prompt checkpoint restore (NOT session KV). Session KV
restore is in `RestoreKvAsync`.

### 5. RestoreKvAsync (WorkerSchedulerService.cs:3505-3884)

```csharp
// L3505-3512: get decode worker, entry, slotId
// L3519-3527: relay path (KV streaming from PREFILL RPC)
// L3540-3558: NoStoreKvRestore skip
// L3561-3563: Store Get {sid}.kv
// L3649-3689: StatePut RPC (push KV to engine)
// L3690+: cross-model guard, register session
```

**Key:** RestoreKvAsync is the **session KV restore** path. It pulls the full session KV
from Store and pushes it to the decode engine slot. This is what #718 wants to skip for
warm-residency fast path.

### 6. SessionEntry Fields (CoordinatorModels.cs:252-266)

```csharp
public sealed class SessionEntry
{
    public string SessionId { get; set; } = "";
    public string NodeName { get; set; } = "";      // bound worker
    public int? SlotId { get; set; }                 // engine slot
    public int NPast { get; set; }                   // KV context size
    public int NPromptTokens { get; set; }
    public bool HasStoreState { get; set; }          // KV saved to Store
    public bool SlotFreed { get; set; }              // slot evicted/released
    public string? PrefixHash { get; set; }
    public string? BoundModel { get; set; }          // model alias (warm affinity)
    public DateTime CreatedAt { get; set; }
    public DateTime LastUsed { get; set; }
}
```

### 7. SessionLedger Maintenance (RepositoriesImpl.cs)

- `Register(L16-26)`: sets NodeName, SlotId, NPast, PrefixHash, SlotFreed=false
- `MarkEvicted(L33-34)`: sets SlotFreed=true, HasStoreState=true
- `MarkStoreState(L36-37)`: sets HasStoreState=true (without touching SlotFreed)
- `UpdateNPast(L29)`: sets NPast
- `UpdateBoundModel(L31)`: sets BoundModel

### 8. Config Flag Pattern (CoordinatorModels.cs)

```csharp
// L130: WarmSlotVerificationEnabled
public bool WarmSlotVerificationEnabled { get; init; } = EnvBool("HYDRA_COORD_WARM_SLOT_VERIFY", true);
// L129: PrefixCheckpointEnabled
public bool PrefixCheckpointEnabled { get; init; } = EnvBool("HYDRA_COORD_PREFIX_CHECKPOINT_ENABLED", true);
// L120: NoStoreKvRestore
public bool NoStoreKvRestore { get; init; } = EnvBool("HYDRA_COORD_NO_STORE_KV_RESTORE", false);
```

## Fix Design

### Warm-Residency Fast Path (RouteAsync)

**Location:** In `RouteAsync`, at two fallthrough points — warm-slot verify failure
(L869) and n_past guard (L918). Both call the shared helper `TryWarmSlotFastPath`.

**Helper:** `TryWarmSlotFastPath(item, target, entry, reason)` — single implementation,
both call sites, distinct log event names via `reason` parameter.

**Gates (all must pass):**
1. `nodeInfo != null && nodeInfo.Healthy` — bound worker is alive
2. `string.Equals(nodeInfo.CurrentModel, entry.BoundModel, OrdinalIgnoreCase)` — exact model match
3. `nodeInfo.Slots.Any(s => s.Id == entry.SlotId)` — engine's /slots poll lists the session's slot (restart guard)
4. `target.CanPrefill` — worker is prefill-capable

**Action when all gates pass:**
- Set `item.RouteType = "warm_slot_fastpath"`
- Set `item.PrefixCacheHit = true`
- Set `item.PrefixNPast = entry.NPast`
- Set `item.PrefillWorker = target`, `item.PrefillSlot = entry.SlotId`
- Return `WorkItemState.Prefill`
- **DecodeLease is NOT released** — mirrors happy-path warm-affinity (L852-864) and
  cold_atomic's pattern where DecodeLease owns the slot through Prefill→Decode.

**When any gate fails:** fall through to existing eviction/restore path unchanged.

**Safety:** The fork's shared-prefix checkpoint mechanism (token-accurate N_COMMON match)
self-corrects stale residency — worst case is a full prefill (same as cold), never corruption,
because the engine only reuses matching token prefixes and the model-match guard prevents
cross-model takeovers.

### Config Flag

```csharp
public bool WarmSlotFastPathEnabled { get; init; } = EnvBool("HYDRA_COORD_WARM_SLOT_FAST_PATH", true);
```

## Evidence Source

- Issue #718: [P1][perf] Warm-slot turns pay full SaveKv/RestoreKv Store cycle
- A/B #5 acceptance: PR #710 comment, 6-turn same-session solo A/B
- Code: WorkerSchedulerService.cs (6859 lines), CoordinatorModels.cs, RepositoriesImpl.cs
