# Revised plan — Coordinator `WorkerScheduler` with **verified** warm-slot reuse (#147 rev2)

> Supersedes the draft in #147. Keeps the good direction (authoritative `WorkerTracker`,
> `asyncio` queue with skip-scan, per-worker `max_prefill_tokens`, stuck-slot detection) and fixes
> the three blockers found in review: scheduler liveness (B1), dispatch race (B2), and the dropped
> M1/M2 session intelligence (B3/B6). The headline addition: **affinity reuse is gated on a real
> check that the warm slot still holds the session's KV** — never trust `session_table` alone.

## Why the warm-slot check
`session_table` says "session S is on RTX slot 0 at n_past=1233", but llama may have since:
LRU-evicted that slot for another session, cleared it (`slot_save_and_clear`), reused the slot id,
or wedged it (`is_processing:true, n_remain:0` — the stuck-slot bug). Routing a follow-up to a
"warm" worker on stale belief causes a wrong-context answer or a 503. So before the fast affinity
path we **probe the worker and confirm the slot is genuinely warm**; otherwise we fall back to
restore-from-Store or a fresh prefill.

## Request decision tree (preserves M1/M2, adds verification)
```
submit(item) → _process(item):
  entry = session_table.lookup(item.session_id)

  ┌─ 1. AFFINITY (warm-slot reuse — fastest, no prefill/migration) ────────────
  │  if entry and entry.slot_id is not None and entry.node healthy/free/not-stuck:
  │      if await verify_warm_slot(worker, entry):        ← THE gate
  │          n_past guard (estimate vs entry.n_past)      ← keep #M1 guard
  │          → route request straight to worker.llama_url, llama reuses cached KV
  │          → update n_past from usage; release; DONE
  │      else: fall through (slot no longer warm)
  │
  ├─ 2. STORE_RESTORE (state durable in Store, slot cold) ─────────────────────
  │  if entry and entry.has_store_state:
  │      pick decode worker → restore_session() → decode → DONE
  │
  └─ 3. COLD (new session, or no reusable state) ──────────────────────────────
     atomic (≤ atomic_token_threshold)  → one mixed worker prefill+decode
     else (concurrency)                 → prefill → save → restore → decode
     + prefix/system-prompt checkpoint restore/save (#107/#130) on the cold path
```

## `verify_warm_slot(worker, entry)` — the new safety gate
Single cheap `GET {worker.llama_url}/slots` (5 s timeout). Returns True only if **all** hold:
1. A slot with `id == entry.slot_id` exists.
2. It is **not stuck**: not (`is_processing == true && n_remain == 0`).
3. Its resident prefix covers the session: `slot.n_past >= entry.n_past` (the fork already exposes
   `n_past` on `/slots`; this is exactly what `_resolve_slot_id` reads today).
4. **Prefix identity matches** (guards slot-id reuse by another session): compare
   `entry.prefix_hash` to a hash of the slot's reported prompt prefix. If llama doesn't expose the
   prompt text, fall back to (3) + the existing n_past guard as the weaker check and log it.

On False → treat the slot as cold (clear `entry.slot_id`, keep `has_store_state`) and continue to
step 2/3. The result is also fed back into the tracker (a stuck slot → `tracker.mark_unhealthy`
+ exclude until the health probe sees it clear).

`session_table` gains one field: `prefix_hash: str | None` (sha256 of the system + first-user
prefix, set when the session is first established) so step 4 is possible.

## Scheduler fixes (the review blockers)
- **B1 (liveness):** the idle wait must wake on **either** signal and never crash the loop:
  ```python
  done, pending = await asyncio.wait(
      {asyncio.create_task(self._worker_freed.wait()),
       asyncio.create_task(self._new_item.wait())},
      return_when=asyncio.FIRST_COMPLETED, timeout=30.0)
  for t in pending: t.cancel()
  # timeout is a normal heartbeat, not an error — loop simply re-scans
  ```
  Wrap the whole `_run` body in `try/except Exception` (log + continue) so one bad item never kills
  the consumer.
- **B2 (dispatch race):** **acquire the worker synchronously in `_run` at dispatch time**, before
  `create_task(_process(...))`. `_process` receives an already-acquired worker; it never re-picks or
  re-acquires for the primary slot. This preserves the "authoritative synchronous state" premise and
  removes the check-then-acquire TOCTOU → no spurious `"worker busy"` 503s.
