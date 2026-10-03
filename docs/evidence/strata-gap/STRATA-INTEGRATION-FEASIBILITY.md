# Strata → Hydra integration feasibility (spike, read-only)

**Epic:** #811 (branch `docs/811-strata-integration` off `epic/811-strata-gap-analysis`).
**Owner-approved:** 2026-10-03. **Read-only spike** — no build, no server, no GPU/rig, no `/mnt/WorkDisk/strata` writes, no `/tmp`, no podman.

**Question (owner):** Strata v0.1.29 beats our llama.cpp fork 2.12× decode (spec-off 4K), 3.01×
prefill (12K) and 1.9× prefix TTFT on the RTX 3060 (`docs/evidence/strata-3060-ab/README.md`,
commit `37826a2a0` — **not on this epic branch**, read via `git show`). Should Strata become the
Flash-Next serving engine behind Hydra, or stay a reference whose ideas we port to the fork?

**Sources.** Strata `/mnt/WorkDisk/strata/src-v0.1.29` (tag `v0.1.29`, upstream
`github.com/Niko1221/Strata`). Hydra: this worktree (`src/head`, `src/core`, `specs/`,
`docs/`, `infra/`).

**Claim tags.** `[verified]` = `file:line` read directly for this document.
`[hypothesis]` = reasoned, not read. **NOT FOUND (scope)** = searched, absent.

**Scope caveats (read before trusting negatives):**
1. `src/llama-cpp` and `src/ik-llama-cpp` are **empty in this worktree** (`ls src/llama-cpp | wc -l`
   → 0). Every fork-side statement below is cited from Hydra's own specs/docs, **not** from fork
   source. Fork handler internals are `[gap]`.
2. Strata `--profile-decode` does not exist — it is a **fork** flag
   (`docs/evidence/strata-gap/IMPL-GAP-FORK.md:43`).
3. Where a number came from our own A/B run rather than source, it is tagged `[measured]`.

---

## 1. Serving surface

### 1.1 What Strata actually serves

The public surface is a **Python `http.server` wrapper**, not the C++ binary.
`serve/server.py` implements only two HTTP methods — `do_GET` (`serve/server.py:1275`) and
`do_POST` (`serve/server.py:1372`). There is **no `do_PUT`, no `do_DELETE`, no `do_HEAD`**
[verified — `grep -n "def do_" serve/server.py` returns exactly those two].

**Complete route table** [verified]:

| Method | Path | `file:line` | Notes |
|---|---|---|---|
| GET | `/fonts/*` | `serve/server.py:1277` | static web asset |
| GET | `/web/*` | `serve/server.py:1292` | static web asset |
| GET | `/metrics` | `serve/server.py:1310` | **JSON**, not Prometheus text (`self._json(200, svc.metrics(...))`, `:1313`; `metrics()` returns a `dict`, `:663`) |
| GET | `/settings` | `serve/server.py:1315` | auth-gated |
| GET | `/mcp` | `serve/server.py:1319` | auth-gated |
| GET | `""` (index) | `serve/server.py:1324` | web chat UI |
| GET | `/health` | `serve/server.py:1331` | `{"status":"ok","max_context","model","images","api_key"}` — **not** auth-gated |
| GET | `/status` | `serve/server.py:1334` | auth-gated |
| GET | `/v1/models`, `/models` | `serve/server.py:1348` | auth-gated |
| GET | `/props` | `serve/server.py:1356` | auth-gated; `total_slots: 1` at `:1407` |
| GET | `/slots` | `serve/server.py:1359` | returns `[{id:0,n_ctx,is_processing}]` (`:1364`) |
| GET | `/v1/status` | `serve/server.py:1366` | auth-gated |
| POST | `/settings` | `serve/server.py:1376` | own-page only (`:1417-1427`) |
| POST | `/v1/chat/completions` | `serve/server.py:1381` → `_openai` `:1450` | OpenAI-compatible |
| POST | `/v1/messages` | `serve/server.py:1383` → `_anthropic` `:1490` | Anthropic-compatible |
| * | *anything else* | `serve/server.py:1385`, `:1370` | 404 |

**NOT FOUND (serve/):** `/v1/completions`, `/v1/embeddings`, `/v1/apply_chat_template`,
`/v1/responses`, `/v1/decode/*`, `/models/load`, `/slots/{id}/state*`,
`/slots/{id}?action=erase`, any `PUT`/`DELETE` route, any `/version`.

**Named features:**

| Feature | Present? | Evidence |
|---|---|---|
| `/v1/chat/completions` streaming (SSE) | **Yes** | `_sse()` `serve/server.py:1444-1448`; `data: ` frames `:1477`; `data: [DONE]` `:1479`; keep-alive comment `:1475`; honoured only when `req.get("stream")` `:1469` |
| Tool calls | **Yes** | `openai_to_messages` extracts `tools` `serve/frontend.py:161-162`, parses `tool_calls` `:151-159`; `finish_reason:"tool_calls"` `serve/server.py:1109`; MCP tools opt-in `strata_mcp` `:1454-1462` |
| Anthropic `tools` | **Yes** | `serve/frontend.py:209-210` |
| Chat template — **Jinja, model-exported, not user-overridable per request** | **Yes** | `tpl = tpath / "chat_template.jinja"` else `ROOT/"serve/chat_template.jinja"` `serve/server.py:1727-1729`; template source echoed in `/props` `:1407`. **No `chat_template` request field is read.** |
| Reasoning / think tags | **Yes** | `reasoning_content` round-trip `serve/frontend.py:149-150`; streamed as its own delta `serve/server.py:1091`, collected `:1130`, `:1142`; `reasoning_effort` / `reasoning:{effort}` / `chat_template_kwargs.enable_thinking` `serve/frontend.py:163-172`; Anthropic `thinking` blocks `:196-197`, `:212-221` |
| `stop` sequences | **NO** | NOT FOUND (serve/): no `req.get("stop")` anywhere. `stop` in source is only the engine's `READY <ctx> stop` capability token (`serve/server.py:190`) and `finish_reason` strings (`:865`, `:1109`) |
| `logprobs` / `top_logprobs` | **NO** | NOT FOUND (serve/) |
| `response_format` / JSON schema / grammar | **NO** | NOT FOUND (serve/) |
| Sampling params | **partial** | `temperature, top_p, top_k (≤64), min_p, repetition_penalty, frequency_penalty, presence_penalty, penalty_last_n, seed, strata_tune{pcie_frac,spec_min_p}, experimental_speed_projection` — `Service.sampling_keys` `serve/server.py:261-306`. **No** `mirostat`, `dry`, `xtc`, `typical_p`, `tfs_z`, `n_probs`. |
| `n_predict` | **NO — silently ignored** | `grep -c 'req.get("n_predict")' serve/server.py` → **0**. Only `max_completion_tokens`/`max_tokens` are read (`:1453`); `0`/`-1` means *"the rest of the context"* |
| `id_slot`, `hydra_config` | **NO — silently ignored** | NOT FOUND (serve/). The `_openai` handler reads only keys it knows (`:1450-1463`) |
| `/metrics` Prometheus text | **NO** | returns a JSON object (`serve/server.py:663-699`, `:1313`) |
| `/version` | **NO** | NOT FOUND (serve/) |
| Auth | **Yes, optional** | `--api-key` / `$STRATA_API_KEY` `serve/server.py:1691-1692`, `:1755-1760`; constant-time compare `_authorized` `:1265-1273`. `/health` exempt `:1331` |
| **Slots** | **exactly one, id 0** | `slot = {"id": 0, ...}` `serve/server.py:1364`; `"total_slots": 1` `:1407` |
| **Concurrent requests** | **No — strictly serialized** | `self.fifo = threading.Lock()` `serve/server.py:574`; docstring *"Requests are serialized by the service's FIFO, so one pipe is enough"* `:151`; `queued` counter `:577`, `:836-840`. `ThreadingHTTPServer` `:1523` accepts connections concurrently but generation is one-at-a-time. |
| `--parallel` / continuous batching | **NO** | NOT FOUND: no `--parallel`/`--n-parallel` flag in `serve/server.py` or `src/program/generate.cpp` |

