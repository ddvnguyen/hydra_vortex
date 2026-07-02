# Plan — Issue #382: Real COMBINED engine mode for dense models — run Qwopus3.6-27B-Coder-Compat-MTP on the 2-GPU pair

> Implementation epic: #383 · Refs: #382 (impl issue) · #381 (spike) · #375 · #376 · #270 (epic) · #258

## Context

`Qwopus3.6-27B-Coder-Compat-MTP-Q5_K_M.gguf` (18.18 GiB) is the first model that cannot fit on either same-host GPU alone (5060 Ti 16 GB / 3060 12 GB) — it requires the pair. Spike #381 measured the winning runtime config: **layer split `-ts 21/44` over ggml-RPC, `-ub 1024`, MTP (`--spec-type draft-mtp --spec-draft-n-max 3`, +53% decode), per-model RoPE `rope-scale 4`** (not the global 5). The existing COMBINED mechanism (`--combined-ot-pattern` expert-split) is the wrong *split strategy* for this dense + hybrid-SSM model — whole-block `-ot` corrupts recurrent-layer state across the RPC boundary and crashes; FFN-only loses throughput.

**Goal (user decision): extend llama-engine's COMBINED mode itself** so it natively handles dense models with an optimized layer-split strategy — a real engine mode (INFO advertises it, hydra semantics apply), not stock `--rpc`/`-ts` flags bypassing the COMBINED concept. The 27B ships as a **switchable config profile** (operator picks 35B-SOLO vs 27B-COMBINED per restart, no dynamic switching in v1), **MTP on by default at 21/44**, and the 27B profile's workers.json **keeps all 3 GPU workers registered** with the 2 RTX GPUs as the COMBINED pair. Handoff = **single epic issue** containing the task breakdown (T1–T8). Forward-prep: a `run_type` registration field per worker (for later dynamic model switching where each model registers its run type); KV model-identity metadata already exists (`model_hash` in Store SQLite + CrossModelGuard) and only needs E2E verification for the 27B.

### What "COMBINED — layer split" means vs today
Today's COMBINED (`-ot` expert-split) is dynamic: both GPUs run independent SOLO engines with the full model; per big request the scheduler borrows the free 3060, flips the head into expert mode, and returns the peer afterward. The 27B has no SOLO baseline — it doesn't fit on either GPU — so the pairing is established **at model load**: the peer's RPC backend is attached *before* load and ~21/65 of the layers are allocated there for the engine's lifetime; every request computes on both GPUs. Same COMBINED shape (5060 Ti head owns HTTP/slots/KV; 3060 peer serves compute+VRAM over ggml-RPC; exclusive pairing), new split strategy (`layer` vs `expert`) and new lifetime (load-time static vs per-request dynamic).

## Verified facts the design rests on

