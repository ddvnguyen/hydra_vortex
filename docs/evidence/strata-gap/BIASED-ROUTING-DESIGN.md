# Biased (cache-aware) expert selection in the fork — design spike

- **Date**: 2026-10-03 · **Branch**: `docs/811-biased-routing-design` → `epic/811-strata-gap-analysis`
- **Type**: DESIGN SPIKE (read-only; no code changes, no builds, no GPU runs)
- **Question**: what is the cheapest way to test training-free, per-layer **selection**
  bias for expert routing on the Flash-Next `qwen4exp` model in the llama.cpp fork —
  i.e. bias *which* experts are selected while leaving the gate **weights unchanged** —
  and does it buy cache hit-rate without paying a quality tax?
- **Status**: proposed, not executed. Everything below that was read off the tree is
  cited `path:line`; anything not read or not run is marked `[hypothesis]`.

Fork citations below are relative to the export tree `.local/q2g/src/`
(a plain export with **no `.git`**; `VERSION` = `0.2.0`).

**Reference-path provenance.** This branch is cut from `epic/811-strata-gap-analysis`,
which at the time of writing does not yet carry the sibling evidence trees this document
cites: `docs/evidence/strata-gap/**` (incl. `RANKING-POC-2.md`, `PROFILE-DECODE.md`,
`prompts/`) lives on `docs/811-ranking-poc-2` (+ `docs/811-nsys-decode`), and
`docs/evidence/model-layer-info/**` on `docs/811-ctx63k-layerinfo`. Paths are written as
they will read once those land on the epic; every line number below was read from the file
as it exists today.

---

## 0. Constraints honoured by this document

| Constraint | How |
|---|---|
| Read-only spike | No source file was modified; the patch in §3 is a **sketch only** |
| `k` stays 10, no `--override-kv` | This experiment never changes `expert_used_count` (10) and never uses `--override-kv` — it biases *which* of the 512 experts compete for the same 10 slots |
| No builds / no GPU use | Build path in §8 is **proposed**, not run |
| CI/CD-only builds | §8 is built from `docs/workflow/05-deploy.md` + the fork workflows, both read |
| Never `/tmp` | Proposed logs/artifacts go under `.local/biasdesign/` (worktree, git-ignored) — §8 |
| Gate weights unchanged | Mechanism chosen (`exp_probs_b`, §2) touches only the tensor used for **ranking**; weights are read from the unbiased `probs` |

---

## 1. The four open questions, answered

### (1) Does `qwen4exp` already provide an `ffn_exp_probs_b` tensor?

**No — the hook exists, the tensor does not.**

- The graph accepts a selection-bias tensor: `build_moe_ffn(..., ggml_tensor * exp_probs_b, ...)`
  (wrapper `src/llama-graph.cpp:2076`, param `:2082`; impl overload `:2120`, param `:2130`).
- The model hard-codes it to null: `llama_model_qwen4exp::graph::build_layer_ffn` passes
  `nullptr` in the `exp_probs_b` position (`src/models/qwen4exp.cpp:1211-1220`, the `nullptr`
  at `:1216`), with `norm_w = true` (`:1218`) and gating `LLAMA_EXPERT_GATING_FUNC_TYPE_SOFTMAX`
  (`:1220`).
- The loader never creates such a tensor: `load_tensors` binds `ffn_gate_inp`, `ffn_down_exps`
  and the fused `gate_up_exps` only (`src/models/qwen4exp.cpp:262-264`); there is no
  `ffn_exp_probs_b` key, so nothing can ever satisfy the parameter.
- Consequence: `if (exp_probs_b != nullptr)` (`src/llama-graph.cpp:2193`) is **always false**
  for this arch — the bias path is dormant, not broken.

**Answer:** nothing to *enable*; something to *supply*. That is why §3 injects the tensor at
graph-build time rather than in the GGUF.

### (2) How is the bias tensor allocated?

Three options were priced; **A is the recommendation**.

