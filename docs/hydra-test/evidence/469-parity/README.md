# #469 parity smoke — RPC PREFILL checkpoint honesty at fork HEAD `45c1435da`

**Verdict: PASS (10/10 asserts).** Live-GPU evidence for issue #751, closing the
runtime-verification gap on the #469 fix (fork PR #60, commit `b431f220ec`) under
the **current** hydra-fork HEAD.

- **What was verified:** on a real hybrid SSM+attention model (Qwen3.5-9B — Gated
  Delta Net + attention, the exact arch class where #469 bit), the RPC PREFILL
  opcode 0x42 reports an **honest** `n_past` equal to the HTTP arm's
  `usage.prompt_tokens` at two prompt lengths (18 and 171 tokens), and an RPC
  0x43 DECODE continued from the RPC-primed slot produces **token-identical**
  output (same seed 42, temperature 0.0) to a plain HTTP completion.
- **The #469 signature, held at HEAD:** `n_past(18) == prompt_tokens(18)` and
  `n_past(171) == prompt_tokens(171)`. The #469 incident was RPC reporting
  `prompt_n=1` vs HTTP `4` — a dishonest checkpoint `pos_max`. Engine traces
  (`engine-head-sm60.log`) show the PR #60 split-loop behavior: prefill creates a
  checkpoint with `pos_min=16/pos_max=17` (n_tokens=18) and
  `pos_min=169/pos_max=170` (n_tokens=171), decode restores to `n_past=17` /
  `n_past=170` and re-decodes the held-back last token as its own micro-batch
  (`#PD-TRACE N_COMMON 1-token re-decode`), then generates 16 tokens identical to
  the HTTP arm.
- **Why:** #469 (P/D-split hallucinated output) was fixed in fork PR #60 but only
  verified at that commit's build; follow-up `45c1435da` (#644) hardened checkpoint
  `pos_min` for hybrid archs. #751 requires proof the behavior holds at runtime on
  the current HEAD build before #469 can be closed.

## Environment

| item | value |
|---|---|
| engine | `llama-server` **version 9697 (`45c1435da`) [shared]**, GNU 14.3.0, CUDA 12.9, sm_60 (see `build-stamp.txt`) |
| binary md5 | `726b21033adf3cdcfa989ba725136a43` |
| hardware | Tesla P100-PCIE-16GB (VM `hydra-p100`, 192.168.122.21) |
| model | `/mnt/kv_slots/Qwen3.5-9B-Q4_K_M.gguf` (hybrid Gated Delta Net + attention) |
| engine flags | `--host 127.0.0.1 --port 18086 --rpc-port 19513 -t 3 -c 65536 --cache-type-k q8_0 --cache-type-v q8_0 --cont-batching --alias hydra-test-lane` (test lane only) |
| driver | `parity-driver.py` (python3 stdlib only), final md5 `0d6552498fa7a2d43954b35d135eb622` |
| PASS run | `parity-run-164940.log` (2026-09-09 16:49:34–16:49:40 UTC, 6.6 s) |
| dates | engine built 2026-09-09 ~13:40–14:10Z; parity runs 2026-09-09 15:34–16:49Z |
| prod | **untouched** — prod engines pid 1603 (:8090) and pid 1685 (:8086/rpc 9502, `/home/vm1/llama-cpp-hydra-sm60`) verified alive before and after; test lane used only 18086/19513 |

## Asserts (2 prompts × 5)

| prompt | prefill status 0x00 | **n_past == prompt_tokens** | decode status 0x00 + valid | completion_tokens equal | decoded text equal |
|---|---|---|---|---|---|
| short (18 prompt tok) | PASS | **PASS 18==18** | PASS (Gate A all-match) | PASS 16==16 | PASS |
| long (171 prompt tok) | PASS | **PASS 171==171** | PASS (Gate A all-match) | PASS 16==16 | PASS |

## How to reproduce

1. On the VM, start the staged engine (test-lane ports only):
   `cd /home/vm1/hydra-fork-head-sm60 && LD_LIBRARY_PATH=. nohup ./llama-server -m /mnt/kv_slots/Qwen3.5-9B-Q4_K_M.gguf --host 127.0.0.1 --port 18086 --rpc-port 19513 -t 3 -c 65536 --cache-type-k q8_0 --cache-type-v q8_0 --cont-batching --alias hydra-test-lane > nodeA.log 2>&1 &`
   wait for `server is listening on http://127.0.0.1:18086` + `hydra rpc: unified server on 0.0.0.0:19513`.
2. `python3 /home/vm1/parity-driver.py` (or set `HYDRA_HOST`/`HYDRA_HTTP_PORT`/`HYDRA_RPC_PORT`/`PARITY_LOG_DIR`).
   Per prompt: (A) HTTP `POST /v1/chat/completions` (seed 42, temp 0.0, max_tokens 16) → text + usage;
   (B) RPC 0x42 PREFILL same messages → assert `n_past == usage.prompt_tokens`, drain the ~100 MB KV+logits blob;
   (C) RPC 0x43 DECODE (v3 hdr, empty KV segment → continue from resident slot) → `GET /v1/decode/{id}` → assert
   completion_tokens and full generated text equal the HTTP arm. Exit 0 only on 10/10.
   Note: prompt content carries a `/no_think` suffix on ALL arms so HTTP/0x42/0x43 tokenize identically;
   the template does not honor it, so the 16 tokens are thinking tokens (see below).
3. Traces: `grep -E "#PD-TRACE (N_COMMON|PREFILL)" nodeA.log`.

## Driver iteration notes (harness bugs fixed during this run — no engine involvement)

- **v1 md5 `ddc9f6e2...`:** request header packed `<HBBQHH` (keyLen u64 / payloadLen u16) vs the server's
  `<HBBHQH>` layout (`server-rpc.h` / `hydra_handle_connection` @45c1435da) — server parsed
  payloadLen ≈ L×2⁴⁸, `std::string` alloc threw, swallowed by the RPC pool's catch-all → 300 s client timeout. Fixed in v2.
- **v2 md5 `ca51e6b8...`:** framing fixed; text assert compared `message.content` only — empty on the HTTP arm
  because all 16 tokens are thinking tokens in `message.reasoning_content`.
- **v3 md5 `88129649...`:** compared reasoning+content — exposed that the `/v1/decode` endpoint reports the
  **same** tokens in both `content` and `reasoning_content` (completion_tokens=16 unchanged), doubling only the
  text fields.
- **v4 md5 `0d655249...` (committed here):** `canon_text()` dedupes the endpoint's field duplication (prefix rule),
  applied identically to both arms, and logs the raw fields (visible in `parity-run-164940.log`).

## Server-side finding (separate from #469 — recommend follow-up issue)

Three test-engine instances (pids 19624, 22714, 22977) died with
`terminate called after throwing 'nlohmann::json type_error.302': type must be number, but is string`
in `server_context_impl::process_single_task` (uncaught on the main task-queue thread) after a 0x43 DECODE
frame whose Gate-A metadata carried `model_capabilities` as a **string** (sent by an ad-hoc diagnostic probe,
not by this driver). One malformed frame therefore kills the whole engine and all its slots. Also observed:
`PREFILL M2: hash pre-pass hashed 0 B` warnings that leave the KV stream/`kv_hash_str` degraded without
failing the response. Crash logs preserved on the VM: `~/hydra-test-lane/nodeA-469{,-rerun,-rerun2}.log`.
The parity path with well-formed frames (this driver) never triggered either issue.

## Links

- Issue #751 (this evidence) · Issue #469 (verified fix; closed by lead with this link)
- Fork PR #60 = commit `b431f220ec` (ancestor of `45c1435da`, verified via `git merge-base --is-ancestor`)
- Fork HEAD `45c1435daba24fd05d292b34b328524e099150e4` (github.com/ddvnguyen/llama.cpp)
- Protocol: `specs/rpc-protocol.md` §0x42/0x43; server impl `tools/server/server-context.cpp`, `tools/server/server-rpc.h`
