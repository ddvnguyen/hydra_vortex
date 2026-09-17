# Evidence rescue (11c) — architect analysis artifacts from /tmp

**Rescued 2026-09-17** from `/tmp/opencode/` (ephemeral; see 11b — the
cross-GPU replication dir `/tmp/opencode/early-router-5060ti/` already
evaporated and was **verified gone, not recreated**). All files below are
byte-identical copies (`cp -p`); sha pins where noted. Nothing here was
re-derived or edited.

## Layout

- `scripts/` — analysis scripts: `prefill_rank_poc.py`, `prefill_score.py`,
  `perlayer_n.py`, `n_sweep.py` (the 10a reference sweep: N=19/38/43/57/76…,
  O/D=0.1298 provisional). **Note:** scripts reference absolute `/tmp/opencode`
  input paths; run them against the rescued copies by symlinking or editing
  paths — the in-repo copies are archival.
- `prefill/` — `prefill_poc_results.json` (57 rows: prefill/loso/subj/oracle
  recall), `prefill_poc_index.json` (57-entry corpus index), plus the two
  11 MB measurement blobs `prefill_poc_{dec,pre}.npy` with `NPY.sha256`.
- `traces/MANIFEST.tsv` — the 57-trace corpus: index name → on-disk blob(s)
  under `/tmp/opencode/trace-collect/traces/` (root for `c01/c02/g01`,
  `v2/` and `v4/` subdirs) with per-file bytes + sha256. **Blobs stay in
  /tmp; the manifest is the in-repo pin.** 57/57 entries resolved, 231 blobs,
  zero missing at rescue time.
- `reports/` — all 9 `REPORT.md` files found in `/tmp/opencode` (namespaced by
  origin dir): `phase0-rank`, `hprefill`, `scorer_v4`, `router-arms`,
  `s1-shortturn`, `s5-allpool-stability`, `s5b-splits`, `stale-replay`,
  `router-taskB_report`.

## Related pins (same failure class, 9f)

- Null_1 KLD band: `/tmp/opencode/kld-bands/null_1-<sha256>.log`
  (content-addressed; figure source of record, not yet in-repo).

## Provenance

- Rescue manifest builder: `/tmp/opencode/build_trace_manifest.py` (not in-repo).
- Lesson applied (9f/11c): anchor bands and corpora to sha256, never to live
  `/tmp` paths or bare mtimes.