- Pass the acquired worker into `_process(item, worker)`; release in the stream/`finally` exactly as
  drafted.

## Preserved M1/M2 logic (must live in `_execute_*`, not be dropped)
Move these out of the old `router.py` into the scheduler execution paths:
- **Session affinity** (step 1) + **warm-slot verify** (new).
- **store_restore** (step 2).
- **Prefix / system-prompt KV checkpoint** restore-on-new-session and save-once
  (`restore_prefix_checkpoint` / `_maybe_save_prefix`, #130/#132) on the cold path.
- **n_past guard** (estimate < `entry.n_past * 0.85` → `SLOT_ERASE` + reset) — keep on the affinity
  and restore paths; it is the second line of defense behind `verify_warm_slot`.
- **n_past + slot tracking from `usage`** in the stream `finally` (`update_n_past`, `_resolve_slot_id`)
  — required or affinity/guard/prefix all silently break on the next turn.
These become explicit **acceptance criteria** so the rewrite can't regress them.

## Files (delta vs the #147 draft)
| File | Change |
|---|---|
| `worker_tracker.py` (NEW) | as drafted; add `mark_unhealthy/mark_healthy` already present |
| `scheduler.py` (NEW) | as drafted **+ B1/B2 fixes + the decision tree + verify_warm_slot call**; `_execute_affinity`, `_execute_store_restore`, `_execute_atomic`, `_execute_concurrency`; acquire-at-dispatch |
| `routing.py` (REWRITE) | keep `pick_best_*`; **add `verify_warm_slot(worker, entry, http)`**; keep `estimate_request_tokens`, `derive_session_id`, `prefix_hash` helper |
| `session_table.py` (MOD) | add `prefix_hash: str | None`; keep `slot_id`, `n_past`, `has_store_state` |
| `health.py` (MOD) | stuck-slot probe **and wire it**: `is_healthy(name)` (or a new `is_routable`) returns False while `stuck_slots[name] > 0`; auto-clear when the probe sees the slot idle |
| `config.py` (MOD) | `max_prefill_tokens=-1`, `atomic_token_threshold=2048`, `worker_error_threshold=3` |
| `router.py` (MOD, not full rewrite) | thin `chat_completion` → `scheduler.submit`; **but keep** the prefix-hash derivation so the session is seeded |
| `app.py` (MOD) | wire `WorkerTracker` + `WorkerScheduler`, start/stop in lifespan |
| tests | `test_scheduler.py` (skip-scan, caps, atomic, **acquire-at-dispatch no double-acquire**, B1 wake-on-either); `test_warm_slot.py` (verify True/stale/stuck/n_past-mismatch/prefix-mismatch); update `test_routing.py`, `test_router.py` |

## Acceptance criteria (additions in **bold**)
1. Queue skip-scan processes workable items behind a blocked front; **front not starved (aging)**.
2. `max_prefill_tokens` keeps >8K prompts off P100.
3. `atomic_token_threshold` uses fast mode for small responses.
4. Worker error counter: 1 retry, unhealthy after 3; **stuck slot excludes the node until cleared**.
5. **Affinity reuse only fires when `verify_warm_slot` passes; a stale/evicted/stuck/mismatched slot
   falls back to store-restore or fresh prefill — never answers from the wrong slot.**
6. **Prefix checkpoint, n_past guard, n_past/slot tracking, store_restore all still work
   (regression tests prove each).**
7. No `_in_flight` dict / no poll-derived capacity; no hardcoded node names.
8. Scheduler survives idle (no 120s death), never double-acquires a worker.
9. `pytest src/coordinator/tests/` green.

## Verification (end-to-end on the live 2-node stack)
- **Warm reuse:** 2 turns same session → turn 2 logs affinity + `verify_warm_slot=ok`, no prefill,
  fast TTFT.
- **Stale slot:** evict/clear the slot between turns (or kill+restart llama) → turn 2 detects
  `verify_warm_slot=false` → restores from Store (or re-prefills), correct answer, no 503.
- **Stuck P100:** force `is_processing:true,n_remain:0` → node excluded, request served on RTX,
  P100 auto-recovers when slot clears.
- **Cap:** 70K prompt with RTX busy + P100 free → queued (skip-scan runs small items) until RTX
  frees; never sent to P100.