**Strata server CLI** (`serve/server.py:1665-1692`) [verified]: `--engine {mock,strata}`,
`--config` (JSON written by `setup.py`), `--host`, `--script`, `--port` (**default 8095**),
`--gpu`, `--tokenizer`, `--open`, `--fit-max-tokens`, `--api-key`, `--mcp-config`.
There is **no** `--model`, `--ctx-size`, `--parallel`, `--jinja`, `--metrics`, `--slots` flag —
the equivalent model/context/quant settings live inside the JSON config's `args[]`
(`engine_args` `serve/server.py:491-497`).

### 1.2 What Hydra requires from an engine

Two planes. **HTTP** (`--port`) for chat + slots + KV streaming; **binary RPC** (`--rpc-port`)
for state and engine-mode control.

**HTTP, by caller** [verified]:

| # | Endpoint | Caller `file:line` | Contract detail |
|---|---|---|---|
| H1 | `GET /health` | `src/core/Hydra.Core/LlamaClient.cs:163-175` | **status code only**, body never parsed (`:168`) |
| H2 | `GET /slots` | `LlamaClient.cs:177-184`; parsed `:245-276` | needs `id` (required, `:259`); optional `n_past` (default 0, `:260`), `is_processing` (`:261`), `n_remain` or `next_token[0].n_remain` (`ParseNRemain` `:226-243`) |
| H3 | `POST /slots/{id}?action=erase` | `LlamaClient.cs:186-196` | **404/501 tolerated as success** (`:191-194`) |
| H4 | `GET /slots/{id}/state` | `LlamaClient.cs:120-130` | **requires `Content-Length`** — throws if absent (`:126-127`) |
| H5 | `PUT /slots/{id}/state?erase_existing=true` | `LlamaClient.cs:132-147` | `application/octet-stream`, 64 KiB buffer (`:134`) |
| H6 | `GET /slots/{id}/state/meta` | `LlamaClient.cs:149-161` | `n_past`, `state_size`, model identity |
| H7 | `POST /v1/chat/completions` | `src/core/Hydra.Core/Services/CompletionProxyService.cs:40` (sync), `:54` (SSE) | body carries `stream`, `n_predict`, `model`, `id_slot`, `hydra_config` |
| H8 | `POST /models/load` | `CompletionProxyService.cs:24-33` | legacy, **skipped in engine mode** (`WorkerSchedulerService.cs:1730-1737`) |
| H9–H11 | `GET/GET/DELETE /v1/decode/{id}` | `src/core/Hydra.Core/Services/IServices.cs:11-18` | merged-decode poll/cancel; 404=retry, 202=poll, 400=terminal, 200=stream (`:14-16`) |
| H12 | `GET /metrics` | **not a Core client** — Prometheus only: `infra/prometheus/prometheus.yml:21-42` (`metrics_path: /metrics` → `localhost:8080/8081`, `192.168.122.21:8086`) | |
| — | `GET /version`, `/props`, `/v1/models` | **NOT FOUND** as engine calls in `src/core` (`/v1/models` poll deliberately removed, `HealthMonitorService.cs:234-243`) | |

**Spec'd engine HTTP surface:** `specs/rpc-protocol.md:657-669` — `/health`, `/version`,
`/slots`, `/slots/:id/state/meta`, `/v1/chat/completions`.

**Parameters Hydra sends** [verified]:
- `stream`, `n_predict`, `model`, `id_slot` — prefill fallback body `WorkerSchedulerService.cs:2421-2437` (`stream=false`, `n_predict=0` at `:2422-2423`).
- `hydra_config` full key set — `src/core/Hydra.Core/Models/EngineConfig.cs` `ToHydraConfigDict()`; includes `model_path, split_mode, tensor_split, n_gpu_layers, n_cpu_moe, n_ctx, cache_type_k/v, rope_scaling, rope_scale, yarn_orig_ctx, spec_type, spec_draft_*, cont_batching, fit, ubatch_size, override_tensor, rpc_servers, flash_attn, kv_unified, cache_prompt, cache_reuse, reasoning, jinja, context_shift, chat_template_kwargs`.
- Per-request T1 overrides via RPC `0x40` — `src/core/Hydra.Core/Models/EngineRequestOverrides.cs:20-39`: `temperature, top_p, top_k, min_p, repeat_penalty, seed, stop, n_predict, n_keep`.
- `tools`, `tool_choice`, `response_format`, client `stop` merged in `WorkerSchedulerService.cs:3925-3995`.
- **NOT FOUND** anywhere in the repo: `return_tokens`, `keep_alive`, a `samplers` request field.

**Binary RPC** (`src/core/Hydra.Shared/Protocol.cs:44-58`) [verified]:

| Opcode | Code | Meaning |
|---|---|---|
| `StateGet` / `StatePut` / `StateMeta` / `GetManifest` | `0x30/31/32/33` | KV stream out / in / meta / chunk manifest |
| `EngineConfigure` | `0x40` | `common_params` delta, T1/T2/T3 tiering (`specs/rpc-protocol.md:174-240`) |
| `EngineInfo` | `0x41` | `engine`, `version`, **`capabilities`**, **`preset_aliases`** — consumed `HealthMonitorService.cs:247-261` |
| `EnginePrefill` | `0x42` | prefill-only, returns `n_past` + inline ~800 MB KV blob |
| `EngineDecode` | `0x43` | merged KV transfer + decode |
| `EngineSetExpertMode` / `EngineSwapQuant` / `EnginePipelineAttach` | `0x44/45/46` | solo↔combined, quant swap, two-engine attach |

Status codes `0x00-0x06` (`Protocol.cs:64-72`), incl. `NotImplemented = 0x06`.
Capability gate: `Protocol.CapMergedDecode = "merged_decode"` (`Protocol.cs:79`) →
`decodeNodeMergedCapable` (`WorkerSchedulerService.cs:4113-4114`).
Graceful degradation is a designed seam: *"The 0x40-0x46 opcodes are NOT supported by the
legacy llama-server binary … Coordinator detects the binary mismatch and falls back to the HTTP
prefill path"* (`specs/rpc-protocol.md:650-654`); HTTP fallback condition
`WorkerSchedulerService.cs:2414-2420`.

