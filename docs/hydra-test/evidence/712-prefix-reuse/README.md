# #712 — Solo/cold route: prefix-KV reuse across turns (evidence)

**Branch:** `fix/712-prefix-reuse` · **Base:** `epic/697-final-verify @ 56eb3f1`
**Rig:** test-a `:19000` (ddv-server podman, host net) → engine nodeA `192.168.122.21:18086`
(Qwen3.5-9B-Q4_K_M, ctx 65536, 16 GPU layers), store `:19501` (tmpfs `/tmp/hydra-test-store`).
**Harness:** `ab_test6.py` — 6 consecutive same-session `force_mode=solo` turns, prompt grows
8323 → 37118 tokens, `max_tokens=64`, streaming SSE. Session state (L1 + store + Postgres)
wiped between runs.

## Result (final, image v6 `c7e9d8d9886d`)

| Turn | Prompt | A/B#4 baseline (canonical) | Before (#712) | After v6 | After/baseline |
|------|--------|---------------------------|---------------|----------|----------------|
| T1   | 8323   | 31.3 s | 33.96 s | **34.28 s** | 1.10× |
| T2   | 14532  | 25.3 s | 99.51 s | **26.82 s** | 1.06× |
| T3   | 20291  | 24.8 s | 100.13 s | **26.71 s** | 1.08× |
| T4   | 26050  | 26.2 s | 136.64 s | **28.44 s** | 1.09× |
| T5   | 31359  | 25.5 s | 197.76 s | **28.08 s** | 1.10× |
| T6   | 37118  | 29.0 s | 234.48 s | **31.79 s** | 1.10× |

**Acceptance criterion 1 (TTFT ≤ 1.5× baseline every turn): PASS** — max ratio 1.10×.
Before the fix, TTFT grew linearly with session length (99 s → 234 s) because every turn
re-issued a full PREFILL (0x42) that decodes from position 0, discarding the session KV.

