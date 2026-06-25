# Spike: engine mode switch — shared-backend SOLO + COMBINED-peer (issue #348)

## Question

Can a single `llama-engine` process serve its own SOLO inference duty *and* simultaneously
expose itself as a COMBINED-mode RPC-backend peer, without a restart, and without the two
duties contending as independent CUDA contexts on the same physical GPU?

## Design

Approach B (always-on dual role), corrected mid-implementation: rather than running the
embedded ggml-RPC server against *independently created* backend instances
(`ggml_backend_dev_init` per device, the old `run_combined_worker` design), the embedded
RPC server now serves through the **same** `ggml_backend_t` instances the engine's own
`llama_context` already built for local inference (`llama_context::get_sched()` →
`ggml_backend_sched_get_backend()`, exposed via the new `llama_hydra_get_compute_backends`
accessor; the backends are handed to a new `ggml_backend_rpc_start_server_with_backends`
that skips `ggml_backend_rpc_start_server`'s internal device-to-backend creation step).

One backend instance per physical device for the whole process — local decode dispatch
(`llama-context.cpp`'s `graph_compute()`) and inbound RPC graph-compute requests
(`rpc_server::graph_compute`/`graph_recompute` in `ggml-rpc.cpp`) are serialized via a
per-device mutex (`ggml_backend_rpc_server_compute_lock`/`unlock`), engaged only when
`--ggml-rpc-port` is configured (`llama_hydra_enable_shared_backend_compute_lock`) — zero
cost on the decode hot path otherwise.

Separately, COMBINED mode's *tensor-level* dual-load (`llama_hydra_load_combined_experts`)
gained a VRAM-headroom check (`ggml_backend_rpc_get_device_memory`, 1.25x headroom factor)
since the peer's VRAM is no longer guaranteed empty under always-on dual role.

## Method

Real hardware: RTX 5060 Ti (host) as COMBINED head, Tesla P100 (KVM VM) as the always-on
SOLO+peer node. Built both targets (`cmake --build build_sm120_static`/`build_sm60_static
--target llama-engine`, `GGML_RPC=ON`, fully static w.r.t. ggml/llama, per
`DevelopmentRunBook.md`'s documented recipe). Temporarily deployed `llama-engine` to both
live hydra-head-managed nodes (additive binary alongside the existing `llama-server`,
config changes reverted after) — see "Pre-existing gaps found" below for why this required
extra steps beyond what #348 itself touches.

P100: `--ggml-rpc-port 9520`, serving its own resident model on `--port 8086` (SOLO) at the
same time. RTX: `--rpc-engine 192.168.122.21:9520 --combined-ot-pattern
"blk\\.[0-2]\\.ffn_(gate|up|down)_exps\\.weight"` (a deliberately small 3-layer slice, to
keep the dual-load fast for a smoke test, not a production-shaped split).

Drove genuinely concurrent traffic with two threads against the live control-RPC ports
(0x4859 wire protocol, `tests/e1_rpc_test.py`'s pattern, adapted to the current JSON-payload
PREFILL/DECODE schema — `e1_rpc_test.py` itself is stale, see below): one thread firing 8
SOLO PREFILL+DECODE cycles directly at P100 (port 9502) while a second thread fired
SET_EXPERT_MODE("combined") + PREFILL/DECODE at RTX (port 9503).

## Findings — correctness

**P100's SOLO duty while also exposing the shared RPC backend: correct, no regressions.**
All 8 concurrent SOLO PREFILL+DECODE cycles on P100 returned `HYDRA_STATUS_OK`, consistent
`n_past`, no errors, no crashes — while P100's embedded RPC server thread (serving the same
backend instance) was simultaneously live and had already received and serviced RTX's
startup dual-load copy. This is the core, riskier half of #348's design (the part the
original "not a supported pattern upstream" comment worried about) and it held up cleanly.

**RTX's own SOLO mode: correct, no regression.** Explicit `SET_EXPERT_MODE("solo")` +
PREFILL + DECODE on RTX succeeded cleanly (`n_past: 21`, valid state/logits sizes) both
before and after the COMBINED-mode crash below — confirming #348's changes don't regress
the SOLO path.

**RTX's COMBINED mode: crashes — but in pre-existing #287/#260 code, not in #348's
changes.** The first real PREFILL after `SET_EXPERT_MODE("combined")` hit a `ggml-backend.cpp`
assertion (`pre-allocated tensor (blk.0.ffn_down_exps.weight) in a buffer (RPC0[...]) that
cannot run the operation (NONE)`) and aborted the process; hydra-head's supervisor restarted
it within ~1s, `last_exit_code: 0` (clean process exit path, not memory corruption).
Root-caused to `llama_hydra_load_combined_experts` allocating the dual-loaded tensors on an
RPC backend that's never registered with the head's own `ggml_backend_sched_t` — unrelated
to anything #348 touches (#348 doesn't modify graph-building, scheduling, or the dual-load
allocation mechanism itself). Filed separately: `ddvnguyen/hydra_vortex#353` /
`ddvnguyen/llama.cpp#12`. This means COMBINED mode itself was apparently never exercised
end-to-end before this spike — consistent with issue #260 (the E2 spike meant to validate
this exact path) having been merged via #287 without ever closing out or producing its
`docs/spike-engine-expert-mode.md` deliverable.

