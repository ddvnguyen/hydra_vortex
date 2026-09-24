# Colibri Expert Atlas on the llama.cpp fork — Design

- **Status:** Ruled (2026-09-17; owner decisions d-dfb6707bc7 + hosting/atlas rulings)
- **Track:** `baseline-flash-next` (t-d1cf43b723)
- **Branch:** `src/llama-cpp` @ `baseline-qwen4exp-mtp` (user-designated start point)

Browse-style observability of *what each MoE expert does* — Colibri's Brain page
(live tier/heat/hit cortex), Atlas labels (measured topic affinity), and the
#175 probe-measurement methodology — wired into the Hydra llama.cpp fork.
qwen4exp first; design must be arch-agnostic (all MoE models route through
`build_moe_ffn`, so the data path is already generic). Final destination:
fork becomes Hydra's LLM engine per `epic/697-final-verify` rules
(fork-isolated, upstream-mergeable, RPC/HTTP contract intact).

**Scoping note (do not relitigate D1):** decision d-2720fc65be dropped
*subject-keyed routing machinery* in the hot path (payoff +2-3% vs subsystem
cost). This workstream is **observability + artifacts first**: it changes no
routing decision, defaults off, and only *measures*. Whether the atlas later
feeds placement priors is a separate owner call, not part of this design.

## 2. What Colibri does (verified, repo paths)

