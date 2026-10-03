# Strata vs fork on the RTX 3060 — concept-level gap (epic #811)

Inputs: `docs/evidence/strata-3060-ab/` (t0006, 9 scored arms), Strata `DETAILS-v0.1.29.md` "How it works", Strata decode timing lines in `logs/S0-*-engine.log`. Everything marked **[derived]** is arithmetic on those measurements; **[hypothesis]** is not yet tested. The implementation-level view is `IMPL-GAP.md` (worker 05927b8d).

## What was measured (3060, GSQ-RCO IQ3_S, ctx 16384, ub 2048 both)

| | Strata S0 | Fork F0 (cache 0, `--n-cpu-moe 99`) |
|---|---|---|
| decode | 32.4 tok/s = 32.1 ms/token | 15.3 tok/s = 65.4 ms/token |
| cold prefill per 2048-token chunk **[derived]** | ~2.5 s after a ~6.7 s first chunk | ~10.2 s, flat (1K, 4K, 12K) |
| GPU expert cache | auto, 2,641 slots (5.06 GiB, 10.7% of 24,576 experts), hit rate 59-61% | none |

## Concept differences

1. **Where decode expert work runs.** Fork F0 puts every expert on the CPU (`--n-cpu-moe 99`): GPU does attention/dense, CPU does 100% of expert GEMMs, serially per layer. Strata keeps all experts pinned in RAM but also holds the hottest ~11% in VRAM; ~60% of expert lookups hit the GPU cache, so the CPU only computes the ~40% misses, **concurrently** with the GPU. Strata's own split of a 29.8 ms verify: GPU-reach wait 16.3 + host 11.55 (of which CPU expert jobs 11.2). **[derived]** If the CPU did all of it with Strata's kernels: 11.2 / 0.4 = ~28 ms CPU + dense GPU time, i.e. roughly 38-45 ms/token, not 65. So the hit-rate offload plausibly explains about half of the 2.0 x gap and implementation (kernels, threads, sync, copies) the rest. **[hypothesis, test E1/E2]**
2. **Miss handling.** Our fork's expert cache is a demand-transfer LRU: a miss copies the expert over PCIe, and the fork's own start-up warning says all expert tensors route through the cache once it is on. At N=22 it moved 6.5 GB per run and decode fell from 15.3 to 9.3 tok/s (F1). Strata never moves an expert on the decode critical path: a miss is computed in place on the CPU; the cache is refreshed adaptively off the critical path. This is the "CPU-serves-misses bypass" we designed and shelved, now shown to work as a product.
3. **Cache sizing/residency.** Strata's cache shares VRAM with nothing the fork needs at the same time (KV is int8, mixers/dense are small); it fills the rest of VRAM. The fork's cache sits on top of fully resident layers: fill-matched N=64 (5.2 GiB) OOMs at load on 12 GB. So the fork cannot even express the same operating point on this card.
4. **Prefill.** Fork prefill is flat at ~200 tok/s (10.2 s per 2048-token chunk at every prompt length) — CPU-expert-bound, because `--n-cpu-moe` also applies to prefill. Strata streams experts to the GPU in 2048-token chunks and amortises them over the batch; per-chunk cost drops to ~2.5 s after a ~6.7 s first chunk (**[derived]** from TTFT 6.7 s at 1K, 9.1 s at 4K, 20.4 s at 12K). Marginal prefill ~730 tok/s vs 200 (3.6x); the fixed first-chunk cost is why the 1K ratio is only 1.22x. Short cold prompts are Strata's weak spot.
5. **Speculation.** Orthogonal to the primary result: S0 clamps it; with MTP (S1) Strata reaches ~39 tok/s. Fork F2 (MTP on CPU draft, `--spec-draft-ngl 0`) is slower (7.1) because the draft shares the CPU-bound path.
6. **Prefix reuse.** Strata TTFT 5.8 s vs fork 10-11 s for a ~730-token append onto a cached ~9K prefix. Both reuse ~9.1K tokens; the remaining time is the append chunk's cost (item 4), so the prefix result is the prefill gap again, not a separate defect. **[derived, untested]**

## What this means for our own history

- Our shelved split-execution result on the 3060 (+9-14%) and every fork 3060 number before 2026-10-01 were measured on the Gen1-stuck PCIe link (3060 H2D is 6.1 GB/s now; the Gen1-era figure is not in these artefacts and should be re-read from the old logs before quoting). Strata's design is built around PCIe being usable; **re-measure the shelved bypass on the healthy link before concluding it was thin**.
- Atlas hit-rate work (h@N vs the N/n_expert baseline) maps directly onto Strata's adaptive cache; Strata's 59-61% at 10.7% residency is ~5.6x the uniform baseline, in the range our atlas measured. The atlas->STRP profile bridge (Tier-2 spike) is the integration point if we adopt Strata's engine rather than port it.

## Experiments proposed (attribute the gap, no assertion)

- **E1 fork timing breakdown:** per-layer GPU-dense / CPU-expert / sync time for F0 at 4K decode (fork telemetry or nsys at `~/nsys-local`), to compare line-for-line with Strata's decode timing.
- **E2 Strata with the GPU expert cache shrunk to ~0** (via its pcie/fit options): if it lands near 15 tok/s the gap is hit-rate offload; if it stays well above, the gap is kernels/threading/sync. Cleanest single test of item 1.
- **E3 re-measure the shelved bypass build on the healthy link** (3060, same cells as t0006).
- **E4 Strata first-chunk cost:** what the ~4 s excess on the first prefill chunk is (cache warm-up, pinning, graph build) and whether it recurs per request.

Open owner decisions: adopt Strata's engine as the 3060 serving path vs port its bypass into the fork (needs IMPL-GAP ranking first); whether to run E2 on the rig now (one GPU = one task; 8091 would need to stay down during it).
