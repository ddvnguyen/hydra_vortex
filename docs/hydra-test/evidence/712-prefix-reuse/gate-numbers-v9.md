# #712 A/B v9 — gate numbers (P100 test lane, image localhost/hydra-core:712-w1g d14dbef86b5e)

## run-1 (ab716-103510-v9) — FAILED, root cause recorded
- Turn 1 → HTTP 503: decode routed to test-b whose registration DECODE probe was malformed
  (empty tokenizer/name → engine Gate A reject + bad-magic close → poisoned RPC channel).
- Secondary: nodeA 8323-tok PREFILL probe blob send hit the VM binary's 10s RPC timeout → EBADF.
- NOT a #712 fix failure — caused by the reconstructed merged [test-a,test-b] worker config.
- run-2 re-runs v8-faithful (test-a only, requires_workers=["test-a"]).

## run-2 (ab716-123449-v9) — PASSED, all 4 gates
Config: v8-faithful (workers=test-a only, requires_workers=["test-a"]); all turns solo on test-a.
Image: localhost/hydra-core:712-w1g (d14dbef86b5e) — Fix A (84261d4) + Fix B (b2c8be5).

| turn | TTFT(s) | baseline TTFT(s) | 1.5x cap | G1 |
|------|---------|------------------|----------|----|
| 1 | 33.04 | 31.3 | 46.95 | PASS |
| 2 | 27.24 | 25.3 | 37.95 | PASS |
| 3 | 26.58 | 24.8 | 37.20 | PASS |
| 4 | 28.38 | 26.2 | 39.30 | PASS |
| 5 | 28.51 | 25.5 | 38.25 | PASS |
| 6 | 31.62 | 29.0 | 43.50 | PASS |

- **G1 PASS** — 6/6 TTFT within 1.5x canonical baseline.
- **G2 PASS** — 5x `#PD-TRACE N_COMMON trim` on nodeA: n_common = 8321 / 14530 / 20289 / 26048 / 31357 (exact expected values).
- **G3 PASS** — coordinator: `stream_done_lease_handed_off`=5, `stream_done_release`=1, `stream_done_no_lease`=0, `evict_save_skipped`=5. (Fix A lease-handoff path exercised.)
- **G4 PASS** — `decode_path MergedCapable=True` x6/6, 0 HTTP 503, 0 store faults.

Notes:
- v9 run-1 (merged [test-a,test-b] config) FAILED turn 1 w/ 503 — root cause = config
  reconstruction artifact (malformed registration DECODE probe to test-b; 10s RPC PREFILL
  timeout on VM binary), NOT the #712 fix. run-1 raw logs saved as *-run1-failed.log.
- Coordinator log container-712-w1i-v9.log = 142KB (run-2, canonical).
- v8 coordinator raw log was lost with its deleted container; v9 run-2 is the canonical raw evidence.
