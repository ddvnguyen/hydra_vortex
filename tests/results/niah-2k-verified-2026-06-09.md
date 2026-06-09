## Eval Test: NIAH-2000 (2026-06-09T17:04:42Z)

| Field | Value |
|-------|-------|
| **Prompt** | 2,000 tokens (~6,000 chars) of software engineering paragraphs + passkey |
| **Needle** | Hidden at 50% depth, repeated at top |
| **Passkey** | `NIAH-TEST` |
| **Model** | Qwopus3.6-35B-A3B-v1-APEX-I-Balanced.gguf |
| **Fingerprint** | b9520-32b1f19aa |
| **Expected** | RTX prefill → KV save → P100 KV restore → P100 decode |

---

### llama-server Metrics (Ground Truth — Source of Truth)

| Metric | RTX Pre | RTX Post | RTX Δ | P100 Pre | P100 Post | P100 Δ | Check |
|--------|---------|----------|-------|----------|-----------|--------|-------|
| `prompt_tokens_total` | 958 | 1916 | **+958** | 1 | 2 | **+1** | ✓/✓ |
| `tokens_predicted_total` | 1 | 2 | **+1** | 200 | 400 | **+200** | ✓/✓ |

**Interpretation:**
- RTX `prompt_tokens_total` +958: RTX prefilled the full prompt
- P100 `prompt_tokens_total` +1: P100 hit KV cache (no re-prefill)
- RTX `tokens_predicted_total` +1: RTX did NOT decode (only 1-token prefill completion)
- P100 `tokens_predicted_total` +200: P100 DID decode

---

### llama-server Slot State

| Node | Pre-test | Post-test |
|------|----------|-----------|
| RTX | slot 0: n_past=0 proc=False
slot 1: n_past=958 proc=False | slot 0: n_past=0 proc=False
slot 1: n_past=958 proc=False |
| P100 | slot 0: n_past=1157 proc=False | slot 0: n_past=1157 proc=False |

---

### llama-server Logs — RTX `:8080`

```
[34m8.02.493.674[0m [32mI [0msrv  proxy_reques: proxying request to model balanced on port 60671
[60671] [34m7.06.295.464[0m [32mI [0msrv  update_slots: all slots are idle
[60671] [34m7.06.295.488[0m [32mI [0msrv  log_server_r: done request: GET /slots 127.0.0.1 200
[34m8.02.493.944[0m [32mI [0msrv  log_server_r: done request: GET /slots 10.89.1.5 200
[34m8.03.222.939[0m [32mI [0msrv  proxy_reques: proxying request to model balanced on port 60671
[60671] [34m7.07.024.726[0m [32mI [0msrv  update_slots: all slots are idle
[34m8.03.232.537[0m [32mI [0msrv  proxy_reques: proxying request to model balanced on port 60671
[60671] [34m7.07.034.332[0m [32mI [0msrv  update_slots: all slots are idle
[60671] [34m7.07.034.357[0m [32mI [0msrv  log_server_r: done request: GET /slots 127.0.0.1 200
[34m8.03.232.811[0m [32mI [0msrv  log_server_r: done request: GET /slots 10.89.1.5 200
[34m8.03.562.820[0m [32mI [0msrv  proxy_reques: proxying request to model balanced on port 60671
[60671] [34m7.07.364.639[0m [32mI [0msrv  update_slots: all slots are idle
[60671] [34m7.07.364.662[0m [32mI [0msrv  log_server_r: done request: GET /slots 127.0.0.1 200
[34m8.03.563.119[0m [32mI [0msrv  log_server_r: done request: GET /slots 10.89.1.5 200
[34m8.03.563.817[0m [32mI [0msrv  proxy_reques: proxying request to model balanced on port 60671
[60671] [34m7.07.387.559[0m [32mI [0mslot get_availabl: id  1 | task -1 | selected slot by LCP similarity, sim_best = 1.000 (> 0.400 thold), f_keep = 1.000
[60671] [34m7.07.387.599[0m [32mI [0mslot launch_slot_: id  1 | task -1 | sampler chain: logits -> penalties -> ?dry -> ?top-n-sigma -> top-k -> ?typical -> top-p -> min-p -> ?xtc -> temp-ext -> dist 
[60671] [34m7.07.387.603[0m [32mI [0mslot launch_slot_: id  1 | task -1 | sampler params: 
[60671] [34m7.07.387.605[0m [32mI [0mslot launch_slot_: id  1 | task 81 | processing task, is_child = 0
[60671] [34m7.07.387.609[0m [32mI [0mslot update_slots: id  1 | task 81 | new prompt, n_ctx_slot = 262144, n_keep = 0, task.n_tokens = 958
[60671] [34m7.07.387.611[0m [35mW slot update_slots: id  1 | task 81 | forcing full prompt re-processing due to lack of cache data (likely due to SWA or hybrid/recurrent memory, see https://github.com/ggml-org/llama.cpp/pull/13194#issuecomment-2868343055)
[60671] [0m[34m7.07.387.613[0m [32mI [0mslot update_slots: id  1 | task 81 | cached n_tokens = 0, memory_seq_rm [0, end)
[60671] [34m7.07.387.928[0m [32mI [0mslot init_sampler: id  1 | task 81 | init sampler, took 0.11 ms, tokens: text = 958, total = 958
[60671] [34m7.10.917.955[0m [32mI [0mslot print_timing: id  1 | task 81 | prompt eval time =    3530.34 ms /   958 tokens (    3.69 ms per token,   271.36 tokens per second)
[60671] [34m7.10.917.958[0m [32mI [0mslot print_timing: id  1 | task 81 |        eval time =       0.00 ms /     1 tokens (    0.00 ms per token, 1000000.00 tokens per second)
```

