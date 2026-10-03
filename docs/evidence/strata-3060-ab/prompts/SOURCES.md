# t0006 benchmark prompt cells - byte provenance

All context text is verbatim UTF-8/LF from real project files, sections joined with `\n\n---\n\n`, each source trimmed at a sentence or line boundary (never mid-word) to fit its char budget = target_tokens * 4.
Token estimate: chars/4; the actual prompt_tokens for each cell are recorded at runtime by the A/B harness.
`p1k.txt` - header `# t0006 benchmark prompt cell p1k`, then `/mnt/WorkDisk/trace-collect-backup/prompts/c01.txt`...`c08.txt` verbatim in numeric order (c09-c22 unused: budget reached, no source trimmed).
`p4k.txt` - `/mnt/WorkDisk/strata/src-v0.1.29/bench/results/` README.md in order: 2026-09-29-layer-split, 2026-09-29-layer-split-limits, 2026-09-28-coder, 2026-09-28-prefill-speed, then 2026-09-28-speed-0114 (sentence-trimmed), after its own header line.
`p12k.txt` - strata `README.md`, then every `*.md` under `bench/results/*/` (path-sorted), then `docs/DETAILS.md` (sentence-trimmed tail), after its own header line; the distinct headers make p1k/p4k/p12k non-prefixes of each other (no cross-cell KV reuse).
`prefix_turn1.txt` - `docs/MULTI_GPU.md` + `docs/SECOND_GPU.md` + `docs/ORCA.md` + `docs/AMD_HIP.md` + `docs/AMD_HIP_PERFORMANCE.md` (sentence-trimmed tail); deliberately carries no p-cell header so its first tokens differ from every prefill/decode cell.
`prefix_turn2.txt` / `prefix_turn3.txt` - authored follow-up questions (~2000 chars each) whose claims reference only facts stated in prefix_turn1's five documents; no source-file bytes reused.
`manifest.json` - cell schema for the harness; `sha256sums.txt` - `sha256sum` of the six `.txt` prompt files.

## 2026-10-02: cell rebuild with real tokenizer counts (t0006 S0-1 abort)

The cells were first sized by chars/4. The Qwen3.5 BPE on this content runs ~2.6-3.7 chars/token, so p4k tokenized to 6301 and p12k to ~19k; p12k + 256 completion exceeded ctx 16384 and the server rejected it with HTTP 400 (arm S0-1 aborted, void). p4k and p12k were trimmed at sentence boundaries to <= 4096 / <= 12288 tokens with the pack tokenizer (/mnt/SSD/strata-models/packs/iq3_s/tokenizer); originals kept under archive-2026-10-02-char-est/ with their old sha256. p1k stays at 1115 tokens (+8.9% of 1K, within noise). warmup.txt added (independent content = t0005 bench prompt): the S0-1 warm-up used p1k.txt verbatim and primed the measured p1k cell (1110/1115 tokens reused on rep0) - fixed for all later arms. sha256sums.txt regenerated; this is a deviation from the pre-registered cell composition, documented for the README.