| # | Option | Diff size | Why not / why yes |
|---|---|---|---|
| **A** | **Graph input tensor**, created in `build_moe_ffn` when `HYDRA_MOE_SELECT_BIAS` is set, host data uploaded in `llm_graph_result::set_inputs` | ~40 lines, 2 files | No GGUF change, no re-shard, dynamic per-step updates for free, CUDA-graph safe (same mechanism as every other graph input), inert when env unset |
| B | Real GGUF tensor named `blk.N.ffn_exp_probs_b` | data + re-shard + 6-shard sync | `llama_model_loader::create_tensor` requires the tensor to exist in the GGUF; missing + `TENSOR_NOT_REQUIRED` → `nullptr`, otherwise it throws `missing tensor` (`src/llama-model-loader.cpp:1171`, missing-tensor branch `:1211-1216`). Correct but expensive and model-file-coupled |
| C | Logit-space bias via the `gate_inp_b` hook (`src/llama-graph.cpp:2162-2165`) | small | This biases the **logits before softmax**, so `probs = softmax(logits + b)` (`:2176-2178`) and therefore the **weights change too** — it fails the "weights unchanged" requirement. It is the faithful Cache-Prior (§10) variant; keep as an explicitly-labelled later comparison arm, not the default |

**Why A is cheap:** the bias is only `n_layer × n_expert × 4 = 48 × 512 × 4 = 98,304 B`
per eval upload (~µs over PCIe, once per `llama_decode`), identical in kind to the existing
`inp_pos` / embedding uploads that run in `llm_graph_result::set_inputs`
(`src/llama-graph.cpp:1433-1441`).

### (3) How does this interact with the expert cache / `--n-cpu-moe`?

- **Placement is unaffected.** `--n-cpu-moe` (`common/arg.cpp:2855`) places *whole layers'*
  expert weight tensors by name regex `LLM_FFN_EXPS_REGEX`
  (`common/common.h:1158`, applied at `common/arg.cpp:2861`); the bias is a graph **input**,
  not a model tensor, so it never matches any buffer-type override and is never "placed".
