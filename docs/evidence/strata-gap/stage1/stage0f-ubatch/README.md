# Stage 0f raw ubatch-sweep artefacts

One JSON per arm (`ub<UB>-r<ROUND>.json`) plus the matching `-engine.log`. The sweep was still
running when `../STAGE0F-UBATCH.md` was written — **re-run the aggregation over these files to get
the final n≥3 numbers**; any file appearing later is a later round.

Fields that came back EMPTY or are known-bad, and why (see STAGE0F-UBATCH.md §9):
- `compute_buffer` — empty in every arm; the engine-log grep pattern did not match.
- `decode.decode_tps` — **invalid**: computed as `(completion_tokens-1)/wall` where `wall` is the
  whole request wall *including prefill*. Use only `decode.coarse_coherent`.
- `h2d_pre` / `h2d_post` — the pinned probe's multi-line record, tail-trimmed; the `avg … GB/s`
  value is intact and every sample was inside 6.00–6.30.

Reproducibility note: VRAM is bit-identical across rounds at each ubatch size
(2048 → 6271/6535, 4096 → 7237/7759, 8192 → 9499/10533 MiB load/after-prefill).
