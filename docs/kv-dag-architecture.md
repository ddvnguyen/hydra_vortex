# Hydra KV DAG + Git-Aware Prefix Cache — Architecture

> **Status:** Proposed design (epic for issue #107 "Hydra.Store Improve").
> **Scope:** Full epic **excluding FP8 quantization**. Realises `docs/store-redesign.md`
> **Phase 5 — Semantic KV features** and extends it into a content-addressed **KV DAG** with
> git-aware reuse. Phases 1–4 of the Store redesign (delta-save, PostgreSQL metadata, NVMe
> write-behind, startup recovery) are already shipped and are the foundation this builds on.

## Objective
Eliminate repeated long-context prefill for coding-agent workloads (80K ctx ≈ 12 min on the P100)
by reusing **ancestor** KV states across conversations and git commits:

- a **KV DAG** of immutable transformer states linked by parent pointers,
- **system-prompt base cache** shared across conversations by default,
- **git-aware** commit-level reuse with nearest-ancestor fallback,
- **content-defined chunking** so mid-sequence edits don't shift every chunk boundary,
- (follow-on) **checkpoints/revert**, **background prefill builder**, **git service**, and
  **hot/warm/cold tiering**.

Targets standard causal transformers served by the llama.cpp fork: Qwen3.6-35B (primary),
DeepSeek, Llama, Mistral.

---

## Core principles
1. **Reuse ancestor KV states.** A request resolves to the *deepest* cached node on its prefix path.
2. **Never merge independent KV caches.** KV is only ever *extended* (`system → +skills → +repo →
   +task`), never combined sideways. The issue body's phrase "git-merge KV across turns" is realised
   as **lineage chaining** (parent → child), not tensor merge.
3. **Content addressing everywhere.** Identical token prefixes ⇒ identical `prefix_hash` ⇒ automatic
   reuse; identical chunk bytes ⇒ identical `hash` ⇒ automatic dedup.

```text
INVALID (merge):                       VALID (chain / DAG):
  KV(System) + KV(Skills) + KV(Repo)     root → system → +skills → +repo@abc123 → task1
                                                                             └────────→ task2
```

---

## Current architecture (baseline)
> Post PR #203: the Python coordinator and Agent services are **deprecated/removed** —
> **Hydra.Core is the single C# service** owning HTTP API, routing, sessions, and the Store.
```text
Client → Hydra.Core :9000 ──HTTP/RPC──► llama-server RTX :8080 / P100 :8086
              │                          (RPC :9503 / :9502 for KV state ops)
              └── Store RPC :9500 (embedded; tmpfs /mnt/llm-ram/store + PostgreSQL metadata)
```
- **Chunks:** content-addressed, **fixed 1 MB** (`src/core/Hydra.Core/ChunkEngine.cs`), SHA-256 named.
- **Metadata (PostgreSQL, `StoreMetadata.cs`):** `sessions`, `chunks`, `session_chunks` (ordered
  manifest). `ChunkRef(Index, Hash, Size)` already supports **variable-size chunks**.
- **Delta-save (Phase 1):** `SyncMissing 0x12` → `PushChunks 0x13` → `PutManifest 0x15`;
  `GetManifest 0x33`. Opcodes in `src/core/Hydra.Shared/Protocol.cs` (next free = `0x16`).
- **Prefix checkpoint (half-done):** Hydra.Core saves/restores `prefix/<hash>:<slot>` but tracks
  "already saved" in an **in-memory `_saved_prefixes` set** — lost on restart, not cross-conversation
  by default. `compute_prefix_hash` lives in `src/core/Hydra.Core/`.
- **Gaps:** no parent pointers / DAG, no git/commit awareness, GC only counts `session_chunks`.

---

## Target data model — the KV DAG node
Generalise "session = manifest" into an immutable **node** with a parent pointer. Sessions,
system/skills prefixes, repo states, and checkpoints are all nodes; lookup reuses the deepest
matching ancestor. Added **alongside** the existing tables (backward compatible).

```sql
kv_nodes(
  node_id          TEXT PRIMARY KEY,        -- ULID
  parent_id        TEXT REFERENCES kv_nodes(node_id),
  kind             TEXT NOT NULL,           -- system|prefix|repo|session|checkpoint
  model_hash       TEXT NOT NULL,           -- model+tokenizer+ctx_len+rope (reuse barrier)
  prefix_hash      TEXT NOT NULL,           -- deterministic SHA256 of the token prefix
  commit_sha       TEXT,                    -- git-aware (nullable)
  repo_id          TEXT,
  repo_fingerprint TEXT,
  n_past           INT  NOT NULL,           -- tokens represented by this state
  total_size       BIGINT NOT NULL,
  manifest_hash    TEXT NOT NULL,           -- hash of ordered chunk list → dedup identical states
  label            TEXT,
  created_at       TIMESTAMPTZ NOT NULL DEFAULT now(),
  last_used_at     TIMESTAMPTZ NOT NULL DEFAULT now());

kv_node_chunks(
  node_id TEXT NOT NULL REFERENCES kv_nodes(node_id) ON DELETE CASCADE,
  idx     INT  NOT NULL,
  hash    TEXT NOT NULL REFERENCES chunks(hash),
  PRIMARY KEY (node_id, idx));             -- frozen, ordered manifest per node

repo_commits(                              -- commit lineage for nearest-ancestor lookup
  repo_id           TEXT NOT NULL,
  commit_sha        TEXT NOT NULL,
  parent_commit_sha TEXT,
  PRIMARY KEY (repo_id, commit_sha));

CREATE INDEX ix_nodes_lookup ON kv_nodes(model_hash, prefix_hash, commit_sha);
```

### Cache key
A node is reusable only within the same `model_hash` = `SHA256(model_hash ‖ tokenizer_hash ‖
context_length ‖ rope_config)`. Qwen3.6 KV is never reused for DeepSeek, etc.

### Prefix hashing
Each segment boundary on the path produces a deterministic hash chain over **tokens** (not raw
text), so cosmetic differences don't break reuse and compression output (M-Perf) stays cache-stable:
```text
h0 = SHA256(system_tokens)
h1 = SHA256(h0 ‖ skills_tokens)
h2 = SHA256(h1 ‖ repo_tokens@commit)
h3 = SHA256(h2 ‖ task_tokens)
```

---

## Request resolution algorithm (Hydra.Core coordinator logic)
```text
1. Compute model_hash and the prefix hash chain [h0..hN].
2. FindNode(model_hash, hN, commit_sha)            # deepest exact match
3. if miss and commit_sha present:
       node = FindNearestAncestorNode(repo_id, commit_sha)   # walk repo_commits upward
4. if still miss: fall back to the deepest prefix-only node (hN-1, hN-2, …, h0)
5. Restore the matched node; prefill ONLY the residual tokens (from node.n_past onward).
6. Save the resulting state as a new node (parent_id = matched node).
```
This guarantees correctness (only suffix tokens are ever appended to a valid ancestor) and turns a
full 80K prefill into a residual prefill whenever any ancestor is cached.

---

## Capability map

| Capability | Mechanism | Round |
|---|---|---|
| **System-prompt base cache** | `kind=system\|prefix` nodes, Store-backed registry (replaces in-memory `_saved_prefixes`), shared cross-conversation by default | First |
| **Content-defined chunking** | FastCDC (gear-hash rolling boundary) behind `HYDRA_STORE_CDC`; manifest already stores per-chunk `Size` | First |
| **Git-aware repo cache** | nodes tagged `commit_sha`/`repo_fingerprint`; `repo_commits` lineage → nearest-ancestor reuse; MVP takes commit info from request headers | First |
| **Checkpoints / revert** | `kind=checkpoint` nodes chained by `parent_id`; revert = Store-side manifest copy + standard restore (no data transfer) | Follow-on |
| **DAG incremental prefill** | continue prefill from the matched ancestor's `n_past` (residual only) | Follow-on |
| **`Hydra.PrefillBuilder`** | on git commit, pre-build `repo@HEAD` node so it is warm before first request | Follow-on |
| **`Hydra.GitService`** | monitor repos, compute fingerprints, register `repo_commits`, trigger PrefillBuilder | Follow-on |
| **Hot/warm/cold tiers** | warm SSD tier between tmpfs (hot) and NVMe cold (current write-behind), driven by `last_used_at` | Follow-on |
| **FP8 quantization** | **excluded from this epic** | — |

---

## RPC / protocol changes
New opcodes in `src/core/Hydra.Shared/Protocol.cs` (single source of truth — the Python
coordinator and its `rpc_client.py` were removed in PR #203):

| Opcode | Name | Request | Response |
|---|---|---|---|
| `0x16` | `SaveNode` | node metadata (JSON) + `PutManifest`-style ordered chunk list | `{node_id, written}` |
| `0x17` | `RestoreNode` | `node_id` | `{n_past, manifest}` or `NotFound` |
| `0x18` | `FindNode` | `{model_hash, prefix_hash, commit_sha?, repo_id?}` | deepest match `{node_id, n_past, kind}` or `NotFound` |
| `0x19` | `ListNodes` | `{kind?, repo_id?}` | `[{node_id, …}]` |

Follow-on (checkpoints): `SaveCheckpoint` / `RestoreCheckpoint` / `ListCheckpoints` reuse the node
machinery with `kind=checkpoint` and `parent_id`.

Save/restore reuse the existing chunked plumbing (`Hydra.Core.StateHandler`, `LocalChunkCache`, delta
push). Only the manifest write target changes (`kv_node_chunks` instead of `session_chunks`).

---

## Garbage collection (correctness prerequisite)
Adding `kv_node_chunks` means a chunk can be referenced by a node but not by any session. The GC and
boot-reconcile reference set **must** be widened, or live node chunks are wrongly deleted:
```sql
-- GcOrphanChunksAsync / ReconcileBootAsync reference set:
SELECT hash FROM session_chunks UNION SELECT hash FROM kv_node_chunks
```
This change must land in the same PR that introduces the node tables (Task A).

---

## Epic decomposition (issues)
Milestone **"Phase 5 — Semantic KV (#107)"**; #107 is the epic.

| Issue | Title | Round | Depends on |
|---|---|---|---|
| #107-A | KV node metadata + DAL + GC fix (Store) | First | — |
| #107-B | Node-aware save/restore/find RPC opcodes | First | A |
| #107-C | FastCDC content-defined chunking | First | — (parallel) |
| #107-D | First-class system-prompt base cache (persistent, cross-conversation) | First | B, (PR #148) |
| #107-E | Git-aware repo cache (commit/fingerprint lookup) | First | B, (PR #148) |
| #107-F | Checkpoints / revert API | Follow-on | B |
| #107-G | DAG deepest-ancestor incremental prefill | Follow-on | B, D, E |
| #107-H | Hydra.PrefillBuilder + Hydra.GitService | Follow-on | E, G |
| #107-I | Hot/warm/cold tiering | Follow-on | A |

**Sequencing gates:** Phase 0 CI must be green (#98) before any PR merges; routing tasks (D, E —
Hydra.Core scheduler/session code) rebase after PR #148 (M-Perf WorkerScheduler) merges. Store-only
tasks (A, B, C) may start as soon as CI is green.

---

## Blockers (state at time of writing, 2026-06)
1. **CI red on `main`** (last 8 runs, tracked by auto-issue #98) — hard merge gate; Phase 0 goal.
2. **#107 was undecomposed** — addressed by the epic tree above.
3. **PR #148 file collision** — Hydra.Core routing work rebases after it merges.
4. **GC correctness** — handled in Task A (UNION reference set).
5. **"merge" vs Rule #2** — resolved as lineage chaining.

---

## Verification
- **Store (A/B):** `dotnet test src/core/Tests.Shared/ && dotnet test src/core/Tests.Core/`; node round-trip
  byte-identical (hash of reassembled buffer == original); GC retains node-only chunks; nearest-
  ancestor lookup over a 3-commit lineage.
- **FastCDC (C):** round-trip byte-identical; on "insert N bytes near the front", CDC re-save dedup
  ratio ≫ fixed-1 MB.
- **Routing (D/E):** `dotnet test src/core/Tests.Core/` + `pytest tests/system`; two conversations with identical system
  prompt → second is a hit and survives a simulated restart; request at `commit B` (child of `A`)
  with only `A` cached → resolves to `A`, prefills only the residual; missing repo headers → behaves
  as prefix-only.
- **E2E:** `dotnet test src/core/Tests.Core/`, `pytest tests/system`; repeat 80K-context request
  shows high `cached_tokens` / TTFT collapse vs cold (`DevelopmentRunBook.md`).
