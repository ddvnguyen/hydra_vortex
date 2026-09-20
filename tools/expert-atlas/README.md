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
| `GET /profile` | `profile.wall_s`, `profile.forwards`, `profile.prompt_tokens`, `profile.completion_tokens`, `profile.slot`, `profile.ts` + 5 phase fields | Wall-clock timing + token counts + slot + honest-zero phase timings |
| `GET /turns` | `turn_seq` | Monotonic turn sequence number (resets on session restart) |
| `GET /experts?turn=N` | `experts_snapshot` | EMAP grid snapshot for turn N (null when telemetry off) |

The `profile` object mirrors the fork's `/profile` ProfileTurn object
verbatim (`src/llama-cpp/tools/server/server-atlas.cpp`, `profile_json`):
`wall_s`, `forwards`, `prompt_tokens`, `completion_tokens`, `slot`,
`ts`, plus phase fields `expert_disk_s`, `expert_wait_s`,
`expert_matmul_s`, `attention_s`, `lm_head_s` (live fork emits honest
`0.0` until phase capture lands). The fork does not emit
`cache_hit_rate` — it is not part of this schema.

Schema version is bumped to **1** for turn records (distinct from
`run-record.schema.json` v1 and `experts.schema.json` v2).

### Schema fields

```
turn-record.schema.json
├── schema_version: 1 (const)
├── turn_seq: integer ≥ 0
├── turn_id: ISO 8601 timestamp
├── profile (mirrors fork /profile ProfileTurn verbatim)
│   ├── wall_s: number ≥ 0
│   ├── forwards: integer ≥ 0
│   ├── prompt_tokens: integer ≥ 0
│   ├── completion_tokens: integer ≥ 0
│   ├── slot: integer ≥ 0
│   ├── ts: integer ≥ 0 (unix epoch)
│   ├── expert_disk_s, expert_wait_s, expert_matmul_s: number ≥ 0 (honest 0.0)
│   └── attention_s, lm_head_s: number ≥ 0 (honest 0.0)
├── experts_snapshot: object | null
│   ├── rows, cols: integer
│   ├── map: string[] (hex EMAP rows)
│   ├── hits_seq, hits_bitmap
│   └── tier_summary: {vram, ram, disk}
├── provenance
    ├── engine_id, model_hash, llama_cpp_commit
    ├── atlas_stage, session_id, host
    ├── recorded_at: ISO 8601
    └── telemetry_enabled: boolean (REQUIRED — telemetry-off records carry false explicitly)
```

### Honest-state discipline

Turn records with `telemetry_enabled: false` are **valid and expected**.
The `experts_snapshot` field is `null` when telemetry is absent — never
zeros pretending to be data. This matches the run-record discipline from
the `expert-metrics` repo.

## Predictor definitions (from #786 STEP1 proposal)

Verbatim from #786 STEP1 §4 — do not paraphrase. Predictor change = new schema_version.

`predictability[layer]` = top-k hit-rate @k of a per-layer linear probe mapping the router-input hidden state to the top-k expert set, measured on HELD-OUT tokens of the probe corpus (train/eval split documented in provenance, e.g. leave-one-prompt-out per category). `prefetch_gain[layer]` = (sum over hit tokens of hit experts' weight bytes) / (sum over all routed experts' weight bytes) under that probe's hits. Comparison baseline column: `predictability_ltr[layer]` = last-token-routing-repeat hit-rate @k computed capture-free from the Stage-A topk stream (fraction of tokens whose topk set is subset of previous token's topk set, per layer). Follow-up column: per-layer LRU-k (k=4).

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
