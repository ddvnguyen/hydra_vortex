# tools/atlas — Expert Atlas pipeline for qwen4exp (offline, parent repo)

Offline port of Colibri's `c/tools/expert_atlas/` analysis onto the
cnre-phase0 route-trace corpus, per `docs/design-colibri-expert-atlas.md`
§4 Stage C / §7.1. Runs entirely on trace files — no GPU, no fork build,
no edits under `src/llama-cpp`.

## What it does

Trace format: `<call> <pos> <layer> <expert>:<gate> ...` rows (format per
`/tmp/opencode/cnre-phase0/TRACE_SPEC.md`); the prefill/decode split is
positional via the sidecar's exact `prefill_tokens`. Geometry (measured,
`QWEN4EXP_PROFILE.md`): 48 routed layers, 512 experts, top-10.

Methodology (#175 / Colibri analyze.py, ported):
- **decode-only** by default (prefill routing is topic-generic; decode-only
  sharpened every affinity number). `--include-prefill` to opt out.
- per-category **mean share across runs** (base-rate correction: run share
  is per-run normalized, so unequal trace lengths cannot bias the affinity)
- `p(c|e) = mean_share_c(e) / sum_c' mean_share_c'(e)`
- `spec = 1 - H/log C` (natural log; C = number of categories)
- **replication gate**: an expert must fire in >= `--min-runs` runs of its
  top category before it counts as a specialist (one hot prompt is ONE
  observation, not N — autocorrelation trap)
- `reliability = fired_runs(top) / runs_of(top)`

## Usage

```sh
cd tools/atlas && mkdir -p out

# 1. analysis (+ reproduction check vs phase0_rank.json, decision d-bb2f0d3b29)
python3 analyze.py --traces \
    coding=/tmp/opencode/cnre-phase0/traces/coding.route \
    general=/tmp/opencode/cnre-phase0/traces/general.route \
    --min-runs 1 --out out/experts.json --ranks out/expert-ranks.json
python3 reproduce.py              # PASS = exact reproduction of phase0_rank.json

# 2. artifacts (two-tier owner ruling, design §4)
python3 emit.py --full out/experts.json.atlas-full.json \
    --experts-out out/experts.json --ranks-out out/expert-ranks.json

# 3. LOP validation (draft corpus: degenerate protocol, see caveats)
python3 validate.py --full out/experts.json.atlas-full.json --out out/lop.json

# 4. pin file ("L <il> <ids hot-first...>")
python3 export_pinfile.py --ranks out/expert-ranks.json --out out/experts.pin --top-n 53
```

`reproduce.py` is the acceptance gate: it recomputes the quantities
phase0_rank.py already computes (self-heldout h@20/30/42, full-decode
per-layer top-42 hot order, train top-42 Jaccard) with this pipeline's own
parser and compares bit-for-bit against `/tmp/opencode/phase0-rank/phase0_rank.json`.
Current corpus: **PASS (all values exact)**.

## Artifacts & schema

`experts.json` — observability tier (Colibri dashboard shape):
```json
{"categories": ["coding", "general"],
 "experts": {"44:49": {"affinity": {"coding": 1.0}, "entropy": 0.0,
                       "top": "coding", "label": "specialist: coding",
                       "spec": 1.0, "reliability": "1/1"}},
 "provenance": {...}}
```

`expert-ranks.json` — ranking tier (future placement-prior consumer):
```json
{"version": 1, "engine_id": "qwen38", "model_hash": "...", "provenance": {...},
 "layers": {"0": {"experts": [
    {"id": 93, "heat": 68, "p": {"coding": 0.9706}, "reap_saliency": null},
    ...]}}}
```
- `heat` = raw decode selection count; order hot-first by heat (reproduces
  the phase0 full-decode `most_common` order, tie by first-encounter).
- Population is every specialist that fired at that layer in the decode
  window (independent of the observability tier's `--min-count` filter).
- `reap_saliency` is **reserved**: null until REAP gate x output-norm
  telemetry exists (design §9; the route-trace surface cannot compute it).

`experts.pin` — pin-file format `"L <il> <ids...>"` consumed by the in-tree
parsers (llama-context.cpp:2334-2363 `hydra_cpu_init`; identical parser in
ggml-cuda.cu:1939-1970 — llama-context site verified against source 2026-09-17).
Parser facts the exporter respects: `#`/blank lines skipped, `il` in
[0, 256), ids whitespace-separated after the second space, 8192-char line
buffer (`--wrap` caps ids/line; multiple `L <il>` lines concatenate).
`--top-n 53` matches the profile's `N=53 pins/layer` budget.

## Provenance discipline (#1078)

Both artifacts carry a mandatory provenance block: model (shard-1
filename), `engine_id` (owner-fixed `qwen38`), fork `commit` (from the
trace sidecars), `probe_set` (category list), `generated_at`, and a corpus
block recording decode-only windowing, the gate value used, and the
corpus-quality label. `expert-ranks.json` additionally keys `model_hash`
as the checkpoint-identity string (shard-1 filename; a full-weights digest
is not computable from traces — flagged, not faked). Exporters refuse
files whose `engine_id` is not `qwen38` (route_trace.h refusal discipline).

## Draft-grade vs final (owner ruling d-dfb6707bc7)

This corpus is **draft-grade**: 2 domains (coding, general), 1 prompt each.
Consequences, recorded in every artifact's provenance:

- `min-runs` replication gate is OFF (1 for pipeline validation): with one
  run per category the >=2-runs gate would drop every expert. Every
  `reliability` is therefore trivially `1/1` and `spec` numbers measure
  one prompt's routing, not the topic.
- LOP validation (`validate.py`) is degenerate on this corpus:
  leave-one-run-out would leave zero training runs for the held-out
  category, so it runs the hold-nothing variant and prints the caveat.
  Draft numbers 2/2 HIT (chance 50%) are an in-sample pipeline check only
  — expected separation with 2 categories is weak; the point is pipeline
  correctness, not labels.
- Affinity labels (generalist/specialist via spec >= 0.5) are draft labels:
  1207/2431 atlas experts are "strong specialists" — an artifact of the
  2-category corpus, not a model property.

Final atlas requires the multi-domain multi-prompt probe corpus (design
§7.3, task-4e0493edd2 trace collection) with `--min-runs 2`, then re-run
this pipeline unchanged.