**Net effect on this spike's question:** the half of #348's design that #287/#260's bug
didn't block — P100 simultaneously serving SOLO duty and acting as an always-on COMBINED
RPC-backend peer, no restart, no contention — is verified correct on real hardware. The
other half (RTX actually driving COMBINED-mode compute *into* that shared backend under
load) is blocked by the separate, pre-existing #353 bug and could not be exercised this
round; once #353 is fixed, re-running this same concurrent-traffic test against RTX's
COMBINED path is the natural follow-up to fully close the loop.

## Pre-existing gaps found (not fixed as part of #348, except where noted)

- **`llama-engine` never set `ctx_http.is_ready`** (unlike `tools/server/server.cpp`'s
  `main()`, which does, right after `load_model()` succeeds). Every HTTP endpoint 503'd
  "Loading model" forever, regardless of model state. This blocked verification outright
  (no `/health` ever passed), so it **was** fixed as part of this PR
  (`llama-engine.cpp`, one line: `ctx_http.is_ready.store(true);` after `ctx_http.start()`).
  Consistent with the research finding that `llama-engine` had never actually been deployed
  to production before this spike.
- **`llama-engine`'s HTTP server doesn't bind until after `load_model()` returns** (unlike
  stock `server.cpp`, which starts HTTP *before* loading the model specifically so `/health`
  can respond during the load window). Not fixed here — out of scope, just slows down
  manual verification (P100's 4-5 minute model load means `/health` connection-refuses for
  that whole window instead of 503ing).
- **COMBINED mode's first-ever real exercise crashes** — see above, filed as #353 /
  fork#12, P0, blocks the "Llama-Engine — P/D split mix-quant" milestone's COMBINED-dependent
  work until fixed.
- **`tests/e1_rpc_test.py` is stale**: its PREFILL test sends raw binary dummy tokens, but
  the current wire format requires a JSON payload (`{"messages": [...], "n_predict": 0}`,
  per `specs/rpc-protocol.md`). Not fixed here (not in #348's path) — worth a follow-up
  cleanup issue so the test script matches the documented protocol.

## Switch-cost: before vs. after

**Before (process-restart required):** flipping a node between "SOLO only" and "SOLO +
COMBINED-peer-available" required killing and relaunching the process with a different
`--role`, paying full model reload — P100's model load alone measured 235-300s typical (per
`node-p100.yaml`'s existing health-check tuning comment), and was repeatedly confirmed at
~3m22s–3m30s across multiple restarts in this spike.

**After:** the *exclusivity* is gone — a single process, started once with
`--ggml-rpc-port` set, is both SOLO-active and RPC-backend-active for its entire lifetime,
confirmed via INFO (0x41): `"solo_active": true, "rpc_backend_active": true` simultaneously
on P100, with `"combined_head_attached": true` reachable from RTX. Toggling whether
`--ggml-rpc-port` is *configured at all* still requires a relaunch (it's a launch-time
argument, not runtime-toggleable) — what's eliminated is the old design's forced choice
between "this process loads a model" and "this process is RPC-servable," which was the
actual restart-the-world cost #217/#348 were about. No serialization-latency-under-load
numbers were collected this round (COMBINED traffic into the shared backend was blocked by
#353), so the *quantified* interference cost of the per-device mutex under sustained
concurrent local+remote compute remains a follow-up measurement once #353 is fixed.

## Decision

Confirms the shared-backend + per-device-mutex architecture (the corrected design, not the
original two-independent-backend-instances framing). The VRAM-headroom check's 1.25x factor
is unvalidated against real sustained-decode KV growth (the COMBINED dual-load in this spike
was a small, short-lived 3-layer slice, not a long-running session) — treat it as a
starting heuristic pending real production-shaped usage.

## Known limitations

- VRAM-headroom check is a one-shot, startup-time snapshot — does not protect against the
  peer's free VRAM shrinking later as its own KV cache grows mid-session (documented in code).
- Mutex granularity is per-backend/coarse (a full graph-compute call is serialized, not
  individual kernels) — acceptable for "cheap opportunistic dual-duty," not "guaranteed
  parallel throughput." Not measured under real load this round (see above).
- True zero-copy tensor sharing (peer exposes its *own* resident tensors directly, no
  duplicate copy) is PIPELINE mode's (0x46) design intent, not solved here — tracked as a
  follow-up under PIPELINE/#287's remaining half, per `specs/rpc-protocol.md`.
- COMBINED-mode end-to-end correctness under #348's design could not be fully verified due
  to the separate #353 crash — re-run once that's fixed.
