# Milestone 2 — Advanced Store Features

## Goal
Content-addressable chunked dedup in Hydra Store. Second save of 800 MB session
stores only ~30 MB of new chunks. Cross-session prefix sharing (shared system prompt).
Prefix checkpoint: save system prompt KV once, restore as warm start for new sessions.

## What "Done" Means
```
✅ PUT_CHUNKED 800 MB → 800 chunks stored, all new
✅ PUT_CHUNKED 830 MB (same session, 30 MB new) → only 30 new chunks written
✅ GET_CHUNKED with client hashes → only missing chunks transferred
✅ Two sessions with shared system prompt → prefix chunks stored once
✅ Prefix checkpoint: save after system prompt → restore for new session → cache_n > 0
✅ GC removes orphan chunks not referenced by any manifest
✅ /debug shows chunk stats (total, deduped, disk usage)
```

## Prerequisites
- M1 complete, full system working with raw PUT/GET

---

## Task M2.1: Chunk Engine

### M2.1.1: Chunker (`store/chunks.py`)
- `CHUNK_SIZE = 1 * 1024 * 1024` (1 MB)
- `chunk_and_hash(data: bytes) → list[ChunkRef]` — split + SHA-256 each
- `ChunkRef(index: int, hash: str, size: int)`
- `Manifest(session_id, version, n_past, total_size, chunk_hashes, created_at)`
- **Lines:** ~50
- **Test:** `tests/store/test_chunks.py`
  - 10 MB data → 10 chunks, all unique hashes
  - Same data twice → identical hashes
  - Append 1 MB → first 10 hashes unchanged, 1 new hash
  - Last chunk smaller than CHUNK_SIZE handled correctly
- **Done when:** all tests pass, hash stability verified

### M2.1.2: Chunk Store (`store/chunks.py` extended)
- `class ChunkStore(chunks_dir, manifests_dir)`
- `store_chunk(hash, data) → bool` — write if not exists, return True if new
- `has_chunk(hash) → bool` — check in-memory hash set
- `get_chunk_path(hash) → Path | None`
- `save_manifest(manifest)` — write JSON to manifests dir
- `load_manifest(session_id) → Manifest | None`
- `diff_plan(manifest, client_hashes) → list[str]` — hashes client needs
- `gc(keep_sessions) → int` — delete unreferenced chunks, return count
- In-memory hash index rebuilt on startup by scanning chunks_dir
- **Lines:** ~100
- **Test:** `tests/store/test_chunk_store.py`
  - Store chunk → has_chunk returns True
  - Store duplicate → returns False (already exists), no disk write
  - Save manifest + load manifest round-trip
  - Diff plan: client has 8/10 chunks → returns 2 missing
  - GC: create chunks, remove manifest, GC deletes orphans
  - Startup rebuild: create files on disk, init ChunkStore, index populated
- **Done when:** all tests pass

### M2.1.3: Store Server Chunked Ops (`store/server.py` extended)
- Add handlers: handle_put_chunked, handle_get_chunked, handle_sync_plan, handle_push_chunks
- PUT_CHUNKED: receive full payload → chunk → dedup → store new → save manifest
- GET_CHUNKED: client sends known hashes in payload → server sends only missing chunks via sendfile
- SYNC_PLAN: client sends hashes → server returns list of missing hashes (no data transfer)
- PUSH_CHUNKS: client sends batch of chunk data (after SYNC_PLAN told it which are missing)
- **Lines:** ~120
- **Test:** `tests/store/test_store_chunked.py` (integration)
  - PUT_CHUNKED 10 MB → 10 chunks stored
  - PUT_CHUNKED 11 MB (same prefix + 1 MB new) → 1 new chunk
  - GET_CHUNKED with no client hashes → all chunks sent
  - GET_CHUNKED with 9/10 hashes → only 1 chunk sent
  - SYNC_PLAN → correct missing list
  - Verify response meta has correct new_chunks / deduped_chunks counts
- **Done when:** all tests pass, dedup ratio verified

---

## Task M2.2: Agent Chunked Transfer

### M2.2.1: Agent State Handler with Chunking (`agent/state_handler.py` updated)
- Update save_to_store: use PUT_CHUNKED instead of raw PUT
- Update restore_from_store: use GET_CHUNKED with local hash cache
- `class LocalChunkCache(cache_dir, max_chunks)` — LRU set of chunk hashes + files
  - On save: remember chunk hashes for this session
  - On restore: report known hashes → receive only missing chunks → reconstruct full file
- **Lines:** ~60 new
- **Test:** `tests/agent/test_state_handler_chunked.py`
  - Save session A → all chunks new
  - Save session A again (more tokens) → only delta chunks sent to store
  - Restore session A on same agent → local cache hit, minimal transfer
  - Restore session A on different agent → no cache, full transfer
- **Done when:** delta transfer confirmed, local cache working

---

## Task M2.3: Prefix Checkpoint

### M2.3.1: Coordinator Prefix Checkpoint (`coordinator/state_manager.py` extended)
- `save_prefix_checkpoint(node_name, slot_id, checkpoint_name)`
  - After system prompt is prefilled, save KV state as a named checkpoint
  - Store under key "prefix/{checkpoint_name}"
- `restore_prefix_checkpoint(checkpoint_name, target_node, slot_id)`
  - Restore system prompt KV as starting point for new session
  - New session starts with cache_n = system_prompt_tokens (no re-prefill)
- Coordinator config: `prefix_checkpoint_name: str = "system_prompt"`
- On first request to each node: detect system prompt, save checkpoint once
- On new sessions: restore prefix before sending to llama-server
- **Lines:** ~60
- **Test:** `tests/coordinator/test_prefix_checkpoint.py`
  - Save prefix → store has "prefix/system_prompt"
  - Restore prefix → slot shows n_past = system_prompt_tokens
  - New session after prefix restore → cache_n = prefix tokens
- **Done when:** new sessions skip system prompt prefill

---

## Task Summary

| Task    | Component | Lines | Test                            | Depends On  |
|---------|-----------|-------|---------------------------------|-------------|
| M2.1.1  | store     | 50    | test_chunks.py                  | —           |
| M2.1.2  | store     | 100   | test_chunk_store.py             | M2.1.1      |
| M2.1.3  | store     | 120   | test_store_chunked.py           | M2.1.2, M1  |
| M2.2.1  | agent     | 60    | test_state_handler_chunked.py   | M2.1.3      |
| M2.3.1  | coordinator| 60   | test_prefix_checkpoint.py       | M2.2.1      |

**Parallel work:** M2.1 (store chunks) is independent. M2.2 depends on M2.1.
M2.3 depends on M2.2 but can be designed in parallel.