- llama-engine filters its hydra flags (`--rpc-engine`, `--combined-ot-pattern`, `--ggml-rpc-port`) out of argv before stock parsing (`llama.cpp/tools/llama-engine/llama-engine.cpp:187-209`); the current COMBINED attach happens **after** model load via `llama_hydra_load_combined_experts` (`src/llama-hydra.cpp:100`, `ggml_backend_rpc_add_server` at :117). Dense layer-split instead needs the peer device registered **pre-load** (stock mechanism: `add_rpc_devices` + `tensor_split`; RPC devices are inserted at the FRONT of the device list, `src/llama.cpp:239-243`, so `tensor_split[0]` = peer → `21/44` = 3060:21, 5060 Ti:44).
- Issue #376 (assert in `ggml_backend_rpc_add_server` when `--rpc-engine` + `--ggml-rpc-port` are both set on one process) is **not triggered** in this topology: head sets only `--rpc-engine`, peer sets only `--ggml-rpc-port`.
- llama-engine currently **cannot start without a model** (`llama-engine.cpp:313-320`; `start_shared_backend_rpc_server` :224-262 needs the live context's backends). The 27B peer cannot load the 27B ⇒ a **peer-only mode** is a new fork feature.
- INFO 0x41 (`tools/server/server-context.cpp:2955-2994`) reports `mode: solo|combined` from `llama_hydra_get_expert_mode`, plus `capabilities` (currently missing `combined` — issue #375), `peer_addr`, `layer_split`. `SET_EXPERT_MODE` = 0x44.
- C# `MultiEngineRouter.Select` (`Services/MultiEngineRouter.cs:24-49`) gates on `CombinedEnabled` + `head.CombinedCapable` (static from workers.json — INFO capabilities are parsed into `EngineInfo` but never written back; #375 doesn't block routing today) + non-empty `head.CombinedOtSplit` + `estTokens > MultiEngineThreshold`. `ApplyMultiEngineAsync` (`WorkerSchedulerService.cs:866-911`) sends `SET_EXPERT_MODE("combined")` and treats `ReportsSolo` (:922-937) as peer-declined fallback. Peer exclusivity: `TryReserveWorkerExclusive` (`RepositoriesImpl.cs:232-243`, requires fully idle pool).
- Coordinator health poll is llama-HTTP (`HealthMonitorService.cs:75-101`, GET `/slots` then `/health`) and **already treats "no slots, server OK" as healthy** (:81-99) — a peer-only llama-engine with HTTP up and 0 slots is health-pollable with zero C# changes.
- hydra-head llama flags are an untyped `params map[string]any` (`src/head/internal/config/config.go:27-35`); list values silently dropped (:279-281); `BuildLlamaArgs` :253-285; validation minimal (:357-380).
- Fork has standalone `llama-fit-params` (`tools/fit-params/fit-params.cpp`) that **exits 1** on no-fit; in-server `--fit` proceeds to OOM anyway (`common/common.cpp:1189-1198`) ⇒ fail-fast belongs in a hydra-head preflight.
- MTP (`--spec-type draft-mtp`, `--spec-draft-n-max`, `common/arg.cpp:3648/3575`) needs a draft context = extra VRAM on both devices. RoPE/YaRN flags are stock (`arg.cpp:1943-1981`).
- KV model-identity already end-to-end: `model_alias/model_hash/model_path` stamped on save, persisted (`Models/StoreMetadata.cs:21,137`), gated on restore by `CrossModelGuard.cs`.
- Doc drift to fix: `node-rtx.yaml:79-81` COMBINED flags are live (CLAUDE.md says commented out); peer ggml-rpc port is 9506 (`node-rtx3060.yaml:88`), not 9504.

## Design

### D1 — Fork: COMBINED layer-split mode in llama-engine (head side)
New hydra flags on llama-engine (added to the argv filter + `hydra_capability_flags`, `llama-engine.cpp:164-209`):
- `--combined-split-mode {expert|layer}` (default `expert` = today's behavior, zero regression).
- `--combined-tensor-split <r0/r1[,…]>` (required in `layer` mode; RPC-first order).

Behavior in `layer` mode (in `llama_engine()`, `llama-engine.cpp:266-352`):
- Probe peer (`try_tcp_connect`, existing) — unreachable ⇒ **fail startup** (unlike expert mode's degrade-to-solo, a layer-split engine without its peer cannot load the model at all; hydra-head's restart/backoff owns retry).
- Register the peer pre-load: `add_rpc_devices(rpc_engine_addr)` + set `tensor_split` from `--combined-tensor-split`, then normal model load spans both devices. `llama_hydra_load_combined_experts` is NOT called; `--combined-ot-pattern` is rejected in layer mode.
- Hydra state: mark mode **combined (static)** — `llama_hydra_get_expert_mode` reports combined; INFO returns `mode:"combined"`, `split_mode:"layer"`, `layer_split:"21/44"`, `peer_addr`, and adds `combined` to `capabilities` (closes the #375 gap for this path). `SET_EXPERT_MODE("combined")` → success no-op (already combined); `SET_EXPERT_MODE("solo")` → error `combined_static` (cannot unsplit a loaded model).

### D2 — Fork: peer-only engine mode (3060 side)
New flag `--peer-only` (or `--combined-peer`): llama-engine starts **without loading a model** — HTTP server up (health `/health`, INFO advertising `mode:"peer"`, `capabilities:["combined-peer"]`, 0 slots), and exposes the ggml-RPC server on `--ggml-rpc-port` backed by locally-created CUDA backends (not context-derived — this is the code change vs `start_shared_backend_rpc_server`, which needs a loaded context; the stock `tools/rpc/rpc-server.cpp` backend-creation path is the reference). Inference endpoints return 503. Fallback if this proves large during implementation: stock `rpc-server` binary + hydra-head TCP health (kept as a task-level contingency, not the plan of record — flag before switching).

### D3 — hydra-head: typed schema + validation (Go, `src/head/internal/config/config.go`)
New optional typed sections under `llama:` (absent = zero behavior change):
```yaml
llama:
  combined:   { split_mode: layer, peer_rpc: "localhost:9506", tensor_split: "21/44", ubatch: 1024 }
              # → --rpc-engine / --combined-split-mode layer / --combined-tensor-split / --ubatch-size
  peer_only:  true                                    # peer profile → --peer-only (+ existing ggml-rpc-port param)
  mtp:        { enabled: true, spec_type: draft-mtp, draft_n_max: 3 }    # → --spec-type / --spec-draft-n-max
  rope:       { scaling: yarn, scale: 4, yarn_orig_ctx: 32768 }          # → --rope-scaling / --rope-scale / --yarn-orig-ctx
  fit:        { preflight: true, print: true, mode: "off", target: "" }  # preflight tool + --fit / --fit-print / --fit-target
```
- Merge: whole-section node-wins; when `rope:` is set, **delete** `rope-scaling`/`rope-scale`/`yarn-orig-ctx` from the merged params map (per-model RoPE beats global.yaml's 35B tuning).
- Validation (extend `Validate()`): `combined.split_mode=layer` ⇒ `peer_rpc`+`tensor_split` required, `tensor_split` matches `^[0-9]+([,/][0-9]+)+$`, params map must NOT contain `rpc`, `rpc-engine`, `tensor-split`, `combined-ot-pattern` (collision with typed section); `peer_only` ⇒ requires `ggml-rpc-port`, forbids `model` requirement (model optional for peer); `mtp.enabled` ⇒ params must not duplicate spec flags; `mtp && combined && !fit.preflight` ⇒ startup warning; list-valued params become a **hard Validate() error** (replaces the silent drop at :279-281).
- `BuildLlamaArgs` appends typed-section flags after the params loop.

### D4 — hydra-head: fit preflight (fail-fast, issue req 4)
In `StartLlama` (`src/head/internal/process/manager.go`), when `fit.preflight`: run `llama-fit-params` with the memory-relevant arg subset (model, ctx-size, parallel, ubatch, ngl, cache-type-k/v, tensor-split, peer rpc, spec-*) before exec'ing llama-engine. Nonzero exit ⇒ don't start, log tool output, surface `lastError` in `/status`, let restart/backoff retry (also fail-fasts on a dead peer). Ship `llama-fit-params` in the OCI image/bind-mount; verify during E2E that the estimate covers the MTP draft context (else compensate via `fit.target`, e.g. `1024/1024` MiB). Profile keeps `--fit off` (deterministic prod config) + `--fit-print on` (log estimates each start).

### D5 — Hydra.Core: COMBINED-static routing + `run_type` (small C#)
`workers-27b.json` keeps **all 3 workers**:
- `rtx`: `role:"head"`, `peer_worker:"rtx3060"`, `combined_capable:true`, `combined_ot_split:"21/44"` (opaque descriptor — C# never sends it for COMBINED; satisfies `ModeUsable`), `run_type:"combined-static"`, `slots:1`, `decode_speed_tps:31`.
- `rtx3060`: `run_type:"combined-static-peer"`, health-polled normally (peer-only engine serves HTTP, 0 slots ⇒ existing "router-ready" healthy path), never independently schedulable.
- `p100`: unchanged (35B SOLO for its own sessions; RTX↔P100 P/D is inert in this profile — CrossModelGuard blocks 27B↔35B KV by hash mismatch, correct).
- Env: `HYDRA_COORD_COMBINED_ENABLED=true`, `HYDRA_COORD_MULTI_ENGINE_POLICY=combined`, `HYDRA_COORD_MULTI_ENGINE_THRESHOLD=0` (every request is physically combined), `HYDRA_COORD_ALLOW_CROSS_MODEL_KV_REUSE=false`.

C# changes (minimal, in `Models/CoordinatorModels.cs`, `Services/MultiEngineRouter.cs`/`WorkerSchedulerService.cs`, `RepositoriesImpl.cs`):
- New `run_type` field on `WorkerConfig` (`solo` default | `combined-static` | `combined-static-peer` | `combined-ot` | `pipeline` | `pd-split`), parsed from workers.json, surfaced in logs + status endpoint — the registration hook for later dynamic model switching.
- `combined-static-peer` ⇒ permanently excluded from scheduling (startup exclusive reservation, reusing `TryReserveWorkerExclusive` semantics).
- `combined-static` head ⇒ requests below/above threshold both valid: multi-engine path applies; `SET_EXPERT_MODE("combined")` succeeds against the D1 engine (reports combined, `ReportsSolo` false) so the existing `ApplyMultiEngineAsync` flow works unmodified; sub-threshold/atomic path must not require the peer to be "free" in the old sense — with threshold 0 all cold routes take the multi-engine path, and the per-request peer reservation trivially succeeds since nothing else can schedule the peer.
- **FLAG (architecture-principles P1)**: CUDA1 exclusivity moves from per-request runtime reservation to a startup-time static reservation ("COMBINED-static"); the 3060's dual-role (SOLO/COMBINED-peer) capability does not exist in the 27B profile. Amend `docs/architecture-principles.md`.

### D6 — Profiles & switching
- `infra/hydra-head/config/node-rtx-27b.yaml`: llama-engine, `model: /models/Qwopus3.6-27B-Coder-Compat-MTP-Q5_K_M.gguf`, `combined: {split_mode: layer, peer_rpc: "localhost:9506", tensor_split: "21/44", ubatch: 1024}`, `mtp`/`rope`/`fit` per D3, `ctx-size: 131072` (yarn ×4 over 32K native), `parallel: 1`.
- `infra/hydra-head/config/node-rtx3060-27b.yaml`: `peer_only: true`, `ggml-rpc-port: 9506`, `CUDA_VISIBLE_DEVICES=1`, normal HTTP health.
- `infra/hydra-core/config/workers-27b.json` per D5.
- `infra/docker-compose.hydra.yml` parametrized via env with today's 35B values as defaults: `-node ${HYDRA_HEAD_RTX_NODE_CONFIG:-…}`, `HYDRA_COORD_CONFIG_FILE`, `HYDRA_COORD_COMBINED_ENABLED`, `HYDRA_COORD_MULTI_ENGINE_*`, `HYDRA_COORD_ALLOW_CROSS_MODEL_KV_REUSE`. Operator switch = `.env` profile block + `podman compose up -d`; P100 untouched. Document peer-before-head start order.
- Fix CLAUDE.md/architecture.md drift (ports 9505/9506; COMBINED flags live, not commented).

## Task breakdown (body of the epic issue; independently assignable)

| # | Task | Repo/files | Depends |
|---|------|-----------|---------|
| T1 | **Fork: COMBINED layer-split head mode** — `--combined-split-mode layer` + `--combined-tensor-split`; pre-load `add_rpc_devices` + tensor_split; fail-fast on unreachable peer; static combined state; INFO `mode:combined`/`split_mode:layer`/`capabilities+=combined` (#375 for this path); SET_EXPERT_MODE semantics (`solo` → `combined_static` error); reject `--combined-ot-pattern` in layer mode | llama.cpp fork: `tools/llama-engine/llama-engine.cpp`, `src/llama-hydra.{cpp,h}`, `tools/server/server-context.cpp` | — |
| T2 | **Fork: `--peer-only` engine mode** — start without model, HTTP health + INFO `mode:peer`, ggml-RPC server from locally-created backends (reference: `tools/rpc/rpc-server.cpp`), 503 on inference. Contingency (flag before switching): stock rpc-server + hydra-head TCP health | fork: `tools/llama-engine/llama-engine.cpp`, `tools/server/*` | — (parallel to T1) |
| T3 | **hydra-head typed schema** — `combined`/`peer_only`/`mtp`/`rope`/`fit` sections, whole-section merge + rope-param deletion, validation rules incl. params-collision rejection + list-param hard error, `BuildLlamaArgs` emission; golden-args test | `src/head/internal/config/config.go`, `config_test.go` | flag names from T1/T2 |
| T4 | **hydra-head fit preflight** — exec `llama-fit-params` pre-start, abort + surface error on exit≠0; ship binary in OCI image | `src/head/internal/process/manager.go`, deploy bits | T3 |
| T5 | **C# run_type + COMBINED-static** — `run_type` field parse/expose; `combined-static-peer` startup exclusive reservation; verify multi-engine flow end-to-end against T1 engine semantics (threshold 0, `ReportsSolo` false); unit tests | `src/core/Hydra.Core/Models/CoordinatorModels.cs`, `Services/{MultiEngineRouter,WorkerSchedulerService,RepositoriesImpl,HealthMonitorService}.cs`, `Tests.Core` | T1 semantics |
| T6 | **Profiles + compose/deploy parametrization** — the three new config files, compose env defaults, `deploy-hydra-head.sh`, `.env.example` | `infra/…` per D6 | T1–T3 shape |
| T7 | **Docs** — architecture-principles "COMBINED-static" amendment (P1 flag), profile-switch runbook (stop order, peer-before-head, env flips), CLAUDE.md port/flag drift fix | `docs/architecture-principles.md`, `DevelopmentRunBook.md`, `CLAUDE.md` | — (parallel) |
| T8 | **Build + E2E on hardware** — build sm86+sm120 fat binary, push OCI image, bump `src/llama-cpp` submodule; deploy 27B profile; preflight rejects dead peer + oversized ctx before CUDA OOM; INFO on head reports `mode:combined`/`split_mode:layer`; bench vs spike (~937 pp4096 / ~21 tg64 at 13/52 bench-ctx; ~31 tg with MTP at 21/44); fit estimate covers MTP draft ctx; profile switch 27B↔35B both directions clean; p100 unaffected; CrossModelGuard blocks 35B↔27B KV restore; all 3 workers visible with run_types, rtx3060 healthy yet never scheduled; kill peer → head fails fast + recovers when peer returns | fork + hydra_vortex | all |

T3 golden-args acceptance (exact head CLI): `--rpc-engine localhost:9506 --combined-split-mode layer --combined-tensor-split 21/44 --ubatch-size 1024 --spec-type draft-mtp --spec-draft-n-max 3 --rope-scaling yarn --rope-scale 4 --yarn-orig-ctx 32768 --fit off --fit-print on` — no `rope-scale 5`, no `--combined-ot-pattern`; no-section config = byte-identical args to today.

## Verification

- Fork: build both arches; T1/T2 exercised by starting head+peer locally (small dense model that fits split, e.g. any small GGUF with `--combined-tensor-split`) before hardware run.
- Go: `go test ./...` in `src/head`. C#: `dotnet test src/core/Tests.Shared && dotnet test src/core/Tests.Core`.
- System: `pytest tests/system` green in the default (35B) profile — default env must reproduce today's exact runtime behavior.
- Hardware E2E: T8 (spike numbers are the acceptance bar).

## Handoff

The task breakdown above is tracked in implementation epic **#383** — coding agents pick up T1–T8 from there (T1/T2/T7 can start in parallel; T3 needs T1/T2 flag names; T5 needs T1 semantics; T8 last).