**The +3 streaming KV endpoints (the fork delta):** `docs/architecture.md:491-502` —
branch `hydra-state-streaming`, *"only `tools/server/server.cpp` is modified (~80 lines, 3
endpoints)"*: `GET /slots/{id}/state` ~800 MB, `PUT /slots/{id}/state` ~800 MB returning
`{restored,n_past}`, `GET /slots/{id}/state/meta`. *[fork source not in this worktree → gap]*

### 1.3 Fit: Strata vs Hydra's HTTP contract

| Hydra requirement | Strata | Verdict |
|---|---|---|
| H1 `/health` status-code only | `serve/server.py:1331` | ✅ |
| H2 `/slots` with `id` | `:1359`, `:1364` (id 0) | ⚠️ `n_past`/`n_remain` absent → `ParseSlots` defaults **0** (`LlamaClient.cs:260,266`) → stuck-slot watchdog (`#299/C7`, `HealthMonitorService.cs:~265+`) sees garbage |
| H3 slot erase | no route → 404 | ⚠️ tolerated (`LlamaClient.cs:191`) but there is nothing to erase — single slot, one FIFO |
| H4/H5/H6 state stream/restore/meta | **no route** (no `do_PUT`) | ❌ 404 → `EnsureSuccessStatusCode` throws (`LlamaClient.cs:144`) |
| H7 `/v1/chat/completions` + SSE + tools | ✅ | ✅ |
| `n_predict=0` prefill-only | **ignored** → generates to context | ❌ **critical** [hypothesis on blast radius; the ignore itself is verified: 0 reads of `n_predict`] |
| `id_slot`, `hydra_config` | silently ignored | ❌ |
| `stop` / `logprobs` / `response_format` | absent | ❌ (Hydra forwards them) |
| multi-slot, `--parallel 2` | 1 slot, FIFO-serialized (`:574`) | ❌ |
| `/metrics` Prometheus text | JSON (`:663`) | ❌ scrape breaks |
| RPC `0x30-0x33`, `0x40-0x46` | **no binary RPC surface at all** | ❌ |
| readiness sentinel (see §2) | `READY <ctx> stop` (`src/program/generate.cpp:3581`), `ready: http://…` (`serve/server.py:1782`) | ⚠️ neither matches Hydra's configured sentinels |

**Bottom line for Q1:** Strata has a *good* OpenAI/Anthropic chat surface for a single-user
desktop app, and it is genuinely streaming + tool-capable. It is **not** a drop-in for the
surface Hydra drives: no KV state endpoints, no binary RPC, one slot, FIFO-serialized, no
`n_predict`, no `stop`.

---

## 2. Process lifecycle

### 2.1 How Hydra Head launches/supervises llama-engine today

- Startup: `manager.StartLlama()` `src/head/main.go:166` (failure → `os.Exit(1)` `:168`);
  OCI pull loop `main.go:115-161`; `defer manager.Shutdown()` `main.go:164`.
- Fit preflight: `exec.Command(fitBin, fitArgs...)` `src/head/internal/process/manager.go:302`
  (sibling `llama-fit-params` binary, `:297-301`).
- **Spawn:** `cmd := exec.Command(m.cfg.Llama.Binary, args...)` `manager.go:332`,
  `cmd.Dir = m.cfg.Llama.WorkingDir` `:333`, env = `os.Environ()` + `cfg.Llama.Env`
  `:336-341`. **Host binary, not a container** — the OCI image is a delivery vehicle.
- **Args:** `BuildLlamaArgs()` `src/head/internal/config/config.go:264-274` →
  `--host`, `--port`, `--rpc-port`, then every key of `llama.params` from YAML, sorted
  (`buildParamsArgsFiltered` `config.go:324-343`; `ggml-rpc-port`/`peer-only` filtered out
  `config.go:319-322`).
- **Config:** `infra/hydra-head/config/node-rtx.yaml:66-80` —
  `binary: /llama/bin/llama-engine`, `port: 8080`, `rpc_port: 9503`,
  `model: /models/Qwopus3.6-35B-A3B-v1-APEX-I-Mini.gguf`, `models-preset`,
  `ctx-size: 128000`, `parallel: 2`; `infra/hydra-head/config/global.yaml:11-38`
  (`flash-attn`, `cache-prompt`, `cache-reuse: 64`, `reasoning`, `jinja`, `metrics`, `slots`, …).
- **Readiness = stdout sentinel, never HTTP:** `onLlamaLine` `manager.go:373-395`,
  `watchReadiness` `manager.go:401-425`; comment *"event-driven readiness signal — the head
  never HTTP-polls llama"* `manager.go:258-259`. Sentinels configured at
  `infra/hydra-head/config/global.yaml:70-75`: `"hydra-engine ready"`, `"server is listening on"`,
  `"router server is listening on"`, `"model loaded"`; `timeout_sec: 180` → `StateSuspect` (`manager.go:421`).
- **Restart:** `shouldRestart` `manager.go:462`, `restartWithBackoff` `manager.go:471`.
- **Stop:** `StopLlama` `manager.go:517` → `stop()` → **`SIGTERM` `manager.go:705-706`**, bounded
  wait, then `SIGKILL`. Port TIME_WAIT wait `manager.go:526,535`.
  **No KV snapshot on shutdown** — NOT FOUND (src/head) any state/KV operation.
- **Health (Core-side, 20 s):** `HealthPollIntervalS=20`
  `src/core/Hydra.Core/Models/CoordinatorModels.cs:80`; per poll `GET /slots`
  (`HealthMonitorService.cs:195-231`) + RPC `0x41` (`:247-261`); `RpcFailureThreshold = 3`
  (`HealthMonitorService.cs:25`).
- **OCI:** `registry.PullBinary` `src/head/internal/registry/manager.go:98`,
  `crane.Pull(ref.String())` `:112`, digest pin verify `:124-128`, binary checksum `:145-156`.
  Refs: `infra/hydra-head/config/node-rtx.yaml:42-45` —
  `ghcr.io/ddvnguyen/llama-server:sm86-sm120-llama-engine-1d227f7`,
  `image_digest: sha256:924bc909…`, `binary: llama-engine`, `dest: /llama/bin/llama-engine`.
  P100 arm: `docs/hydra-head.md:31-38` (`sm60` image).
- **CI:** this repo **never compiles the engine**. It calls the fork's reusable workflow:
  `uses: ddvnguyen/llama.cpp/.github/workflows/hydra-build.yml@hydra-fork` with
  `build_llama_engine: true`, `build_llama_server: false`, `arch_sm86_sm120`,
  `arch_sm60` — `.github/workflows/deploy-heads.yml:127-137`; `IMAGE_REPO: ghcr.io/ddvnguyen/llama-server`
  `deploy-heads.yml:88`; P100 tag `:205`. Digest pinning gate in `ci.yml:313-327,452-458`.
  Tag format `<arch>-<binary>-<fork-version>-<short-sha>` — `docs/workflow/05-deploy.md:15-49`.

