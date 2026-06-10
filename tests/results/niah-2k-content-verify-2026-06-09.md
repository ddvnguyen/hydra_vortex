## Eval Test: NIAH-2000 Content Verdict (2026-06-09T17:10:19Z)

| Field | Value |
|-------|-------|
| **Prompt** | 2,000 tokens (~6,400 chars) software engineering haystack |
| **Passkey** | `VERIFY-3EF0` at 50% depth + top |
| **Model** | Qwopus3.6-35B-A3B-v1-APEX-I-Balanced.gguf |
| **Fingerprint** | b9520-32b1f19aa |
| **Expected** | RTX prefill → P100 decode + passkey retrieved |

---

### 1. P/D Split Verification (llama-server Metrics — Ground Truth)

| Metric | RTX Pre | RTX Post | RTX Δ | P100 Pre | P100 Post | P100 Δ | Criterion |
|--------|---------|----------|-------|----------|-----------|--------|-----------|
| `prompt_tokens_total` | 1916 | 2870 | **+954** | 86 | 87 | **+1** | RTX Δ>100✓ P100 Δ≤5✓ |
| `tokens_predicted_total` | 2 | 3 | **+1** | 827 | 1105 | **+278** | RTX Δ≤5✓ P100 Δ≥3✓ |

**P/D Split: ✓ **PASS****

---

### 2. Content Quality Verification (Model Output — This Is As Important as P/D)

| Check | Result | Criterion |
|-------|--------|-----------|
| Content present | ✗ 0 chars | Non-empty response |
| Reasoning present | ✓ 914 chars | >50 chars of thinking |
| Passkey recall | ✗ Not found | Passkey `VERIFY-3EF0` in output |
| Finish reason | ✓ stop | Must be `stop` |
| prompt_ms | 63.982ms | ⚠ possible re-prefill |
| cached_tokens | 953 | ✓ KV cache used |

**Content Quality: ✗ **FAIL** — no-content passkey-missing**

**Content** (0 chars): 

**Reasoning** (914 chars): 1、在pom.xml中引入依赖： <dependency>     <groupId>org.springframework.boot</groupId>     <artifactId>spring-boot-starter-data-redis</artifactId> </dependency> 2、在application.properties配置文件中添加Redis的配置信息： spring.redis.host=192.168.1.100 spring.redis.port=6379 spring.redis.password=123456 3、在Java代码中注入RedisTem

---

### 3. llama-server Logs — Evidence from Both Nodes

#### RTX `:8080`
```
[34m13.35.163.684[0m [32mI [0msrv  proxy_reques: proxying request to model balanced on port 60671
[60671] [34m12.38.965.533[0m [32mI [0msrv  update_slots: all slots are idle
[34m13.35.177.037[0m [32mI [0msrv  proxy_reques: proxying request to model balanced on port 60671
[60671] [34m12.38.978.843[0m [32mI [0msrv  update_slots: all slots are idle
[60671] [34m12.38.978.864[0m [32mI [0msrv  log_server_r: done request: GET /slots 127.0.0.1 200
[34m13.35.177.319[0m [32mI [0msrv  log_server_r: done request: GET /slots 10.89.1.5 200
[34m13.35.517.655[0m [32mI [0msrv  proxy_reques: proxying request to model balanced on port 60671
[60671] [34m12.39.319.430[0m [32mI [0msrv  update_slots: all slots are idle
[60671] [34m12.39.319.452[0m [32mI [0msrv  log_server_r: done request: GET /slots 127.0.0.1 200
[34m13.35.517.911[0m [32mI [0msrv  log_server_r: done request: GET /slots 10.89.1.5 200
[34m13.35.518.435[0m [32mI [0msrv  proxy_reques: proxying request to model balanced on port 60671
[60671] [34m12.39.342.535[0m [32mI [0mslot get_availabl: id  0 | task -1 | selected slot by LRU, t_last = -1
[60671] [34m12.39.342.543[0m [32mI [0msrv        update:  - cache state: 0 prompts, 0.000 MiB (limits: 8192.000 MiB, 360192 tokens, 8589934592 est)
[60671] [34m12.39.342.544[0m [32mI [0msrv  get_availabl: prompt cache update took 0.01 ms
[60671] [34m12.39.342.581[0m [32mI [0mslot launch_slot_: id  0 | task -1 | sampler chain: logits -> penalties -> ?dry -> ?top-n-sigma -> top-k -> ?typical -> top-p -> min-p -> ?xtc -> temp-ext -> dist 
[60671] [34m12.39.342.587[0m [32mI [0mslot launch_slot_: id  0 | task -1 | sampler params: 
[60671] [34m12.39.342.590[0m [32mI [0mslot launch_slot_: id  0 | task 138 | processing task, is_child = 0
[60671] [34m12.39.342.591[0m [32mI [0mslot slot_save_an: id  1 | task -1 | saving idle slot to prompt cache
[60671] [34m12.39.342.794[0m [35mW srv   prompt_save:  - saving prompt with length 958, total state size = 72.772 MiB (draft: 0.000 MiB)
[60671] [0m[34m12.39.382.408[0m [32mI [0mslot prompt_clear: id  1 | task -1 | clearing prompt with 958 tokens
```