- **The cache is keyed on expert ids, not on scores.** `--moe-expert-cache-size` (`common/arg.cpp:2876`,
  help `:2879`: *"When enabled, all MoE expert tensors use the cache regardless of --cpu-moe or
  --n-cpu-moe"*) drives demand admission keyed by the selected expert
  (`docs/design-moe-demand-admission-phase15.md` §1: *"miss = unique expert not in slot table"*).
  Changing *which* ids are selected changes misses/evictions/admission only; the id ↔ weights
  mapping is untouched, so **correctness is preserved**.
- **Name does not collide with the cache.** The bias tensor is created fresh per graph build
  and never goes through `create_tensor`, so it cannot be captured by the cache's tensor-buft
  override path: when the cache is on the fork *prepends* an override for the pattern
  `\.ffn_(up|down|gate|gate_up)_(ch|)exps` (`src/llama.cpp:320-363`, pattern inline at
  `:341-344`, “regardless of --cpu-moe / --n-cpu-moe” warning `:359-361`).
- **Measure the real payoff from the engine's own telemetry.** Cumulative
  `moe-cache: %zu caches hits=… misses=… evictions=… hit-rate=…%` is logged from
  `ggml/src/ggml-cuda/moe-cache.cu:14775`; the server prints it at the end of every request
  when the cache is on (`tools/server/server-context.cpp:804-805`) and once more at
  `llama_model_free` (`src/llama-model.cpp:2865-2874`).
- **Open item:** whether our rig currently runs with `--moe-expert-cache-size` at all is
  **not established** — the `rank-p0` arm config used is the *Strata* binary
  (`exe: …/build-sm86/strata`, `--expert-cache auto`), and the fork has **no** `--expert-profile`
  equivalent (grep of `common/arg.cpp` finds only `--moe-expert-cache-size` /
  `--moe-expert-cache-l2-pinned-mb`). `docs/design-prefill-fastpath-DRAFT.md` also notes the
  cached-MoE machinery lives on the `feat/763-reconcile-qwen4exp-mtp` lineage.
  `[verify in the run configs before Phase 3]`

### (4) What exactly does the bias perturb — scores or logits?

**Scores, after softmax.** Selection runs `ggml_argsort_top_k(ctx0, selection_probs, n_expert_used)`
(`src/llama-graph.cpp:2237-2241`), and `ggml_argsort_top_k` is a **descending** `ggml_argsort`
over the tensor it is given (`ggml/src/ggml.c:5441-5452`), not over logits. With the bias hook:

```
probs           = softmax(router logits)          // src/llama-graph.cpp:2176-2178
selection_probs = probs + exp_probs_b             // :2193-2195   ← biased (ranking only)
topk            = argsort_desc(selection_probs)[:10]   // :2237-2241
weights         = get_rows(probs, topk)           // :2266        ← UNBIASED (values used)
```

So this is DeepSeek-V3-style `expert_bias` semantics, and it satisfies the requirement exactly:
**the ranking is biased, the gate weights are not** (`weights` comes from the unbiased `probs`;
`norm_w` then renormalises the selected weights as it does today).

**Scale of the grid.** Softmax over 512 experts sums to 1, so the *mean* expert probability is
`1/512 = 0.001953`. The bias `b` lives in **probability units**, not logit units — Cache-Prior's
`λ·Δ_avg` (logit units, §10) is not transferable numerically. Anchors for the grid:
`b = 0.002` ≈ one mean expert, `b = 0.01` ≈ 5×, `b = 0.05` ≈ 25× (near-forced selection →
stress arm). Where the top-10 probability floor actually sits for this model is
`[hypothesis]` — hence the Phase 1 calibration run in §7.

---

## 2. Mechanism summary (one paragraph)

The fork already implements "bias the selection, not the weights": `exp_probs_b` is added to
the softmax probabilities to form `selection_probs`, `topk` is arg-sorted from
`selection_probs`, and the weights actually multiplied into the expert outputs are read from
the *unbiased* `probs` (`src/llama-graph.cpp:2193-2195` → `:2237-2241` → `:2266`). The route
tracer already stashes the selected ids as `res->t_moe_topk[il]` (`:2243-2245`, vector declared
`src/llama-graph.h:997`, initialised `src/llama-graph.cpp:1390-1391`) and marks them as graph
outputs only when `HYDRA_TRACE_ROUTES|HYDRA_PIN_FILE|HYDRA_EXPERT_STATS|HYDRA_EAN_STATS` is set
(`:1471-1475`), so measurement needs no new graph plumbing. All that is missing is the bias
*data* and a way to hand it to the graph — §3.

---

## 3. Minimal patch sketch (NOT applied)

Sketch only — no line below is in the tree. Two files, ~40 lines, process-global env gate.

```diff
--- a/src/llama-graph.h
+++ b/src/llama-graph.h
@@ struct llm_graph_result
     std::vector<ggml_tensor *> t_moe_topk;
+    // hydra: per-layer selection-bias inputs (HYDRA_MOE_SELECT_BIAS); null unless set
+    std::vector<ggml_tensor *> t_moe_select_bias;
```

```diff
--- a/src/llama-graph.cpp
+++ b/src/llama-graph.cpp
@@ llm_graph_result::init  (near t_moe_topk.resize, :1390-1391)
     t_moe_topk.resize(LLAMA_MAX_LAYERS + 1);
     std::fill(t_moe_topk.begin(), t_moe_topk.end(), nullptr);
+    t_moe_select_bias.resize(LLAMA_MAX_LAYERS + 1);
+    std::fill(t_moe_select_bias.begin(), t_moe_select_bias.end(), nullptr);

@@ llm_graph_context::build_moe_ffn (impl)   // at :2191, replacing the exp_probs_b check
-    if (exp_probs_b != nullptr) {
-        selection_probs = ggml_add(ctx0, probs, exp_probs_b);
-        cb(selection_probs, "ffn_moe_probs_biased", il);
-    }
+    const char * bias_path = getenv("HYDRA_MOE_SELECT_BIAS");
+    ggml_tensor * bias = exp_probs_b;
+    if (bias == nullptr && bias_path && *bias_path && il >= 0) {
+        // archs that ship a real exp_probs_b are never overridden
+        hydra_select_bias_load_once(hparams);          // validates size, fails LOUD on mismatch
+        bias = ggml_new_tensor_1d(ctx0, GGML_TYPE_F32, n_expert);
+        ggml_set_input(bias);                          // graph input -> uploaded every eval
+        if (res && (size_t) il < res->t_moe_select_bias.size()) {
+            res->t_moe_select_bias[il] = bias;
+        }
+    }
+    if (bias != nullptr) {
+        selection_probs = ggml_add(ctx0, probs, bias);
+        cb(selection_probs, "ffn_moe_probs_biased", il);
+    }

@@ llm_graph_result::set_inputs             // after the existing input loop, :1433-1441
+    const char * bias_path = getenv("HYDRA_MOE_SELECT_BIAS");
+    if (bias_path && *bias_path) {
+        const std::vector<float> & data = hydra_select_bias_host();  // load-once
+        for (size_t il = 0; il < t_moe_select_bias.size(); ++il) {
+            auto * t = t_moe_select_bias[il];
+            if (t == nullptr) continue;
+            const size_t off = il * (size_t) t->ne[0];
+            GGML_ASSERT(off + t->ne[0] <= data.size());   // size checked at load too
+            ggml_backend_tensor_set(t, data.data() + off, 0, t->ne[0] * sizeof(float));
+        }
+    }
```

Supporting helper (same file, sketch):

```cpp
// HYDRA_MOE_SELECT_BIAS=<path> : little-endian float32, n_layer rows of n_expert,
// laid out [il][expert]. Loaded once; any size mismatch -> GGML_LOG_ERROR + abort
// (never silently ignored). File of all zeros == arm "b=0 plumbing control".
static const std::vector<float> & hydra_select_bias_host();   // load-once, cached
static void hydra_select_bias_load_once(const llama_hparams & hparams);  // size validation
```

**Properties this sketch relies on (all `[hypothesis]` until implemented):**

1. `ggml_new_tensor_1d` + `ggml_set_input` inside graph build is the same input-tensor
   contract every `llm_graph_input_*` class uses; `set_inputs` runs before each
   `ggml_backend_graph_compute`, so the upload happens once per eval. Under
   `GGML_CUDA_GRAPHS=ON` the captured graph reads a stable address — the values are rewritten
   before the next run, exactly like `inp_pos`.
2. Graph structure changes only when the env var is present (fixed per process), so the
   graph cache/reuse keys stay coherent; **toggling the bias per request is not supported**
   (restart the server per arm instead).
3. Cost: 98,304 B/eval upload, one `ggml_add` of `[n_expert, n_tokens]` per MoE layer —
   one extra elementwise add per layer per decode step.

**Explicitly out of scope for the patch:** `k` (stays `expert_used_count = 10`),
`--override-kv`, expert weight tensors, GGUF files, any KV/GPU-cache format.

---

## 4. Measurement plan (two instruments, both already in the tree)

### 4.1 Hit fraction and routing change — zero new engine code

The engine already exports per-token routes:

- `HYDRA_EXPERT_STATS=1` turns on the Stage-A counters; `HYDRA_EXPERT_CAPTURE=1` additionally
  records router-input hidden state + **topk per layer per token**, flushed per probe by
  `POST /capture/flush?cat=&idx=` (`tools/server/server.cpp:396`) to a sidecar JSON
  `{"layers": {"0": {"hidden": [...], "topk": [[ids], ...]}}}`
  (`tools/expert-atlas/README.md:62-87`).
- Driver: `tools/expert-atlas/sweep.sh` (greedy confounds — `temp 0`, `top-p 1.0`, no MTP/spec,
  decode-only — documented in its header; `NGEN` defaults to 64) with `HYDRA_EXPERT_META=1`
  for `GET /experts` (`tools/server/server.cpp:309`).
- Per-token ids are also reachable programmatically via `llama_get_moe_topk` /
  `llama_get_moe_topk_nrows` (`include/llama.h:1077,1082`); read-out is decode-only by design
  (`src/llama-context.cpp:2268`).

**Metric definitions**

| # | Metric | Definition | Caveat |
|---|---|---|---|
| (i) | hit fraction in X | `Σ_{token,layer} |topk ∩ X| / (N_tokens × 48 × 10)` over the sidecar stream | decode-only; prompt/prefill routing is not captured |
| (ii) | routing change | fraction of `(token, layer)` pairs whose top-10 **set** differs from the b=0 baseline | greedy arms diverge in text; compare only over the **common token prefix** (up to first generated-token divergence) and report the divergence point itself as a quality signal |
| (iii) | teacher-forced KL | mean KLD of biased vs unbiased logits, same tokens, from `llama-perplexity` | token streams identical by construction — the only metric that is not confounded by divergence |

Cross-check: the Stage-A counters (`g_counts`, indexed `gridRow*cols+expert`,
`tools/server/server-atlas.cpp:232-235`, exposed via `GET /turns/:seq` routing slice,
`tools/server/server.cpp:349-376`) give per-expert per-layer totals →
`Σ_{e∈X} count / Σ count` reproduces metric (i) aggregated over tokens, without sidecars.

### 4.2 Teacher-forced KL — existing flow, never PPL

- Flow: `common/arg.cpp:2584-2591` (`--kl-divergence`, `--kl-divergence-base/--save-all-logits`),
  implementation `tools/perplexity/perplexity.cpp` (`kl_divergence_result` `:175`,
  `log_softmax` + KLD `:191`, reported as `Mean    KLD: …` at `:1949`).
- Ready-made driver: `scripts/hydra-kl-gate.sh` (disarmed dump `:30-32`, armed compare `:40-41`,
  greps `Mean    KLD` `:42`, threshold compare `:45`). **Two caveats it must not inherit:**
  it hardcodes logs into `/tmp/opencode/` (`:31,:40,:42`) — forbidden here, redirect to
  `.local/biasdesign/logs/`; and it exports `HYDRA_PIN_FILE` (`:39`), which is a *different*
  selection mechanism — our driver must leave pin state unset.
- **Noise floor first.** Run dump + compare with the bias **unset** and read the floor before
  judging any arm. Precedent: E1 measured a calibrated floor ≈ `4e-4` (median `0.000358`) and
  set the gate at `0.01` (`docs/analysis/e1-postmortem.md:189,236-238`). Our config's floor is
  a fresh measurement `[hypothesis]`.
- **Why the zero-file control matters:** E1 has a recorded case where *env state alone*
  moved KLD to `0.0329` against a `4e-4` floor
  (`docs/analysis/e1-postmortem.md:149`). A bias file of all zeros must read at the floor;
  if it does not, the injection path itself perturbs computation and the experiment stops.
- Corpus: primary `scripts/eval/wikitext-2-raw/wiki.test.raw` (precedent + matches Cache-Prior's
  WikiText methodology, §10); secondary = the 19 held-out/training prompts concatenated
  (13,973 words ≈ `~18.6K` tokens `[estimate]`), ctx 8192.

---

## 5. Experiment arms

**Set X** (the set we want routing to favour) — 4 arms, all 256-of-512 per layer:

| X | Source | How it is parsed | Alignment risk |
|---|---|---|---|
| (a) RCO kept set | `.local/coder-rco/rco-allocation.txt` | Section 2 lines `blk.<il>.ffn_*_exps.weight[<idx>]: <type-or-empty>`; **kept = non-empty** (`# Experts: 256 of 512 retained per layer, 48 layers`, file header `:8`) | `[hypothesis]` expert indices only transfer if the RCO pruning was applied to the *same* base model as ours; also verify `ffn_gate/up/down_exps` kept sets agree (they should — one-off check) |
| (b) shipped Strata profile | `/mnt/WorkDisk/strata/src-v0.1.29/data/expert-profile.bin` | `STRP` magic, header `struct.pack("<5I", magic, ver=1, 48, 512, n, n)` with `n = 24576` `(layer, expert)` `<HH>` pairs in **global rank order**, then a `48×512 i32` table (`tools/make_profile.py`); top-256/layer = first 256 occurrences of that layer | none (same model) |
| (c) trace ranking | `.local/ranking2/A.bin` | same `STRP` layout (verified header: `ver=1, 48, 512, 24576, 24576`) | built from a different prompt set; that is the point |
| (d) random control | seeded generator, `seed=42` | 256/layer, generated once and committed with the results | none |

**Magnitude grid** `b` ∈ {0.002, 0.01, 0.05} (§1 rationale), replaced by Phase-1 calibration
values if the response knee sits elsewhere.

| Phase | Arms | Purpose | Runtime / arm `[estimate]` | Count | Total |
|---|---|---|---|---|---|
| **P0 plumbing** | b=0 (env unset) vs b=0 (all-zero file) | prove the hook is inert at zero: KL must read ≤ 3× floor; routes must be identical | capture 4 min + 2× KL 2.5 min | 1 + 2 KL | **~9 min** |
| **P1 calibration** | X=(b), 5 magnitudes {0.001,0.003,0.01,0.03,0.1}, 1 prompt | find the knee of hit-fraction vs b before committing the grid | ~1.5 min (1 prompt) | 5 | **~8 min** |
| **P2 grid** | X ∈ {a,b,c,d} × b ∈ {calibrated 3} | metrics (i) (ii) (iii) per arm | capture 4 min · KL 2.5 min | 12 + 12 KL | **~48 + 30 min** |
| **P3 cache payoff** *(optional)* | best 3 P2 arms with `--moe-expert-cache-size N` | read `moe-cache: hits/misses/evictions/hit-rate` (`moe-cache.cu:14775`) — the actual system metric | ~5 min | 3 | **~15 min** |
| | | | | **GPU total** | **≈ 2 h** |

Per-arm arithmetic (measured inputs):

| Component | Rate | Work | Time |
|---|---|---|---|
| model load | — | 6-shard GGUF | ~60 s `[hypothesis]` |
| prefill | 205.8–207.6 tok/s (measured: `PROF prefill 2029 tok / 9.86 s`, `PROFILE-DECODE.md`) | 19 prompts, ~18.6 K tokens | ~90 s |
| decode | 30.6–37.4 tok/s median (measured: `RANKING-POC-2.md` §6.1); 71–73 ms/tok in the PROFILE-DECODE arms (13.7–14 tok/s) → use 30 tok/s | 19 probes × `NGEN=64` = 1,216 tok | ~41 s |
| flush/IO | — | 19 sidecars (hidden + topk) | ~20 s `[hypothesis]` |

**Decision gates (pre-registered):**

- **Abort** if P0's zero-file control reads above 3× the measured floor — the instrument,
  not the hypothesis, is then in question (E1 precedent, §4.2).
- **GO to ship** iff, at some `(X, b)`: hit fraction in X improves **≥ +15 pp** over the
  unbiased baseline, **and** `Mean KLD ≤ 0.01` (or ≤ 3× the freshly measured floor,
  whichever is larger), **and** routing change (ii) ≤ 10 % of `(token, layer)` pairs,
  **and** (if P3 runs) `moe-cache` hit-rate improves.
- **NO-GO** if no magnitude reaches +10 pp without exceeding `0.01` KLD — i.e. the
  miss/quality coupling reported at small scale in arXiv 2608.18261 (§10) holds here too.
- A `+15 pp` hit-fraction gain on a *static* set X does **not** by itself prove a system win;
  P3 (or §4.1's counters against the live residency) is what converts it.

---

## 6. Build & binary path (proposed, CI-only — nothing was built)

Facts:

- The local tree has `build/bin/llama-server` only (built 2026-09-28 21:09; **0** source files
  under `src/ include/ tools/ common/ ggml/` are newer than it, so it is current for this
  export). `build/tools/perplexity/` holds CMake output but no binary — **`llama-perplexity`
  does not exist in this tree yet.** The target does exist: `tools/perplexity/CMakeLists.txt`
  defines `llama-perplexity`, and `build/CMakeCache.txt` has `LLAMA_BUILD_TOOLS:BOOL=ON`.
- Local builds are out (CI/CD-only). The sanctioned path is
  `docs/workflow/05-deploy.md:22-27`:
  `gh workflow run hydra-build.yml --repo ddvnguyen/llama.cpp --ref hydra-fork -f build_llama_server=true …`
  and the parent repo reuses that workflow from `.github/workflows/dev-test.yml:112`
  (`uses: ddvnguyen/llama.cpp/.github/workflows/hydra-build.yml@hydra-fork`).
- **Gap:** `hydra-build.yml` only resolves two checkboxes — `build_llama_engine` /
  `build_llama_server` (`.github/workflows/hydra-build.yml:11-15`, combos `:128-140`) — and
  `build-combo.sh` builds exactly one `--target $BINARY` (`:72`) after configuring with
  `-DLLAMA_BUILD_EXAMPLES=OFF` (`:35`; tools remain governed by `LLAMA_BUILD_TOOLS`), then
  packages/pushes an OCI image. There is no artifact upload and no perplexity combo today.

**Proposal (to be raised, not run):** add a third dispatch input `build_llama_perplexity`
to the fork's `hydra-build.yml` (or a minimal `hydra-tools.yml`) that runs
`build-combo.sh sm86-sm120 llama-perplexity …` with the image push bypassed and
`actions/upload-artifact` instead. That is a fork PR — §0 forbids touching trees now, so this
is recorded as the follow-up. Estimated CI wall time 20–40 min `[hypothesis]`.

The bias patch itself (§3) also lands as a fork PR on `hydra-fork`, behind the env var so the
default binary is bit-identical when `HYDRA_MOE_SELECT_BIAS` is unset.

---

## 7. Risks and failure modes

| # | Risk | Severity | Mitigation |
|---|---|---|---|
| R1 | Bias scale wrong → grid sees no effect or instant quality cliff | med | Phase 1 calibration (§5) before the grid |
| R2 | Instrument/env state shifts logits (E1 §5 defect, KLD 0.0329 vs floor 4e-4) | **high** | P0 zero-file control; abort rule in §5 |
| R3 | Greedy arms diverge → metric (ii) and (i) confounded by different token streams | med | compare on common prefix only; KL (iii) is teacher-forced and immune |
| R4 | CUDA-graph interaction with the new input tensor untested | med | mechanism is the standard graph-input one `[hypothesis]`; P0 capture run is the smoke test |
| R5 | Capture is decode-only → hit fraction says nothing about prefill residency | med | prefill quality covered by KL; note the gap, don't over-claim |
| R6 | RCO set (a) indices may not align across fine-tunes | med | treat (a) as the weakest prior; (b)/(c)/(d) carry the conclusion |
| R7 | Cache flags may not be enabled in the current rig | med | open item §1(3); P3 only runs if they are |
| R8 | Upload every eval (98 KB) even when the bias is static | low | negligible vs decode step; optimize only if measured |
| R9 | `llama-perplexity` unavailable → no (iii) | high for the gate | §6 CI proposal is on the critical path |

---

## 8. Prior art

| Work | What it establishes for us |
|---|---|
| **Skliar et al., “Mixture of Cache-Conditional Experts for Efficient Mobile Device Inference”**, TMLR 2025 / arXiv [2412.00099](https://arxiv.org/abs/2412.00099) — *Cache-Prior* | Training-free cache-aware routing: `z' = z + λ·Δ_avg·m̃` on the **router logits**, weights from the **unmodified** logits; `λ=0` ↔ baseline, `λ=1` ↔ fully cache-driven. Reported: miss rate 35 %→16 % (Qwen1.5-MoE), 28 %→7 % (DeepSeek-V2), 22 %→9 % (Phi-3.5-MoE); perplexity cost 0.1–3 %, downstream < 0.1 %, up to 2× on-device speedup; > 50 % miss reduction overall. Our §3 differs deliberately: bias in **probability** space (`exp_probs_b`) so weights are provably untouched — C in §1 is their exact variant if we ever want it |
| **arXiv [2608.18261](https://arxiv.org/abs/2608.18261)** — “Cacheable by Design? …” (pre-registered negative result) | The trade-off we must test for, not assume away: locality training at λ=0.05 cut misses **59–60 %** but cost **+2.1–3.1 % PPL**, failing the pre-registered **≤ 1 %** gate; λ=0.02 stayed at +0.3 % PPL but only 15 % miss reduction — *“no loss weight threads the joint bar at this scale”*, and the tax does not shrink at 340 M. It also reports **training-free cache-aware rerouting stacking with trained locality to ~80 % miss reduction**, which is the regime our experiment probes |
| **ReMoE**, arXiv [2605.27081](https://arxiv.org/abs/2605.27081) (ICML 2026) | The *training* alternative: router-only fine-tuning with a temporal-locality loss + Trust-KL anchor; +26–27 % expert reuse, +8.4 % throughput (vLLM offload), TPOT −43.6–49.8 % (llama.cpp/Jetson). Useful as the upper bound / as a “what if training-free fails” escape hatch |
| **SpecMD**, arXiv [2602.03921](https://arxiv.org/abs/2602.03921) | Cache-aware routing credited with 10–20 % of achieved speedups in their decomposition; also documents that “standard routing preserves original model behaviour with unmodified router logits” — i.e. biasing selection is an accepted fidelity trade |
| In-repo: `docs/evidence/strata-gap/RANKING-POC.md`, `RANKING-POC-2.md`, `docs/analysis/e1-postmortem.md`, `docs/design-moe-demand-admission-phase15.md` | Trace-ranked profiles did **not** beat shipped (`RANKING-POC-2` §6.3: A − S T1 = −11.00 pp, 36.7× the within-arm spread) — a *static profile* is weak; that is the motivation for testing a *biased router* instead, which can adapt per token. Also the KL floor/gate and the admission model we must not break |

---

## 9. Recommendation (go / no-go)

**GO — for a gated, cheap pilot, not for the full grid yet.**

1. Land the §3 patch as an env-gated fork PR (bit-identical when unset) + the §6 CI target for
   `llama-perplexity`.
2. Run **P0 (plumbing)**: zero-file control must read at the KL floor. Fail → stop, debug the
   instrument, do not collect data.
3. Run **P1 (calibration)**: one set X, five magnitudes → pick the 3 that bracket the knee.
4. Only then run **P2 (grid)** and, if the cache flags are live, **P3**.

Total ≈ 2 h of GPU plus one CI build; the failure mode that costs the most time (a biased
instrument, or a bias that silently reaches the weights) is explicitly caught in P0/P1 before
any grid data exists. The stop condition mirrors the published small-scale result
(arXiv 2608.18261): if hit-rate gains and KL cost are inseparable across the calibrated
grid, the answer for this model is **no-go** and the next lever is ReMoE-style router
fine-tuning, not a bigger bias.

---

## 10. Verification status

| Claim | Status |
|---|---|
| Bias hook exists, always null for `qwen4exp`; loader never creates the tensor | **verified** (`qwen4exp.cpp:1216`, `:262-264`, `llama-graph.cpp:2193`) |
| Selection = descending argsort of `probs + bias`; weights read from unbiased `probs` | **verified** (`llama-graph.cpp:2193-2195`, `:2237-2241`, `:2266`; `ggml.c:5441-5452`) |
| `gate_inp_b` would change the weights → not eligible | **verified** (`llama-graph.cpp:2162-2178`) |
| GGUF tensor injection is expensive/required-to-exist | **verified** (`llama-model-loader.cpp:1171`, `:1211-1216`) |
| Cache keyed on ids; telemetry at `moe-cache.cu:14775`; per-request print `server-context.cpp:804-805` | **verified** (source read; behaviour not run) |
| Capture sidecars carry per-token topk; sweep driver exists | **verified** (README `:62-87`, `sweep.sh`) |
| KL flow, `Mean KLD` line, E1 floor `4e-4` / gate `0.01` | **verified** (source + `e1-postmortem.md:189,236-238`) |
| CI-only build path, no perplexity combo today | **verified** (`05-deploy.md:22-27`, `hydra-build.yml:11-15`, `build-combo.sh:35,72`) |
| Prompt corpus sizes (13,973 words / 19 prompts) | **verified** (`wc -w` over `prompts/*.txt`) |
| Cost estimates (~4 min/capture arm, ~2 h total) | `[estimate]` from measured tok/s |
| CUDA-graph input-upload pattern works as sketched | `[hypothesis]` |
| Router probability distribution / knee location | `[hypothesis]` → Phase 1 |
| RCO index alignment across fine-tunes | `[hypothesis]` |
| Rig currently uses fork `--moe-expert-cache-size` | `[unverified]` → open item §1(3) |
| `llama-perplexity` build time in CI | `[hypothesis]` |