### 2.2 What launching Strata instead would take

| # | Change | Size | Evidence / why |
|---|---|---|---|
| L1 | **A Head "engine adapter"** — today Head has **no engine interface**: `cfg.Llama.Binary` is a bare string (`config.go:266` region, used at `manager.go:332`) plus a `binaries.<name>.{source,image_digest,binary,dest}` map. Adding Strata means either (a) a new `binary` entry + config, or (b) a real `EngineLauncher` interface. | (a) S  (b) M | `[hypothesis]` on shape; absence of any interface verified by grep of `src/head` |
| L2 | **Launch the right process.** Strata is `python -m serve.server --config <json>` which then `subprocess.Popen([exe, "--serve", *args])` the C++ binary (`serve/server.py:169`, config `:1744`). Head runs **one** `exec.Command`. So either Head execs Python (needs the `.venv` interpreter, `PATH`, `cwd`) or we build a tiny launcher/`--serve` wrapper binary. | S–M | `serve/server.py:169,491-497,1744` |
| L3 | **Readiness sentinel.** Strata prints `READY <ctx> stop` (`src/program/generate.cpp:3581`, consumed by Python at `serve/server.py:187-190`) and `ready: http://…` (`serve/server.py:1782`). Neither matches `global.yaml:70-75`. Add a sentinel → one-line YAML change. | XS | `infra/hydra-head/config/global.yaml:70-75` |
| L4 | **Ports.** Strata default `--port 8095` (`serve/server.py:1674`); Hydra node YAML expects 8080/8081/8086 + `rpc_port`. Pass `--port`. | XS | `node-rtx.yaml:68-69` |
| L5 | **Model path → pack path.** Hydra's registry is GGUF-shaped: `models.json` `model_file_aliases` → `*.gguf` (`infra/hydra-core/config/models.json:23-27`), `engine_config.model_path`, preset INI `model = /models/….gguf` (`infra/models-preset.ini`, `node-rtx.yaml:74`). Strata needs **three** paths: `--pack`, `--native <shard1>`, `--ple-gguf <shard2>` (real config `/mnt/WorkDisk/strata/strata-iq3s.json`). `hydra_config.model_path` would have to become a structured blob or a single "profile" name. | M–L | Strata config verified; `models.json:23-27` verified |
| L6 | **Model distribution.** Hydra delivers models by GGUF path on the `:ro` `/models` mount (`CLAUDE.md:40-42`). Strata needs **GGUF shards + a pack directory + an MTP `rt/` dir** (78 GB + 1.5 GB + 6.5 GB — see §5). Store/`model_file_aliases` would need a multi-file manifest. | M–L | `CLAUDE.md:40-42`; `du` on `/mnt/SSD/strata-models` |
| L7 | **OCI packaging (CI-only).** New workflow building `strata` for `sm86+sm120` and `sm60`… except **sm60 is refused** (§5). So a **2-arch RTX-only image**. Build scripts already exist and pass: `build-v0.1.29-sm120.sh:9-14`, `build-v0.1.29-sm86.sh:9-14` (`BUILD_RC=0`, `[122/122]` in the logs). Container needs CUDA runtime + the Python venv + `strata_tokenizer`. | M | scripts verified; `deploy-heads.yml:127-137` is the pattern to copy |
| L8 | **Health shape.** `/health` body isn't parsed (`LlamaClient.cs:168`) → fine as-is. `/slots` shape degrades (§1.3). `/metrics` JSON breaks the Prometheus scrape (`infra/prometheus/prometheus.yml:23`). | XS (health) / S (metrics) | |
| L9 | **`n_predict=0` prefill semantics.** Without a prefill-only mode, Hydra's non-engine-mode prefill fallback (`WorkerSchedulerService.cs:2420-2437`) would trigger a full generation. | **blocker** unless engine mode with `0x42` is implemented | `WorkerSchedulerService.cs:2422-2423`; 0 reads of `n_predict` in Strata |

**Minimum viable integration = L1(a)+L2+L3+L4 only**, and only for a *non-engine-mode*,
single-model, single-node path that speaks H1/H2/H7. Everything else (H4–H6, RPC `0x40-0x46`)
is a separate, larger work item.

---

## 3. KV / prefix state

### 3.1 What Strata has

**Prefix / conversation cache — yes, in-process only.**
`--serve`'s conversation cache is documented at `src/program/generate.cpp:697-710`:
*"What a sequence leaves behind splits in two … POSITIONAL state — the KV cache of the 12 QSA
layers and their pooled indexer keys, the draft layer's KV … RUNNING state — the 36 GDN
recurrences and conv histories, each QSA layer's indexer tail … a checkpoint is a copy of
exactly these: ~118 MB."*
- Checkpoint struct `ConvCheckpoint` `src/program/generate.cpp:719-728`.
- Save: `checkpoint_save` `generate.cpp:746-760` — `cudaMemcpy` Device→Host of `gdn`, `ple`, `tails`.
- Restore: `checkpoint_restore` `generate.cpp:762-779` — Host→Device. *"the positional cells
  below it are the caller's to guarantee"* (`:762`).
- Retention: `--prompt-cache N` default **6**, `--prompt-cache-every 16384`,
  `--prompt-cache-root 2048` (`generate.cpp:284-289`, help `:376-378`).
- Reported per request as `reused` / `hits` / `lookups` in the `DONE` line
  (`generate.cpp:4315-4317`) and surfaced by Python (`serve/server.py:256-259`).

**Slot save / KV export / cross-process state transfer — NOT FOUND.**
Search of `src/`+`include/`+`serve/` for `save|restore|export|import|serialize|migrate` in
relation to KV/slot returns only the two in-process functions above
(`generate.cpp:746,762`) plus an unrelated kernel comment (`include/strata/kernels/kv_stream.hpp:79`).
There is **no** HTTP route, **no** CLI flag, **no** env var, and **no** file format for
exporting KV state. The only file writes in `generate.cpp` are debug dumps
(`--dump-logits/-residual/-mixed/-layers/-halves/-routing/-final-r` at
`:1562,2467,2609,2638,4380,5033,5053`) — **none of them is KV**.

