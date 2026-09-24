# Design Note — In-Source Decode Profiler (`--profile-decode`)

Track `t-d1cf43b723` §91. Owner-directed: instrumentation lives IN THE FORK, gated by one runtime
flag, default OFF, committed as fork code. This note proposes the design. **Nothing is implemented
until the architect approves.** Source anchors verified on `baseline-flash-next` @ `86af0c9af`
(fork tree `src/llama-cpp`): `llama_context::decode` at `src/llama-context.cpp:1656`;
`llama_context_params` at `include/llama.h:359`; `common_params` at `common/common.h:448`;
flag registration pattern in `common/arg.cpp` (`add_opt`, :1440ff). No NVML usage exists anywhere
in the tree today — proposed via `dlopen`, no hard link dependency.

## 1. Flag and plumbing

- New common flag `--profile-decode` (server) → `common_params::profile_decode` (default `false`),
  registered in `common/arg.cpp` with the standard `add_opt` pattern.
- Plumb to the context: extend `llama_context_params` (`include/llama.h:359`) with
  `bool profile_decode;` (new field, default false — C callers that zero-init are unaffected), and
  `llama_context` stores it in `cparams`/member. Server copies it from `common_params` where it
  builds context params (same path as every other perf param).
- A one-time `profiler::init(ctx, device_index)` at context construction when the flag is on:
  opens NVML via `dlopen("libnvidia-ml.so.1")` (no build/link dependency; if NVML is unavailable
  the profiler degrades to timing-only and says so once on stderr), allocates event/state buffers,
  records the target CUDA device handle.

### Zero cost when OFF — mechanism and verification

- All hot-path instrumentation sits behind ONE branch: `if (prof_enabled)` evaluated once at
  `decode()` entry (`llama-context.cpp:1656`). When off: no allocation, no timing calls, no NVML
  calls, no events — the only added work is one predictable register test per decode call.
- Verification leg: interleaved n=3, same config (CUDA0 `C-P0` config, ctx 16384), leg A =
  pre-profiler binary (archived build-g3, fp `895c522343eeaa53`), leg B = profiler binary flag OFF.
  PASS if tok/s within the established MTP-off noise band (±1.5%). This is a gate: fail ⇒ fix
  before any ON leg exists.

## 2. What it measures (the open questions, mapped)

