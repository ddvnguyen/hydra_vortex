# Phase 2b Live E2E Test — Runtime Engine Reconfigure (T1/T2/T3)

**Status**: ready for live E2E
**Scope**: validate the 0x40 CONFIGURE wire path end-to-end on the live
5060 Ti + RTX 3060 stack (the same-host COMBINE pair).
**Prereqs**:
- `ddvnguyen/llama.cpp#41` (the fork-side C++ work) merged on
  `hydra-fork`
- `ddvnguyen/hydra_vortex` parent PR (Phase 2b C# work) merged on
  `main`, with the submodule pointer bumped to the fork SHA that
  includes the C++ work
- The new `llama-engine` binary built (sm_120 + sm_60) and pushed
  via the standard deploy path

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
cat /opt/hydra/bin/llama-engine | strings | grep -E "v0\.[0-9]+|commit" | head -3
# Expect: build SHA matches the parent submodule pointer

# 3. Check the engine INFO advertises the new 0x40 support
# (Phase 2b adds an "engine_configure" capability in the response)
curl -s http://localhost:8080/v1/info | jq '.capabilities'
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

**WARNING**: this test changes the engine's `n_ctx`. After running it,
the engine's context size is whatever the override set (the engine
doesn't auto-revert). Restart the engine to restore the default.

### Pre-flight

```bash
# Confirm no slots are in-flight
curl -s http://localhost:8080/v1/info | jq '.slots | length'
# Expect: the slot count (typically 1 or 2)
```

### Test 1: defer then trigger

```bash
# Step 1: send a T2 request (n_ctx change) while slots are busy
# (start a long-running request first to occupy a slot)
LONG_REQ=$(curl -s -X POST http://localhost:8080/v1/chat/completions \
  -H "Content-Type: application/json" \
  -d '{
    "model": "moe-35b-mini",
    "messages": [{"role": "user", "content": "Write a 2000-token essay."}],
    "max_tokens": 2000
  }' &)

# Step 2: while the long request is in flight, try to override n_ctx
# via the admin endpoint (Phase 2b will add /admin/engine-configure
# in a follow-up; for now, the C# side drives 0x40 automatically
# based on the EngineConfigApplier service — see the C# PR for the
# test hook).
sleep 1  # let the long request start
# (Admin endpoint call goes here)

# Step 3: the engine returns tier=T2 and deferred_keys=[n_ctx]
# Step 4: when the long request finishes, the engine rebuilds the
#         context (visible in the engine logs as "hydra: T2 rebuild
#         applied (n_ctx=...)").

# Step 5: wait for the long request to finish
wait $LONG_REQ
```

### Expected log lines (engine side)

```
hydra: CONFIGURE received (slot 0) (Phase 2b)
hydra: CONFIGURE deferred n_ctx=131072 (waiting for slot-free)
hydra: applying pending config (T2=yes T3=no, age=12453ms)
hydra: T2 rebuild applied (n_ctx=131072, cache=q8_0/q8_0)
```

If the request never gets to a slot-free state (e.g. another long
request keeps starting), the drain timeout kicks in:

```
hydra: pending CONFIGURE drain timed out (>300s); discarding
```

In that case, the operator can increase the timeout via
`HYDRA_COORD_PROFILE_SWITCH_DRAIN_TIMEOUT` (default 300s).

---

## T3 test — profile switch (MoE → DENSE) without restart

**Goal**: switch the engine from the MoE profile (COMBINE-OT expert
routing) to the DENSE profile (COMBINE-static layer split) without
restarting the engine process.

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

### Test 1: trigger a T3 override

```bash
# Step 1: send a T3 override via the C# side. In Phase 2b's first
# land, this is driven by the EngineConfigApplier service when the
# operator runs `bash scripts/set-profile.sh dense` (which is updated
# in a follow-up PR to call the applier instead of restarting).

# For now: use a test harness that calls the applier directly.
# (The exact harness is the EngineConfigApplierTests.cs integration
# test, plus an admin endpoint to be added in a follow-up.)

# The applier sends 0x40 with:
# {
#   "model": {"path": "/models/Qwopus3.6-27B-Coder-Compat-MTP-Q5_K_M.gguf"},
#   "split_mode": "layer",
#   "tensor_split": [25.0, 40.0],
#   "n_ctx": 96000,
#   "n_gpu_layers": 65,
#   "override_tensor": "token_embd\\.weight=CPU,output\\.weight=CPU,output_norm\\.weight=CPU"
# }

# The engine returns:
# {
#   "success": true,
#   "tier": "T3",
#   "params_applied": {"state_chunk_size": 2097152},
#   "deferred_keys": ["model", "split_mode", "tensor_split", "n_ctx", "n_gpu_layers", "override_tensor"]
# }

# Step 2: wait for the slot-free moment. The engine logs:
#   hydra: applying pending config (T2=no T3=yes, age=1234ms)
#   hydra: T3 rebuild applied (model='...', split_mode=layer)

# Step 3: confirm the new profile
sleep 5  # let the rebuild complete
curl -s http://localhost:8080/v1/info | jq '.preset_aliases, .layer_split, .mode'
# Expect: preset_aliases now includes "dense-27b-q5" (or similar);
#          layer_split non-empty; mode=combined (if peer is up)
```

### Test 2: send a request after the profile switch

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
hydra: CONFIGURE received (slot 0) (Phase 2b)
hydra: CONFIGURE deferred T3 keys (waiting for slot-free)
hydra: applying pending config (T2=no T3=yes, age=...ms)
hydra: T3 rebuild applied (model='/models/Qwopus3.6-27B-Coder-Compat-MTP-Q5_K_M.gguf', split_mode=layer)
```

---

## Pass / fail criteria

The E2E test PASSES when:

1. **T1**: per-request `temperature`, `top_p`, `top_k`, `seed`, `stop`,
   `max_tokens` overrides all apply correctly. The engine uses the
   per-request values on the next token, with no engine restart.
2. **T2**: an `n_ctx` change while slots are busy is deferred, and the
   engine rebuilds the context once all slots are free. The new
   `n_ctx` is in effect for the next request.
3. **T3**: a `model.path` + `split_mode` + `tensor_split` change while
   slots are busy is deferred, and the engine rebuilds the model once
   all slots are free. The new model is in effect for the next request.
4. **Backward compat**: the legacy `state_chunk_size` startup call
   (`WorkerSchedulerService.cs:2842`) still works — the engine
   returns the new response shape with `tier="T1"` and
   `params_applied={"state_chunk_size":<post-clamp>}`.

The E2E test FAILS when:

- Any T1 override silently ignored (the engine uses the default)
- `request_overrides_failed` log line appears for a T1 request
- T2/T3 deferred rebuild never fires (the engine returns success
  but the new config isn't in effect for the next request)
- `state_chunk_size` startup call returns a different shape than
  the legacy contract expected

## What this test does NOT cover

- Multi-engine (0x44 SET_EXPERT_MODE) interaction with 0x40 — the
  two opcodes are independent but exercise the same engine context.
  The SET_EXPERT_MODE path is unchanged by this PR; this E2E only
  tests the 0x40 path.
- The C# `EngineConfigApplier` service's failure modes (RPC error,
  unknown alias, peer unreachable). The unit tests in
  `EngineConfigApplierTests.cs` cover these; the E2E only validates
  the happy path.
- T2/T3 mid-decode safety. The design has a hard contract that the
  rebuild waits for slot-free; the E2E does not attempt to violate
  that. Unit tests for the "drain timeout" and "deferred_keys echo"
  cover the safety boundary.

## Cross-references

- Fork-side: `ddvnguyen/llama.cpp#41` (PR) + `#40` (issue)
- Parent-side docs: `ddvnguyen/hydra_vortex#406`
- Parent-side code (this work): see `git log feat/phase-2b-engine-config-applier`
- Spec: `specs/rpc-protocol.md` (the 0x40 CONFIGURE entry post-#406)
- Design RFC: `docs/phase-2b/design-rfc.md`
