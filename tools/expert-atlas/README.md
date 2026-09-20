# tools/expert-atlas — Turn-record schema and predictor definitions

Turn-level telemetry records for the expert-atlas pipeline. Staged here on
the parent epic branch (`epic/787-per-turn-telemetry`); will move to
`ddvnguyen/expert-metrics` once the repo is set up for CI.

## What this provides

| File | Purpose |
|---|---|
| `schema/turn-record.schema.json` | JSON Schema for per-turn telemetry records |
| `tools/validate_turns.py` | Validator — checks turn records against the schema |
| `models/<model>/artifacts/turn-NNNNNN.json` | Example turn records (telemetry-on and telemetry-off) |

## Turn-record schema (v1)

One turn-record captures a single turn's telemetry snapshot, sourced from
the fork's live endpoints:

| Endpoint | Field(s) | Description |
|---|---|---|
| `GET /profile` | `profile.wall_s`, `profile.forwards`, `profile.n_prompt_tokens` | Wall-clock timing + forward pass counts |
| `GET /turns` | `turn_seq` | Monotonic turn sequence number (resets on session restart) |
| `GET /experts?turn=N` | `experts_snapshot` | EMAP grid snapshot for turn N (null when telemetry off) |

Schema version is bumped to **1** for turn records (distinct from
`run-record.schema.json` v1 and `experts.schema.json` v2).

### Schema fields

```
turn-record.schema.json
├── schema_version: 1 (const)
├── turn_seq: integer ≥ 0
├── turn_id: ISO 8601 timestamp
├── profile
│   ├── wall_s: number ≥ 0
│   ├── forwards: integer ≥ 0
│   ├── n_prompt_tokens: integer ≥ 0
│   ├── n_gen_tokens: integer ≥ 0 (optional)
│   └── cache_hit_rate: 0..1 (optional)
├── experts_snapshot: object | null
│   ├── rows, cols: integer
│   ├── map: string[] (hex EMAP rows)
│   ├── hits_seq, hits_bitmap
│   └── tier_summary: {vram, ram, disk}
└── provenance
    ├── engine_id, model_hash, llama_cpp_commit
    ├── atlas_stage, session_id, host
    ├── recorded_at: ISO 8601
    └── telemetry_enabled: boolean
```

### Honest-state discipline

Turn records with `telemetry_enabled: false` are **valid and expected**.
The `experts_snapshot` field is `null` when telemetry is absent — never
zeros pretending to be data. This matches the run-record discipline from
the `expert-metrics` repo.

## Predictor definitions (from #786 STEP1 proposal)

Three predictor tiers are defined for the Edge0 prerouter predictability
family. These predict **which experts will be selected in the next turn's
decode** given the current turn's routing history.

### 1. Linear-probe (primary, #786 STEP1)

A lightweight logistic-regression probe that predicts per-(layer, expert)
selection probability from the current turn's routing features.

**Input features:**
- Per-(layer, expert): selection count from the current turn's EMAP
- Per-layer: total forward count, cache hit rate, n_prompt_tokens
- Global: turn_seq (positional encoding)

**Output:** Per-(layer, expert) probability of being in the next turn's
top-k set.

**Training:** Online logistic regression (sklearn LogisticRegression or
equivalent), fit on a sliding window of the last N turns (default N=16).
Warm-start each turn; evaluate top-k hit-rate on the next turn's EMAP.

**Hit-rate metric:** `predictability = fraction of next-turn top-k
experts that were predicted in the top-k by the linear probe.`

**Complexity:** O(n_layers × n_experts × window) per turn update.
Negligible — runs in the telemetry collector, not the engine.

### 2. LTR baseline (Learning-to-Rank, #786 STEP1)

A gradient-boosted decision tree (LightGBM RankNet-style) that ranks
experts by predicted selection probability.

**Input features:** Same as linear-probe.

**Output:** Per-layer ranked expert list (replaces the probability
vector with a rank order).

**Training:** Offline batch training on accumulated turn records.
Evaluate using NDCG@k and hit-rate@k against held-out turns.

**Why a baseline:** Linear-probe is online and fast; LTR captures
non-linear feature interactions that logistic regression misses. The
comparison validates whether the simpler model is sufficient.

**Complexity:** O(n_trees × n_experts × n_layers) per inference.
One inference per turn (batch); cost is dominated by tree traversal.

### 3. LRU-k follow-up (#786 STEP1)

A per-layer LRU cache of the last k distinct expert selections, used as
a "recently active" predictor.

**Input:** Per-layer ordered list of recently selected experts.

**Output:** The k most recent experts per layer as the predicted next
turn's top-k set.

**Hit-rate metric:** Same as linear-probe — fraction of next-turn
top-k experts present in the LRU-k set.

**Why follow-up:** LRU-k has zero training cost and captures the
strong temporal locality of MoE routing (recent experts are likely to
fire again). It serves as a non-parametric lower bound — if a trained
model cannot beat LRU-k, the training overhead is not justified.

**Complexity:** O(k) per layer per turn. Essentially free.

### Predictor comparison protocol

| Metric | Definition | Target |
|---|---|---|
| `predictability` | top-k hit-rate of the predictor on the next turn's EMAP | > 0.5 (better than random) |
| `prefetch_gain` | estimated fraction of expert-weight bytes avoidable per layer under the predictor's hits | Measured, not targeted |
| Coverage | fraction of turns where the predictor ran | 1.0 (no gaps) |

The predictor with the highest `predictability` becomes the primary
Edge0 metric. The others are recorded for ablation studies.

## Usage

```sh
# Validate a single turn record
python3 tools/expert-atlas/tools/validate_turns.py \
    tools/expert-atlas/models/qwen35b-a3b/artifacts/turn-000000.json

# Validate all turn records in a directory
python3 tools/expert-atlas/tools/validate_turns.py \
    --dir tools/expert-atlas/models/qwen35b-a3b/artifacts/
```

## Future: move to ddvnguyen/expert-metrics

These files will migrate to the `expert-metrics` repo once it's set up
for CI. The schema will be referenced by the validator in that repo's
`tools/` directory. Until then, they live here on the epic branch for
development and review.

## Provenance

- Issue: [#787](https://github.com/ddvnguyen/hydra_vortex/issues/787) sub-task 4
- Predictor definitions: [#786 STEP1](https://github.com/ddvnguyen/hydra_vortex/issues/786#issuecomment-5747874824)
- Parent epic branch: `epic/787-per-turn-telemetry`
- Commit prefix: `#787.5`
