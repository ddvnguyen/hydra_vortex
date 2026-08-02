# RFC: Phase 2b — fork-side CONFIGURE handler extension (PR 2 of the Phase 2b stack)

| Field | Value |
|---|---|
| Status | Draft — design reference for the parallel C++ implementation |
| Phase | 2b — runtime reconfigure path for `llama-engine` |
| Parent tracker | ddvnguyen/hydra_vortex#397 |
| Fork tracker | ddvnguyen/llama.cpp#36 (Phase 2) + ddvnguyen/llama.cpp#40 (this PR) |
| Parent-side docs PR | ddvnguyen/hydra_vortex#406 (`d06d9df` — merged) |
| Parent-side code PRs | ddvnguyen/hydra_vortex#402 (Phase 2a, merged) — feeds the `EngineConfig` record this PR consumes |
| Target binary | `llama-engine` (the fork's `tools/server/server.cpp` wired in `#375` / `#383`) |
| Target opcode | `0x40` `CONFIGURE` |
| Author | Senior engineering (this document); C++ implementation by the parallel sub-agent |

---

## 1. Background

### 1.1 Current state

The current 0x40 `CONFIGURE` entry in `specs/rpc-protocol.md:157-161` is a 4-line stub. The actual handler in `src/llama-cpp/tools/server/server-context.cpp:2932-2966` is 34 lines and recognizes exactly one key:

```cpp
// server-context.cpp:2932-2966 (current)
case SERVER_TASK_TYPE_HYDRA_ENGINE_CONFIGURE:
{
    auto res = std::make_unique<server_task_result_hydra_engine>();
    res->id = task.id;
    res->op = HYDRA_OP_CONFIGURE;
    res->rpc_status = HYDRA_STATUS_OK;
    res->success = true;
    // hydra#334: "state_chunk_size" (bytes) tunes the STATE_GET
    // socket-stream chunk size (llama_io_write_socket) without a
    // rebuild. Unknown/absent keys are ignored — CONFIGURE is meant
    // to accept a superset of engine params over time.
    if (!task.hydra_action.config_json.empty()) {
        try {
            const json cfg = json::parse(task.hydra_action.config_json);
            if (ctx_tgt && cfg.contains("state_chunk_size")) {
                const size_t bytes = cfg.at("state_chunk_size").get<size_t>();
                llama_hydra_set_state_chunk_size(ctx_tgt, bytes);
                res->state_chunk_size_applied = (uint64_t)llama_hydra_get_state_chunk_size(ctx_tgt);
                SRV_INF(...);
            }
        } catch (const std::exception & e) {
            res->success = false;
            res->rpc_status = HYDRA_STATUS_ERROR;
            res->error = std::string("CONFIGURE: invalid config_json: ") + e.what();
            ...
        }
    }
    queue_results.send(std::move(res));
} break;
```

The C# side today (`src/core/Hydra.Core/Services/WorkerSchedulerService.cs:2842` — the `state_chunk_size` call at startup, and `HydraEngineClient.cs:113-115`) sends a `{"state_chunk_size": N}` payload. That call site must keep working unchanged.

The result struct (`server-task.h:679-735`) carries the legacy `state_chunk_size_applied` echo and a generic `success`/`error` pair. Nothing today reports `tier`, `params_applied`, or `deferred_keys`.

### 1.2 Why this is insufficient

The wire schema in `specs/rpc-protocol.md` (extended by `d06d9df`) commits the engine to a **tier model** with three rebuild-cost classes:

| Tier | Cost | Examples |
|---|---|---|
| **T1** | Cheap, in-place | sampling, `n_predict`, `seed`, `n_keep`, `antiprompt`, `state_chunk_size` |
| **T2** | Context rebuild | `n_ctx`, `cache_type_k`/`v`, RoPE/YARN knobs |
| **T3** | Model rebuild | `n_gpu_layers`, `split_mode`, `tensor_split`, `model.path`/`model.alias` |

The current single-key handler:
1. Has no concept of deferred keys — every key would either apply mid-decode (correctness bug for T2/T3) or be rejected.
2. Has no tier reporting — the Coordinator cannot tell T1 from T2 from T3 in the response.
3. Has no `params_applied` echo (only `state_chunk_size_applied`).
4. Has no plumbing for the T3 COMBINED-mode teardown/rebind that happens around `llama_model_load_from_file` (see §5).
5. Cannot refuse a malformed or out-of-range value with a clean `BAD_REQUEST` (0x05) without losing the partial-success semantics that T1 callers expect.

The Coordinator's Phase 2a work (`a82cf15`, `db68c2d` — `src/core/Hydra.Core/Models/EngineConfig.cs:19-58`) defines an `EngineConfig` record that is dead code at runtime without this fork-side handler: every field on it (`NGpuLayers`, `NCtx`, `CacheTypeK`, `SplitMode`, `TensorSplit`, `OverrideTensors`, `RpcServers`) needs a path through 0x40.

### 1.3 Cross-references

| Issue / PR | Role |
|---|---|
| ddvnguyen/llama.cpp#36 | v4 design handoff; Phase 2 umbrella |
| ddvnguyen/llama.cpp#40 | Fork-side issue for PRs 2+3 of the Phase 2b stack (this work) |
| ddvnguyen/hydra_vortex#397 | Parent tracker |
| ddvnguyen/hydra_vortex#402 | Phase 2a — `EngineConfig` + `ModelRegistry` are the input shapes this PR consumes |
| ddvnguyen/hydra_vortex#406 | Parent-side docs PR — wire schema (`d06d9df specs/rpc-protocol.md`) |
| llama-hydra#334 | The legacy `state_chunk_size` precedent — same response-echo pattern, same call site |
| llama-hydra#348 | The "always-on dual role" architecture the slot lifecycle sits on top of |
| llama-hydra#368 | The "bind on activation" precedent for `llama_hydra_rebind_combined_experts` — T3 must mirror this fail-open style |
| llama-hydra#383 T1 | The COMBINED-static layer-split mode; T3 has to preserve its invariants (see §5) |

---

## 2. Tier classification

### 2.1 Per-tier summary

| Tier | Rebuild cost | Engine primitive | Lock strategy |
|---|---|---|---|
| **T1** | Field-write on `llama_cparams` / `common_params_sampling` / per-slot fields. No rebuild, no graph invalidation. | `llama_hydra_set_state_chunk_size`, sampler re-init via `common_sampler_init`, in-place field write on `params_base` | `process_single_task` is single-threaded (`server-queue.cpp:139-180`), so no extra lock needed for the write. The values only matter on the *next* decode — the in-flight slot keeps its already-bound sampler. |
| **T2** | `llama_context` rebuild via `llama_new_context_with_model(model, cparams)` then `llama_free` on the old ctx. KV cache is destroyed. | `llama_free` → `llama_new_context_with_model` → rebind `ctx_tgt` / `ctx_dft` / `smpl` on every slot | `update_slots` slot-free check (`server-context.cpp:3712-3728`); `update_slots` is the only caller that may run while slots are idle but processing is in progress, so the deferred trigger fires from there (see §3, §4). |
| **T3** | Model reload via `impl->load_model(swapped_params)` (the path `server-context.cpp:3093` already uses for the per-PREFILL `model` swap). New `llama_model_t`, new `llama_context_t`, new sched. | `llama_model_load_from_file` / `llama_model_free` / `llama_new_context_with_model`. For COMBINED mode: also `llama_hydra_clear_combined_bindings` → optional `llama_hydra_preload_rpc_device` (layer-split) → `llama_hydra_load_combined_experts` or `llama_hydra_rebind_combined_experts` → `llama_hydra_set_expert_mode`. | Same slot-free check as T2, but the deferred window is also bounded by `HYDRA_COORD_PROFILE_SWITCH_DRAIN_TIMEOUT` (default 300s) — see §4.3. |

### 2.2 T1 keys (apply immediately)

All T1 keys are read from the JSON payload and applied in `process_single_task` before the response is sent. The legacy `{"state_chunk_size": N}` payload (sent once at startup by `WorkerSchedulerService.cs:2842`) is the degenerate T1 case.

| Key | Type | Field written | Notes |
|---|---|---|---|
| `state_chunk_size` | size_t (bytes) | `ctx_tgt->cparams.hydra_state_chunk_size` via `llama_hydra_set_state_chunk_size` | Clamped to `[64 KiB, 64 MiB]` by `llama_hydra_clamp_state_chunk_size` (`llama-context.cpp:801`); echo the post-clamp value in `params_applied` |
| `n_predict` | int32 | `params_base.n_predict` (per-engine default) | Per-request `n_predict` from `task_params::n_predict` overrides this on each request; this is the *default* for unspecified requests |
| `n_keep` | int32 | `params_base.n_keep` | Same precedence as `n_predict` |
| `seed` | uint32 | `params_base.sampling.seed` | Used by `common_sampler_init`; existing sampler instances pick it up on the next token, but the safe pattern is to re-init the sampler (see §3.3) |
| `antiprompt` | array\<string\> | `params_base.sampling.antiprompt` | Replaces (not appends) the array. Empty array clears |
| `sampling.temp` | float | `params_base.sampling.temp` | Direct field write |
| `sampling.top_p` | float | `params_base.sampling.top_p` | Direct field write |
| `sampling.top_k` | int32 | `params_base.sampling.top_k` | Direct field write |
| `sampling.min_p` | float | `params_base.sampling.min_p` | Direct field write |
| `sampling.penalty_repeat` | float | `params_base.sampling.penalty_repeat` | Direct field write |
| `sampling.xtc_*`, `sampling.mirostat*`, `sampling.dry_*` | per-field | matching `common_params_sampling` field | Field-name match — `cfg["sampling"]["xtc_threshold"]` → `params_base.sampling.xtc_threshold`. Unknown sub-keys silently ignored. |
| `sampling.grammar` | string (GBNF) | `params_base.sampling.grammar` | If non-empty, re-parse via `common_grammar_parse` before storing |
| `sampling.logit_bias` | array of `{token:int, bias:float}` | `params_base.sampling.logit_bias` | Replace the array. Validate each entry; reject the whole CONFIGURE on a malformed entry (see §7) |

The `sampling.*` set is large; the handler iterates the object with `for (auto it = cfg["sampling"].begin(); it != cfg["sampling"].end(); ++it)` and switches on `it.key()`. This makes the schema **forward-compat by construction** — any new sub-key added to `common_params_sampling` upstream is automatically a no-op (silently ignored) until we add a case for it.

**Lock:** none. `process_single_task` is serialized (`server-context.cpp:2180`, called only from `queue_tasks.on_new_task` at `server-context.cpp:1291-1292`). In-flight slots keep their already-bound sampler until they finish; the new defaults apply to the next token of in-flight slots and to all subsequent tasks. This is the same pattern the legacy `state_chunk_size` already uses (`llama-context.cpp:813-820` calls it "benign data race").

### 2.3 T2 keys (defer to next slot-free moment)

T2 keys are written into `pending_config` in the CONFIGURE handler but **not** applied until `update_slots` observes all slots idle. Applying T2 mid-decode is forbidden — `llama_n_ctx` is baked into the `ggml_context` of `llama_context`, and the KV cache layout depends on it. There is no in-place "shrink/grow the KV cache" API in llama.cpp at the time of this RFC.

| Key | Type | Field that requires rebuild | Notes |
|---|---|---|---|
| `n_ctx` | int32 | `llama_context::cparams.n_ctx` | Must be ≤ `llama_model_n_ctx_train(model_tgt)`. If larger, clamp + echo (deferred — the rebuild will fail otherwise) |
| `cache_type_k` | string (ggml_type name) | `llama_context::cparams.type_k` | e.g. `"f16"`, `"q8_0"`, `"q4_0"`. `ggml_parse_type` for parsing |
| `cache_type_v` | string (ggml_type name) | `llama_context::cparams.type_v` | Same parser |
| `rope_freq_base` | float | `llama_context::cparams.rope_freq_base` | RoPE base; baked into `ggml_context` |
| `rope_freq_scale` | float | `llama_context::cparams.rope_freq_scale` | RoPE scale |
| `rope_scaling_type` | enum string | `llama_context::cparams.rope_scaling_type` | `"none"`, `"linear"`, `"yarn"` |
| `yarn.*` | per-field | `llama_context::cparams.yarn_*` (`yarn_orig_ctx`, `yarn_ext_factor`, `yarn_attn_factor`, `yarn_beta_fast`, `yarn_beta_slow`) | Field-name match against the `common_params` keys already in use at `server-context.cpp`-CLI-parsing time |
| `attention_type` | enum string | `llama_context::cparams.attention_type` | `"causal"`, `"non-causal"` |

**Lock:** write to `pending_config` is fine without a lock (same reason as T1). The *apply* in `update_slots` holds no extra lock — the slot-free check (`server-context.cpp:3712-3728`) is the implicit barrier; we only apply when no slot is processing and no slot is mid-transfer.

**Apply sequence** (in `update_slots`, when `pending_config` non-empty and all slots idle):
1. Snapshot `pending_config` into a local, clear `pending_config` to empty (atomic-ish via the queue's task-serialization: another CONFIGURE arriving at this moment will just refill `pending_config`, applying on the *next* slot-free moment — see §6.2).
2. Call `llama_free(ctx_tgt)` (and `ctx_dft` if present).
3. Rebuild `llama_context` with the new cparams.
4. Re-init per-slot samplers (`common_sampler_init`) — the sampling fields in `pending_config` are already merged into `params_base.sampling` at this point (sampling was applied immediately at T1; this re-init is for the samplers themselves to pick up the new field set on the new `model_tgt`-bound context).
5. Re-issue pending prompts: there are none — by the slot-free contract, all slots are idle and have no `prompt.tokens` worth keeping. (If a slot is mid-decode when CONFIGURE arrives, it *finishes* before the slot-free moment; the prompt state at the moment of finish is the one we keep, not whatever was in flight at CONFIGURE time.)
6. Reset the drain timer (or stop it — see §4.3).

### 2.4 T3 keys (defer; may rebuild the model)

T3 is the most expensive class — it changes the `llama_model_t`, the device layout, or both. In COMBINED mode, the model rebuild implies a peer rebind cycle (§5).

| Key | Type | Field that requires rebuild | Notes |
|---|---|---|---|
| `n_gpu_layers` | int32 | `common_params::n_gpu_layers` (the `mparams.n_gpu_layers` actually consumed by `llama_model_load_from_file` via `common_model_params_to_llama`) | Must be ≥ 0; clamp to model's layer count |
| `n_cpu_moe` | int32 | `common_params::n_cpu_moe` | Qwen35MoE / DeepSeek-style partial CPU offload. May force a model reload even with same `n_gpu_layers` because tensor placement changes |
| `override_tensor` | string | `common_params::tensor_buft_overrides` | `--override-tensor`-style pattern. e.g. `"blk\\.(2[0-9])\\.ffn_.*_exps\\.weight=CPU"` (the `MoE` profile pattern). For COMBINED mode, this is the per-request knob the C# side now sets without restart. |
| `split_mode` | enum string | `common_params::split_mode` | `"none"`, `"layer"`, `"row"`. Layer mode is the #383-T1 layer-split path |
| `tensor_split` | array\<float\> | `common_params::tensor_split` | Per-device layer ratio for layer-split; e.g. `[21.0, 44.0]` for the 27B MTP profile. The format `r0/r1` in the spec is the CLI form; the wire form is the JSON array. |
| `model.path` | string | `common_params::model.path` | Absolute path to the GGUF. The C# side resolves `ModelAlias` → path via `ModelRegistry` before sending; the engine does not re-validate the alias |
| `model.alias` | string | `common_params::model_alias` (the alias that drives `model_name` derivation in `load_model`) | Optional; defaults to "preserving the current alias when not provided" |

**Lock:** same as T2. Apply at the next slot-free moment from `update_slots`.

**Apply sequence** (in `update_slots`, when `pending_config.t3_keys` non-empty and all slots idle):
1. Same drain/snapshot as T2.
2. **COMBINED-mode teardown** if the engine is in combined mode (see §5).
3. `impl->load_model(swapped_params)` — the same call site `server-context.cpp:3093` uses for the per-PREFILL `model` swap. Verified pattern: the call rebuilds `model_tgt`, `ctx_tgt`, `ctx_dft` (if any), `smpl`, `slots`, and `params_base`. Slots get a fresh empty `server_slot` array; any in-flight slot's decode is already done (slot-free contract) and has no `prompt` to preserve.
4. **COMBINED-mode re-attach** if the engine was in combined mode and the new `tensor_split`/peer is reachable (see §5).
5. Reset the drain timer.

---

## 3. State machine for the CONFIGURE handler

### 3.1 Sequence diagram (mermaid)

```mermaid
sequenceDiagram
    autonumber
    participant Coord as Coordinator (C# WorkerSchedulerService)
    participant Queue as queue_tasks<br/>(server-queue.cpp)
    participant PST as process_single_task<br/>(server-context.cpp:2180)
    participant Cfg as CONFIGURE handler<br/>(server-context.cpp:2932)
    participant Impl as server_context_impl
    participant Update as update_slots<br/>(server-context.cpp:3711)
    participant Model as llama_model / llama_context

    Coord->>Queue: 0x40 CONFIGURE + JSON payload
    Queue->>PST: dequeue task
    PST->>Cfg: dispatch(SERVER_TASK_TYPE_HYDRA_ENGINE_CONFIGURE)
    
    Cfg->>Cfg: Step 1: json::parse(payload)<br/>(catch → BAD_REQUEST, success=false)
    Cfg->>Cfg: Step 2: classify each key<br/>(T1 / T2 / T3 bucket)
    
    loop Step 3: T1 keys (apply immediately)
        Cfg->>Impl: write field on params_base / ctx_tgt
        Note over Impl: Sampling fields land in<br/>params_base.sampling; <br/>state_chunk_size lands in<br/>ctx_tgt->cparams via<br/>llama_hydra_set_state_chunk_size
    end
    
    Cfg->>Impl: Step 4: pending_config.t2_keys += {…}<br/>pending_config.t3_keys += {…}
    
    Cfg->>Queue: queue_results.send(OK,<br/>tier=highest(present keys),<br/>params_applied, deferred_keys)
    Queue-->>Coord: 0x40 response
    
    Note over Coord,Model: ...time passes...<br/>in-flight slot finishes its decode
    
    Update->>Update: Step 6a: slot-free check<br/>(server-context.cpp:3712-3728)
    alt all slots idle && pending_config non-empty && drain timer not expired
        Update->>Impl: snapshot pending_config
        Update->>Model: T2 → llama_free + llama_new_context_with_model<br/>T3 → impl->load_model + (COMBINED teardown/rebind)
        Update->>Impl: pending_config.clear()
    else slots busy or drain expired
        Update->>Update: leave pending_config; try again next iteration
    end
```

### 3.2 Step 1 — Parse JSON

In `server-context.cpp:2932-2966`, replace the inline parse with a structured call:

```cpp
json cfg;
try {
    cfg = json::parse(task.hydra_action.config_json);
} catch (const std::exception & e) {
    res->success = false;
    res->rpc_status = HYDRA_STATUS_BAD_REQUEST;  // 0x05
    res->error = std::string("CONFIGURE: invalid JSON: ") + e.what();
    res->tier = "T1";
    res->params_applied = json::object();
    res->deferred_keys = json::array();
    queue_results.send(std::move(res));
    break;
}
if (!cfg.is_object()) {
    res->success = false;
    res->rpc_status = HYDRA_STATUS_BAD_REQUEST;
    res->error = "CONFIGURE: payload must be a JSON object";
    res->tier = "T1";
    res->params_applied = json::object();
    res->deferred_keys = json::array();
    queue_results.send(std::move(res));
    break;
}
```

**Lock:** none. **Error mapping:** any parse error → `HYDRA_STATUS_BAD_REQUEST` (0x05, added in M-Perf.9 #289 — see `specs/rpc-protocol.md:367`).

### 3.3 Step 2 — Classify each key

A small `classify_key(const std::string& key) -> Tier` helper drives this. The classification is fixed; if a key is *only* T2, it is T2; if it is *only* T3, it is T3; if both, T3 wins (the larger rebuild subsumes the smaller). Unknown keys land in a `_unknown` bucket that is silently dropped from the response (forward-compat).

```cpp
enum class Tier { T1, T2, T3 };
static Tier classify_key(const std::string & k) {
    // T1
    if (k == "state_chunk_size") return Tier::T1;
    if (k == "n_predict" || k == "n_keep" || k == "seed" || k == "antiprompt") return Tier::T1;
    if (k == "sampling") return Tier::T1; // sampling.* sub-keys inherit
    // T2
    if (k == "n_ctx" || k == "cache_type_k" || k == "cache_type_v") return Tier::T2;
    if (k == "rope_freq_base" || k == "rope_freq_scale" ||
        k == "rope_scaling_type" || k == "yarn" ||
        k == "attention_type") return Tier::T2;
    // T3
    if (k == "n_gpu_layers" || k == "n_cpu_moe" || k == "override_tensor" ||
        k == "split_mode" || k == "tensor_split" || k == "model") return Tier::T3;
    return Tier::T1; // unknown → T1, silently ignored (the apply step skips it)
}
```

The "highest tier present" rule (`max(T1, T2, T3)` in tier order) determines the response's `tier` field. The response's `deferred_keys` is the union of the T2 keys and the T3 keys (always flattened to top-level for the wire — see §3.5).

### 3.4 Step 3 — Apply T1 keys in-place

T1 apply happens *before* the response is sent. Pseudocode (the actual C++ is a tight `for` over the JSON object's keys):

```cpp
json params_applied = json::object();
for (auto it = cfg.begin(); it != cfg.end(); ++it) {
    const std::string & k = it.key();
    if (classify_key(k) != Tier::T1) continue;        // handled in step 4
    if (k == "state_chunk_size") {
        const size_t bytes = it.value().get<size_t>();
        llama_hydra_set_state_chunk_size(ctx_tgt, bytes);
        params_applied["state_chunk_size"] = (uint64_t)llama_hydra_get_state_chunk_size(ctx_tgt);
    } else if (k == "n_predict") {
        int32_t v = it.value().get<int32_t>();
        if (v < -1) { /* reject with BAD_REQUEST, see §7 */ }
        params_base.n_predict = v;
        params_applied["n_predict"] = v;
    } else if (k == "n_keep") {
        params_base.n_keep = it.value().get<int32_t>();
        params_applied["n_keep"] = params_base.n_keep;
    } else if (k == "seed") {
        params_base.sampling.seed = it.value().get<uint32_t>();
        params_applied["seed"] = params_base.sampling.seed;
    } else if (k == "antiprompt") {
        params_base.sampling.antiprompt = it.value().get<std::vector<std::string>>();
        params_applied["antiprompt"] = params_base.sampling.antiprompt;
    } else if (k == "sampling") {
        const json & s = it.value();
        for (auto sit = s.begin(); sit != s.end(); ++sit) {
            apply_sampling_subkey(params_base.sampling, sit.key(), sit.value(), params_applied["sampling"]);
        }
    }
    // unknown T1 keys (per classify_key's default): silent drop
}
```

**Re-init of samplers on T1 sampling change.** The existing `server_slot::smpl` (`server-context.cpp:207`) was bound at slot creation time via `common_sampler_init(model_tgt, task.params.sampling)` (`server-context.cpp:1625`). When a T1 sampling field changes via CONFIGURE, we **do not** re-init the in-flight slots' samplers — the in-flight decode keeps its bound sampler (preserves the "no in-flight rewrite" contract), and the new fields land on the next `common_sampler_init` call when a fresh task lands on a free slot. This is the same implicit-barrier pattern `llama_hydra_set_state_chunk_size` uses (`llama-context.cpp:813-820` explicitly calls it out as a benign data race, acceptable because CONFIGURE is a one-shot startup call). For T1 sampling this is *not* a startup call, but the *read* side (decode) still picks up the new field on the next token after the slot's current decode finishes — and the cost of getting it half-applied (mid-token) is that the in-flight decode keeps the old field until completion. Documented in the wire spec as "T1 sampling fields apply to the next task on each slot, not to the in-flight decode" — see §6.1.

**Lock:** none. `process_single_task` is serialized.

### 3.5 Step 4 — Record T2/T3 in `pending_config`

Add a new field to `server_context_impl` (in `server-context.cpp:686-712`):

```cpp
// server-context.cpp (add to server_context_impl)
struct hydra_pending_config {
    // T2 — context rebuild
    bool has_t2 = false;
    std::optional<int32_t> n_ctx;
    std::optional<ggml_type> cache_type_k;
    std::optional<ggml_type> cache_type_v;
    std::optional<float> rope_freq_base;
    std::optional<float> rope_freq_scale;
    std::optional<llama_rope_scaling_type> rope_scaling_type;
    std::optional<float> yarn_orig_ctx, yarn_ext_factor, yarn_attn_factor;
    std::optional<float> yarn_beta_fast, yarn_beta_slow;
    std::optional<llama_attention_type> attention_type;
    // T3 — model rebuild
    bool has_t3 = false;
    std::optional<int32_t> n_gpu_layers;
    std::optional<int32_t> n_cpu_moe;
    std::optional<std::string> override_tensor;
    std::optional<llama_split_mode> split_mode;
    std::optional<std::vector<float>> tensor_split;
    std::optional<std::string> model_path;
    std::optional<std::string> model_alias;
    // drain accounting
    std::chrono::steady_clock::time_point received_at = std::chrono::steady_clock::now();
};
hydra_pending_config pending_config;
std::atomic<bool> pending_config_busy{false}; // set during apply in update_slots
```

Each T2/T3 key in the payload updates the corresponding `optional` and sets `has_t2` / `has_t3`. The drain timer `received_at` is set on every CONFIGURE that touches the pending set (i.e. resets the drain window — see §4.3).

The response's `deferred_keys` is a flat list of top-level keys that landed in pending_config:

```cpp
std::vector<std::string> deferred_keys;
if (pending_config.has_t2) {
    if (cfg.contains("n_ctx"))              deferred_keys.push_back("n_ctx");
    if (cfg.contains("cache_type_k"))       deferred_keys.push_back("cache_type_k");
    // …etc
}
if (pending_config.has_t3) {
    if (cfg.contains("n_gpu_layers"))       deferred_keys.push_back("n_gpu_layers");
    // …etc
}
```

**Lock:** none for the write. The *apply* in `update_slots` uses a swap pattern: snapshot into a local `hydra_pending_config snap` (move-assign `std::move(pending_config)`, which zeros out the impl-level `pending_config` by its destructor + re-init; reset `pending_config.received_at` to `now()` for the next round). The `pending_config_busy` atomic prevents two `update_slots` rounds from applying simultaneously (only one runs at a time, so this is belt-and-braces; the `process_single_task` queue is the real barrier).

### 3.6 Step 5 — Response

Response shape (per `specs/rpc-protocol.md` post-#406):

```json
{
  "success": true,
  "tier": "T1" | "T2" | "T3",
  "params_applied": { "state_chunk_size": 4194304, "n_predict": 256, ... },
  "deferred_keys": ["n_ctx", "n_gpu_layers"]
}
```

The `tier` field is the **highest** tier present in the request (T3 > T2 > T1). For the legacy degenerate `{"state_chunk_size":N}` payload, `tier="T1"`, `deferred_keys=[]`, `params_applied={"state_chunk_size":<post-clamp>}` — bit-identical in shape to the new response (the C# call site at `WorkerSchedulerService.cs:2842` keeps working unchanged; see §6.3).

Add to `server_task_result_hydra_engine` (in `server-task.h:679-735`):

```cpp
struct server_task_result_hydra_engine : server_task_result {
    // ... existing fields ...
    // CONFIGURE: response shape (Phase 2b — ddvnguyen/llama.cpp#40)
    std::string tier;                    // "T1" | "T2" | "T3" (empty when not CONFIGURE)
    json        params_applied;          // echo of T1 keys with post-clamp values
    std::vector<std::string> deferred_keys;  // T2/T3 keys scheduled for the next slot-free
    // ... existing fields ...
    virtual json to_json() override;
};
```

The `to_json` method (at `server-task.cpp:2025-2064`) adds the three new fields to the output when `op == HYDRA_OP_CONFIGURE`. The legacy `state_chunk_size_applied` field stays in the struct (the field shape is the wire contract for v0 clients); for new clients, the Coordinator reads `params_applied["state_chunk_size"]` and ignores the legacy field. Both fields are populated identically so the response is single-source-of-truth.

### 3.7 Step 6 — Slot-free apply

When `update_slots` runs (the loop at `server-queue.cpp:139-180` calls it once per "all tasks processed" cycle), it first checks if all slots are idle. Modify that check (at `server-context.cpp:3712-3728`) to also drain `pending_config`:

```cpp
// server-context.cpp:3712-3728 (modified)
{
    bool all_idle = true;
    for (auto & slot : slots) {
        if (slot.is_processing() || slot.hydra_transferring->load()) {
            all_idle = false;
            break;
        }
    }
    if (all_idle) {
        if (pending_config.has_t2 || pending_config.has_t3) {
            // Drain check: respect the operator-bounded timeout
            const auto now = std::chrono::steady_clock::now();
            const auto drain_timeout = std::chrono::seconds(pending_drain_timeout_s);
            if (now - pending_config.received_at < drain_timeout) {
                apply_pending_config();   // see §3.7.1
            } else {
                SRV_WRN("hydra: pending CONFIGURE drain timed out (>%llds); keeping old config\n",
                        pending_drain_timeout_s);
                // Clear pending_config and emit a delayed response — see §7.3
                pending_config = hydra_pending_config{};
                pending_drain_response_pending = true;  // see §4.3
            }
        }
        SRV_INF("%s", "all slots are idle\n");
        return;
    }
}
```

The `apply_pending_config` is what does the rebuild work; see §3.7.1, §3.7.2.

#### 3.7.1 Apply T2

```cpp
void apply_pending_config_t2(const hydra_pending_config & snap) {
    if (!snap.has_t2) return;

    common_params new_params = params_base;
    if (snap.n_ctx) {
        int32_t v = *snap.n_ctx;
        if (v > llama_model_n_ctx_train(model_tgt)) {
            v = llama_model_n_ctx_train(model_tgt);
            SRV_WRN("hydra: n_ctx clamped to model_n_ctx_train=%d\n", v);
        }
        new_params.n_ctx = v;
    }
    if (snap.cache_type_k) new_params.cache_type_k = *snap.cache_type_k;
    if (snap.cache_type_v) new_params.cache_type_v = *snap.cache_type_v;
    if (snap.rope_freq_base)   new_params.rope_freq_base   = *snap.rope_freq_base;
    if (snap.rope_freq_scale)  new_params.rope_freq_scale  = *snap.rope_freq_scale;
    if (snap.rope_scaling_type) new_params.rope_scaling_type = *snap.rope_scaling_type;
    if (snap.yarn_orig_ctx)    new_params.yarn_orig_ctx    = *snap.yarn_orig_ctx;
    if (snap.yarn_ext_factor)  new_params.yarn_ext_factor  = *snap.yarn_ext_factor;
    if (snap.yarn_attn_factor) new_params.yarn_attn_factor = *snap.yarn_attn_factor;
    if (snap.yarn_beta_fast)   new_params.yarn_beta_fast   = *snap.yarn_beta_fast;
    if (snap.yarn_beta_slow)   new_params.yarn_beta_slow   = *snap.yarn_beta_slow;
    if (snap.attention_type)   new_params.attention_type   = *snap.attention_type;

    // Free the existing context (KV cache is destroyed)
    llama_free(ctx_tgt);
    if (ctx_dft) llama_free(ctx_dft.get());

    // Rebuild context
    auto cparams = common_context_params_to_llama(new_params);
    ctx_tgt = llama_new_context_with_model(model_tgt, cparams);
    if (!ctx_tgt) {
        SRV_ERR("hydra: T2 rebuild failed (n_ctx=%d cache=%d/%d); keeping old context\n",
                new_params.n_ctx, new_params.cache_type_k, new_params.cache_type_v);
        // Re-allocate the old context with the OLD params — the original
        // ctx_tgt is gone, so we have to rebuild with what we had.
        ctx_tgt = llama_new_context_with_model(model_tgt, common_context_params_to_llama(params_base));
        GGML_ASSERT(ctx_tgt);  // the old config worked; this must work too
        return;
    }
    if (new_params.speculative.has_dft() || spec_mtp_enabled()) {
        // rebuild ctx_dft — same pattern as load_model
        auto cparams_dft = common_context_params_to_llama(new_params);
        // … see load_model for the full draft path …
    }

    // Re-init each slot's sampler (the in-flight slot's smpl is already
    // gone — by the slot-free contract, all slots are idle and the slots
    // array still has the same set of server_slot structs, but their
    // smpl unique_ptrs are intact and bound to the now-freed ctx_tgt).
    for (auto & slot : slots) {
        slot.smpl.reset(common_sampler_init(model_tgt, params_base.sampling));
    }

    n_ctx = llama_n_ctx(ctx_tgt);
    SRV_INF("hydra: T2 rebuild applied (n_ctx=%d, cache=%d/%d)\n",
            n_ctx, new_params.cache_type_k, new_params.cache_type_v);
}
```

**Lock:** none beyond the slot-free barrier. The `llama_free` + `llama_new_context_with_model` cycle is the cost we are paying for the T2 class; it is the reason T2 is in its own tier and not grouped with T1.

**Error handling:** if `llama_new_context_with_model` returns null (e.g. `n_ctx` exceeds available memory), we rebuild with the **old** `params_base` to keep the engine alive. The error is logged at SRV_ERR; the response (§7) carries `success=false`, `error="T2 rebuild failed; old config preserved"`. The Coordinator decides whether to retry with a smaller `n_ctx` or surface to the operator.

#### 3.7.2 Apply T3

```cpp
void apply_pending_config_t3(const hydra_pending_config & snap) {
    if (!snap.has_t3) return;

    common_params new_params = params_base;
    if (snap.n_gpu_layers)   new_params.n_gpu_layers   = *snap.n_gpu_layers;
    if (snap.n_cpu_moe)      new_params.n_cpu_moe      = *snap.n_cpu_moe;
    if (snap.override_tensor) new_params.override_tensor = *snap.override_tensor;
    if (snap.split_mode)     new_params.split_mode     = *snap.split_mode;
    if (snap.tensor_split)   new_params.tensor_split   = *snap.tensor_split;
    if (snap.model_path)     new_params.model.path     = *snap.model_path;
    if (snap.model_alias)    new_params.model_alias    = { *snap.model_alias };

    // COMBINED teardown — see §5
    const bool was_combined = hydra_combined_head_attached || hydra_combined_static;
    if (was_combined) {
        if (!hydra_current_peer.empty()) {
            ctx_tgt->hydra_remove_combined_rpc_backend(hydra_current_peer.c_str());
        }
        llama_hydra_clear_combined_bindings(ctx_tgt, hydra_peer.c_str());
        hydra_combined_head_attached = false;
    }

    // Full model reload — same call site as the per-PREFILL model swap
    if (!load_model(new_params)) {
        SRV_ERR("hydra: T3 rebuild failed (model='%s'); keeping old model\n",
                new_params.model.path.c_str());
        // load_model() failure leaves impl in a bad state — we have to
        // try to reload the OLD params to keep serving.
        if (!load_model(params_base)) {
            GGML_ABORT("hydra: cannot recover from failed T3 rebuild");
        }
        return;
    }

    // Re-look-up the slot (load_model() rebuilds slots)
    // (the in-flight slot is gone by the slot-free contract — slots is
    //  a fresh array of empty server_slots after load_model)

    // COMBINED re-attach — see §5
    if (was_combined) {
        reattach_combined_mode();
    }

    SRV_INF("hydra: T3 rebuild applied (model='%s', split_mode=%s)\n",
            params_base.model.path.c_str(), params_base.split_mode.c_str());
}
```

**Lock:** none beyond the slot-free barrier. `load_model` itself is not thread-safe (it mutates `params_base`, `model_tgt`, `ctx_tgt`, the slot array) — the call is made from `update_slots` which is the only thread that may run while slots are idle. The serialization is structural.

**Error handling:** see §7.4 for the T3-peer-unreachable case.

---

## 4. Slot-free trigger

### 4.1 Where the check happens

`server-context.cpp:3711-3728` is the canonical slot-free check:

```cpp
void update_slots() {
    // check if all slots are idle
    {
        bool all_idle = true;
        for (auto & slot : slots) {
            if (slot.is_processing() || slot.hydra_transferring->load()) {
                all_idle = false;
                break;
            }
        }
        if (all_idle) {
            SRV_INF("%s", "all slots are idle\n");
            return;     // <-- we inject the pending-config drain HERE
        }
    }
    // ... continue with the per-slot decode loop
}
```

`server_slot::is_processing()` (at `server-context.cpp:331-333`) is `state != SLOT_STATE_IDLE`. The state machine (`server-context.cpp:78-85`) is `IDLE → WAIT_OTHER → STARTED → PROCESSING_PROMPT → DONE_PROMPT → GENERATING → IDLE` (on slot release). A slot that has just finished decoding and is awaiting a checkpoint save is still `IDLE`; a slot mid-state-save is `IDLE` (the M2 stream is the slot's own thread, tracked by `slot.hydra_transferring->load()`, not by the slot state — the existing check covers both).

**The slot-free trigger is therefore: "all slots in `SLOT_STATE_IDLE` AND no slot has `hydra_transferring` set."** That is the same condition `update_slots` already uses to short-circuit (`server-context.cpp:3723-3727`); the only change is that when we take that short-circuit, we also check `pending_config` and apply it before returning.

### 4.2 How the deferred config applies

The drain lives in `update_slots`'s short-circuit branch. Pseudocode for the full drain:

```cpp
if (all_idle) {
    if (pending_config.has_t2 || pending_config.has_t3) {
        const auto now = std::chrono::steady_clock::now();
        const auto drain_timeout = std::chrono::seconds(pending_drain_timeout_s);
        if (now - pending_config.received_at < drain_timeout) {
            SRV_INF("hydra: applying pending config (T2=%s T3=%s, age=%lldms)\n",
                    pending_config.has_t2 ? "yes" : "no",
                    pending_config.has_t3 ? "yes" : "no",
                    std::chrono::duration_cast<std::chrono::milliseconds>(now - pending_config.received_at).count());
            // Move-swap to a local; any new CONFIGURE arriving now will
            // re-populate pending_config while we apply.
            hydra_pending_config snap;
            {
                std::lock_guard<std::mutex> lk(pending_config_mtx);
                snap = std::move(pending_config);
                pending_config = hydra_pending_config{};  // re-init defaults
                pending_config.received_at = now;
            }
            pending_config_busy.store(true);
            try {
                if (snap.has_t2) apply_pending_config_t2(snap);
                if (snap.has_t3) apply_pending_config_t3(snap);
            } catch (const std::exception & e) {
                SRV_ERR("hydra: pending config apply threw: %s\n", e.what());
                // Best-effort recovery: re-emit a failed-CONFIGURE response
                // (see §7.3)
            }
            pending_config_busy.store(false);
        } else {
            SRV_WRN("hydra: pending CONFIGURE drain timed out (>%llds); discarding\n",
                    pending_drain_timeout_s);
            std::lock_guard<std::mutex> lk(pending_config_mtx);
            pending_config = hydra_pending_config{};
            pending_drain_response_pending = true;  // §4.3
        }
    }
    SRV_INF("%s", "all slots are idle\n");
    return;
}
```

The `pending_config_mtx` is a new `std::mutex` in `server_context_impl`. The move-swap pattern means a CONFIGURE arriving mid-apply (race window: the `update_slots` thread has moved the old pending out and is rebuilding) will not lose its values — they land in the re-initialized `pending_config` and apply on the *next* `update_slots` round. The race we explicitly *do not* worry about is two `update_slots` calls overlapping; the main loop in `server-queue.cpp:139-180` is single-threaded by design.

### 4.3 Drain timeout (`HYDRA_COORD_PROFILE_SWITCH_DRAIN_TIMEOUT`, default 300s)

**Where the env var is read.** Add to `server_context::set_hydra_capabilities` (or a new `set_hydra_drain_timeout` method, mirroring the pattern at `server-context.cpp:4980-4995`). The C# side has the corresponding setting at `CoordinatorModels.cs:113-115` (it sends the timeout, the engine respects it). The default if the env var is unset is 300 seconds — the same default `specs/rpc-protocol.md` (post-#406) commits to.

In the engine, the value is read at startup and stored on `server_context_impl`:

```cpp
// server-context.cpp (server_context_impl private fields)
int pending_drain_timeout_s = []() {
    const char * e = getenv("HYDRA_COORD_PROFILE_SWITCH_DRAIN_TIMEOUT");
    if (!e || !*e) return 300;  // default 300s
    int v = std::atoi(e);
    if (v < 1) return 1;        // floor at 1s
    if (v > 3600) return 3600;  // ceiling at 1h to bound tail latency
    return v;
}();
```

**What happens on timeout.** When `now - pending_config.received_at >= drain_timeout` at the slot-free moment, we **discard** the pending config and emit a delayed "timed out" response on the *next* CONFIGURE call (or on the next INFO call — see §6.2 for the design choice). The rationale: the alternative (sending a delayed response to a now-long-finished request) would be a wire-protocol violation; the alternative (emitting a fire-and-forget log) hides the failure from the Coordinator. The "next call surfaces it" pattern is the cleanest, and matches the legacy `state_chunk_size` echo behavior — the Coordinator can poll INFO if it really needs to know.

**Operator action on timeout.** The Coordinator's `WorkerSchedulerService` can either:
- Retry with a more aggressive `n_ctx`/tensor-split that costs less to drain (e.g. lower `n_gpu_layers` so the reload is faster).
- Surface to the operator as a "profile switch abandoned — too much in-flight traffic" warning.
- The default action is encoded in a new env var on the Coordinator side, `HYDRA_COORD_PROFILE_SWITCH_TIMEOUT_ACTION` (`"retry" | "warn" | "abort"`, default `"warn"`). This is a parent-side concern (PR 4 of the stack), not a fork-side one — it is listed here only to bound the design.

---

## 5. COMBINED-mode interaction

When the engine is in COMBINED mode (either expert-split or layer-split) and a T3 key changes, the model reload (§3.7.2) tears down `model_tgt` and `ctx_tgt`. The expert bindings (the dual-resident routed-expert tensors on the peer) and the peer's RPC device registration are tied to the lifetime of `ctx_tgt`'s `llama_context` and `model_tgt`'s `llama_model`. They must be torn down before the reload and re-attached after — the *order matters* and the failure modes differ between expert-split and layer-split.

### 5.1 Order of operations (T3 + COMBINED)

```
Before reload (in update_slots, all slots idle, pending_config has T3):
  1. was_combined = (hydra_combined_head_attached || hydra_combined_static)
  2. If was_combined:
     2a. llama_hydra_set_expert_mode(ctx_tgt, 0)        # switch to SOLO so the
                                                            next decode (the
                                                            pre-reload one — none
                                                            here, slot-free) does
                                                            not crash
     2b. ctx_tgt->hydra_remove_combined_rpc_backend(hydra_current_peer)
                                                            # unregister peer
                                                            backend from ctx's
                                                            sched
     2c. llama_hydra_clear_combined_bindings(ctx_tgt, hydra_peer)
                                                            # drop the binding
                                                            metadata; frees the
                                                            synthetic buffers
     2d. hydra_combined_head_attached = false
     2e. (layer-split only) llama_hydra_preload_rpc_device("")  # unregister
                                                                  # (the new
                                                                  # load_model
                                                                  # re-preloads
                                                                  # with the new
                                                                  # tensor_split)
  3. apply_pending_config_t3() -> load_model(new_params)  # rebuilds model_tgt, ctx_tgt, slots
  4. If was_combined:
     4a. (layer-split only) llama_hydra_preload_rpc_device(hydra_peer)
                                                            # MUST be before the
                                                            # next step — the
                                                            # device needs to be
                                                            # registered before
                                                            # the model is loaded
                                                            # (so the layer
                                                            # allocator can place
                                                            # tensors on it)
     4b. (expert-split only) llama_hydra_rebind_combined_experts(
                                  ctx_tgt, hydra_peer, peer_dev,
                                  hydra_combined_pattern)
                                                            # rebind on demand
                                                            # (same call as
                                                            # SET_EXPERT_MODE,
                                                            # #368 fix)
     4c. If rebind returned > 0:
           hydra_combined_head_attached = true
           llama_hydra_set_expert_mode(ctx_tgt, 1)
         Else:
           SRV_WRN("hydra: T3 rebuild: peer %s unreachable; staying solo\n",
                   hydra_peer.c_str())
           llama_hydra_set_expert_mode(ctx_tgt, 0)
           hydra_combined_head_attached = false
           (the engine continues to serve — fail-open, see §5.3)
  5. Resume update_slots() — slots is a fresh array, all idle, ready for new tasks
```

The step 2/4 split is **critical** for layer-split mode (`hydra_combined_static == true`): the peer's RPC device has to be registered *before* `load_model` runs, because llama.cpp's stock layer allocator (which `load_model` invokes) places whole layers on devices that are registered at load time (`src/llama-cpp/src/llama.cpp:239` per the comment in `llama-hydra.h:41-42`). If we forget step 4a, the new `tensor_split` (e.g. `[21.0, 44.0]` for the 27B MTP profile) cannot be honored — the layer allocator sees only the local CUDA device.

The step 2/4 split is **load-bearing** for expert-split mode (`hydra_combined_head_attached == true && !hydra_combined_static`): `llama_hydra_clear_combined_bindings` (step 2c) frees the synthetic buffer metadata that points at the peer's RPC backend; without it, the new `ctx_tgt` (from step 3's `load_model`) would inherit a stale binding to the old peer's device, and the rebind in step 4b would skip the `llama_hydra_load_combined_experts` initial-copy (it would think the binding is still valid). The #368 fix's fail-open semantics mean a missing `clear_combined_bindings` is a silent correctness bug, not a crash — the rebind returns 0 (no work to do), `hydra_combined_head_attached` stays `false`, and the engine serves solo when the operator asked for combined. (For an operator this looks like "T3 rebuild silently dropped my COMBINED mode" — the exact failure mode we are designing to avoid.)

### 5.2 Code sketch

```cpp
void apply_pending_config_t3_combined_teardown() {
    // Step 2
    if (hydra_combined_static) {
        // Layer-split: unregister the peer's RPC device so the new load
        // can re-register with the new tensor_split
        // (load_model calls llama_hydra_preload_rpc_device at startup
        //  with the configured peer; for T3 we override that path to
        //  use the new split_mode/tensor_split)
        if (!hydra_peer.empty()) {
            // No public unregister — the device will be replaced when
            // load_model() runs (it calls preload_rpc_device with the
            // NEW params). We do not need an explicit teardown here.
        }
    } else if (hydra_combined_head_attached) {
        // Expert-split
        llama_hydra_set_expert_mode(ctx_tgt, 0);
        if (!hydra_current_peer.empty()) {
            ctx_tgt->hydra_remove_combined_rpc_backend(hydra_current_peer.c_str());
        }
        llama_hydra_clear_combined_bindings(ctx_tgt, hydra_peer.c_str());
        hydra_combined_head_attached = false;
    }
}

void apply_pending_config_t3_combined_reattach() {
    // Step 4
    if (hydra_combined_static) {
        // Layer-split: the new load_model already preloaded the peer
        // device with the new tensor_split. Nothing to do here — the
        // split is baked in. Just ensure the mode flag is set.
        llama_hydra_set_expert_mode(ctx_tgt, 1);
        return;
    }
    // Expert-split
    if (hydra_peer.empty() || hydra_combined_pattern.empty()) return;
    if (!llama_hydra_peer_reachable(hydra_peer.c_str())) {
        SRV_WRN("hydra: T3 rebuild: peer %s unreachable; staying solo\n",
                hydra_peer.c_str());
        llama_hydra_set_expert_mode(ctx_tgt, 0);
        return;
    }
    // Re-resolve the peer's RPC device
    ggml_backend_reg_t rpc_reg = ggml_backend_reg_by_name("RPC");
    if (!rpc_reg) {
        SRV_WRN("hydra: T3 rebuild: RPC backend not available; staying solo\n");
        return;
    }
    using add_server_fn_t = ggml_backend_reg_t (*)(const char *);
    auto add_server_fn = (add_server_fn_t) ggml_backend_reg_get_proc_address(rpc_reg, "ggml_backend_rpc_add_server");
    ggml_backend_reg_t peer_reg = add_server_fn ? add_server_fn(hydra_peer.c_str()) : nullptr;
    ggml_backend_dev_t  peer_dev = (peer_reg && ggml_backend_reg_dev_count(peer_reg) > 0) ? ggml_backend_reg_dev_get(peer_reg, 0) : nullptr;
    if (!peer_dev) {
        SRV_WRN("hydra: T3 rebuild: peer %s has no device; staying solo\n", hydra_peer.c_str());
        return;
    }
    int32_t n_bound = llama_hydra_rebind_combined_experts(
        ctx_tgt, hydra_peer.c_str(), peer_dev, hydra_combined_pattern.c_str());
    if (n_bound <= 0) {
        SRV_WRN("hydra: T3 rebuild: rebind returned %d; staying solo\n", n_bound);
        return;
    }
    hydra_combined_head_attached = true;
    llama_hydra_set_expert_mode(ctx_tgt, 1);
    SRV_INF("hydra: T3 rebuild: COMBINED re-attached on peer %s (%d layers bound)\n",
            hydra_peer.c_str(), n_bound);
}
```

This is exactly the SET_EXPERT_MODE pattern at `server-context.cpp:3629-3662` — same `ggml_backend_reg_by_name("RPC")` lookup, same `add_server_fn` proc-address, same `llama_hydra_rebind_combined_experts` call. The PR's value-add is **factoring** this pattern (currently inlined twice — once at SET_EXPERT_MODE, once at PREFILL model-swap) into a single `reattach_combined_mode()` helper that all three call sites use.

### 5.3 Fail-open vs fail-stop on peer unreachable

**Decision: fail-open** (matches #368, matches the existing SET_EXPERT_MODE pattern at `server-context.cpp:3652-3655`).

Rationale:
- The T3 rebuild may have been triggered for a reason *unrelated* to COMBINED mode (e.g. operator changes `n_gpu_layers`). Forcing the engine offline because the peer is down is a regression vs. the "engine is always SOLO-capable" invariant (`docs/architecture-principles.md: P1–P3`).
- The Coordinator's `ReportsSolo()` path already handles a "combined requested, solo served" situation — it falls back to SOLO routing on the wire, and logs a warning. The operator can intervene (re-route, restart the peer, etc.) without taking the engine down.
- The alternative — fail-stop, refuse the T3 — would mean a single T3 key change forces the Coordinator into a retry loop that may never converge if the peer is genuinely down. The drain timeout (§4.3) bounds the wait; the fail-open makes the wait productive.

The response in the fail-open case (§7.4) carries `success=true` (the T3 rebuild itself succeeded) plus a `combined_applied: false` field in `params_applied` so the Coordinator knows the engine is no longer in COMBINED mode.

---

## 6. Open design questions

### Q1: T3 deferred trigger

**Default: "all slots fully released" + operator-bounded timeout (300s default).**

The "all slots fully released" trigger is the *minimum* trigger — it is the slot-free check at `server-context.cpp:3712-3728`. We never apply T2/T3 while a slot is processing (correctness — see §2.3) or mid-transfer (correctness — the M2 socket-stream is using the `ctx_tgt` we want to free).

The operator-bounded timeout (`HYDRA_COORD_PROFILE_SWITCH_DRAIN_TIMEOUT`) bounds the *wait* — the operator can configure a smaller drain window for production (e.g. 30s) where a T3 change should fail fast and let the operator re-route, or a larger window (e.g. 600s) for dev where a slow drain is acceptable.

| Trigger | Pros | Cons |
|---|---|---|
| **"All slots released" + timeout (default)** | Correct (slot-free contract); bounded wait; operator-tunable; matches existing patterns (the SET_EXPERT_MODE peer switch already requires slot-free at `server-context.cpp:3574-3581`) | Long-tail latency for the in-flight slot's decode to finish; the engine may serve stale config for the drain window |
| "Immediate" (apply right now, abort in-flight) | Zero drain latency | **Disallowed** — would invalidate the in-flight slot's decode state, violating the slot-free contract. Would also need a new "abort and replay" path that doesn't exist |
| "Quiescent" (no traffic for N seconds) | Lower drain latency in steady state | A single in-flight slot blocks the drain indefinitely; harder to reason about; the "N" is a tunable the operator must keep consistent with traffic shape |
| "Operator command" (separate `APPLY` opcode) | Explicit; no timeout race | Adds a 2-message protocol (CONFIGURE + APPLY); the operator must remember to send both; fails if the operator's tool forgets |

The default is "all slots released + 300s timeout." The timeout is the fallback — under steady state, the drain fires as soon as the in-flight decode finishes (which is "soon" for a typical 100-200ms decode).

### Q2: T3 race during decode

**Default: "old config wins"** — the in-flight decode finishes with the old config; the T3 change applies to the *next* decode after the slot is released.

The "old config wins" rule is the slot-free contract's natural extension: we promised not to mutate state mid-decode, and the decode is "with the old config" by definition (it started before the CONFIGURE was even received). The new config applies to whatever lands on the slot *next*.

| Rule | Pros | Cons |
|---|---|---|
| **"Old config wins" (default)** | Simple; matches the slot-free contract; the in-flight decode is never invalidated; the response is honest (the deferred_keys are still deferred) | The "next decode" sees a config that may be very different from what the previous tokens were generated with (sampling change mid-session) — but this is a *feature* for the operator (they explicitly asked for the change) |
| "New config wins" (apply mid-decode) | None worth the cost | **Disallowed** — invalidates the in-flight decode, violates the slot-free contract, no llama.cpp API supports it |
| "Re-decode" (rewind the slot to its checkpoint, re-decode with new config) | Consistent semantics mid-session | Massive cost (re-prefill up to the checkpoint); llama.cpp has no such API; the #289 model identity guard explicitly rejects cross-quant restores for the same reason |
| "Per-request override" (the C# side sends the per-request `task_params`; CONFIGURE just sets a default) | Maximum flexibility | This is the per-request overrides PR (PR 5 in the stack) — a *layer* on top of CONFIGURE, not a replacement. The default "old config wins" still applies for slots that have not received a per-request override |

The "per-request override" alternative is implemented in PR 5 of the stack (deferred to the next round); "old config wins" remains the right default for PR 2.

### Q3: Backward compatibility

**Default: keep legacy `{"state_chunk_size": N}` working as degenerate T1.**

The C# call site at `WorkerSchedulerService.cs:2842` (and its test in `tests/e1_rpc_test.py:101-108`) sends `{"state_chunk_size": N}` at startup. The new handler accepts this unchanged:
- All keys classify as T1.
- `tier="T1"`, `params_applied={"state_chunk_size":<post-clamp>}`, `deferred_keys=[]`.
- The legacy `state_chunk_size_applied` field on `server_task_result_hydra_engine` stays in the wire response (for v0 clients that read it directly); the new `params_applied` field is the new path.

| Compat policy | Pros | Cons |
|---|---|---|
| **Keep legacy as T1 (default)** | Zero C# changes for the existing call site; the wire response is a strict superset of the old shape; tests stay green | Two fields on the wire carry the same value (the legacy `state_chunk_size_applied` and `params_applied["state_chunk_size"]`); minor wire bloat |
| Deprecate `state_chunk_size` field, require `sampling` namespace | Cleaner schema | Breaks the C# call site today; would need a coordinated C# + engine release; the deprecation window is unbounded (operators may have their own scripts that send the old shape) |
| Version the opcode (0x40 v1, 0x47 v2) | Clean separation | Breaks the wire — the C# side has to know the version; the existing `0x40-0x46` numbering (per `specs/rpc-protocol.md:151-152`) is full, so 0x47 collides with the reserved-for-future range; would need a new range and a multi-month rollout |

The default keeps the legacy path working. The C# side can choose to migrate to the new `params_applied` field on its own schedule; the engine is a strict superset.

### Q4: `EngineConfigApplier` (parent-side) timing

> **Outcome note (2026-07-22):** the implementation diverged from this RFC.
> `EngineConfigApplier` was never wired up and was deleted as dead code in
> PR #488. Engine config instead reaches the engine as a `hydra_config` dict
> injected into the PREFILL request body (`WorkerSchedulerService.cs:1209`,
> #481 Phase 2b / #487). This section is retained as design history.


**Default: startup + profile switch.** The parent-side `EngineConfigApplier` (PR 4 in the stack) is invoked at two moments:
1. **Startup** — once per worker, after the engine's `INFO` (0x41) returns `solo_active=true` and the model is loaded. The applier looks up the worker's `EngineConfig` from `ModelRegistry` and sends a single `CONFIGURE` with the full key set.
2. **Profile switch** — when the operator changes the profile (the `.env-moe` ↔ `.env-dense` switch via `bash scripts/set-profile.sh`, per `CLAUDE.md` "Profiles"), the applier sends a fresh `CONFIGURE` with the new profile's `EngineConfig` shape.

The applier does **not** send CONFIGURE on every request — per-request overrides (PR 5 in the stack) use a different code path (the per-request `task_params` already carry the per-request overrides, which the C# side merges into the request body; the engine respects them via `common_params_sampling` fields on `task_params`).

| Applier timing | Pros | Cons |
|---|---|---|
| **Startup + profile switch (default)** | Bounded call rate (O(1) per worker, O(1) per profile change); no per-request overhead; the drain semantics are clean (the applier sends one CONFIGURE, the engine drains) | The per-request override layer (PR 5) is a separate code path; the operator must remember to send a profile switch to change defaults |
| Per-request (every `task_params` ships with the full config) | Maximum consistency | Wire bloat; the C# side has to ship the full config on every request; the engine has to re-classify keys on every request; per-request CONFIGURE is wrong (CONFIGURE is a "set defaults" opcode, not a "do this once" opcode) |
| Startup + every Nth request | The middle ground | The "N" is a tunable the operator must keep consistent with traffic shape; the Nth-request CONFIGURE may land mid-decode and need a drain window of its own |

The default "startup + profile switch" is the right shape for the engine's drain semantics — CONFIGURE is a *rare* opcode (it sets defaults), not a per-request one.

---

## 7. Error handling

The wire response always carries a JSON `meta` object with `success`, `tier`, `params_applied`, `deferred_keys`, and (on failure) `error`. The `status` byte on the 12-byte response header is one of `0x00` (OK), `0x02` (ERROR), or `0x05` (BAD_REQUEST) per `specs/rpc-protocol.md:362-368`.

### 7.1 Malformed JSON payload

```
status: 0x05 (BAD_REQUEST)
meta: {
  "success": false,
  "tier": "T1",
  "params_applied": {},
  "deferred_keys": [],
  "error": "CONFIGURE: invalid JSON: <exception message>"
}
```

**Behavior:** no fields are applied. `pending_config` is unchanged. The legacy `state_chunk_size_applied` is 0 (its default sentinel — `server-task.h:725-730`).

**Mapping rule:** `json::parse` throws on any syntax error. The handler catches `std::exception` and converts to `BAD_REQUEST`. The C# side already maps `BAD_REQUEST` to a per-call error path (per `specs/rpc-protocol.md:367` and `WorkerSchedulerService.cs`'s existing handling).

### 7.2 Recognized key with an invalid value (e.g. `n_predict: -1` mid-decode)

This is **two** sub-cases that get the same wire response:

**Sub-case A: the value is invalid regardless of state** (e.g. `n_ctx: 0`, `n_ctx: -100`, `cache_type_k: "not_a_type"`, `tensor_split: []`, `model.path: ""`).

```
status: 0x05 (BAD_REQUEST)
meta: {
  "success": false,
  "tier": "T3",                                  // highest tier that had a key
  "params_applied": {                            // partial: keys whose value was valid
    "n_predict": 256
  },
  "deferred_keys": ["n_gpu_layers"],             // T2/T3 keys whose value was valid
  "error": "CONFIGURE: invalid value for n_ctx: must be > 0"
}
```

**Sub-case B: the value is valid in isolation but invalid in current state** (e.g. `n_predict: -1` on a slot mid-decode — the existing `task_params::n_predict` per-request override is what's authoritative; the engine-level default is `-1` (unlimited)).

For sub-case B, the value is **accepted** and the response is `success=true`. The "mid-decode" constraint is on T2/T3 only, not on T1. This matches the T1 §3.4 contract: T1 fields apply to the next task on each slot, not to the in-flight one. `n_predict` is T1; the in-flight decode keeps its bound sampler; the next task sees the new default.

For T2/T3, the in-flight decode is preserved by the slot-free contract — the value is *deferred* and applied when slots are free. There is no "invalid value in current state" failure mode for T2/T3 (the rebuild may fail, which is §7.3).

### 7.3 T2/T3 key when the slot-free trigger never fires (drain timeout)

The CONFIGURE was accepted (the response went out immediately with `success=true` and the keys in `deferred_keys`). The drain waited `pending_drain_timeout_s` and the slots are still busy.

The Coordinator does **not** receive a delayed wire response (the request is long-since-returned). It receives a **timeout signal** in the response of the *next* CONFIGURE it sends, or in the next INFO (0x41) it polls, or via the engine's structured log. The mechanism:

```
// In the next CONFIGURE response (or INFO), if a prior CONFIGURE timed out:
meta: {
  "success": true,                              // this CONFIGURE succeeded
  "tier": "T1",
  "params_applied": {...},
  "deferred_keys": [],
  "last_pending_timeout": {                      // ad-hoc field; documented in the wire spec
    "at": "<ISO-8601>",
    "had_t2": true,
    "had_t3": false,
    "age_s": 312,
    "drain_timeout_s": 300,
    "abandoned_keys": ["n_ctx"]                 // which T2/T3 keys were abandoned
  }
}
```

**Status byte:** `0x00` (the new CONFIGURE itself succeeded; the timeout is a *side-channel* notification, not a failure of the current call).

**Behavior on the engine side:** when the drain times out, the pending config is discarded. The engine stays on the old config. The next CONFIGURE / INFO surfaces `last_pending_timeout`. After the next CONFIGURE or INFO consumes the field, it is cleared. The field is `null` when there is no pending timeout (so the C# side can poll INFO cheaply to detect the timeout).

### 7.4 T3 key when the peer is unreachable (COMBINED mode)

```
status: 0x00 (OK — the T3 rebuild itself succeeded; COMBINED fell back to SOLO)
meta: {
  "success": true,
  "tier": "T3",
  "params_applied": {
    "model.path": "/mnt/SSD/.../dense-27b-q5.gguf",
    "combined_applied": false                   // the COMBINED re-attach failed
  },
  "deferred_keys": [],
  "error": null
}
```

**Status byte:** `0x00`. The T3 rebuild itself succeeded (the model swapped to the dense 27B q5 file). The COMBINED-mode re-attach failed because the peer was unreachable — but per §5.3 the engine is **fail-open** and continues to serve SOLO. The `combined_applied: false` field is the signal to the Coordinator; its `ReportsSolo()` path will fall back to SOLO routing until the peer is back (matching the existing SET_EXPERT_MODE fall-back pattern at `server-context.cpp:3652-3655`).

The C# side reads `combined_applied` from the response and updates its `EngineCapabilities` cache (the same cache that `EngineInfo` already populates from INFO). The next COMBINED plan request from the Coordinator's `MultiEngineRouter.Select` will be re-routed to SOLO until the next INFO poll shows `combined_head_attached=true`.

---

## 8. Test plan

### 8.1 Unit tests — `src/llama-cpp/tests/test-hydra-configure-t1-t2-t3.cpp`

A new unit test file in the `tests/` directory, following the pattern of `tests/test-hydra-state-chunk-size.cpp` (43 lines, header + `expect_eq` helpers + `main`). CMake target added to `tests/CMakeLists.txt`.

The test does not spin up a full `server_context` — that requires a model and a CUDA device. Instead, it tests:

1. **The classifier** (`classify_key`) — every documented key in §2 maps to the right tier; unknown keys map to T1 (and are silently dropped by the apply step).
2. **The post-clamp echo for `state_chunk_size`** — same bounds as `llama_hydra_clamp_state_chunk_size` (64 KiB–64 MiB); a malformed value (negative, non-integer) is rejected.
3. **The `tier` aggregation rule** — T1 + T2 → T2; T2 + T3 → T3; T1 + T3 → T3; T1 only → T1; empty payload → T1 with no apply.
4. **The `deferred_keys` list** — exactly the T2/T3 keys present in the input, in the order they appear in the input.
5. **The `params_applied` echo** — T1 keys echo the post-clamp / coerced value; T2/T3 keys are *not* in `params_applied` (they are in `deferred_keys`).
6. **The fail-open for T3+COMBINED+peer-down** — given a mock `hydra_peer_reachable()` that returns false, the `apply_pending_config_t3_combined_reattach()` function returns without setting `hydra_combined_head_attached = true`; the function never throws.

The test runs in CI (per `tests/CMakeLists.txt`); it does not need a GPU.

```cpp
// test-hydra-configure-t1-t2-t3.cpp (sketch)
#include "server-task.h"      // for HYDRA_OP_CONFIGURE etc.
#include "llama-hydra.h"      // for state-chunk-size clamping reference
#include <cassert>
#include <cstdio>
#include <string>
#include <vector>

static int g_failures = 0;
static void expect_eq(const char * what, auto actual, auto expected) {
    if (actual != expected) {
        fprintf(stderr, "FAIL: %s — expected %lld, got %lld\n", what,
                (long long)expected, (long long)actual);
        g_failures++;
    }
}

int main() {
    // 1. classifier
    expect_eq("state_chunk_size → T1", classify_key("state_chunk_size"), Tier::T1);
    expect_eq("sampling → T1",         classify_key("sampling"),         Tier::T1);
    expect_eq("n_predict → T1",        classify_key("n_predict"),        Tier::T1);
    expect_eq("n_ctx → T2",            classify_key("n_ctx"),            Tier::T2);
    expect_eq("cache_type_k → T2",     classify_key("cache_type_k"),     Tier::T2);
    expect_eq("yarn → T2",             classify_key("yarn"),             Tier::T2);
    expect_eq("n_gpu_layers → T3",     classify_key("n_gpu_layers"),     Tier::T3);
    expect_eq("model → T3",            classify_key("model"),            Tier::T3);
    expect_eq("tensor_split → T3",     classify_key("tensor_split"),     Tier::T3);
    expect_eq("unknown → T1 (silent)", classify_key("not_a_real_key"),   Tier::T1);

    // 2. state_chunk_size clamp
    // (delegated to llama_hydra_clamp_state_chunk_size; just verify the
    //  post-clamp value ends up in params_applied)
    // …

    // 3-5. Build a mock json payload, run it through the (refactored)
    //      pure helper `classify_and_partition(json) -> Response`,
    //      assert the response shape.
    // …

    // 6. T3 + COMBINED + peer down (mocked)
    // …

    return g_failures == 0 ? 0 : 1;
}
```

The pure-function factoring is a prerequisite — the CONFIGURE handler in `server-context.cpp:2932-2966` is currently entangled with the `server_task` struct and the `ctx_tgt` pointer; we need to extract the classify + partition logic into a free function that the test can call without those dependencies. The factoring is small (~30 LOC) and is part of PR 2.

### 8.2 Integration tests — `tests/system/test_phase0_scheduler_feasibility.py`

Extend the existing `test_phase0_scheduler_feasibility.py` to add a `test_configure_t1_applies_immediately` case. The existing test boots an `llama-engine` head + `rpc-server` peer (lines 130-200); the new case:

1. Sends a raw 0x40 CONFIGURE with `{"state_chunk_size": 4194304}` to the head's RPC port.
2. Parses the response header (12 bytes) and the meta JSON.
3. Asserts `status == 0x00` (OK).
4. Asserts `meta["success"] == true`, `meta["tier"] == "T1"`, `meta["params_applied"]["state_chunk_size"] == 4194304`, `meta["deferred_keys"] == []`.
5. Then sends `{"n_ctx": 16384, "n_gpu_layers": 99}` and asserts `meta["tier"] == "T3"`, `meta["deferred_keys"]` contains `"n_ctx"` and `"n_gpu_layers"`, `meta["params_applied"]` is empty (the request contains only T2/T3 keys).
6. Optionally: poll INFO (0x41) until `deferred_keys` are observed as applied (or until the drain timeout expires — the test should accept either outcome and assert only on the response shape, not on the eventual apply).

The wire framing is the same as `tests/e1_rpc_test.py:101-108` (the existing Test 2). The new test reuses the same `create_request` helper.

This test requires the live engine to be booted; it is gated on the `LLAMA_ENGINE_BIN` env var (the existing `test_phase0_scheduler_feasibility.py:56-67` pattern).

### 8.3 E2E tests — `tests/system/test_combine_profile_switch.py`

A new test file that exercises the full profile-switch path end-to-end. The test:

1. Boots two `llama-engine` instances:
   - **head** with `--combined-ot-pattern "blk\\.[0-2]\\.ffn_.*_exps\\.weight" --rpc-engine peer:9505` (the MoE 35B profile's head).
   - **peer** (a separate llama-engine running in peer-only mode, per the #383 T2 `peer-only` mode) on the loopback RPC port.
2. Loads the MoE 35B Q3_K-mini model (the MoE profile's model).
3. Sends a PREFILL + DECODE on the head in SOLO mode — verifies baseline works.
4. Sends a `SET_EXPERT_MODE(combined)` — verifies the head binds the expert tensors to the peer (the #368 path).
5. Sends a second PREFILL + DECODE in COMBINED mode — verifies the COMBINED path works.
6. Sends a 0x40 CONFIGURE with `{"model": {"path": "<path to dense 27B q5>", "alias": "dense-27b-q5"}, "split_mode": "layer", "tensor_split": [21.0, 44.0]}`.
7. Asserts the response: `tier="T3"`, `deferred_keys` contains all three keys, `params_applied` is empty.
8. Sends PREFILLs and DECODEs to the head in a tight loop (to keep slots busy) for 60 seconds.
9. After 60 seconds, polls INFO — verifies the pending config has *not* been applied yet (slots are still busy).
10. Stops the PREFILL/DECODE loop, waits 5 seconds for slots to drain.
11. Polls INFO — verifies the pending config *has* been applied: the head now reports `model_alias="dense-27b-q5"`, the peer's RPC device is registered, and the model is the dense 27B q5.
12. Sends a PREFILL + DECODE in SOLO mode on the new model — verifies the model swap was clean.
13. Sends a `SET_EXPERT_MODE(combined)` — verifies the COMBINED re-attach against the new model works (the §5.1 step 4 path).

The test is gated on the model files existing on disk (env vars `MOE_35B_MODEL`, `DENSE_27B_MODEL`); it skips cleanly with `pytest.skip` when the models are not available, matching the existing `test_phase0_scheduler_feasibility.py:33-40` pattern.

The test is the **gate** for PR 2 — without it, the COMBINED-mode teardown/rebind path in §5 is unverified.

---

## 9. PR stack

The Phase 2b stack is **6 PRs**, ordered by dependency. PR 1 is the parent-side docs PR (already merged as `ddvnguyen/hydra_vortex#406` / `d06d9df`). PRs 2 and 3 are fork-side. PRs 4, 5, 6 are parent-side. Each PR is independently reviewable; the dependency graph is a chain (no parallel merges).

### PR 1 — Parent-side wire schema (DONE — `ddvnguyen/hydra_vortex#406`)

| | |
|---|---|
| Files | `specs/rpc-protocol.md` |
| LOC | +98 / -4 (the diff in `d06d9df`) |
| Status | **merged** |
| Dep | — |

### PR 2 — Fork-side CONFIGURE handler extension (this PR)

| | |
|---|---|
| Files | `src/llama-cpp/tools/server/server-context.cpp`, `src/llama-cpp/tools/server/server-context.h`, `src/llama-cpp/tools/server/server-task.h`, `src/llama-cpp/tools/server/server-task.cpp`, `src/llama-cpp/tests/test-hydra-configure-t1-t2-t3.cpp` (new), `src/llama-cpp/tests/CMakeLists.txt` |
| LOC | ~+450 / -34 (the +34 is the existing single-key block being replaced) |
| Dep | PR 1 (the wire schema) |
| Risk | Medium — touches the inference thread's task dispatch, but only the CONFIGURE branch (a new task type that is currently a 34-line stub). The drain logic in `update_slots` is the highest-risk piece; mitigation is the `pending_config_busy` atomic + the move-swap pattern in §3.5 |
| Test | `tests/test-hydra-configure-t1-t2-t3.cpp` (unit), plus the live-engine smoke from `tests/e1_rpc_test.py:101-108` (already green) |
| Fork PR target | `ddvnguyen/llama.cpp#40` |

LOC breakdown:
- `server-context.cpp`: +180 (new pending_config struct + apply functions + drain timeout read + COMBINED teardown/reattach factored helper), -34 (the single-key CONFIGURE block).
- `server-context.h`: +6 (`set_hydra_drain_timeout` setter, used by `llama-engine.cpp`'s startup).
- `server-task.h`: +10 (three new fields on `server_task_result_hydra_engine`).
- `server-task.cpp`: +25 (the new `to_json` branches for the three new fields).
- `tests/test-hydra-configure-t1-t2-t3.cpp`: +180 (new file).
- `tests/CMakeLists.txt`: +8 (the new test target).

### PR 3 — Fork-side T3 mutators

| | |
|---|---|
| Files | `src/llama-cpp/include/llama-hydra.h` (add `llama_hydra_set_split_mode`, `llama_hydra_set_tensor_split`, `llama_hydra_set_n_gpu_layers_ctx`, `llama_hydra_set_override_tensor`), `src/llama-cpp/src/llama-hydra.cpp` (implementations), `src/llama-cpp/src/llama-context.{h,cpp}` (the underlying cparams writes) |
| LOC | ~+200 |
| Dep | PR 2 (the handler dispatches to these mutators) |
| Risk | Low — the mutators are thin wrappers over `cparams` field writes; the only non-trivial one is `llama_hydra_set_split_mode` which must invalidate the graph-reuse key (`cparams.hydra_expert_mode` already does this, see `src/llama-cpp/src/llama-graph.h:688`) |
| Test | covered by PR 2's `test-hydra-configure-t1-t2-t3.cpp` (the mutators are called from the handler) |

This PR is split from PR 2 because the mutators are a clean unit — they expose the `cparams` setters that PR 2 needs, but they could equally well be called from other paths (e.g. a future `EnginePipelineAttach` 0x46 use, or the per-request override layer in PR 5). Reviewing them in isolation is easier.

### PR 4 — Parent-side `EngineConfigApplier` — ❌ NOT SHIPPED (deleted, PR #488)

| | |
|---|---|
| Files | `src/core/Hydra.Core/Services/EngineConfigApplier.cs` (new), `src/core/Hydra.Core/Services/WorkerSchedulerService.cs` (invoke on startup + on profile switch), `src/core/Hydra.Core/Models/EngineConfig.cs` (extend if needed), `src/core/Hydra.Core/Tests.Core/EngineConfigApplierTests.cs` (new) |
| LOC | ~+300 |
| Dep | PR 2 (the wire format the applier sends) |
| Risk | Medium — the applier is the new code path that turns the Phase 2a `EngineConfig` record (dead code today) into live 0x40 calls. The startup invocation is straightforward; the profile-switch invocation is where the new `set-profile.sh` script gets hooked |

### PR 5 — Parent-side per-request overrides

| | |
|---|---|
| Files | `src/core/Hydra.Core/Services/WorkerSchedulerService.cs` (merge per-request `task_params` into the `EnginePrefill` body), `src/core/Hydra.Core/Models/CoordinatorModels.cs` (new `PerRequestOverride` config), `src/core/Hydra.Core/Tests.Core/` (new tests) |
| LOC | ~+150 |
| Dep | PR 4 (the applier establishes the baseline) |
| Risk | Low — per-request overrides are a layer on top of the engine's defaults; the engine's drain semantics are unchanged. The C# side just forwards the per-request fields the operator set on the request |

### PR 6 — E2E test for profile switch

| | |
|---|---|
| Files | `tests/system/test_combine_profile_switch.py` (new) |
| LOC | ~+250 |
| Dep | PRs 2, 3, 4 (the full stack) |
| Risk | Low — test-only; the failure mode is "the test fails," not "production breaks" |
| Test | `pytest tests/system/test_combine_profile_switch.py` against the live stack |

This PR is the gate for closing the parent-side issue (`ddvnguyen/hydra_vortex#397`). It is filed as a separate PR (not bundled with PR 4) because the test requires a 2-engine boot + 2 model files + 60s of traffic, which is a heavy CI gate — keeping it isolated lets the other PRs land without that gate.

### PR ordering summary

```
PR 1 (DONE) → PR 2 (fork) ─┬─→ PR 3 (fork)
                           │
                           └─→ PR 4 (parent) ─┬─→ PR 5 (parent)
                                              │
                                              └─→ PR 6 (test, gate)
```

The fork PRs (2, 3) can land first, then the parent PRs (4, 5, 6) — but the parent side does not have to wait for PR 3 to land if PR 2's mutator coverage is sufficient for the applier's needs (the applier only needs `state_chunk_size`, `n_predict`, `seed`, `n_ctx` initially; the rest can land in PR 5's per-request override layer).