---

### llama-server Logs — P100 `:8086`

```
8.05.497.872 I srv  update_slots: all slots are idle
8.08.081.774 I slot process_sing: id  0 | task -1 | erasing 1 existing checkpoints before STATE_PUT restore
8.08.152.796 I slot create_check: id  0 | task -1 | created context checkpoint 1 of 32 (pos_min = 0, pos_max = 957, n_tokens = 958, size = 62.813 MiB)
8.08.152.800 I srv  process_sing: hydra: STATE_PUT slot=0 restored=76306884 B n_past=958 n_prompt_tok=958
8.08.154.673 I srv  update_slots: all slots are idle
8.08.156.267 I srv  log_server_r: done request: PUT /slots/0/state 192.168.122.1 200
8.08.180.415 I slot get_availabl: id  0 | task -1 | selected slot by LCP similarity, sim_best = 1.000 (> 0.100 thold), f_keep = 1.000
8.08.180.468 I slot launch_slot_: id  0 | task -1 | sampler chain: logits -> penalties -> ?dry -> ?top-n-sigma -> top-k -> ?typical -> top-p -> min-p -> ?xtc -> temp-ext -> dist 
8.08.180.474 I slot launch_slot_: id  0 | task -1 | sampler params: 
8.08.180.476 I slot launch_slot_: id  0 | task 277 | processing task, is_child = 0
8.08.180.479 I slot update_slots: id  0 | task 277 | new prompt, n_ctx_slot = 180224, n_keep = 0, task.n_tokens = 958
8.08.180.482 I slot update_slots: id  0 | task 277 | Checking checkpoint with [0, 957] against 957...
8.08.203.336 W slot update_slots: id  0 | task 277 | restored context checkpoint (pos_min = 0, pos_max = 957, n_tokens = 958, n_past = 957, size = 62.813 MiB)
8.08.203.340 I slot update_slots: id  0 | task 277 | cached n_tokens = 957, memory_seq_rm [957, end)
8.08.203.573 I slot init_sampler: id  0 | task 277 | init sampler, took 0.16 ms, tokens: text = 958, total = 958
8.11.836.820 I slot print_timing: id  0 | task 277 | n_decoded =    100, tg =  27.83 t/s
8.14.848.342 I slot print_timing: id  0 | task 277 | n_decoded =    183, tg =  27.71 t/s
8.15.460.565 I slot print_timing: id  0 | task 277 | prompt eval time =      63.06 ms /     1 tokens (   63.06 ms per token,    15.86 tokens per second)
8.15.460.570 I slot print_timing: id  0 | task 277 |        eval time =    7217.02 ms /   200 tokens (   36.09 ms per token,    27.71 tokens per second)
8.15.460.571 I slot print_timing: id  0 | task 277 |       total time =    7280.07 ms /   201 tokens
8.15.460.571 I slot print_timing: id  0 | task 277 |    graphs reused =        395
8.15.460.612 I slot      release: id  0 | task 277 | stop processing: n_tokens = 1157, truncated = 0
8.15.460.624 I srv  update_slots: all slots are idle
8.15.460.702 I srv  log_server_r: done request: POST /v1/chat/completions 192.168.122.1 200
8.15.472.156 I srv  update_slots: all slots are idle
```

---

### Hydra Core Timeline

```
event=request_timeline trace_id=64888f59a5ed4abb session_id=sess_7645c7aef6335c18993785be queue_wait_ms=0 node=rtx route_type=cold_concurrency prefill_ms=3555 save_kv_ms=3710 restore_kv_ms=3913 decode_ms=11218 total_ms=11218
```

---

### Response Quality

| Check | Result |
|-------|--------|
| HTTP | 200 |
| finish_reason | length |
| prompt_tokens | 958 |
| completion_tokens | 200 |
| cached_tokens | 957 |
| prompt_ms | 63.058ms |
| cache_n | 957 |
| Reasoning | ✓ 200 chars |
| Content |  |
| Reasoning (first) | 1、在pom.xml中引入依赖： <dependency>     <groupId>org.springframework.boot</groupId>     <artifactId>spring-boot-starter-data-redis</artifactId> </dependency> 2、在application.yml中配置redis连接信息： spring:   redis: |

---

### Overall: ✓ **PASS** — P/D split verified via llama-server metrics