| # | Question | Mechanism |
|---|----------|-----------|
| a | PCIe link state DURING decode (settles gen4-vs-gen1) | NVML `nvmlDeviceGetCurrPcieLinkGeneration` / `...LinkWidth` / `nvmlDeviceGetPcieThroughput(RX/TX)` sampled every N steps inside the decode loop (host-side, µs-scale, out of the device's way). Deltas over the window → gen, width, rx/tx KB/s under load. First-ever under-traffic gen observation. |
| b | Host↔device traffic per step | NVML rx/tx byte counters per window (measured demand), cross-checked against explicit `cudaEvent` brackets on the known per-step copy points (D2H logits pull, H2D inputs) if present. Bytes/step = window bytes / steps. |
| c | Per-step time split: GPU compute / CPU expert / transfer / sync-wait | Per step: `t_wall` (chrono around decode body), `t_cpu_busy` (process CPU-clock delta), `t_dev` (CUDA event pair around the device-side graph segment, read back out of band), sync points timed at the known `cudaStreamSynchronize`/`cudaMemcpy` sites. Transfer-vs-wait residual is labeled INFERRED, not measured — stated honestly. |
| d | Per-layer device + time | Inside `ggml-cuda`, when profiling enabled: at graph-compute time scan the cgraph ONCE per graph instance (graphs repeat in continuous decoding — cache boundary list), identify first node per `blk.N.` group, record one `cudaEvent` at each boundary on the compute stream; CPU-offload layer time via host-side chrono at the same boundary scan. 48 event records/step ≈ 50–100 µs ≈ ~0.2% of a ~50 ms step. Event results read back every N steps via `cudaEventQuery`/`ElapsedTime` — NO sync on the hot path. |
| e | MTP first-class | Instrument at the draft/verify decode call sites in the fork's spec path: `t_draft`, `t_verify`, tokens drafted, tokens accepted, per step — aggregated per window. Acceptance becomes a per-step observable, not a server aggregate. |

## 3. Non-perturbation policy (the hard rule)

- **No `cudaDeviceSynchronize` / no stream sync on the hot path, ever.** All device-side results
  (events, timings) are read back OUT OF BAND: every N steps (default N=64) during the natural
  host-side aggregation window, plus a final summary at context release.
- NVML reads: every N steps, host-side, µs-scale.
- Event records: ~0.2% overhead (§2d); all other hot-path additions are host timestamps (~tens of ns).
- Stated expected overhead: **< 1% wall**; VERIFIED, not assumed — every ladder point runs one
  profiler-OFF replicate; ON vs OFF delta per point is reported as the overhead number.
- Falsification guard: if the ON-vs-OFF delta exceeds 2%, the ON numbers are VOID and the overhead
  becomes the finding.

## 4. Output format

- `fprintf(stderr, "PROF {...}")` structured key=value lines — direct stderr, NOT
  `LLAMA_LOG_DEBUG` (we learned at `llama-model-loader.cpp:1246` that DEBUG lines vanish at default
  verbosity; the server's lib-log filter does not touch our lines). Same grep-able pattern as the
  allocation lines we already rely on.
- One line per window (per N steps) + one final summary per generation. No per-token lines.
- Window line carries: step range, tok/s, gen, width, rx/tx KB/s, t_wall/cpu/dev/sync ms,
  bytes/step, draft/verify ms + tokens + acceptance (when MTP on), per-layer device map digest.
- Final summary: medians of all of the above + per-layer time table (placed vs CPU-offloaded).

## 5. Lifecycle (fork rules, non-negotiable per the bank)

- Branch `feat/decode-profiler` off `baseline-flash-next` tip (`86af0c9af` content) in the fork
  (ddvnguyen/llama.cpp); conventional commits + Co-Authored-By; PR (CI-gated). This is a NEW
  fingerprint — same-config deltas only, self-contained comparisons; build-g3 absolutes = context,
  never comparators.
- Build ONLY in a loaded window (12-min rule); full provenance + contamination check + rule-5
  archive (fp'd, count-exact) BEFORE any measurement leg. Never a measurement binary from a
  "backup:" commit. Unit-test surface: profiler-off binary reproduces pre-profiler tok/s (§1);
  profiler ON on the 5060 Ti sanity config emits parseable lines with plausible magnitudes
  (gen must read 5/x16 on the 5060 Ti — a self-check of the NVML path itself).

## 6. The measurement the owner asked for (after approval)

1. Re-probe H on the 3060 with MTP head loaded (ctx per ladder discipline; open question below);
   derive P_max — architect expects ~4–5 of the 7 (head ≈ 2.3 layers). Boot decides.
2. Ladder: 3060 `M-P0` / `M-P_max`, interleaved, n=5 (bimodality returns with MTP — medians +
   full min-max, acceptance reported per run, acceptance-adjusted comparison per §89).
3. Profiler ON for all legs + one profiler-OFF replicate per point (overhead number per point).
4. **Report order: per-step time split and link-state-under-load FIRST** — the owner's "see what
   happens" outranks tok/s.
5. Everything §91 voided stays voided; §76 stays UNEXPLAINED until this instrument says otherwise.

### Open questions for the architect (answer with approval or amendments)

- **ctx for the 3060 ladder**: propose ctx 16384 (§79/§85 single-3060 discipline; keeps P_max
  derived against the same margin rules), NOT production 81920 — the 3060 ladder numbers are
  self-contained either way. One ctx-81920 leg can ride as a production probe if wanted.
- **(c) transfer-vs-wait disambiguation**: proposed split makes the residual INFERRED; a fully
  measured transfer number requires instrumenting every `cudaMemcpy`/copy-op in ggml-cuda —
  feasible but broader diff. Default: inferred residual + NVML byte counters; opt-in deeper
  bracketing only if the first split is ambiguous.
- **(d) per-layer events live in `ggml-cuda`** (graph-boundary scan). If the graph-repeat caching
  proves fragile in the moe/continuous path, fallback = context-level device-span only (§2c) +
  per-layer from host-side boundary timestamps, clearly labeled coarser. Fallback is declared, not
  silent.

## 7. Predictions on record (falsifiable)

- **Architect** (self-declared poor record, target not guide): 3060 MTP + 4–5 placed layers lands
  ~20–21 tok/s (inside the owner's remembered 17–23).
- **Leader**: 13.6–15.0 tok/s. Reasoning on record: the 17–23 band is the VOID-labeled old-era
  binary which had `--decode-overlap` + ple-prefetch — features ABSENT from build-g3 and absent
  from this profiler branch (no-overlap FLOOR per §88). On the 3060's ~97%-CPU pipeline, MTP adds
  draft+verify CPU compute; §70's C3 mechanism gives MTP ~+5% there. M-P0 ≈ 13.2×1.03–1.05 ≈
  13.6–13.9; M-P_max(4–5) ≈ (13.2 + 4.5×0.2065)×1.03–1.05 ≈ 14.6–14.9. If the profiler instead
  shows a large transfer/wait share that MTP's multi-token verify amortizes, the number lands
  higher — that is exactly what the instrument is for. The BAND QUESTION (is the remembered number
  overlap-dependent?) is answerable from these legs either way.
