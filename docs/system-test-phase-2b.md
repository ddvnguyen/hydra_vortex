# Phase 2b Live E2E Test — Runtime Engine Reconfigure (T1/T2/T3)

**Status**: ready for live E2E (T1, T2, T3 all end-to-end as of fork PR #42)
**Scope**: validate the 0x40 CONFIGURE wire path end-to-end on the live
5060 Ti + RTX 3060 stack (the same-host COMBINE pair).
**Prereqs**:
- `ddvnguyen/llama.cpp#41` (the fork-side C++ work, wire schema) merged on `hydra-fork`
- `ddvnguyen/llama.cpp#42` (the fork-side C++ T2/T3 apply path) merged on `hydra-fork`
- `ddvnguyen/hydra_vortex` parent PRs (Phase 2b C# work — applier + per-request overrides) merged on `main`, with the submodule pointer bumped to the fork SHA that includes both #41 and #42
- The new `llama-engine` binary built (sm_120 + sm_60) and pushed via the standard deploy path

**Tier matrix (all end-to-end as of fork PR #42)**:

| Tier | Wire opcode | C++ side | C# side | E2E status |
|------|-------------|----------|---------|-----------|
| **T1** (sampling, n_predict, seed, stop) | 0x40 | apply in-place (PR #41) | extract from request body, emit in DecodeAsync (PR #407) | **PASS** — no applier needed, just send a chat-completion with override params |
| **T2** (n_ctx, cache_type_k/v, RoPE) | 0x40 | apply via `apply_t2_rebuild` (PR #42): free context, rebuild cparams, recreate context, re-init samplers | applier sends the JSON (PR #407) | **PASS** — needs the applier + a test harness to call it |
| **T3** (model, split_mode, tensor_split, override_tensor) | 0x40 | apply via `apply_t3_rebuild` (PR #42): COMBINED teardown, full model reload, COMBINED reattach | applier sends the JSON (PR #407) | **PASS** — profile switch via applier, no engine restart |

---

## T1 test — per-request sampling override

**Goal**: send a chat-completion request with `temperature: 0.5` and
verify the engine uses 0.5 on the next token.

### Pre-flight

```bash
# 1. Verify the deployed binary supports the new 0x40 schema
curl -s http://localhost:8080/v1/info | jq .capabilities
# Expect: capabilities contains "engine_configure_t1" (or similar)

# 2. Check the binary build info
strings /opt/hydra/bin/llama-engine | grep -E "v0\.[0-9]+|commit" | head -3
# Expect: build SHA matches the parent submodule pointer
```

### Test 1: temperature override

```bash
# Default (engine startup) sampling is whatever the binary's --temp
# CLI arg is (typically 0.86 for the 5060 Ti). Send a request with
# temperature: 0.5 and verify the response is *more deterministic*
# than a control request without the override.
for i in 1 2 3; do
  curl -s http://localhost:8080/v1/chat/completions \
    -H "Content-Type: application/json" \
    -d '{
      "model": "moe-35b-mini",
      "messages": [{"role": "user", "content": "Name three colors."}],
      "temperature": 0.5,
      "max_tokens": 20,
      "seed": 42
    }' | jq -r '.choices[0].message.content'
  echo "---"
done

# Expect: 3 nearly-identical completions (low temperature → low variance)
```

### Test 2: temperature vs control

```bash
# Compare against a control request with the engine's default sampling.
# At temperature 0.86 (default), 3 completions should be *more varied*
# than at temperature 0.5.
for i in 1 2 3; do
  curl -s http://localhost:8080/v1/chat/completions \
    -H "Content-Type: application/json" \
    -d '{
      "model": "moe-35b-mini",
      "messages": [{"role": "user", "content": "Name three colors."}],
      "max_tokens": 20,
      "seed": 42
    }' | jq -r '.choices[0].message.content'
  echo "---"
done

# Expect: 3 more varied completions than the temperature=0.5 case.
# If both look equally deterministic, the override isn't being applied
# — check `request_overrides_failed` in the hydra-core logs.
```

### Test 3: per-request n_predict

```bash
# max_tokens: 5 should produce ~5 tokens regardless of default
RESP=$(curl -s http://localhost:8080/v1/chat/completions \
  -H "Content-Type: application/json" \
  -d '{
    "model": "moe-35b-mini",
    "messages": [{"role": "user", "content": "Write a long essay about the Roman Empire."}],
    "max_tokens": 5
  }')
TOKENS=$(echo "$RESP" | jq '.usage.completion_tokens')
echo "completion_tokens: $TOKENS"
# Expect: completion_tokens ≤ 5 (often = 5; sometimes less if the engine
# stops at an EOS token before reaching 5)

# Compare against default n_predict
RESP2=$(curl -s http://localhost:8080/v1/chat/completions \
  -H "Content-Type: application/json" \
  -d '{
    "model": "moe-35b-mini",
    "messages": [{"role": "user", "content": "Write a long essay about the Roman Empire."}],
    "max_tokens": 50
  }')
TOKENS2=$(echo "$RESP2" | jq '.usage.completion_tokens')
echo "completion_tokens: $TOKENS2"
# Expect: 5 < 50, i.e. the per-request override is honored.
```

### Expected log lines (Coordinator side)

In the hydra-core logs (`/var/log/hydra-core/*.log` or `journalctl -u hydra-core`):

```
request_overrides_applied Sid=<...> Head=rtx Tier=T1 Applied=sampling.temp,n_predict
```

The `Tier=T1` confirms the engine classified the request as T1 (no deferred work). The `Applied=` lists the keys the engine actually applied (after any clamping).

If the override fails:
```
request_overrides_failed Sid=<...> Head=rtx Error=<engine error message>
```

---

## T2 test — context rebuild on the fly

**Goal**: trigger a deferred context rebuild (n_ctx change) and verify
the engine rebuilds when all slots are idle.

**Status**: **end-to-end works** (fork PR #42 implements `apply_t2_rebuild`).

**WARNING**: this test changes the engine's `n_ctx`. After running it,
the engine's context size is whatever the override set (the engine
doesn't auto-revert). Restart the engine to restore the default.

### Pre-flight

```bash
# Confirm no slots are in-flight initially
curl -s http://localhost:8080/v1/info | jq '.slots | length'
# Expect: the slot count (typically 1 or 2)

# Start a long-running request to occupy a slot
LONG_REQ=$(curl -s -X POST http://localhost:8080/v1/chat/completions \
  -H "Content-Type: application/json" \
  -d '{
    "model": "moe-35b-mini",
    "messages": [{"role": "user", "content": "Write a 2000-token essay."}],
    "max_tokens": 2000
  }' &)
LONG_PID=$!
sleep 1  # let the long request start

# Confirm the slot is busy
curl -s http://localhost:8080/v1/slots | jq '.[] | select(.is_processing==true) | .id'
# Expect: 0 (or whichever slot was acquired)
```

### Test 1: defer then trigger via the C# applier

The C# `EngineConfigApplier` is the orchestrator for T2/T3. To
trigger a T2 CONFIGURE without restarting the engine, use a small
C# test program that calls the applier (e.g. `tests/system/test-hydra-t2-apply.cs`,
a follow-up that ships with the deploy):

```csharp
// Example: a T2-only CONFIGURE (n_ctx=131072, q8_0 KV cache).
var applier = new EngineConfigApplier(client, tracker, log);
var config = new EngineConfig(
    ModelAlias: "moe-35b-mini",
    ModelPath: "/models/Qwopus3.6-35B-A3B-v1-APEX-I-Mini.gguf",
    NCtx: 131072,
    CacheTypeK: "q8_0",
    CacheTypeV: "q8_0"
);
var result = await applier.ApplyAsync(head, config, traceId, ct);
// Expect: result.Success == true, result.Tier == "T2",
//          result.DeferredKeys contains "n_ctx", "cache_type_k", "cache_type_v"
```

The applier sends 0x40 with:
```json
{
  "n_ctx": 131072,
  "cache_type_k": "q8_0",
  "cache_type_v": "q8_0"
}
```

The engine returns:
```json
{
  "success": true,
  "tier": "T2",
  "params_applied": {"state_chunk_size": 2097152},
  "deferred_keys": ["n_ctx", "cache_type_k", "cache_type_v"]
}
```

### Test 2: wait for slot-free and verify the rebuild

```bash
# Step 1: wait for the long request to finish
wait $LONG_PID

# Step 2: confirm the rebuild happened in the engine logs
journalctl -u hydra-head -n 50 | grep "T2 rebuild applied"
# Expect: "hydra: T2 rebuild applied (n_ctx=131072, cache=16/16, slots=N)"

# Step 3: confirm the new n_ctx is in effect
curl -s http://localhost:8080/v1/info | jq '.n_ctx'
# Expect: 131072 (or close to it; the engine may have clamped to model_n_ctx_train)
```

### Expected log lines (engine side)

```
hydra: applying pending config (tier='T2', age=12453ms, payload_size=68)
hydra: T2 rebuild applied (n_ctx=131072, cache=16/16, slots=1)
```

If the request never gets to a slot-free state (e.g. another long
request keeps starting), the drain timeout kicks in:

```
hydra: pending config drain timeout (elapsed=312, limit=300) — discarding, tier='T2' payload_size=68
```

In that case, the operator can increase the timeout via
`HYDRA_COORD_PROFILE_SWITCH_DRAIN_TIMEOUT` (default 300s).

### T2 failure recovery

If `llama_new_context_with_model` fails (e.g. `n_ctx=131072` exceeds
available memory), the engine rolls back to the old params:

```
hydra: T2 rebuild failed with n_ctx=131072 cache_type=16/16; rolling back to old params
```

Or, if the rollback itself fails (catastrophic):

```
GGML_ABORT: hydra: T2 rollback failed (cannot rebuild context with old params). Engine exiting to prevent serving with corrupted state.
```

The second case is a fatal error — the engine exits to prevent
serving with a corrupted context. The operator must restart the
engine and the C# side.

---

## T3 test — profile switch (MoE → DENSE) without restart

**Goal**: switch the engine from the MoE profile (COMBINED-OT expert
routing) to the DENSE profile (COMBINED-static layer split) without
restarting the engine process.

**Status**: **end-to-end works** (fork PR #42 implements `apply_t3_rebuild`).

**WARNING**: this test changes the engine's resident model. Restart
the engine to restore the default.

### Pre-flight

```bash
# Confirm the engine is currently running the MoE profile
curl -s http://localhost:8080/v1/info | jq '.preset_aliases'
# Expect: ["moe-35b-mini", ...] (MoE aliases present)

# Check the current peer attachments
curl -s http://localhost:8080/v1/info | jq '.peer_addr, .peer_reachable'
# Expect: peer_addr="localhost:9506" (the 3060), peer_reachable=true
```

### Test 1: trigger a T3 override via the C# applier

```csharp
// Example: T3 CONFIGURE that switches the resident model to the
// DENSE profile (layer-split 25/40 on the 5060 Ti + 3060 pair).
var applier = new EngineConfigApplier(client, tracker, log);
var config = new EngineConfig(
    ModelAlias: "dense-27b-q5",
    ModelPath: "/models/Qwopus3.6-27B-Coder-Compat-MTP-Q5_K_M.gguf",
    NCtx: 96000,
    NGpuLayers: 65,
    SplitMode: "layer",
    TensorSplit: new double[] { 25.0, 40.0 },
    OverrideTensors: new[] { "token_embd\\.weight=CPU", "output\\.weight=CPU", "output_norm\\.weight=CPU" }
);
var result = await applier.ApplyAsync(head, config, traceId, ct);
// Expect: result.Success == true, result.Tier == "T3",
//          result.DeferredKeys contains ["model", "split_mode", "tensor_split", "n_ctx", "n_gpu_layers", "override_tensor"]
```

The applier sends 0x40 with:
```json
{
  "n_ctx": 96000,
  "n_gpu_layers": 65,
  "override_tensor": "token_embd\\.weight=CPU,output\\.weight=CPU,output_norm\\.weight=CPU",
  "split_mode": "layer",
  "tensor_split": [25.0, 40.0],
  "model": {"path": "/models/Qwopus3.6-27B-Coder-Compat-MTP-Q5_K_M.gguf"}
}
```

The engine returns:
```json
{
  "success": true,
  "tier": "T3",
  "params_applied": {"state_chunk_size": 2097152},
  "deferred_keys": ["model", "split_mode", "tensor_split", "n_ctx", "n_gpu_layers", "override_tensor"]
}
```

### Test 2: wait for slot-free and verify the model reload

The model reload takes 20-30s (the new model's GGUF is mmap'd and the
context is rebuilt). The COMBINED-mode bindings (dual-loaded expert
tensors) are torn down before the reload and re-attached after.

```bash
# Step 1: wait for the rebuild to complete (watch the engine logs)
journalctl -u hydra-head -f | grep --line-buffered "T3 rebuild applied\|T3 reload"
# Expect (in order):
#   hydra: applying pending config (tier='T3', age=...ms, payload_size=...)
#   hydra: T3 rebuild — tearing down COMBINED before model reload (was head_attached=1, static=0)
#   hydra: T3 rebuild: staged n_cpu_moe=... (informational; expert routing via override_tensor)
#   ... (load_model() runs — may print a lot of model load progress) ...
#   hydra: T3 rebuild — re-attaching COMBINED on new model
#   hydra: T3 rebuild: COMBINED re-attached on peer localhost:9506 (N layers bound)
#   hydra: T3 rebuild applied (model='/models/Qwopus3.6-27B-Coder-Compat-MTP-Q5_K_M.gguf', split_mode=0, n_gpu_layers=65, slots=N)

# Step 2: confirm the new profile is in effect
curl -s http://localhost:8080/v1/info | jq '.preset_aliases, .layer_split, .mode'
# Expect: preset_aliases now includes "dense-27b-q5" (or similar);
#          layer_split non-empty (the new tensor_split); mode=combined (if peer is up)
```

### Test 3: send a request after the profile switch

```bash
# The engine should now be running the DENSE model. A chat-completion
# request should produce DENSE-format output (different tokenization,
# different vocabulary than the MoE model).
curl -s http://localhost:8080/v1/chat/completions \
  -H "Content-Type: application/json" \
  -d '{
    "model": "dense-27b-q5",
    "messages": [{"role": "user", "content": "Hello"}],
    "max_tokens": 10
  }' | jq -r '.choices[0].message.content'
# Expect: a coherent response (the engine is on the DENSE model)
```

### Expected log lines (engine side)

```
hydra: applying pending config (tier='T3', age=...ms, payload_size=...)
hydra: T3 rebuild — tearing down COMBINED before model reload (was head_attached=1, static=0)
hydra: T3 rebuild: staged n_cpu_moe=8 (informational; expert routing via override_tensor)
hydra: T3 rebuild — re-attaching COMBINED on new model
hydra: T3 rebuild: COMBINED re-attached on peer localhost:9506 (N layers bound)
hydra: T3 rebuild applied (model='/models/Qwopus3.6-27B-Coder-Compat-MTP-Q5_K_M.gguf', split_mode=0, n_gpu_layers=65, slots=N)
```

### T3 failure recovery

If the model reload fails (e.g. the new GGUF path doesn't exist or
the file is corrupted), the engine rolls back to the old model:

```
hydra: T3 reload to '/path/to/new.gguf' failed; rolling back to old model
```

If the rollback itself fails (catastrophic), the engine exits via
`GGML_ABORT`. The operator must restart the engine and the C# side.

If the peer is unreachable during the COMBINED reattach, the engine
stays solo (fail-open, same as SET_EXPERT_MODE):

```
hydra: T3 rebuild: peer localhost:9506 unreachable; staying solo
```

The COMBINED mode reverts to SOLO without breaking the engine.
The Coordinator's `ReportsSolo()` path detects the fallback.

---

## Pass / fail criteria

The E2E test PASSES when:

1. **T1**: per-request `temperature`, `top_p`, `top_k`, `seed`, `stop`,
   `max_tokens` overrides all apply correctly. The engine uses the
   per-request values on the next token, with no engine restart.
2. **T2**: an `n_ctx` / `cache_type_k` / `cache_type_v` / RoPE / YaRN
   change while slots are busy is deferred, the engine rebuilds the
   context (free + recreate via `llama_new_context_with_model` +
   per-slot sampler re-init) once all slots are free, and the new
   config is in effect for the next request.
3. **T3**: a `model.path` + `split_mode` + `tensor_split` +
   `override_tensor` change while slots are busy is deferred, the
   engine tears down the COMBINED-mode bindings, fully reloads
   `llama_model` via `load_model()`, reattaches the COMBINED-mode
   bindings, and the new model is in effect for the next request.
4. **Backward compat**: the legacy `state_chunk_size` startup call
   (`WorkerSchedulerService.cs:2842`) still works — the engine
   returns the new response shape with `tier="T1"` and
   `params_applied={"state_chunk_size":<post-clamp>}`.

The E2E test FAILS when:

- Any T1 override silently ignored (the engine uses the default)
- `request_overrides_failed` log line appears for a T1 request
- T2/T3 deferred rebuild never fires (the engine returns success
  but the new config isn't in effect for the next request)
- `hydra: T2 rebuild applied` / `hydra: T3 rebuild applied` log
  lines don't appear within the drain timeout
- `state_chunk_size` startup call returns a different shape than
  the legacy contract expected
- A `GGML_ABORT` from `apply_t{2,3}_rebuild` indicates a
  catastrophic failure — the engine is in a bad state and must
  be restarted

## What this test does NOT cover

- Multi-engine (0x44 SET_EXPERT_MODE) interaction with 0x40 — the
  two opcodes are independent but exercise the same engine context.
  The SET_EXPERT_MODE path is unchanged by this work; this E2E
  only tests the 0x40 path.
- The C# `EngineConfigApplier` service's failure modes (RPC error,
  unknown alias, peer unreachable). The unit tests in
  `EngineConfigApplierTests.cs` cover these; the E2E only validates
  the happy path.
- T2/T3 mid-decode safety. The design has a hard contract that the
  rebuild waits for slot-free; the E2E does not attempt to violate
  that. Unit tests for the "drain timeout" and "deferred_keys echo"
  cover the safety boundary.
- The profile switch trigger (e.g. `bash scripts/set-profile.sh
  dense` calling the applier instead of restarting). The applier
  is implemented; wiring it to the profile switch script is a
  follow-up.

## Cross-references

- Fork-side:
  - `ddvnguyen/llama.cpp#41` (PR: wire schema + best-effort stub)
  - `ddvnguyen/llama.cpp#42` (PR: T2/T3 apply path)
  - `ddvnguyen/llama.cpp#40` (issue: fork-side Phase 2b tracking)
- Parent-side:
  - `ddvnguyen/hydra_vortex#406` (PR: docs — the wire schema)
  - `ddvnguyen/hydra_vortex#407` (PR: applier + per-request overrides)
  - `ddvnguyen/hydra_vortex#397` (issue: parent tracker)
  - `ddvnguyen/hydra_vortex#402` (PR: Phase 2a — `EngineConfig` + `ModelRegistry`)
- Spec: `specs/rpc-protocol.md` (the 0x40 CONFIGURE entry post-#406)
- Design RFC: `docs/phase-2b/design-rfc.md`
