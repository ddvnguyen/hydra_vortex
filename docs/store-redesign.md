# Hydra Store v2 — Redesign

> Unifies the Store-related backlog into one coherent architecture, implemented in
> phases. Supersedes the loose-JSON-manifest + broken-delta design.
> Issues absorbed: **#58** (delta-save), **#33** (protocol/test quality), **#107**
> (semantic KV: checkpoints, system-prompt base, cross-conversation reuse),
> **#86/#87/#88/#89** (SQLite metadata + NVMe write-behind + startup recovery),
> **#90** (real obs), **#91** (model distribution).

## Why
The Store today is tmpfs-only (volatile, lost on reboot), dedups on disk but **re-uploads
the full ~800 MB KV state on every save** (delta path broken — `PUSH_CHUNKS` uses
batch-local indices so partial pushes corrupt the manifest), and keeps metadata as loose
per-session JSON files (no refcounting, no GC, no persistence, no checkpoints). The
redesign makes the Store a real content-addressed, persistent, checkpoint-capable KV
store while fixing the P1 delta-save correctness/perf gap first.

## Layered architecture (target)

```
┌─────────────────────────────────────────────────────────────┐
│ RPC surface (StoreServer.cs)                                 │
│  raw: PUT/GET/DEL/STAT/LIST                                  │
│  chunked: SYNC_MISSING · PUSH_CHUNKS · PUT_MANIFEST ·        │
│           GET_CHUNKED · GET_MANIFEST · PUT_META              │
│  model:  PUT_MODEL / GET_MODEL (#91)                         │
├─────────────────────────────────────────────────────────────┤
│ Metadata (SQLite, Microsoft.Data.Sqlite)  #87               │
│   sessions(session_id, n_past, total_size, updated_at, …)   │
│   chunks(hash PK, size, refcount, backed_up, nvme_path)     │
│   session_chunks(session_id, idx, hash)   ← ordered manifest │
│   checkpoints(session_id, version, label, created_at)  #107 │
│   models(name, hash, size, backed_up)     #91               │
├─────────────────────────────────────────────────────────────┤
│ Chunk store (content-addressed blobs)                        │
│   tmpfs hot tier  /mnt/llm-ram/store/chunks/<hash>          │
│   NVMe cold tier  <nvme>/chunks/<hash>     #88              │
│   global index = SQLite chunks table (replaces _knownHashes) │
├─────────────────────────────────────────────────────────────┤
│ Background services                                          │
│   WriteBehindService  tmpfs→NVMe, marks backed_up   #88     │
│   StartupRecovery     NVMe→tmpfs top-N recent       #89     │
│   ChunkGC             refcount==0 → delete                   │
└─────────────────────────────────────────────────────────────┘
```

## Phase plan (each phase = its own branch + PR, `Closes #N`)