Cross-check against the fresh baseline run recorded in `ab-before-BASELINE.txt`
(engine-native prefix cache, no Hydra): T3–T6 ratios 1.07/1.05/1.04/1.03× (T1/T2 in that
run are cache-warmth artifacts of the immediately preceding baseline pass — the canonical
A/B#4 table is the agreed reference).

## Acceptance criterion 2 — engine traces new-token-only prefills

`engine-nodeA-final-v6.log`, one `#PD-TRACE N_COMMON` line per turn decode:

```
T1: n_common=8323  (1-token re-decode; cold full prefill, unchanged by design)
T2: n_common=8321  → prompt eval 6211 tokens   (new: 14532−8321)
T3: n_common=14530 → prompt eval 5763 tokens   (new: 20291−14530)
T4: n_common=20289 → prompt eval 5763 tokens   (new: 26050−20289)
T5: n_common=26048 → prompt eval 5313 tokens   (new: 31359−26048)
T6: n_common=31357 → prompt eval 5763 tokens   (new: 37118−31357)
```

Per-turn prompt-eval time is flat (24–27 s) regardless of context length — the engine
prefills only the delta. **No full-context PREFILL after T1.** PASS.

## What changed (coordinator-only, engine binary untouched)

1. **Restore-hit skip-PREFILL** (`TryRestoreSessionKvAsync`, `PickDecodeAsync`, `DecodeAsync`):
   on a session-KV STATE_PUT hit with valid identity + `model_match`, the solo/cold route
   stamps `WorkItem.SoloKvRestoreHit`, skips the PREFILL stage, and pins the DECODE to the
   restored slot (lease conversion: Short→Long for cold_concurrency, Long reused for atomic).
   The engine's completion path (`update_slots → get_common_prefix`) then prefills only the
   delta. Gate: `RouteType ∈ {cold_concurrency, solo_prefix_restore}`; P/D split, migration,
   model-mismatch, or empty-identity restores fall back to full PREFILL.
2. **Save/restore race** (`_sessionSaveInFlight` TCS per session): registered at decode
   dispatch (both `_pendingBgSaves` sites), completed on every bg-save exit path. The next
   turn's restore waits ≤30 s for the previous turn's store Put before its store GET —
   without this, the restore read the *previous* blob and the delta grew by one full turn
   (observed: T3 at 1.9× baseline in run 2 of the v1 image).
3. **Health capability carry-forward** (`HealthMonitorService.PollWorkerAsync`): the poll
   builds a fresh `NodeInfo` each cycle; one failed/empty 0x41 INFO RPC (observed: RPC
   channel busy behind a multi-hundred-MB state transfer) silently blanked
   `EngineCapabilities` → the next decode took the HTTP fallback with **no log line at all**
   (the T4 144 s anomaly). Last-known `EngineCapabilities` + `PresetAliases` now carry
   forward while the new poll's sets are empty; `health_poll_ok` logs `Caps=N` and a
   `health_caps_carried` warning fires on carry.
4. **`model_path` strip on restore hits** (`StripReloadTriggerForRestoreHit`): the HTTP
   fallback body injects the alias `EngineConfig` dict, whose `model_path` is the engine's
   T3-reload trigger. On a restore hit the model is guaranteed resident — the path is
   stripped so a fallback turn can never attempt a (destructive) model rebuild. T1 slot keys
   (cache types, ubatch, flash_attn…) survive.
5. **Redundant evict-save skip** (`EvictWarmAndColdRouteAsync`): the force-mode evict ran a
   second full STATE_GET+PUT of the exact state the stream-end bg save had just persisted.
   `SessionEntry.StoreNPast` now records the n_past each save committed; the evict awaits
   the in-flight bg save (same TCS as #2) and skips the save when
   `StoreNPast == ledger.NPast` (`evict_save_skipped`). A failed/stale bg save still saves.
   (P100 run #2: this redundant save serialized with the bg save on the engine RPC channel
   and cost +16 s on T2's TTFT — 43.3 s, the only turn above 1.5× before this fix.)
6. **Diagnostics**: `decode_path` log line at the merged-decode gate (one per decode —
   `MergedCapable/Relay/KvChunks/RestoreHit`), making silent path skips visible.

## Acceptance criterion 3 — no regressions

- `Tests.Core`: **722/722** (717 at base + 5 new: strip ×2, health carry ×1, evict-skip ×2)
- `Tests.Shared`: **70/70**
- Warnings: identical set to base (25, line-shifts only; `Hydra.Core` Release build)
- Known pre-existing flake (not introduced here): `MergedDecodeModelAliasTests` fails ~1 in 5
  full-suite runs under parallel execution (static `ModelConfigLoader` shared with
  `WorkerSchedulerTests`); 14/14 × 5 in isolation.

## Final run (v6) marker summary — `coord-final-v6.log`

| Marker | Count | Meaning |
|--------|-------|---------|
| `decode_path … MergedCapable=True` | 6/6 | every turn took the merged 0x43 decode path |
| `merged_decode_initiated` | 6 | |
| `solo_prefix_decode_pinned` | 5 | T2–T6 pinned to the restored slot |
| `hydra_config_model_path_stripped` | 5 | defensive strip on every restore-hit decode (config never reached the engine — merged path carries no `hydra_config`) |
| `health_caps_carried` | 5 | five INFO polls failed/emptied; caps carried forward (pre-fix: silent HTTP fallback) |
| `evict_save_skipped` / `evict_saved` | 1 / 0 | no redundant 250 MB+ re-saves |
| engine `applying hydra_config` | 0 | no T3 rebuild attempts |
| engine `load_model` | 0 | |

## Iteration history (all images built from this branch, test-a only)

| Image | T1 | T2 | T3 | T4 | T5 | T6 | Notes |
|-------|----|----|----|----|----|----|-------|
| before `a20c91f` | 34.0 | 99.5 | 100.1 | 136.6 | 197.8 | 234.5 | the bug: linear growth (full re-prefill every turn) |
| v2 `b313be6` | 34.3 | 29.9 | 48.0 | 48.6 | 48.8 | 52.0 | skip-PREFILL works, but T3–T6 read a stale store blob (delta one turn too big) — save-wait not yet in |
| v3 `15955a1` | 34.5 | 38.5 | 26.7 | **144.1** | 35.4 | 31.9 | save-wait fixes T3; T4 = health-cap gap → silent HTTP fallback → `model_path` mismatch → engine T3-rebuild attempt → slot wipe → full 26050-token prefill |
| v5 `481df4f` r1 | 47.5 | 32.4 | 26.7 | 28.4 | 28.0 | 32.4 | T4 fixed (carry-forward + strip); T1 cold-engine artifact |
| v5 r2 | 34.1 | **43.3** | 26.6 | 28.5 | 28.0 | 31.6 | redundant evict save serialized with the bg save on the engine RPC channel (+16 s on T2) |
| v6 `c7e9d8d` | 34.3 | 26.8 | 26.7 | 28.4 | 28.1 | 31.8 | evict-save skip; flat; **final** |

`ab-after-v5-run1.txt` / `ab-after-v5-run2.txt` / `coord-v5-run2-evict-race.log` hold the
intermediate evidence (T4 anomaly timeline, evict-save race).

## Files

- `ab-before-HYDRA.txt` — pre-fix 6-turn run (image `a20c91f`)
- `ab-before-BASELINE.txt` — engine-native prefix-cache baseline run
- `ab-after2-HYDRA.txt`, `ab-after3-HYDRA.txt` — intermediate (v2/v3: save-wait era, T4 anomaly)
- `ab-after-v5-run1.txt`, `ab-after-v5-run2.txt` — intermediate (T4 fix; evict race)
- `ab-final-v6.txt` — final 6-turn run
- `coord-final-v6.log` — coordinator log, final run (podman logs)
- `coord-v5-run2-evict-race.log` — coordinator log showing the 18 s evict-save stall
- `engine-nodeA-final-v6.log` — nodeA engine log segment, final run (N_COMMON traces)
