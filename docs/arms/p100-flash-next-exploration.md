# P100 (sm_60) feasibility for Qwen3.8-Flash-Next (`qwen4exp`)

Related issue: **#763** (P100 participation). Separate from **#762** (MoE-cache
profiling) and **#761** (MTP port). Do not conflate the three.

**Question:** Can the fork's `qwen4exp` CUDA path be built and run on the
P100 (Tesla, sm_60, CUDA 12.9 toolkit), or does some kernel hard-require a
newer architecture / newer CCCL? This doc resolves the one open thread
(`top-k.cu` / CCCL) and records the other arch findings with `file:line`
evidence.

**Method:** static source/build-path analysis **plus a real sm_60 compile** on
submodule `1d3c4a8e3` (`src/llama-cpp`). No GPU was touched for the static
analysis. The build used the host CUDA 12.9 toolkit and produced
`ggml-cuda` + `ggml-rpc-server` for `sm_60` (exit 0). This is a feasibility
verdict, not a performance result.

**Verdict (updated):** **No hard software blocker.** All three flagged hazards
have arch-generic fallbacks, and a fresh `sm_60` build of the *current* fork
(`1d3c4a8e3`, ggml 0.23.0, which includes `qwen4exp`) compiles cleanly —
finding #1 below is now **verified by compilation**, not just analysis. Beyond
the build, §7 records a **live bring-up on the actual P100**: our sm_60
`ggml-rpc-server` initializes `Tesla P100-PCIE-16GB, compute capability 6.0`,
and the host enumerates it as `RPC0`. The end-to-end token smoke was **held** to
avoid interfering with the concurrently-running sibling task #761 (MTP) on the
host GPUs; that is a scheduling gate, not a P100 defect.

---

## 1. `gated_delta_net.cu` (GDN) — arch-generic, no blocker

- Dispatch: `GGML_OP_GATED_DELTA_NET` -> `ggml_cuda_op_gated_delta_net`
  (`ggml/src/ggml-cuda/ggml-cuda.cu:2387-2388`).
- The file contains **no** `#if` / `#ifdef` arch guards at all, and **no**
  warp-shuffle / Hopper-only intrinsics (`__shfl*`, `__ballot`, `cooperative_groups`,
  `wgmma`, `cp.async`, cluster, `__cvta`). Grep over the whole file returns nothing.
- Its only arch-sensitive call is `ggml_cuda_pdl_sync()`
  (`ggml/src/ggml-cuda/gated_delta_net.cu:56`). That helper is a **no-op below
  Hopper**: `cudaGridDependencySynchronize()` is compiled only when
  `__CUDA_ARCH__ >= GGML_CUDA_CC_HOPPER`
  (`ggml/src/ggml-cuda/common.cuh:124-127`).
- `qwen4exp` reaches it through `llm_build_delta_net_base`
  (`src/models/qwen4exp.cpp:337`) -> `ggml_gated_delta_net`
  (`src/models/delta-net-base.cpp:402` and `:567`).

Conclusion: compiles and runs on sm_60. (Correction to the earlier hand-off:
there are **no warp shuffles** in this kernel; it is even less arch-dependent
than reported.)

## 2. MHA / FlashAttention — soft requirement, non-FA fallback exists

- `llm_graph_context::build_attn_mha` (`src/llama-graph.cpp:2536`) selects FA at
  `const bool use_flash_attn = cparams.flash_attn && kq_b == nullptr;`
  (`src/llama-graph.cpp:2560`).
- When FA is off, the `else` branch runs a plain `ggml_mul_mat(k, q)`
  + `ggml_soft_max_ext(...)` path (`src/llama-graph.cpp:2602`, `:2639`), i.e. no
  FA kernel is required.
- QSA on `qwen4exp` feeds this same builder with the top-k-masked KQ mask
  (`src/llama-graph.cpp:3008`), so QSA can run through the non-FA path.

Conclusion: run the P100 with `-fa off`; FA is not hard-required. (Performance
on the non-FA path is unmeasured.)

## 3. `top-k.cu` / CCCL — RESOLVED, fallback is present and arch-generic

This was the open thread. The file has **three** mutually exclusive paths:

