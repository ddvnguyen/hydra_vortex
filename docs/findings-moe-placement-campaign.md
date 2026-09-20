# MoE Expert-Placement Campaign — Findings (§55–§90)

**Status:** COMPLETE. Hold for owner direction on remaining leads.
**Date:** 2026-09-20. **Track:** t-d1cf43b723. **Binary:** build-g3 @ 86af0c9af,
fused fingerprint `895c522343eeaa53` (llama-server 895c522343eeaa53 / libllama.so
b6abfd2ef76d0c27 / libllama-common.so d5662c509eb5a67f / libggml-cuda.so 09fcd3c233cd59c5).
Raw evidence: `/mnt/storage/hydra-evidence/e1-campaign/place*/`; binaries archived:
`/mnt/WorkDisk/binary-archive/build-g3/`.

## 1. The Production Config (verbatim, reproducible)

```bash
export LD_LIBRARY_PATH=/mnt/WorkDisk/workspace/worktree/1q3ry0vb/baseline-flash-next/src/llama-cpp/build-g3/bin:$LD_LIBRARY_PATH
CUDA_VISIBLE_DEVICES=0 llama-server \
  -m /mnt/SSD/qwen3.8-flash-next-apex-mini/Qwen3.8-Flash-Next-APEX-I-Mini-00001-of-00006.gguf \
  --split-mode layer -ngl 99 \
  -ot 'blk\.(39|4[0-7])\.ffn_.*_exps.*=CUDA0,ffn_.*_exps.*=CPU' \
  -c 81920 --parallel 1 --flash-attn on --jinja -t 6 \
  --host 127.0.0.1 --port 18360
```

**Measured result: 22.2541 tok/s decode (n=3, ±0.6%), prefill 141.6 tok/s** at full
production context (ctx 81920), 9/48 expert layers on the 5060 Ti, MTP OFF.
+15.2% over the same-config zero-placement baseline (19.3863). All runs
fingerprint-verified; dose asserts (0 vs 9 GPU expert layers) pass per leg.

## 2. The Measured Curves (two cards, two curves — never one)

**RTX 3060 (CUDA1, sm_86; PCIe link state under load UNKNOWN — see §91/§92 correction), ctx 16384, MTP off, n=3–6:**
0L → 13.1536, 4L → 14.0290, 7L → 14.5988 tok/s. **Slope 0.2065 tok/s/layer.**
7/48 layers is the physical cap (8th OOMs; KV/ctx levers free <1 layer).

**RTX 5060 Ti (CUDA0, sm_120, PCIe gen5 x16), MTP off, n=3:**
- ctx 16384: 0L → 19.0734, 6L → 20.9682, 11L → 23.0901. s1 = 0.3158, s2 = 0.4244
  (s2>s1 at ~1.7σ — convexity lead, NOT established).
- ctx 81920: 0L → 19.3863, 9L → 22.2541. Slope 0.3187/layer — larger compute
  reserve does not break the slope.

Cross-card comparison is same-shape only (different cards); offload mechanism
family is identical (both materialize CUDA_Host pinned buffers), which licenses
slope-shape comparison.

## 3. Closed Questions (each: verdict + evidence)

- **[§91 CORRECTION — MECHANISM VOIDED] Prefill collapse is UNEXPLAINED again.**
  Same binary, one card each: CUDA0-solo prefill 122 tok/s vs 3060-solo 4.7 (26×).
  The earlier closure attributed this to the 3060's gen1-x4 link ceiling (985 MB/s
  vs 2.3–4 GB/s measured demand, decode-scoped dmon). The owner corrected the
  premise: **the 3060 is PCIe gen4 x4 (~7.88 GB/s); gen1 readings were idle
  downtrain states** — every gen sample sat in a low-traffic window, so the link
  state under sustained decode was never observed. The measured 2.3–4 GB/s is
  only ~30–50% of a gen4-x4 link. §76 REOPENS as UNEXPLAINED (config/host
  question; the commit bisect stays closed — it hunted code, the delta is not
  code). The in-source decode profiler (§92, `docs/design-decode-profiler.md`)
  measures the link state under load and the per-step time split; that report
  replaces this section.
