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