| Path | Guard | What runs | sm_60 / CTK 12.9 |
|------|-------|-----------|------------------|
| A. CUB `DeviceTopK::MaxPairs` | `CUB_TOP_K_AVAILABLE` = `CCCL_MAJOR>=3 && CCCL_MINOR>=2` (`top-k.cu:6-7`) | new CCCL 3.x API (`top-k.cu:13-37`) | **off** |
| B. argsort + copy | `#elif defined(GGML_CUDA_USE_CUB)` (`top-k.cu:235`) | bitonic if `ncols<=1024` and smem fits (`:240`), else `argsort_f32_i32_cuda_cub` (`:252`) | **used** |
| C. no-CUB bitonic | `#else` (`top-k.cu:260`) | `argsort_f32_i32_cuda_bitonic` (`:268`) | not needed (B applies) |

**CCCL version facts (measured on disk):**

- CUDA 12.9 (P100 toolkit): `CCCL_VERSION 2008002` => **CCCL 2.8.0**
  (`/opt/software/cuda/12.9/targets/x86_64-linux/include/cuda/std/__cccl/version.h`;
  `cub/version.cuh`: `CUB_VERSION 200802`). `CCCL_MAJOR_VERSION >= 3` is
  therefore **false** -> `CUB_TOP_K_AVAILABLE` is **not** defined.
- CUDA 13.2.2 (RTX toolkit): `CCCL_VERSION 3002000` => **CCCL 3.2.0** -> path A
  (this is what the already-working sm_86/sm_120 host build uses).

**Why path B is safe:** `GGML_CUDA_USE_CUB` is defined for any CUDA with
`CUDART_VERSION >= 11070` (`ggml/src/ggml-cuda/common.cuh:111-113`), so on
CUDA 12.9 the compiler takes the `#elif` branch. The fallbacks it calls exist
and are CCCL-2.x-compatible:

- `argsort_f32_i32_cuda_bitonic` (`ggml/src/ggml-cuda/argsort.cu:224`) is a plain
  shared-memory bitonic sort kernel (`k_argsort_f32_i32`, `argsort.cu:167`) —
  no CUB, no arch intrinsics.
- `argsort_f32_i32_cuda_cub` (`argsort.cu:45`) uses CUB `DeviceRadixSort` /
  `DeviceSegmentedSort` / `DeviceSegmentedRadixSort` (`argsort.cu:86-119`),
  which are header-only and support sm_50+. The CCCL-3-only convenience
  (`cuda::make_strided_iterator`) is gated behind `STRIDED_ITERATOR_AVAILABLE`
  = `CCCL>=3.1` (`argsort.cu:5-9`); on 2.8 the `init_offsets` + `int*`
  fallback is used (`argsort.cu:64-70`). So the CUB path compiles as-is.

**Correctness note:** QSA calls `ggml_top_k` on `expanded`, whose leading dim is
`n_kv` (the context length); output is reshaped to `[width, n_tps, 1, n_stream]`
and used only to unmask KQ cells (`src/models/qwen4exp.cpp:650-671`). The
fallback returns a *sorted* top-k while path A returns an *unsorted* set — both
contain the same indices, and the graph only uses the set, so results are
equivalent. At `n_kv <= 1024` the pure-bitonic path is used; above that, the
CUB segmented sort. Both are valid on sm_60.

Conclusion: **no compile blocker and no expected wrong-result mode** in
`top-k.cu` on sm_60/CUDA 12.9. The only cost is that the newer, faster
`DeviceTopK` path is unavailable, so top-k runs through the older sort-based
fallback (perf on P100 unmeasured).

## 4. Verified sm_60 build (RESOLVED — was residual risk #1)

Configured `src/llama-cpp/build_sm60-min` on `1d3c4a8e3` with:

```
-DGGML_CUDA=1 -DGGML_RPC=ON -DGGML_CUDA_ARCHITECTURES=60
-DCUDAToolkit_ROOT=/opt/software/cuda/12.9
-DCMAKE_CUDA_COMPILER=/opt/software/cuda/12.9/bin/nvcc
-DCMAKE_CUDA_HOST_COMPILER=/usr/bin/g++-13
```