**Multi-GPU = layer split, not P/D.** `docs/MULTI_GPU.md:1` *"Strata on two or three GPUs
(layer split)"*, runtime line `:66` `strata serve: layer split: layers 0-18 (CUDA0), 19-47
(CUDA1), one hand-off per window`. `--layer-split` `src/program/generate.cpp:1017`;
`serve/server.py:1741` adds it automatically for multi-GPU. **NOT FOUND**: any
prefill-node/decode-node role split in `src/`, `include/`, `docs/` → **Strata has no P/D mode.**

### 3.2 What Hydra's KV migration needs

- ~800 MB blob (60–80K ctx) `specs/rpc-protocol.md:5,121,354`; `docs/architecture.md:500-501`.
- **The engine serializes it**; Hydra only pipes opaque bytes:
  `LlamaClient.GetStateAsync` `LlamaClient.cs:120-130`, `PutStateAsync` `:132-147`,
  or RPC `0x30/0x31` (`WorkerSchedulerService.cs:5562+`).
- Cross-node transport is Core↔Store chunked (`Protocol.cs:30-31`, opcodes `0x10-0x15`),
  1 MB chunks, SHA-256, tmpfs (`docs/architecture.md:291-333`).
- Model identity travels with every blob (`model_alias/model_hash/model_path`,
  `specs/rpc-protocol.md:126-173`) — cross-model restore is refused
  (`docs/architecture.md:506-509`).
- P/D handoff is **owned by `WorkerSchedulerService`**, not the engine
  (`WorkerSchedulerService.cs:1772-1774` state machine, `SaveKvAsync :2959-3009`,
  `RestoreKvAsync :3503-3536`, relay channel `:2069/:3517`).
- Engine-side break points if a new engine is used: `Content-Length` on state GET
  (`LlamaClient.cs:126-127`), `n_tokens > n_past` guard (`specs/rpc-protocol.md:176-178`),
  model-identity triple, `NOT_IMPLEMENTED ≠ ERROR` (`specs/rpc-protocol.md:704-716`).

### 3.3 Verdict

**P/D split with Strata is not feasible without building the export/import path from scratch.**
Strata's checkpoint deliberately **excludes the positional KV cells** (`generate.cpp:710`,
`:762`) and keeps only ~118 MB of *running* state, in host RAM, for one process. Hydra needs a
serializable, transferable, model-identified ~800 MB positional state. Two hard mismatches:

1. **No serialisation format exists** (NOT FOUND, §3.1).
2. **No cross-node role split exists** (NOT FOUND, §3.1) — Strata splits *layers*, Hydra splits
   *roles*.

**Consequence for the Hydra value proposition:** Strata can only serve the *single-GPU,
same-node, no-migration* slice of Hydra. The capabilities that justify Hydra as a coordinator —
KV migration across the 3060/5060 Ti/P100, mix-quant P/D, `0x42/0x43` merged decode, COMBINED
expert-split — would all be **fork-only**. Running Strata "behind Hydra" would mean the
coordinator degrades to a single-slot, FIFO-serialized, one-GPU router for that node: exactly
the thing `One GPU = one compute task` plus the Store was built to go beyond.
`[hypothesis]` — the qualitative conclusion follows from the two NOT-FOUNDs; the *degree* of
value loss is a judgement call for the owner.

---

## 4. Speculative decode / MTP

**Flags** (`src/program/generate.cpp`) [verified]:

| Flag | Line | Meaning |
|---|---|---|
| `--spec T` | `:1007` | verify window in tokens (plan v0.3 P6, `:247`) |
| `--mtp DIR` | `:1010` | MTP head directory |
| `--mtp-window N` | `:1011` | MTP's own window cap (`:302`) |
| `--mtp-max-t M` | `:1028` | cap MTP windows at M tokens (0 = `--spec`) |
| `--spec-min-p F` | `:1014` | acceptance probability floor |
| `--suffix-draft N` | `:1027` | prompt-lookup draft from an earlier repeat of the last N+ tokens; **default 3, `0` = MTP only** (help `:382-383`) |
| `--spec-oracle`, `--spec-corrupt`, `--spec-split`, `--no-spec-split` | `:1008,1009,1016,1061` | test/debug |

**Can speculation be disabled? No, in `--serve` mode.** Verified at
`src/program/generate.cpp:3087-3092`:

```cpp
if (o.spec < 2 || o.mtp.empty() || o.prefill_chunk <= 0 || …) {
    std::fprintf(stderr, "strata serve: needs --spec T, --mtp DIR and --prefill CHUNK …");
    return 2;
}
```

and `--mtp` is silently dropped without `--spec >= 2` (`generate.cpp:1752-1755`).
This confirms deviation **D1** in the A/B report: *"the engine requires `--spec >= 2` and
`--mtp`"* — `--spec 0` is not expressible, so the primary "spec-off" arm S0 actually ran
`--spec 2 --spec-min-p 1.0 --suffix-draft 0` (`docs/evidence/strata-3060-ab/README.md`, D1).

**MTP head loading:** `MtpDrafter::load(const std::string& rt_dir, …)`
`src/core/mtp.cpp:133`, declared `include/strata/core/mtp.hpp:44`. It takes a **directory**,
not a GGUF: the live config uses `--mtp /mnt/SSD/strata-models/mtp/rt`, which contains
`dense.bin` (116 MB), `dense.txt`, `draft_vocab.bin`, `experts.bin` (708 MB) [verified `ls`].
A repacked GGUF (`mtp-q2_0.gguf`, 889 MB) also sits one level up — `prep-mtp.sh` builds `rt/`
from it `[hypothesis on the exact conversion step — prep-mtp.sh not fully re-read]`.
The drafter is loaded *before* the host arena (`generate.cpp:1750-1764`) and the draft layer
always uses canonical geometry *"even when the target is pruned"* (`:1760-1761`).

**Acceptance behaviour:**
- Accepted drafts accumulate `draft_accepted += a` (`generate.cpp:4170`), offered `:4085`.
- Emitted in the `DONE` line: `DONE <generated> <prompt> <prompt_ms> <decode_ms> <finish>
  <drafts_accepted> <drafts_offered> <reused> [hits] [lookups]` (`generate.cpp:4315-4317`),
  parsed by Python `serve/server.py:254-259`.
- Decode timing line: `"strata decode timing: %lld windows, avg T %.2f, %.2f tokens/window,
  %.2f ms/window = …"` (`generate.cpp:4212-4219`) — this is where the A/B's
  *"avg T 2.54"* and *"3.11 ms of 53.49 ms/window"* come from `[measured]`.
- Summary: `drafts accepted %lld of %lld` `generate.cpp:4323-4326`, suffix-draft windows `:4359`,
  per-round histogram `:4943-4956`.

**Are the S1 results usable?** S1 = `--spec 4 --spec-min-p 0.5` (suffix-draft default 3),
38.81 tok/s at 4K vs S0's 32.44 = **+19.6 %**, draft-adjusted ≈ 41.2 tok/s, fill 5.06 GiB,
hit rate 57–66 % `[measured — docs/evidence/strata-3060-ab/README.md, S1 row]`.
It was pre-registered **informational only**, alongside F1/F2 `[measured — leader ruling]`.
Usable as a *directional* signal that MTP helps; **not** usable as a primary rule input, and
the run inherited D1 (S1 also cannot be spec-off).

**Not portable to the fork as-is:** fork MTP is `--spec-type draft-mtp --spec-draft-model
<gguf> …` (`models.json:37-47` `engine_config`), i.e. a GGUF draft model, and measured F2 was
the *worst* arm (7.15 tok/s) because drafting ran on CPU (`--spec-draft-ngl 0`) on top of the
cache-thrash path `[measured]`.

---

## 5. Model / quant / arch coverage

### 5.1 One architecture, five quants