### Phase 1 — Correct delta-save protocol  *(#58 P1, #33 M2-P2-004)* ← START HERE
Foundational and highest-value. No SQLite yet — keep the existing chunk store + JSON
manifest, but fix the protocol so partial pushes are correct.
- **SYNC_MISSING (0x12, repurpose SYNC_PLAN):** request payload = JSON `["hash",…]` (the
  full ordered hash set the Agent intends to store). Response = `{missing_hashes:[…]}` —
  hashes **absent from the global chunk index** (`ChunkStore.HasChunk`). No existing
  manifest required (drops today's restore-oriented semantics).
- **PUSH_CHUNKS (0x13):** request payload = `[4B size][body]…` of **only the missing
  chunks**. Store hashes + persists each (dedup). Does **not** build the manifest.
  Response `{stored, total}`.
- **PUT_MANIFEST (new 0x15):** request payload = JSON `{n_past, total_size,
  chunks:[{index,hash,size}]}` (full ordered list, authoritative global indices). Store
  validates every referenced hash is resident, writes the manifest. Response `{written}`.
- **Agent `SaveToStoreChunkedAsync`:** tee state → LocalChunkCache (full ordered list +
  bodies on local disk), `SYNC_MISSING`, `PUSH_CHUNKS`(missing bodies from cache),
  `PUT_MANIFEST`(full ordered list). First save uploads all; re-save uploads ≈delta.
- **Tests:** round-trip byte-identity; re-save of identical state pushes 0 bodies;
  re-save with one changed tail chunk pushes exactly 1; fix #33 tautology assertion.

### Phase 2 — SQLite metadata  *(#87, part of #86)*
Replace `_knownHashes` set + JSON manifests with `Microsoft.Data.Sqlite`. Tables above.
Chunk **refcounting** + GC (`refcount==0 → delete`) — fixes today's unbounded chunk dir.
Manifests become `session_chunks` rows. Backward-compat import of existing JSON manifests.

### Phase 3 — Write-behind tmpfs→NVMe  *(#88, part of #86)*
`WriteBehindService : BackgroundService` periodically copies chunks where `backed_up=0`
to the NVMe tier, sets `backed_up=1, nvme_path`. Config: NVMe dir, interval, batch size.

### Phase 4 — Startup recovery  *(#89, part of #86)*
On boot, restore the top-N most-recently-updated sessions' chunks NVMe→tmpfs (target
<30 s) so hot sessions survive reboot. Sessions not yet recovered fault-in on demand.

### Phase 5 — Semantic KV features  *(#107)*
> Full epic design (KV DAG, git-aware reuse, content-defined chunking; quantization excluded):
> **`docs/kv-dag-architecture.md`**. Decomposed into issues #107-A … #107-I.
- **Checkpoints / revert:** `checkpoints` table = named manifest versions per session;
  `git`-like — each turn appends a manifest version, revert = restore an earlier one.
- **System-prompt base cache:** detect + tag prefix manifests (`prefix/<hash>`), shared
  across sessions (already half-done via coordinator prefix checkpoints — Store-side
  makes it first-class + cross-conversation default reuse via the shared chunk index).
- **Content-defined chunking (optional):** FastCDC instead of fixed 1 MB so mid-sequence
  edits don't shift all boundaries. Lower priority — KV grows by append, so fixed-size
  already dedups the unchanged prefix; revisit if cross-turn dedup ratio is poor.

### Phase 6 — Model distribution  *(#91)*
`PUT_MODEL`/`GET_MODEL` (raw, possibly chunked for the ~25 GB GGUF). Agent does
GET-if-missing on startup → no manual scp.

### Cross-cutting — Obs  *(#90)*
Each phase adds real metrics: dedup ratio + bytes-saved (Phase 1), chunk count/refcount/
GC (Phase 2), backed-up lag (Phase 3), recovery time (Phase 4), checkpoint count (Phase 5).

## Critical files
- `src/core/Hydra.Core/StoreServer.cs` — RPC handlers (rewrite SYNC/PUSH, add PUT_MANIFEST/MODEL)
- `src/core/Hydra.Core/ChunkStore.cs`, `ChunkEngine.cs` — chunk store + (Phase 2) SQLite index
- `src/core/Hydra.Core/StoreMetadata.cs` *(new, Phase 2)* — SQLite schema + DAL
- `src/core/Hydra.Core/WriteBehindService.cs`, `StartupRecovery.cs` *(new, Phase 3/4)*
- `src/core/Hydra.Core/StateHandler.cs`, `LocalChunkCache.cs` — delta save/restore client
- `src/core/Hydra.Shared/Protocol.cs` — opcodes (PUT_MANIFEST 0x15, PUT_MODEL/GET_MODEL);
  single source of truth (the Python coordinator's `rpc_client.py` was removed in PR #203)
- `src/core/Tests.Core` — round-trip + delta + corruption-guard tests

## Verification (per phase)
1. `dotnet test` Core + Shared green.
2. Phase 1: instrument a wire-bytes counter; assert re-save transfers ≪ full state and
   restore is byte-identical (hash the reassembled buffer == original).
3. Phase 3/4: kill+restart the Store container; confirm hot sessions restore from NVMe.
