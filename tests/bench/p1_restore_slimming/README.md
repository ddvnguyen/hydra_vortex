# #720 P1 restore-slimming — acceptance harness (P100 test lane)

Acceptance legs for the P1 change `a920cb8ca` (stream non-merged chunked
restores into STATE_PUT) + `#721` fix `92479daf4` (skip prefix save when KV
already streamed to store). Verified live on the 2-engine P100 rig
(`ssh hydra-p100`, user `vm1`) against coordinator **test-a** (`:19000`,
store RPC `:19500`, debug `:19501`).

## Rig preconditions

- Image `localhost/hydra-core:latest` built from the worktree HEAD
  (contains both commits — check with
  `python3 -c "d=open('Hydra.Core.dll','rb').read(); print(d.count('prefix_save_skipped_streamed'.encode('utf-16-le')), d.count('state_stream_planned'.encode('utf-16-le'))"`
  on a `podman cp`'d copy of `/app/Hydra.Core.dll` — expect 1 and 1).
- Engines: nodeA `http://192.168.122.21:18086` (RPC 19513), nodeB
  `http://192.168.122.21:18087` (RPC 19514), sm_60 fork
  `/home/vm1/hydra-fork-fix-sm60/llama-server`, model
  `Qwen3.5-9B-Q4_K_M.gguf` (alias `qwen3.5-9b-test`).
- Store chunk data lives in PG L2 (`chunk_data_l2`,
  `HYDRA_CHUNK_CACHE_BACKEND=pg`) with a tmpfs front; the read path
  (GET_CHUNKED/GET) serves **tmpfs-resident chunks only** — pre-existing
  #470 behavior. Make chunks resident before restore (legitimate: store
  startup recovery does this — after a coordinator restart the two
  `kv/*` sessions are re-seeded at boot).
- Confirm no poisoned zero-byte artifacts before running:
  `psql: select count(*) from chunks where size=0` (expect 0),
  `select count(*) from chunk_data_l2 where size=0` (expect 0),
  `find <store-root>/chunks -type f -size 0 | wc -l` (expect 0).

## Leg (c) — `legc/` : StateHandler streaming restore, direct

Drives `StateHandler.SaveToStoreChunkedAsync` →
`RestoreFromStoreChunkedAsync` against the live store + engines, bypassing
the scheduler (proves P1 items 1–3: no full-blob assembly buffer, no
trailing meta round-trip, ordered chunk stream).

```bash
dotnet build src/core/Hydra.Core -c Debug
cd tests/bench/p1_restore_slimming/legc
dotnet build -c Debug
# <sid> [nosave] [srcslot]
dotnet run --no-build -c Debug -- legc-<date>
```

Expected: `verdict: PASS`, restore `n_past` == source `n_past`,
`dst_meta_after.state_size` byte-exact, `heap_delta_bytes` ≈ 1.2 MB
(streaming: bounded by chunk size, not session size). Engine log:
`STATE_PUT slot=<n> restored=<bytes> B n_past=<n>` (non-zero) on the
destination engine. `probe_getchunked.py` is a raw-wire GET_CHUNKED (0x11)
debug probe.

**Gotcha (bit us once):** never leave stray `.cs` diagnostic files inside
the `legc/` project dir — the SDK auto-includes them and a copy of an old
`StateHandler.cs` will shadow the real one via CS0436 and silently run the
pre-P1 code path.

## Leg (d) — P/D split across the two engines

Two-worker config `workers-test-a-legd.json` (deploy as
`/etc/hydra/config/workers-test-a.json`, restart the coordinator):

- **A** `test-a`: type 3 (prefill+decode), prefill_priority 1,
  decode_priority **2**, 18086/19513.
- **B** `test-a-d2`: type 2 (decode-only), decode_priority **1**,
  18087/19514, slots 2.

Turn 1 (prompt > `HYDRA_COORD_ATOMIC_THRESHOLD`, default 2048) must
classify Prefill: prefill on A → `prefill_streamed_to_store` → PickDecode
→ B → KV restore → decode on B. Turn 2 `force_mode=solo` → Atomic.

```bash
curl -s http://127.0.0.1:19000/health   # 200
# then any 2-turn chat with X-Session-Id against :19000 (see leg_a.py shape)
```

**Known gate (report, don't hack):** the merged vs non-merged *P/D RestoreKv
stage* is chosen by engine capability, not config —
`WorkerSchedulerService.cs:3790`
(`EngineCapabilities.Contains(CapMergedDecode)`), capabilities populated
from the ENGINE INFO (0x41, empty key) poll at
`HealthMonitorService.cs:256`. The sm_60 fork engines on this rig advertise
`merged_decode`, so the P/D stage takes the #470 merged stream path
(`restore_kv_stream_planned` / `restore_kv_merged_stream_from_store`).
There is no config/env override. P1's non-merged path is still exercised
through the coordinator by the **PrefixRestore stage** of every warm/solo
turn (see leg a) — engine evidence `STATE_PUT ... restored=<bytes> B`.

## Leg (a) — `leg_a.py` : 6-turn solo warm regression

Mirrors the A/B #5 workload shape: ~5.7k tokens/turn, `max_tokens=64`,
`force_mode=solo` every turn (bypasses warm reuse → every turn after the
first runs PrefixRestore from the store + delta prefill), fixed
`X-Session-Id`.

```bash
python3 leg_a.py 19000 p1smoke-final-<date>
# JSON written to /tmp/p1_smoke/leg_a_<port>.json
```

Expected: 6× HTTP 200, cumulative `prompt_tokens` per turn
(≈ 5189 / 10423 / 15656 / 20890 / 26124 / 31358 on the P100 rig),
elapsed growing ~31s → ~174s. Coordinator log: 5× `solo_kv_restored`
(NPast exact cumulative), 6× `prefix_save_skipped_streamed` (the #721 fix),
`PrefixRestore->Prefill` ms in the 300–1900 range. Engine log: 5×
`STATE_PUT slot=0 restored=<144..509 MB> B n_past=<exact>`, 0 quarantines.

## Evidence gathered 2026-09-01 (UTC)

| Leg | Result | Key numbers |
|---|---|---|
| c   | PASS | legc-20260901: 653 MB / 78 chunks, restore wall 1378 ms, n_past 31421 exact, heap Δ 1,197,200 B · legc2-20260901: 162 MB / 20 chunks, 487 ms, n_past 3232 exact, heap Δ 1,181,792 B |
| d   | PASS (P/D routing; merged stage) | turn 1b P/D: prefill A 10.5 s → save 148 MB → decode B, `restore_kv_merged_stream_from_store` 155,587,162 B, 94 ms, 200 in 148.1 s · turn 2 solo: 200 in 173.6 s; nodeB 2× `Gate A pass`, 0 quarantines · non-merged P/D branch engine-gated (see above) |
| a   | PASS | 6×200 (31.4/52.6/75.2/109.7/154.9/173.8 s), 5× solo_kv_restored exact, 6× prefix_save_skipped_streamed, 5× STATE_PUT 144–509 MB, 0 quarantines |
