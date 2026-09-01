# Hydra-TEST bring-up — root cause: engine binary predates M2 hash fix

**Date:** 2026-08-28 ~17:05 ICT · **By:** Track A lead (auto-drive)

## What happened during bring-up

Host-side cores (`hydra-core-test-a/b`) came up green on the first try:
- `hydra_test` DB created (N1 isolation), cores on :19000/:19001, stores :19500/:19502.
- Cores poll VM engines (192.168.122.21:18086/18087) healthy — `health_poll_ok`.
- `models-test.json` (9B test model) registered; single chat completion through the
  full stack core→engine works: `200 OK, 32 tokens` (≈4.5 s).

But concurrent (≥2) requests via the cores wedge: engine logs show

```
hydra: PREFILL slot=0 done n_past=16 kv=52970604 ...
hydra rpc: PREFILL slot=0 M2 stream failed: PREFILL M2: hash pre-pass hashed 0 B, expected 52970612 B
hydra rpc: recv failed on fd=37 (0/16 bytes): Resource temporarily unavailable
```

and the coordinator-side requests end in `499` after the client timeout.

## Root cause (verified in source + git archaeology)

`llama_io_write_hash` (the #470 M2 hash pre-pass, introduced `234083a45`,
2026-08-14) returned a **hardcoded `n_bytes() = 0`**, so
`llama_state_seq_hash()` always reports 0 bytes → the caller's
`hashed != expected` check fails → PREFILL M2 always errors on this build.

- Fix `3206b13b6` ("fix(engine): M2 hash pre-pass n_bytes stub + header
  double-count (#470)", 2026-08-16) adds the missing `bytes_written` counter.
- `3206b13b6` is **NOT an ancestor of `a7b40fdce`** — the commit the test
  engine (`~/hydra-min-test/llama-engine`, built 2026-08-27) was compiled from.
- Fork tip `ddvnguyen/hydra-fork` @ `67ceb00bd` **contains the fix**
  (`n_bytes() { return bytes_written; }` confirmed on that branch).

## Why production is unaffected

- Prod P100 engine (`60bde13af`, v9701, deployed 2026-08-18) also predates the
  fix, but the prod P100 worker is **decode-only** (`worker_type: 2`) — the
  PREFILL M2 path never fires for it, so the bug is dormant there.
- The hydra-TEST engines run **Mixed** (`worker_type: 3`) → they take the
  PREFILL M2 path → the bug bites. First time this code path is exercised on
  sm_60 hardware.

## Remediation in flight

Rebuilding the engine from fork tip `67ceb00bd` (has the fix) for sm_60 on the
host with CUDA 13.2 toolkit (P100 VM driver 580.173 supports it), target
deploy path `~/hydra-fork-fix/` on the VM, then relaunch both test engines.
This engine bump is test-lane-only; prod residents untouched.

## Also fixed this run (lead follow-ups, in feat/hydra-test-p100)

- `workers-test-a/b.json`: engine endpoints → `192.168.122.21:1808x` (VM), since
  cores run host-side (VM has no podman; engines run bare per minifleet pattern).
- New `infra/hydra-core/config/models-test.json` (9B Q4 entry + alias) and
  `HYDRA_COORD_MODELS_FILE` override in the compose.
- `up.sh`: pg container name `pg` → `infra-postgres`; store/L1 dirs →
  `/tmp/hydra-test-store` + `/tmp/hydra-test-l1` (host `/mnt/llm-ram` absent).
- Test payload model → `qwen3.5-9b-test`; client timeout 30→120 s.