#### P100 `:8086`
```
13.40.681.668 I slot create_check: id  0 | task -1 | created context checkpoint 1 of 32 (pos_min = 0, pos_max = 953, n_tokens = 954, size = 62.813 MiB)
13.40.681.674 I srv  process_sing: hydra: STATE_PUT slot=0 restored=76263284 B n_past=954 n_prompt_tok=954
13.40.684.067 I srv  update_slots: all slots are idle
13.40.685.922 I srv  log_server_r: done request: PUT /slots/0/state 192.168.122.1 200
13.40.709.349 I slot get_availabl: id  0 | task -1 | selected slot by LCP similarity, sim_best = 1.000 (> 0.100 thold), f_keep = 1.000
13.40.709.437 I slot launch_slot_: id  0 | task -1 | sampler chain: logits -> penalties -> ?dry -> ?top-n-sigma -> top-k -> ?typical -> top-p -> min-p -> ?xtc -> temp-ext -> dist 
13.40.709.443 I slot launch_slot_: id  0 | task -1 | sampler params: 
13.40.709.446 I slot launch_slot_: id  0 | task 963 | processing task, is_child = 0
13.40.709.449 I slot update_slots: id  0 | task 963 | new prompt, n_ctx_slot = 180224, n_keep = 0, task.n_tokens = 954
13.40.709.452 I slot update_slots: id  0 | task 963 | Checking checkpoint with [0, 953] against 953...
13.40.732.367 W slot update_slots: id  0 | task 963 | restored context checkpoint (pos_min = 0, pos_max = 953, n_tokens = 954, n_past = 953, size = 62.813 MiB)
13.40.732.372 I slot update_slots: id  0 | task 963 | cached n_tokens = 953, memory_seq_rm [953, end)
13.40.732.595 I slot init_sampler: id  0 | task 963 | init sampler, took 0.15 ms, tokens: text = 954, total = 954
13.43.261.195 I srv  log_server_r: done request: GET /slots 192.168.122.1 200
13.44.678.393 I slot print_timing: id  0 | task 963 | n_decoded =    100, tg =  25.61 t/s
13.47.698.943 I slot print_timing: id  0 | task 963 | n_decoded =    168, tg =  24.26 t/s
13.50.733.103 I slot print_timing: id  0 | task 963 | n_decoded =    248, tg =  24.90 t/s
13.51.859.476 I slot print_timing: id  0 | task 963 | prompt eval time =      63.98 ms /     1 tokens (   63.98 ms per token,    15.63 tokens per second)
13.51.859.481 I slot print_timing: id  0 | task 963 |        eval time =   11086.03 ms /   278 tokens (   39.88 ms per token,    25.08 tokens per second)
13.51.859.482 I slot print_timing: id  0 | task 963 |       total time =   11150.02 ms /   279 tokens
```

#### Hydra Core Timeline
```
event=request_timeline trace_id=15682f581e614663 session_id=sess_dda9db421aa481a482940684 queue_wait_ms=0 node=rtx route_type=cold_concurrency prefill_ms=4104 save_kv_ms=4266 restore_kv_ms=4487 decode_ms=15661 total_ms=15661
```

---

### 4. Final Verdict: ⚠ **PARTIAL** — P/D split PASS but content quality FAIL — no-content passkey-missing