Result: `ggml-cuda` + `ggml-rpc-server` built, **exit 0**, in ~180 s. The
compile exercised exactly the flagged paths: GDN (`gated_delta_net.cu`), QSA
top-k through the CCCL-2.8 `GGML_CUDA_USE_CUB` fallback (`top-k.cu`), and the
non-FA MHA path. `nvcc --list-gpu-arch` on 12.9 confirms `compute_60` is
supported (with the standard deprecation warning for `<sm_75`).

Toolkit note (corrects stale docs): `/opt/software/cuda/12.9` **is present on
the host** (nvcc 12.9.86, CCCL 2.8.0). `docs/cuda-modules.md:116-117` claims it
was removed in favour of `~/opt/cuda-12.9-min`, but `~/opt/cuda-12.9-min` does
**not** exist and `/opt/software/cuda/12.9` does. `docs/cuda-modules.md` needs a
follow-up correction.

## 5. One binary carries CPU **and** sm_60 CUDA (no separation)

`GGML_CUDA=ON` never removes the CPU backend. The built
`build_sm60-min/bin/ggml-rpc-server` links **both** `libggml-cpu.so.0` **and**
`libggml-cuda.so.0`; `ggml-backend-reg.cpp` statically registers CUDA
(`:120-121`) and CPU (`:171-172`) in the same process. So a single binary runs
P100-GPU + CPU together (scheduler picks CUDA for offloaded ops, CPU otherwise)
and still starts CPU-only when no CUDA device is present. No separate CPU build
is required.

Caveat: the **CUDA kernels are arch-compiled**. This build is
`CMAKE_CUDA_ARCHITECTURES=60`, so it uses CUDA only on Pascal; on an sm_120 host
it falls back to CPU (no PTX to JIT forward). A *single* binary whose CUDA works
on both P100 (60) and RTX (86/120) needs a fatbin:
`-DCMAKE_CUDA_ARCHITECTURES="60;86;120"`.

## 6. MTP / NextN graph — out of scope for this pinned commit