| Layer | Colibri | Notes |
|---|---|---|
| UI | `web/src/Brain.tsx`, `App.tsx`, `lib/api.ts` — React+Vite SPA, tabs chat/brain/profiling | Brain polls `/experts` every 1.5s, `/health` every 5s |
| Gateway | `c/openai_server.py` — stdlib HTTP, serves `web/dist` static + endpoints | One process, one port |
| Wire | Engine stdout line protocol: `STAT/TIERS/EMAP/HITS/PROF/HWINFO` (`openai_server.py:2880-3024`) | `EMAP rows cols map`, `HITS seq bitmap-hex` |
| Brain encoding | Hex, 2 chars/expert, `rows×cols` grid: `tier=byte>>6` (0=disk/1=RAM/2=VRAM), `heat=byte&63`; hover lookup `"{realLayer}:{col}"` | GLM hardcodes `row+3` layer remap + MTP row — a port must derive from model config |
| Atlas artifact | `web/public/experts.json`: `{categories[], experts{"layer:expert":{affinity,entropy,top,label}}}` | Static, per-model, produced offline |
| Measurement | `c/tools/expert_atlas/`: `sweep.sh` (TOPP=0, MTP=0 DRAFT=0, `--temp 0`, per-probe `.coli_usage` reset) → `analyze.py` (mean share, spec=1−H/log C, replication gate ≥2/3 runs) → `validate.py` (leave-one-prompt-out 29/30=96.7%) | `--web` emits dashboard shape |
| Multi-model | `c/family_registry.py` (FAMILIES descriptors dispatch on `model_type`) + **one shared telemetry header `c/route_trace.h`** included by 7 engines; `.coli_usage` header carries FNV-1a `engine_id` — histories from another engine are refused | Per-model atlas; co-activation is (model, workload)-bound (#119) |

Key #175 measured results that discipline our port:
- Specialization is a function of weights (bit-exact within a kernel path); **identity** replicates across ISAs, **margins** don't (hard core 721 / soft margin 637) → ship `spec` + `reliability` fields, "weak" qualifier below spec≈0.7.
- **Decode-only** is the right measurement window (prefill routing is topic-generic; decode-only sharpened every affinity number).
- Confound traps: top-p pruning (TOPP=0), speculative drafts polluting counts (MTP/DRAFT=0), accumulating usage history (per-probe reset), autocorrelation (replication gate across prompts).

## 3. What the fork already has (reuse, do not invent)

| Capability | In-tree hook | File |
|---|---|---|
| Single source of expert ids | `res->t_moe_topk[il]` stash in `build_moe_ffn` + `ggml_set_output` promotion gated on `HYDRA_TRACE_ROUTES/HYDRA_PIN_FILE` | llama-graph.cpp:2127-2131, 1386-1396 |
| Route trace dump | `extract_moe_topk` — `HYDRA_TRACE_ROUTES` + `HYDRA_TRACE_OUT`, decode-only, per-step `L <il> <ids…>` | llama-context.cpp:2257-2307 (call :1970) |
| Sync-before-D2H read discipline | `ggml_backend_synchronize` → `tensor_get_async` → `sched_synchronize` | llama-context.cpp:2415-2421 |
| Pin-file format | `"L <il> <ids…>"`, identical parser in 3 places | llama-context.cpp:2334-2363, ggml-cuda.cu:1939-1970 |
| Hit/miss counters | CPU: `hydra_cpu_hit/miss(_l)`, atexit dump; GPU: `hydra_hit/miss_exp`, per-(layer,site) | llama-context.cpp:2320-2449, ggml-cuda.cu:1889-2140 |
| Server read surface | Binary RPC 0x30/0x31/0x32 on `--rpc-port`; HTTP debug `GET /slots/:id/state/meta` | server-rpc.h, server-context.cpp:5668-5897, server.cpp:307-328 |
| Fork-local pattern | env-gated no-op default, `hydra:`/`hydra#N` tagged comments, optional-source build guard (`hydra_rpc.cpp`), cross-repo issue/PR links | throughout |

Gap: no HTTP/RPC endpoint exposes expert routes or aggregate expert state —
that is the one missing surface, and it is the whole of Stage B below.

## 4. Design — five stages, each independently shippable

### Stage A — telemetry aggregation (engine, no UI)
- New env `HYDRA_EXPERT_STATS` (path, or `1` → default path): aggregate
  per-(layer, expert) `{count, heat, last_access}` over decode steps, plus
  per-turn hits bitmap, built from the same `t_moe_topk` decode extraction the
  route tracer already performs (single `ggml_set_output` gate extended with
  the new env name — 1 line in llama-graph.cpp:1386).
- Exit dump in Colibri's `.coli_usage`-compatible triples (`layer expert
  count`) so the existing analyze tooling consumes it unchanged, plus a
  per-probe **reset** affordance (delete/rename at startup) for the probe
  harness snapshot protocol.
- Decode-only gate inherited (prefill topk reads return stale data on the
  dual-GPU MoE dispatch — documented at llama-context.cpp:2378-2383).
- MTP/draft rows excluded from counts (conflated rejected drafts — #175
  trap 2); NextN row reported separately.

### Stage B — server surface (the missing read path)
- **Binary op `0x33 EXPERT_META`** on the existing `--rpc-port` listener
  (0x3x is the reserved llama-server state range): returns geometry
  `{rows, cols, dense_prefix, nextn_rows, engine_id, model_hash}` + the EMAP
  hex map + hits bitmap. Colibri's byte encoding (tier>>6, heat&63) adopted
  verbatim so a future Colibri front-end can consume it unmodified.
- **HTTP `GET /experts`** (debug/poll tier, like `/slots/:id/state/meta`):
  same payload as JSON. Heat derives from Stage A counters; tier bits report
  residency bucket we actually have today (all-VRAM / cpu-moe / pinned-store)
  — honest 0/2-bit semantics until hybrid tiering exists, documented.
- Env-gated; no-op when telemetry disabled.

### Stage C — atlas artifact (offline, per-model)
- Port `tools/expert_atlas/` (sweep.sh + analyze.py + validate.py) as
  `tools/expert-atlas/` in the fork, driving llama-server via HTTP:
  probes.json (start from Colibri's 10-category set), greedy `--temp 0`,
  MTP/draft off, per-probe stats reset, replication gate, decode-only split.
- **Owner ruling (2026-09-17): two tiers, two separated files.**
  1. **`experts.json` — observability tier**, Colibri dashboard shape
     `{categories[], experts{"layer:expert":{affinity, entropy, top, label}}}`
     plus `spec` and `reliability` per entry; keyed by the *real* layer index
     derived from hparams (dense prefix, NextN), not the grid row. Consumed
     only by the Brain tooltip.
  2. **`expert-ranks.json` — ranking tier**, designed for the future
     placement-prior consumer: per-layer ordered hot-first expert lists with
     per-subject scores, `engine_id` + model-hash keyed. **Contract with the
     engine's existing inputs:** a tiny exporter must map its entries into
     the in-tree pin-file format `"L <il> <ids…>"` (parser already present in
     3 places: llama-context.cpp:2334-2363, ggml-cuda.cu:1939-1970), so the
     ranking file is directly convertible to pin consult / GPU pin-store
     inputs without touching engine code.
- Provenance fields (model, engine_id, commit, probe set) mandatory in both
  files — #1078 lesson: current shipped GLM atlas has undocumented corpus
  provenance.

### Stage D — Brain page port, hosted by a separated service
- **Owner ruling (2026-09-17): UI lives in a separated service, not inside
  llama-server.** The fork ships only the read surfaces (Stage B endpoints +
  the two atlas files); a standalone small service (own repo dir under Hydra,
  e.g. `atlas-web/`) polls `GET /experts` + `GET /experts.json` on one or
  **many** engines and serves the Brain UI. Rationale:
  - Hydra runs multiple engines (5060 Ti, 3060, P100 VM) — one service
    aggregates all of them into a single cortex view; per-engine hosting
    cannot do that.
  - Fork upstream diff stays minimal (read-only endpoints only); the vendored
    React build never enters the engine binary.
  - Works standalone against the baseline rig (single engine) pre-Hydra.
- Port `web/src/Brain.tsx` + `lib/api.ts` (Brain + health poll only;
  profiling/chats out of scope) into that service.
- Replace GLM's hardcoded `row+3`/MTP-row remap with the per-model mapping
  served by the engine's geometry payload (Stage B `EXPERT_META`: dense
  prefix, MoE layer set, NextN rows) — the only place Colibri hardcodes a
  model disappears entirely; the UI derives the grid from the engine.
- Tooltip: `experts.json` lookup + specialist/generalist label, entropy,
  top-3 affinity; "weak" qualifier for spec<0.7; depth-heuristic fallback
  when no atlas artifact is present.

### Stage E — Hydra integration
- Hydra Head declares the engine with `--rpc-port` + `HYDRA_EXPERT_STATS`
  in node YAML (existing services schema); Core/Head get expert telemetry
  through the same listener ops they already speak (0x30-0x32 → 0x33).
- Atlas service (separated, owner-ruled) polls the same endpoints; Hydra Head
  gains no new compute path.
- No Store/session coupling (epic/697 constraint C-series): the atlas is
  side-artifact data, never on the request path.

## 5. Multi-model (all-MoE) support — the actual abstraction

Colibri needs one C file per family because each engine is its own binary.
The llama.cpp fork does **not**: every MoE arch already funnels router top-k
into `t_moe_topk` via `build_moe_ffn`. The only per-model inputs are hparams
and GGUF metadata, available at model load:

```
HydraExpertAtlasDescriptor {
  engine_id        // arch name + FNV-1a(model hash) — colibri route_trace.h discipline
  moe_layers[]     // real layer indices with routed experts
  n_routed_experts // grid cols
  dense_prefix     // hparams.first_k_dense_replace equivalent
  nextn_rows[]     // MTP/NextN layers (reported separately, never mixed into trunk counts)
  n_expert_used    // top-k
}
```

Counters, EMAP encoding, hits bitmaps, atlas schema, and the probe harness
are all model-agnostic. A new MoE model needs zero new engine code — it
inherits the surface by routing through `build_moe_ffn` (as qwen4exp already
does, qwen4exp.cpp:1232-1245). Atlas labels are per-model artifacts by
construction (engine-id refusal rule adopted from route_trace.h).

## 6. Upstream-merge accounting (epic/697 discipline)

| Location | Budget |
|---|---|
| llama-context.cpp/h (HYDRA section) | fork-local, env-gated |
| server-rpc.h (op 0x33 + struct) | fork-local file |
| server-context.cpp (op handler), server.cpp (HTTP route) | fork-local hunks, tagged |
| tools/expert-atlas/ | new fork-local dir (probe harness + exporters) |
| atlas-web/ service (UI) | parent-repo service, **not in the fork at all** |
| Flow | fork issue → fork PR (target `baseline-flash-next`) → submodule bump → parent PR |

## 7. Sequencing (re-ordered 2026-09-17: Colibri side first)

Gate 0 stands — fork-side stages (A/B/C engine bits) wait for the in-flight
Stage-1 dual-op store to land on `baseline-qwen4exp-mtp` (same files/env
namespace). But the **Colibri part starts first, entirely offline** — no fork,
no GPU, no collision:

1. **Atlas pipeline (parent repo `tools/atlas/`, offline)**: port Colibri's
   `analyze.py` / `validate.py` logic onto our existing trace corpus
   (`/tmp/opencode/cnre-phase0/traces/*.route`, format per
   `cnre-phase0/TRACE_SPEC.md`), reusing the track's `phase0_rank.py`
   machinery (acceptance: reproduces `phase0_rank.json`, d-bb2f0d3b29).
   Emits first draft `experts.json` + `expert-ranks.json` for qwen4exp;
   `reap_saliency` field reserved. Pin-file exporter (`"L <il> <ids...>"`)
   lands with it.
2. **atlas-web service scaffold** (Stage D): Brain port + mock adapter
   implementing the Stage B contract (geometry + EMAP + HITS + `/experts.json`)
   so the UI is visually verifiable before any engine endpoint exists.
3. When the in-flight multi-prompt route-trace collection (task-4e0493edd2)
   completes, its traces upgrade the draft atlas from 2-domain to real
   multi-domain coverage.
4. Stage-1 lands → fork stages A/B/C start on `baseline-qwen4exp-mtp`.

## 8. Risks

- **Per-step D2H cost**: decode-only extraction already env-gated; keep the
  new aggregation behind the same gate and measure its cost once (48×topk
  I32 per step is small, but the sync pattern must be respected exactly).
- **Tier-bits honesty**: our fork has no disk tier; EMAP tier semantics are
  provisional (documented mapping) until hybrid residency exists.
- **Probe-set bias**: labels only meaningful with the full confound protocol
  (replication gate, decode-only, greedy, no MTP); a bare histogram run must
  never be published as an atlas.

## 9. REAP — same ranking goal, removal vs RAM-tiering

**Source:** CerebrasResearch/reap (arXiv:2510.13999, ICLR 2026, Router-weighted
Expert Activation Pruning). Owner directive 2026-09-17: we rank experts by the
same idea but **never remove** — the lowest tier stays resident in RAM at
runtime instead of being deleted.

### What REAP actually does
- **Metric (paper §4 Eq. 9):** per MoE layer, per expert j:
  `S_j = mean over tokens where j is top-k-selected of ( g_j(x) · ‖f_j(x)‖₂ )`
  — router gate value × expert output L2 norm, averaged over only the tokens
  that select it. Despite the informal name "Router Experts with Attention
  Preservation", there is **no attention term** — "preservation" refers to
  keeping the router's independent modulation of expert outputs (their §3
  theorem: merging error grows with router-policy variance, pruning error
  does not).
- **Policy:** uniform per-layer budget (25%/50% evaluated), one-shot
  checkpoint surgery, no fine-tuning, works on quantized checkpoints. Optional
  super-expert preservation (99.5th-percentile max activation never pruned).
- **Evidence relevant to us:** on Qwen3-30B-A3B @50% removal, REAP keeps
  code-avg 0.557 vs baseline 0.6xx while **frequency-based ranking collapses
  to 0.470** (and Qwen3-Coder-480B frequency pruning → 0.011). Pure routing
  frequency is a *bad* removal-safety signal. REAP covers Qwen3 MoE first-class
  (`model_util.py:MODEL_ATTRS`).

### Two different rankings — do not conflate (same lesson as #175's complements ≠ substitutes)

| | REAP saliency S_j | Route-trace ranking (ours) |
|---|---|---|
| Measures | **Removal-safety**: contribution magnitude *when selected* (quality risk of dropping it) | **Placement-demand**: how hot the expert is on the actual workload |
| Source | Static calibration over a checkpoint (calibration-set-sensitive — their own §5.1) | Live HYDRA route traces / Stage A counters, workload-conditional |
| Failure if used alone for placement | An often-fired, low-saliency expert would wrongly land in the slow RAM tier | A never-fired expert occupies hot-tier slots while a cold-but-heavy expert thrashes |

**Consequence for the RAM cold tier (Stage C `expert-ranks.json`):** the file
carries **both** scores per (layer, expert) —
1. `heat` / frequency from route traces (placement demand), and
2. `reap_saliency` computed REAP-style (gate × activation norm; calibration
   set documented in provenance, per their domain-sensitivity finding).

Cold-tier membership rule (principle, final numbers owned by measurement):
`cold = low placement-demand AND low removal-safety` — the intersection, not
either alone. The REAP score is the workload-independent prior tier; the
route-trace heat is the runtime-conditional tier. Mapping to our engine:
"keep lowest tier at RAM at runtime" = cold-tier experts execute on CPU via
the existing hybrid cpu-moe split machinery; nothing is deleted.

### Instrumentation note
REAP's EAN term needs `‖f_j(x)‖₂` (expert output norm) which our current
surface does not capture (`t_moe_topk` gives ids; gates are available in the
selection weights; output norms are not). Options: (a) a small env-gated
reduction in the MoE FFN path that records per-expert output norms during
probe runs (fits the Stage A pattern, decode-only, default off), or (b) run
REAP's own observer on the HF checkpoint if available (their pipeline supports
Qwen3 MoE; our qwen4exp arch would need a MODEL_ATTRS entry — feasible, attr
remap only). Decide at Stage C implementation; the artifact schema reserves
the field either way.
