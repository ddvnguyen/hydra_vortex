# ReAP-first re-authoring, d-e122f322ba — 8 v2 SHORT replacements (DRAFTS, UNVERIFIED)

**Status: UNVERIFIED.** No rig, no decode runs. These drafts have never been
generated against; the first collection leg adjudicates (greedy EOS or
sustained decode). If EOS recurs, iterate once on the failing shape — do not
re-litigate the settled ones. Collection stays parked until then.

Files: `<id>.txt` in this directory = full replacement prompt text, pack idiom
(task head + bridge + pasted context). Pack application recipe for the rig owner:

```sh
cd /tmp/opencode/trace-collect/prompts/v2/
for id in c13 c16 c17 c19 c20 c21 g15 g10; do
  cp <this-dir>/$id.txt $id.txt   # replaces the dead prompt, same filename
done
# then re-emit manifest lines (see table below) and verify pair ratios
```

## STEP 1 — ReAP suitability survey (small sample only)

| Source | Sample | Verdict |
|---|---|---|
| `theblackcat102/evol-codealpaca-v1` (instruction/output, 0.2–1.8k chars, 3 rows) | Debug-OCR + recursion heads | Heads usable, far too short standalone; needs pack-style context padding. Marginal. |
| `open-r1/Mixture-of-Thoughts[code]` (user scaffold + `<think>` CoT + solution, 5.8–25k tok, 3 rows; row 0 deep-read: max-average-segment≥k, C++17) | Constraint-analysis → step-by-step → full implementation | **Suitable.** Decode-heavy by construction; sliceable to band; scaffold re-targetable across languages. Used for c13, c19. |
| `SWE-bench/SWE-smith-trajectories(tool)` (43-msg agent trajectories, 53–160k chars, 3 rows + 1 deep) | Bug-report + repro + patch arc; resolved=True | **Suitable.** Ideal debug-shape material; sliceable. Used for c17 (instance below). |
| `Salesforce/xlam-function-calling-60k` | — | **Excluded:** 401 gated, unsampleable without HF auth. No provenance fabricated. |
| `scripts/generation_quality_analysis.py` | — | **Absent** from repo, /tmp, and searchable worktrees. Moot for pack format: collection uses RAW `-p` (no chat template, PROMPTS.md §0), so drafts stay raw-prompt idiom. |

Total fetched: ~10 rows. No datasets cloned.

## STEP 2 — the 8 replacements (old → new manifest lines)

Old lines are the current pack manifest (`prompts/v2/manifest.txt`); new lines
are computed from these drafts (sha12 scheme verified against pack: c13
`eaf3aae16167` reproduces). Subtypes preserved per PROMPTS.md split.

| id | old (chars/sha12) | new (chars/est_tok/sha12) | provenance |
|---|---|---|---|
| c13 go/L | 10181 `eaf3aae16167` | 12560/3140 `0b9b519909c6` | **ReAP-adapted (MoT-code row 0 scaffold):** bounds-first → step-by-step → full Go implementation. Same uploader domain as dead c13; EntityTooSmall/resume/test demands force extended generation. |
| c16 rust/L | 10180 `603808d7c8f4` | 12002/3000 `8529ea86e9cb` | **c14-shape:** incident framing (fuzz crash-8814) + fault chain + 3 confirm commands + wrapper + seed test + proof line. Same FFI domain, debug idiom. |
| c17 python/L | 10184 `ffffb24958fc` | 12157/3039 `efb4209fa25c` | **ReAP-adapted (SWE-smith tool, `marshmallow-code__apispec.8b421526.func_pm_remove_assign__kdkrbg6a`):** APISpec summary-field bug, repro→root-cause→diff→green-run→behavior-decision five-part demand. |
| c19 ts/L | 10193 `9973b24fb5cf` | 12112/3028 `75a5902282ef` | **ReAP-adapted (MoT-code scaffold):** constraint analysis (600 tiles/worker caps) → pool design → pool class + backpressure + stress test. Same farm domain. |
| c20 go/L | 10121 `8053f8cb8bde` | 12168/3042 `f03f829e86a1` | **c14-shape:** race incident + cycle explanation + before/after declarations + validation + hammer test. Same SIGHUP domain. |
| c21 rust/L | 10210 `73945c6d8c64` | 12000/3000 `406945a302cd` | **c14-shape:** bench-regression incident + copy-chain analysis + chunked rework + equivalence test + lifetime note. Same wasm domain. |
| g15 edit/L | 10131 `e8e735dbafa3` | 12003/3000 `98e1b4815bdf` | **g16-shape (same edit subtype, alive neighbor):** rewrite directive + exact output spec (200-word brief) + budget table + 6 bullets + ledger. Fresh domain (proposal, not cover letter) to avoid EOS-pattern carryover. |
| g10 compare/M | 2249 `18cb547d5479` | 2454/613 `01807497bfb9` | **g09-shape (compare, M-tier neighbor):** arithmetic table + breakeven + 2 assumption footnotes + gut-check, ~400 words. Fresh domain (heat pump vs furnace). |

Design notes (all drafts): enumerated multi-part generation demands ending in a
hard artifact (diff/test/table); varied pasted context (no closed monotonous
tail dump as the ONLY context type); pack bridge idiom kept; no
"skeleton"/truncation-inviting wording. No verified EOS discriminator exists —
these maximize the known-good correlates; the collection leg decides.

## Known trade-off (leader call)

New L drafts sit at ~3.0–3.1k tok vs mates at ~2.5k tok → pair-length parity
(≥92% rule, PROMPTS.md §1) breaks for pairs 13/16/17/19/20/21/15. Options:
(a) accept asymmetry (L-tier moved to the -c 8192 regime; length-confound
control now lives in analysis, not pairing); (b) stretch the 7 mates to match.
Brief's 3–6k band was followed; flagging, not deciding.