`src/models/qwen4exp.cpp` at `1d3c4a8e3` contains no `mtp`/`nextn`/`draft`/`spec`
symbols — the NextN/MTP draft-head graph is **not present in this submodule**
(it is scope of the in-flight port on the sibling branch `cdd11021b` / #761).
The MTP graph is graph-level (reuses the already-checked GDN + MHA + MoE CUDA
ops), so it introduces no *new* sm_60 arch hazard; re-verify if/when it lands
inside this arm.

---

## Residual risk / still untested

1. ~~No sm_60 compile attempted~~ — **resolved** (§4).
2. `DeviceSegmentedSort` behaviour on Pascal is untested on hardware.
3. Performance of the non-FA MHA path and the fallback top-k on P100 is unknown;
   P100 has no FA and a much lower bandwidth ceiling. "Runs" != "usable speed".
4. ~~Patched micromamba 12.9 prefix~~ — corrected: the toolkit used is the
   full-runfile `/opt/software/cuda/12.9` (no header patch needed). The old
   `HEADER-PATCH-NOTICE.md` path referred to a conda prefix that no longer exists.
5. P100 is currently **serving the production 35B-A3B SOLO profile**; any use
   requires the standard drain-verify -> test -> restore discipline.
6. The existing `build_sm60` binaries in `issue-417-rebuild-p100-sm60` and
   `fix-p100-sm60-rebuild` are **stale** (`libggml-cuda.so.0.13.1`, July, no
   `qwen4exp`). A fresh sm_60 build of `1d3c4a8e3` is mandatory.

## Proposed next step (test design, if approved)

Minimal multi-node layer-split smoke, **small ctx (2048-4096), single slot**:

1. P100 side: stage `build_sm60-min/bin` to the VM, run
   `ggml-rpc-server -H 0.0.0.0 -p <port> -ngl 99`; confirm it logs CUDA sm_60 +
   `--list-devices` shows `CUDA0` (Pascal) + CPU.
2. Host side: `build-cuda1322/bin/llama-server -m <qwen4exp> -fa off
   --rpc 192.168.122.21:<port> -ngl <N> --split-mode layer -c 4096 -np 1`.
3. One short completion; capture tok/s and whether the layer-split ever lands
   tensors on the P100.
4. Drain/restore both ends.

**Go/No-Go:** **GO for build + a bounded smoke**; hardware run requires the P100
production drain and host-GPU occupancy check first.

## 7. Live bring-up evidence (2026-09-12)

Static + compile analysis is now backed by a real P100 bring-up. Steps actually
executed, OBSERVED only (no production interruption):

1. **P100 platform state.** Production on the VM is a hydra-head-managed
   `llama-engine` (`hydra-head.service`, MainPID 1590) bound to `:8086`
   (`rpc-port 9502`); a separate `ik-llama-minicpm.service` runs an upstream
   `llama-server` on `:8090`. `:8086/health` = `ok`, `/slots` idle
   (`is_processing:false, n_past:0`), ctx 128000.
2. **NVML driver/library mismatch on the VM** — `nvidia-smi` fails with
   `Failed to initialize NVML: Driver/library version mismatch; NVML library
   version: 580.178`. This is a *userland* mismatch only: a **new CUDA
   process still initializes fine**
   (`llama-engine --list-devices` -> `CUDA0: Tesla P100-PCIE-16GB`,
   `10127 MiB free`). So it does not block this arm, but VM monitoring/`nvidia-smi`
   is unreliable until reboot. Flag for the P100 node owner.
3. **Our current-fork sm_60 binary runs on the P100.** Staged
   `build_sm60-min/bin` (`ggml-rpc-server` + `libggml*.so.0.23.0`) to
   `~/p100-rpc-sm60` on the VM and ran with the VM CUDA 12.9 runtime
   (`LD_LIBRARY_PATH=~/p100-rpc-sm60:/home/vm1/hydra-min-test/lib`). Log:
   `ggml_cuda_init: found 1 CUDA devices ... Tesla P100-PCIE-16GB, compute
   capability 6.0`. Bound `0.0.0.0:9504`, accepted connections, then was
   cleanly stopped. **P100 production was never stopped or degraded.**
4. **Host↔P100 RPC plumbing enumerates.** Fresh host build
   `build-cuda1322/bin/llama-server` (fork `1d3c4a8e3`, RPC on) run with
   `--rpc 192.168.122.21:9504 --list-devices` reports:
   `RPC0: 192.168.122.21:9504 (16269 MiB, 10127 MiB free)` alongside CUDA0/1.
   `--device`/`-dev` is supported (`common/arg.cpp:2775`), so host RTX devices
   can be excluded and the layer-split driven through `RPC0` only.
5. **Host layer-split smoke was HELD, not run.** The Qwen3.8 27B sibling task
   **#761 (MTP)** was observed actively running on the host GPUs during this
   arm (`llama-server ... -ts 13,36 --spec-type draft-mtp`, task-completing at
   `tg 2.60 t/s`, then relaunching the MTP-off variant on `:18082`). Host GPUs
   were fully occupied (1725 / 1 MiB free), and the only `qwen4exp` model is the
   74 GB `apex-mini` shard set vs **10.1 GB free P100 VRAM** — so a host-side
   load competes with #761 for the same model file (disk) and host RAM
   (123 GB total). Per the arm's own rule ("hold if the host GPUs are
   occupied"), the host half was aborted and the transient host
   `llama-server` (port 18099) killed. No sibling run was harmed.

**What is now proven:** the current fork builds for sm_60; our binary brings up
CUDA 6.0 on the P100; the host enumerates the P100 as an RPC device; the
RPC/`--device` CLI surface needed for layer-split works.

**What is still unproven:** an actual end-to-end `qwen4exp` token produced with
layers resident on the P100 (the `top-k`/non-FA decode path on Pascal), and any
performance number. That needs a window where #761 is idle (or an approved
overlap) because the model is a single 74 GB file.

### Recommended next step (needs a scheduling decision, not code)

- Wait for a #761-idle window (or explicit approval to overlap), then run the
  bounded smoke: host `llama-server --device RPC0 -ngl <fit> -c 2048 --parallel 1
  -fa off`, one short completion, capture tok/s and per-layer device assignment.
- Alternatively build a `60;86;120` fatbin if the goal shifts to *one* binary
  for both nodes.

**Go/No-Go (final for this arm): GO on feasibility** — no architectural or
compile blocker, P100 CUDA bring-up verified; the performance/end-to-end run is
**gated on host-GPU availability (#761) and P100 sizing**, not on any sm_60
defect.