**Hard gate — one GGUF architecture only:**
`include/strata/artifact/gguf_reader.hpp:464-474` — *"The engine is specialised to ONE model;
anything else must be refused with a precise error"*, `check_architecture()` returns
`"architecture is '<x>', this engine requires 'qwen4exp'"` when `general.architecture != "qwen4exp"`.
Enforced at all three read sites: `src/artifact/gguf_reader.cpp:57`,
`src/core/native_dense.cpp:78`, `src/core/native_head.cpp:27`.
Geometry defaults baked in: `block_count=48, hidden=2560, head_count=24, head_count_kv=2`
(`gguf_reader.hpp:465-469`).

**Quant dispatch tables** (type ids from `third_party/llama.cpp/ggml/include/ggml.h:390-433`):
- `iq_mmvq` `src/kernels/cuda/iq_kernels.cu:639-648`: `16,17,18,20,21,22,23,29,11,42` =
  IQ2_XXS, IQ2_XS, IQ3_XXS, IQ4_NL, IQ3_S, IQ2_S, IQ4_XS, IQ1_M, Q3_K, Q2_0;
  `default:` → **`std::exit(1)`** (`:648`).
- grouped expert gate/up `iq_kernels.cu:723-732`: `16,17,18,21,22,23,29,42`;
  down `:738-742`: `20,23,42`. Both `default:` → `std::exit(1)`.
- native GGUF MMVQ `src/kernels/cuda/native_mmvq.cu:1448-1462`: adds `2,6,8,12,13,14`;
  `default:` → `throw std::invalid_argument(...)` (recoverable).
- **Not supported anywhere:** Q4_K/Q5_K/Q6_K are native-MMVQ-only (no grouped-expert kernel),
  **BF16**, TQ1_0/TQ2_0, MXFP4, NVFP4, Q4_1/Q5_1, IQ1_S `[verified by absence across the three tables]`.
- Packer constraints: gate type **must equal** up type per layer (`tools/iq_pack.py:305-306`);
  small projections must be BF16 or `--compat-bf16` (`iq_pack.py:64-70,136-143`);
  PLE key native type ∈ {42,18,23} (`src/program/generate.cpp:1629`).

**Consequences:**