- **Decode bimodality = draft-acceptance nondeterminism.** ±16% spread with MTP
  on vs ±1.5% with MTP off, reproduced on three binaries (including the "pristine"
  86af0c9af). Decode tok/s tracks acceptance run-by-run (16.92@acc0.48 → 24.78@0.92).
  A measurement condition to design around; MTP-off is how.
- **Dual-GPU is net-negative for this workload.** −9% at zero dose (12.01 vs 13.15)
  from crossing/scheduler overhead; per-layer benefit 71% of single-device (bridge
  slope 0.1459 vs 0.2065); dose-23 projection (+28%, corrected) does not beat the
  single-device result in hand. Closes "just add the second GPU" with measurements.
- **Context is cheap on this model (hybrid attention+recurrent).** KV is only
  48 MiB at ctx 16384 (RS buffers ~113 MiB). The ctx 81920 delta is 1,638 MiB =
  1.75 expert layers — compute-reserve scaling, CARD-INDEPENDENT. Validated
  end-to-end: constant predicted ~22.2, production leg measured 22.2541.

## 4. Open Leads (honestly scoped — owner's choice, none started)

- **Draft-acceptance pinning** (recommended by architect's owner question): at
  acc ≈ 0.92 MTP legs hit 24.78–24.89, BEATING the 23.09 production config; at
  the ~0.5 cluster they lose. Pinning acceptance near 0.9 is worth ~+12%. Cost:
  a real research question (draft selection/training), no build needed.
- **Convexity point**: s2>s1 at 1.7σ; one extra ladder point (P3 or P9) settles
  whether placement benefit accelerates with dose — i.e. whether more VRAM pays
  superlinearly. 1–2 legs.
- **decode-overlap / ple-prefetch port**: build-g3 lacks both; every MTP number
  here is the NO-OVERLAP variant — a floor, not a ceiling. Porting could change
  the MTP verdict. Needs a build (which has twice reintroduced noise into this
  campaign).

**MTP statistical status:** no evidence that placement stops working under MTP —
acceptance-adjusted estimate (~0.289 tok/s/layer, +2.309 ± 1.431 at acc 0.61,
t ≈ 1.61) is in family with the firmly-established MTP-off 0.3651, but the MTP-arm
estimate is underpowered at n=5. Consistent-with, NOT demonstrated. MTP's median
gain (+2.9%) does not pay for the 3 placement layers its head costs (2,180 MiB).

## 5. Terminal Limits

Total expert weight ≈ 45 GB. No card on this rig holds it. Full placement — and
therefore the knee where CPU saturation lifts — is UNREACHABLE on this hardware.
"Knee not reached on any available hardware" is a complete, legitimate result:
placement benefit is still rising at 23% of the dose range on the best card.

## 6. Method Rules Minted (outlive the campaign)

1. **Cheap leg gates the expensive arm** — a probe that can void an arm runs first.
2. **Interleave, never block** — blocked designs confound treatment with time.
3. **Same-config deltas only** — a baseline from a different config composition
   (1,808.36 vs 3,425.54) produces bogus per-layer figures.
4. **Decode-scoped dmon** — sampling the load window gives false "benign" verdicts;
   start instruments after health-ok, cover the measured window.
5. **Archive binaries before any reconfigure** — a build dir is an address, not a
   binary; identity = embedded commit + link mtime + fused fingerprint.
6. **A number from a comment is not a measurement** — and a vacuous assert (empty
   override map, grep exit codes) is worse than no assert; verify instruments read
   what they claim to read.

## 7. §92–§94 Profiler Arm (feat/decode-profiler, PR #156)

**Instrument:** `--profile-decode` (default OFF), in-source, single hot-path branch.
NVML via dlopen; CUDA event bracket — caller-managed span per decode step
(span-begin before sched compute, span-end after; per-call records suppressed
while open) after two instrument bugs were caught by calibration: (a) event
readout must cudaEventSynchronize (decode returns before GPU drain; the query
guard always failed → −1 sentinel), (b) per-graph_compute records only measured
the last split segment (t_dev≈0 even for GPU-bound prefill). Prefill step gets
its own `PROF prefill` line (threshold n>256 so the 42-token warmup cannot steal
the tag). Gate PASS: profiler-off vs build-g3 median +0.31% (±1.5% band); CUDA0
calibration leg reads t_dev≈t_wall clean.

**§93 CPU-fallback hypothesis: REFUTED.** 512-token prefill: 3060 t_wall 72,681.6 ms
/ **t_dev 72,674.8 ms (~100% device)**; CUDA0 2,860.7 / 2,854.1 (~100%). The 26×
prefill gap is genuine device-side slowness on sm_86, shape-dependent (prefill
25×, decode 1.45×). Fatbin verified: 143× sm_86 + 143× sm_120 SASS, no PTX.
Standard gates exonerated (AMPERE_MMA_AVAILABLE + CP_ASYNC_AVAILABLE both defined
≥ Ampere; BLACKWELL path is NVFP4-only). Pinning the exact slow kernel needs
per-kernel profiling → follow-on (§94 open item).

**§91 correction holds under load:** 3060 PCIe during decode legs rx ≈ 197–207
MB/s, tx ≈ 23–25 (demand-driven, ~2.5% of gen4-x4 capacity — link NOT the
bottleneck); CUDA0 rx ≈ 1,691 MB/s. No gen1 story anywhere in the data.

**3060 results (CVD=1, fp ff0bda54d59eb659 / libllama d3448cb406018ff9):**

| Leg | ctx | dose | MTP | prof | tok/s (acc) |
|-----|-----|------|-----|------|-------------|
| C-P0 ×3 | 16384 | 0 | off | on | 12.8828 / 13.0949 / 12.9823 → **median 12.9823** |
| C-P0off | 16384 | 0 | off | off | 12.9380 (overhead nil) |
| M-P0 ×5 | 16384 | 0 | on | on | 11.25(.510) 14.40(.797) 10.30(.423) 13.92(.744) 13.48(.738) → **median 13.4832** |
| MP0off | 16384 | 0 | on | off | 15.2541 (.863) |
| M-P4 ×5 (P_max) | 16384 | 44 | on | on | 16.78(.921) 14.23(.718) 11.52(.475) 15.20(.786) 11.38(.452) → **median 14.2290** |
| MP4off | 16384 | 44 | on | off | 11.5183 (.452) |
| REPRO M-P0 ×5 | 81920 | 0 | on | on | 15.02(.863) 13.63(.728) 11.19(.500) 12.24(.593) 12.34(.616) → **median 12.3441** |
| REPROoff | 81920 | 0 | on | off | 10.1459 (.390) |
| C0CAL (CUDA0) | 16384 | 0 | off | on | 18.8729 / 18.9195, prefill ~121 |

H probe: MP0 boot leaves 6,229 MiB → MTP head 2,350 + ~916 MiB/layer →
**P_max = 4** (ncmoe 44; MP4 dose assert: 264 CUDA_Host overrides = 44×6).

**Reads (pre-registered attribution):**
1. **MTP at P0 helps when acceptance is high** (13.92–15.25 vs C-P0 12.98,
   +7…+17.5%) and **hurts when low** (10.30–11.52, −13…−21%). Acceptance is
   graded today (0.39–0.92), not strictly bimodal. M-P0off 15.25@.863.
2. **Placement under MTP (P_max=4 vs P0), median: 14.2290 vs 13.4832 = +5.5%.**
   At matched high acceptance: 16.78(.921) vs 15.25(.863) ≈ +10%. Positive,
   consistent with MTP-off 0.2065 GB/layer slope direction; underpowered at n=5.
3. **REPRO M-P0@81920: median 12.34, best 15.02 — owner's 17–23 NOT reproduced.**
   Per pre-registration (≈14 ⇒ missing ~6–8 tok/s IS decode-overlap +
   ple-prefetch) → **porting decode-overlap + ple-prefetch to the fork is the
   critical path** for the 3060 production number.
4. **§76 closes as: sm_86 shape-dependent device-side kernel slowness**
   (config/OS/link/CPU-fallback all exonerated; mechanism pinned one level deep,
   exact kernel pending per-kernel profiling).

**Process lessons minted:** never patch a running leg script (bash incremental
read); dose verify counts CUDA_Host expert layers = 48 − dose (CVD masking
removes CUDA1); cancel mid-leg leaves leg-script children (kill orphans before
relaunch); wait for load transients to decay before relaunching gated legs.

## 8. §100 AMENDMENT — the 26× is a PER-PREFILL FIXED COST; marginal is 4.9×

§100(d) two-prefill leg (same server process, same 767-token prompt, `cache_prompt:false`,
fp ff0bda54d59eb659): prefill1 163,290.2 ms / prefill2 **163,112.0 ms** — the fixed cost
RECURS on every prefill. STRIKE the "26× device-side slowness" framing: it is a fixed
~58 s per-prefill cost plus a marginal rate of ~27.7 ms/token (259→512-token curve) vs
~5.6 ms/token on CUDA0 = **4.9× marginal — a plausible card-to-card ratio**. Decode
(13.15/12.98/14.60) is unaffected.

Consequences: (1) the 3060 as configured is unusable for multi-turn serving (every new
prefill pays ~58 s); (2) Q5 per-kernel profiling is JUSTIFIED with a sharp target: a
batch-shape-selected, n-insensitive, PER-PREFILL fixed-cost operation (its cost recurs at
identical shapes, so it is not one-time graph capture/autotune); (3) v1 profiler limitation
noted: the prefill tag fires once per context, so the second prefill emitted no PROF
prefill line — response prompt_ms carried the proof.

Two-prefill leg also exposed prompt-cache confounds: with cache ON, slices of the
repetitive harness prompt reused 715–757 of 767 tokens (prompt_n 52 and 10); use
`cache_prompt:false` for any prefill re-measurement.

§97/§98/§99/§99a/§100 rulings (Q-rank revisions, flag-mapping table, bundle decision,
port set + ladder) are banked in docs/leader-handoff-state.md §§97–100.

## 9. §101 FINDING — the overlap mechanism was never two flags on our stack

Owner's standing question ("why did our implement not deliver") is largely answered by source
archaeology: `--decode-overlap`'s queue-draft mechanism (77b80e2e7) depends on retained-draft-state
machinery — `retained[]` per-seq state, `retain_draft_state` / `finish_accept(vector)` interface in
common/speculative.cpp — that OUR lineage deleted before diverging (merge-base 82d6bb284). The gap
to the mechanism-bearing lineage (feat/763-reconcile-qwen4exp-mtp) is **318 commits / 484 files /
+27,579 −52,439 lines**. The first cherry-pick attempt failed semantically, not textually: the
incoming `retain_draft_state` override targets a virtual our base removed and references three impl
members (`retained`, `queued_draft`, `n_overlap_discarded`) that do not exist in our base
(common/speculative.cpp:1330+ draft-MTP impl). Consequence: the old 17–23 binary carried the whole
moe-cache era, so the flag pair alone was never the deliverable.

Resolution per §101: option B — the PROFILER (our 4 commits, 11 files, +403 lines) is ported onto
feat/763-reconcile (branch feat/profiler-on-763, tip 081f5da5d) and the flag ladder runs THERE as a
MEASUREMENT VEHICLE (never a production figure). Conflicts resolved: ggml-cuda.h (both sides kept),
ggml-cuda.cu (duplicate destructor dropped; prof event cleanup spliced into 763 destructor),
llama-context.cpp (both kept; span wraps the 763 certificate-gated dispatch block — 4 compute call
sites — and prof_finish() prepended to the 763 destructor), server-context.cpp (763 overlap loop +
prof_verify bracket both kept). End-verification per §100(b): all 3 flags in common/arg.cpp,
queue/discard_draft_overlap present, staged_input machinery present (ba47f35ef dependency
satisfied), ple_prefetch live, Linux gates present.
