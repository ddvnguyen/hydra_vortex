# POC Results — Verified Facts

## Test Environment
- Model: Darwin-36B-Opus-APEX-I-Balanced.gguf (qwen35moe)
- RTX 5060 Ti: CUDA 13.2, sm_120, flash-attn, n-cpu-moe 24
- Tesla P100 (VM): CUDA 12.9, sm_60, n-cpu-moe 28
- KV cache: q8_0 for both K and V

## P100 Prefill (no cache): 110 tok/s
```
prompt_n: 2968, prompt_ms: 27009, prompt_per_second: 110
Projected: 80K tokens ≈ 12 minutes
```

## P100 Decode: 28 tok/s
```
predicted_per_token_ms: 36.1, predicted_per_second: 27.7
```

## Cross-GPU Save/Restore: WORKS ✅
```
Save on RTX → restore on P100 → continuation: cache_n=2964, prompt_ms=221ms
Speedup: 122× vs full re-prefill
```

## SSM Truncation: BROKEN ❌
```
Trigger:  n_tokens ≤ n_past (same-length prompt)
Log:      "failed to truncate tokens with position >= N - clearing the memory"
Result:   cache_n=0, full re-prefill
Affects:  --cache-prompt, --cache-reuse on qwen35moe
Root:     Mamba SSM recurrent state doesn't support partial removal
```

## Critical Constraint
```
n_tokens >  n_past → cache reused ✅
n_tokens == n_past → cache nuked  ❌
n_tokens <  n_past → cache nuked  ❌
```