| Target | Supported? | Evidence |
|---|---|---|
| **P100 (sm_60)** | **NO — build refuses** | `CMakeLists.txt:71-75` `if(_base LESS 75) message(FATAL_ERROR "Strata needs an NVIDIA GPU of compute capability 7.5 or newer …")`; install-time refusal `setup.py:331-334` and `:430` |
| **sm_75 (Turing)** | experimental, FP32-FMA fallback | `CMakeLists.txt:65-67`; `src/kernels/cuda/native_qsa_score.cu:64-70` |
| **RTX 3060 (sm_86)** | **Yes — we build it** | `build-v0.1.29-sm86.sh:11` `-DCMAKE_CUDA_ARCHITECTURES=86`, `BUILD_RC=0`, `[122/122]` in `build-v0.1.29-sm86.log` |
| **RTX 5060 Ti (sm_120)** | **Yes — default arch** | `CMakeLists.txt:61` default `120`; `build-v0.1.29-sm120.sh:11`, `BUILD_RC=0`. Warn if CUDA < 13.0 (`CMakeLists.txt:76-80`, issues #220/#224) — we build with 13.2.2 |
| AMD / ROCm | gfx1100 only | `cmake/hip_backend.cmake:4-8` |

**Model family coverage.** Only `qwen4exp`. Installer menus: `qwen` / `swift` / `coder`
(`setup.py:82-110`), five shipped sizes `Q2_0, IQ2_XS, IQ3_XXS, IQ3_S, IQ1_M` (`setup.py:60-80`).

**Against Hydra's actual fleet** (`infra/hydra-core/config/models.json:23-77`):
`qwen3.6-35B-mini` / `qwen3.6-35B-balanced` / `qwen3.6-27B-coder` GGUFs → **all Qwopus3.6
(qwen35moe arch)**, quant tiers Q3_K-mini / Q5_K_M / Q5_K-balanced.
→ **None of them can be loaded by Strata** (arch gate + Q4_K/Q5_K have no grouped-expert
kernel). Our live model `Qwen3.8-Flash-Next-APEX-I-Mini` is also **unpackable**: gate≠up in
48/48 layers, PLE key/output are Q6_K
(`docs/evidence/strata-5060ti/README.md:4,53,63` and `:116-142`; packer gate `iq_pack.py:305-306`).

**Q4/Q5 quality tier:** Strata has **no Q4_K/Q5_K/Q6_K grouped-expert path** (§ above).
Hydra's `quality_tier` 1–3 (`models.json:38,48,58,68,78`) maps onto quants Strata cannot run.
Only IQ2_XXS/IQ2_XS/IQ3_XXS/IQ3_S/IQ1_M/Q2_0 are natively servable.

### 5.2 Pack/repack workflow and disk layout

- Pack = `index.txt` + `dense.bin` [+ `experts.bin`] + `native_experts.txt` + `tokenizer/`
  (`tools/iq_pack.py:5-23`). **Non-expert quantized tensors, `token_embd` and `output` are still
  served from the GGUF** via `--native` (`iq_pack.py:12,47`), and the 28.8 GB
  `per_layer_token_embd.weight` is `NOT_IN_PACK` — *"read from its GGUF by the engine"*.
  So a deployment needs **both** the pack **and** the GGUF shards.
- Real cost, measured: `prep-iq3s.log:4-5` → `index.txt: 1079 tensors, 302 served natively,
  0 in extra.bin, arena 1.43 GiB`, `PACK_RC=0`. Script: `prep-iq3s.sh:3-6`
  (sets `TMPDIR=/mnt/WorkDisk/strata/tmp`, `STRATA_GGUF_PY=…gguf-py`, runs `tools/iq_pack.py`).
- On disk today [verified `du`]:
  - `/mnt/SSD/strata-models/GSQ-RCO` **78 GB** (2 GGUF shards)
  - `/mnt/SSD/strata-models/packs/iq3_s` **1.5 GB**
  - `/mnt/SSD/strata-models/mtp` **6.5 GB** (`rt/` 805 MB of it)
  - ⇒ **≈ 86 GB per model, per quant.**
- Write safety: the engine mmaps GGUF `MAP_PRIVATE`/`O_RDONLY`
  (`include/strata/artifact/gguf_reader.hpp:386-392`) and never writes into the pack/model dir —
  only to explicit `--dump-*` paths and the shared-settings file next to the config
  (`serve/server.py:1765`). So a `:ro` mount works `[verified]`.
- **Storage-rule conflict:** `CLAUDE.md:40-42` — `/mnt/WorkDisk/LLM-Models` = production
  models (NVMe), `/mnt/SSD` = non-production, mounted `:ro` as `/models`. All 86 GB of Strata
  material lives on `/mnt/SSD` today. A production Strata deployment must either move to
  `/mnt/WorkDisk/LLM-Models` or re-classify. `[open decision]`

---

## 6. Project risk

| Dimension | Finding | Evidence |
|---|---|---|
| **License** | **MIT** — `Copyright (c) 2026 Niko1221 and the Strata contributors` | `LICENSE:1-3`. Vendored `third_party/ggml` and `third_party/llama.cpp` also MIT (`third_party/ggml/LICENSE:1-3`, `third_party/llama.cpp/LICENSE:1-3`). No SBOM / `THIRD_PARTY_LICENSES` **NOT FOUND** |
| **Size** | **70,097 lines / 262 files** (86 `.cpp` 33,491 · 50 `.cu` 16,059 · 85 `.hpp` 8,381 · 4 `.h` 128 · 37 `.py` 12,038), excl. `third_party/`, `build-*` | `find \| xargs wc -l` over `src-v0.1.29` [method: `cloc` not installed] |
| vs. our fork | `src/llama-cpp` ≈ **1.53 M lines / 1,820 files** | same method |
| **Tests** | **Off and not shipped.** `STRATA_BUILD_TESTS` defaults OFF unless `tests/CMakeLists.txt` **and** `bench/micro` exist — neither does; upstream comment: *"The test suite and the micro benchmarks … the published source leaves out"* `CMakeLists.txt:47-51`. Our builds pass `-DSTRATA_BUILD_TESTS=OFF` (`build-v0.1.29-sm{120,86}.sh:10`). Runnable-without-GPU: 4 Python suites (`serve/test_server.py:3`, `serve/test_mcp.py`, `tools/test_iq_pack.py`, `tools/test_shards.py`). 52 `add_test(...)` declarations exist but mostly kernel-parity oracles against pinned llama.cpp (`CMakeLists.txt:134-789`) | |
| **CI** | **NOT FOUND** (`.github/`, `.gitlab-ci.yml`, root CI YAML) | |
| **Upstream** | `github.com/Niko1221/Strata` (`src-v0.1.29/.git/config`). Both checkouts **shallow**; `src-v0.1.29` is a **single-tag** fetch of `v0.1.29` (`git rev-list --count HEAD` = 1). Sibling `/mnt/WorkDisk/strata/src` has **30 tags `v0.1.0`→`v0.1.29`, 2026-09-24 → 2026-09-30 (~daily)**, 270 visible commits, 232 reachable from `v0.1.29` | |
| **Bus factor / authors** | Multi-author: Niko1221 183, Jakub Luwierski 11, pete 9, pipeob0 8, Guillaume PUTIER 7, Andy 6, … **but 149/270 visible commits carry a Claude co-author trailer** | `git shortlog -sn --all`; `git log --all --format='%b' \| grep -i co-authored-by` |
| **Churn risk** | 30 releases in 7 days; `setup.py:64-66` pins `MIN_ENGINE = (0,1,29)` forcing engine↔setup lockstep | tag dates; `setup.py:64-66` |
| **Patches we already carry** | **Zero source patches.** `git status --short` in `src-v0.1.29` = untracked files only. We only have **build wrappers**: `build-v0.1.29-sm120.sh` / `-sm86.sh` differ from `build.sh` only in source dir, build dir, arch (`120`/`86`), `-j 6 nice -n 19` | |
| **Silent build-correctness risk** | Both scripts pass `-DSTRATA_GGML_DIR=$(pwd)/third_party/llama.cpp`, which **replaces the upstream `FetchContent` pin** (`CMakeLists.txt:651-663`, pin `3cf03257…`). That path is a **symlink to an unversioned working tree** (no `.git`, no checksum, no `_deps` copy) → *equality to the pinned commit is UNVERIFIED* | `[verified gap]` |
| **Instrumentation we rely on** | Strata: `--dump-routing` (`generate.cpp:950`, write `:2467`, help `:371`), `--dump-logits/-residual/-mixed/-layers/-halves/-final-r` (`:938-950,1006`), `--stats` (`:1075`), `STRATA_DECODE_TIMING`, `STRATA_PREFILL_TIMING`, `STRATA_TRACE`, `STRATA_VERIFY_PROFILE` (`src/core/verify.cpp:270`), `STRATA_GGUF_PY`, `STRATA_OLD_SAMPLER`, `STRATA_SAMPLER_ONE_BLOCK`, `STRATA_EXPERIMENTAL_SM75` (env-var inventory greped across `src/`,`include/`,`serve/`,`tools/`). Fork: `--profile-decode` (`common/arg.cpp:1977-1982`), `GGML_SCHED_DEBUG` — **fork-only** | `IMPL-GAP-FORK.md:41-46`, `PROFILE-DECODE.md` |
| **Upstream-tracking model** | We pin a **tag** (`v0.1.29`). Upstream is tag-driven (30 tags/7 d), PR-based (tag subjects reference `#197 #187 #188 #207 #154 #224 #220`), and **releases are not API-stable** (`MIN_ENGINE` lockstep). Local history is incomplete (shallow), so a true commit total cannot be reported | `[verified gap]` |

---

## 7. Options, cost, recommendation

Costs are **agent-days** for one engineer familiar with both codebases; risk is stated as
(owner-judged) blast radius if it goes wrong. All estimates are `[hypothesis]` — no option has
been prototyped.

| # | Option | What it means | Cost | Risk | Notes |
|---|---|---|---:|---|---|
| **A** | **Strata as primary Flash-Next engine on 3060/5060 Ti; fork elsewhere** | Head gains an engine adapter (L1), launches `serve.server` (L2), sentinel + port (L3/L4), pack-path model registry (L5/L6), RTX-only OCI image in CI (L7). KV migration, P/D, COMBINED **stay fork-only** → two engine families coexist permanently. | **18–25** | **High** — dual engine semantics across the fleet; `models.json`/preset INI/preset-alias/`0x41 capabilities` all fork-shaped; `/metrics` and `/slots` already mismatch; `n_predict` mismatch is a silent-wrong-behaviour bug class | Directly contradicts owner's *"prefer clean cutover over dual-path unless unavoidable"* — and the dual-path here is **unavoidable**, because P100 (sm_60) cannot run Strata at all (`CMakeLists.txt:71-75`) |
| **B** | **Strata fork in our org, carrying patches** | Clone `Niko1221/Strata` into `ddvnguyen/strata`, pin `v0.1.29`, carry: KV export/import (new), HTTP state routes (new), multi-slot/FIFO removal (new), `n_predict`/`stop`/`logprobs` (new), Prometheus `/metrics` (new), P/D role split (new), sm_60 (impossible — `FATAL_ERROR`), plus our `STRATA_GGML_DIR` fix. | **30–45** | **High** — we would own a 70 K-loc engine with **no CI, no shipped tests, no SBOM**, upstream cutting ~daily releases we can't track cleanly | The "patches" are not patches — items 1–6 are *new subsystems*. Cost is dominated by rebuilding what the fork already has |
| **C** | **Port Strata's miss-handling / GPU-resident adaptive expert cache into the fork** | The ranked-gap analysis says the win comes from *where expert GEMMs run* + *publish-first plan/GPU-reach pipeline* + *pre-fill chunk DMA ring*, not from the packaging (`docs/evidence/strata-gap/IMPL-GAP.md:23-34`, gaps #1/#2/#4/#8). Target: fork `--moe-expert-cache-size` thrash (F1 = 9.28 vs F0 = 15.30 `[measured]`) and `ggml_backend_sched` per-layer split/sync (gap #2, est. 15–25 ms/token `[hypothesis]`). | **15–25** (staged) | **Medium** — touches ggml sched/CUDA-graph paths; but each stage is measurable on the existing fork rig and reverts cleanly | **Keeps one engine, one protocol, P100+P/D/COMBINED intact.** Highest ratio of measured-signal to new-surface |
| **D** | **Keep fork; Strata reference only** | Status quo: Strata stays an A/B reference + source of ideas. Epic #811 already ships the ranked gap table. | **2–4** (docs + continued A/B) | **Low** | Zero integration risk; also **zero** of the 2.12×/3.01× captured |

### Recommendation

**C, with D as the default until a fork stage lands — do not do A or B.**

Rationale:
1. **The measured win is portable in principle, not bound to Strata's packaging.**
   `IMPL-GAP.md:23-34` attributes the 2.12×/3.01× to *where expert GEMMs run* (gap #1),
   *per-layer CPU↔GPU serialization* (gap #2) and *prefill chunk + DMA ring* (gap #4) —
   all fork-addressable, none requiring an engine swap.
2. **A is explicitly excluded by two owner rules at once.** It creates the dual-path the owner
   said to avoid, *and* it cannot cover the fleet: **P100 (sm_60) is a hard build failure**
   (`CMakeLists.txt:71-75`), so the fork stays in production regardless.
3. **B rebuilds the fork inside a less-maintained shell**: no CI, tests not shipped, 30
   releases/7 days, `MIN_ENGINE` lockstep, and the six subsystems Strata lacks (§3, §1.3) are
   all *new code*, not patches.
4. **Strata cannot serve any model Hydra currently serves** (§5.1) — A/B would require a second
   model line, ~86 GB per quant, on top of everything else.

### Smallest first integration step

**Not** an engine swap. Ship a **read-only engine-capability probe behind a flag, one node** —
the cheapest thing that turns our biggest `[hypothesis]` into evidence.

Scope (all read-only w.r.t. Hydra behaviour):
1. New Head config key `llama.kind: llama-engine | strata` defaulting to `llama-engine`
   (config.go `Llama` block), plus `readiness.sentinels` override so `READY` matches
   (`infra/hydra-head/config/global.yaml:70-75`).
2. New `binaries.strata-*` entry (OCI image built by a **new CI workflow**, patterned on
   `deploy-heads.yml:127-137`, `sm86+sm120` only) + `llama.kind=strata` on **one** node
   (`infra/hydra-head/config/node-rtx3060.yaml`).
3. A **probe script** (not a scheduler change) that runs the §1.3 matrix against the live
   Strata instance and writes results to `docs/evidence/strata-gap/INTEGRATION-PROBE.md`.

Explicitly **out of scope** for step 1: any `WorkerSchedulerService` change, any KV/RPC work,
any model-registry change, any second node.

**Pre-registered success criteria (frozen before the probe runs):**

| # | Criterion | Pass bar |
|---|---|---|
| S1 | Head starts Strata and reaches `StateReady` via sentinel | ready within `timeout_sec: 180` (`global.yaml:75`), 3/3 restarts |
| S2 | `GET /health` returns 2xx from Core's poller | 20/20 polls over 400 s (`CoordinatorModels.cs:80`) |
| S3 | `GET /slots` parses; `id`, `is_processing` correct vs. a known busy/idle state | 10/10 correct; `n_past`/`n_remain` **expected to fail** (recorded, not gated) |
| S4 | `POST /v1/chat/completions` `stream:true` returns SSE that Core can proxy end-to-end | 10/10 complete, `finish_reason` present |
| S5 | **Negative, expected-fail gate:** `n_predict=0` does **not** produce a generation | **expected FAIL** → confirms §1.3 blocker before any scheduler work |
| S6 | Negative: `GET /slots/0/state` returns 404 (documented, not gated) | confirms KV gap in §3 |
| S7 | Prometheus `/metrics` scrape fails to parse JSON (documented, not gated) | confirms §1.3 |

S5 and S6 are **designed to fail**; they are the go/no-go evidence for whether Option A can
ever be more than a single-node curiosity. If S5 fails as predicted, the smallest viable
follow-up is a ~10-line read of `n_predict` in `serve/server.py` — and *that* decision goes
back to the owner, not into the scheduler.

---

## Gaps / NOT FOUND

1. **`src/llama-cpp` and `src/ik-llama-cpp` are empty in this worktree** → all fork-side claims
   cited from Hydra specs/docs; the `hydra-state-streaming` 3-endpoint handlers are `[gap]`.
2. **`docs/evidence/strata-3060-ab/` is not on this epic branch** — it lives on
   `docs/811-t0006-strata-vs-fork-evidence` (commit `37826a2a0`), read via `git show`.
3. `return_tokens`, `keep_alive`, `samplers` request field — NOT FOUND anywhere in the Hydra tree.
4. Core→Head control channel — no `9700`/`HeadUrl` reference in `src/core`; Head is never called
   by Core.
5. Head-side KV snapshot on shutdown — NOT FOUND in `src/head/internal`.
6. `DELETE /slots/{id}` documented (`specs/rpc-protocol.md:99`) but code uses
   `POST /slots/{id}?action=erase` (`LlamaClient.cs:188-189`) → **doc/code conflict, unresolved.**
7. Strata upstream CI posture — NOT FOUND (`.github/` absent from published source).
8. `tests/CMakeLists.txt`, `bench/micro/`, `docs/pack-format.md`,
   `pyproject.toml`/`requirements.txt`/`THIRD_PARTY_LICENSES` — NOT FOUND at `src-v0.1.29/`.
9. Equivalence of our local `third_party/llama.cpp` to upstream pin `3cf03257…` — UNVERIFIED.
10. True Strata upstream commit/author totals — not determinable locally (shallow clones);
    `git fetch --unshallow` needs network + writes → out of scope for this spike.
11. Exact MTP `rt/` conversion step in `prep-mtp.sh` — not fully re-read (`[hypothesis]` above).
12. All option costs in §7 are `[hypothesis]`.

---

## Change summary

**Changed:** created this document only (`docs/evidence/strata-gap/STRATA-INTEGRATION-FEASIBILITY.md`)
on branch `docs/811-strata-integration`. No source, config, or evidence files touched; no build,
server, GPU, podman, or `/tmp` use; `/mnt/WorkDisk/strata` opened read-only.

**Assumptions:**
- Strata v0.1.29 = `/mnt/WorkDisk/strata/src-v0.1.29`; Hydra = this worktree off
  `origin/epic/811-strata-gap-analysis`.
- Line counts from `find | xargs wc -l` (`cloc` unavailable).
- Quant type ids mapped from Strata's pinned `ggml.h`.

**Risks / open questions:**
- The two load-bearing negatives (no KV export, no P/D role split) are **absence-of-evidence**
  claims from bounded grep; a re-index or a wider read could contradict them.
- Option C's cost range (15–25 d) is the least evidence-backed number here — it depends on the
  still-open fork-side gap #2 attribution (`IMPL-GAP.md:58-66`).
- S5 (`n_predict`) is the single highest-leverage probe: it decides whether Option A is
  18–25 days or 18–25 days *plus* an engine-mode RPC port.
