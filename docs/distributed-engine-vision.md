# Distributed Engine Vision — scaling Hydra beyond one GPU and one box

> **Status:** design / roadmap. Stitches issues **#270** (placement), **#271** (KV cache),
> **#272** (scheduler) into one scale-out narrative for the `llama-engine` epic **#161**.
> Baseline numbers are from the finished gate spike **#149**.

## Why this doc exists

The #149 spike answered the question *"how do we run Qwopus-35B fastest on the box we have
today?"* — and the answer was blunt: **a small quant on a single RTX wins, the P100 is not
needed.** That conclusion is correct, but it is **bounded to two assumptions**:

1. exactly **2 devices** (RTX host + one P100 VM), and
2. a model that **fits in one 16 GB GPU** (Nano IQ2 at 10.9 GB).

The project goal is broader: **support multiple devices on the same network and models larger
than a single GPU** (beyond 35B, higher quants, longer context). The moment either assumption
breaks, the #149 winner stops existing — there is no single-GPU option for a model that doesn't
fit — and Hydra must become a genuinely distributed engine. This doc defines *when* each
technique turns on and *how* the three layers interlock.

## The #149 baseline (measured, 2 devices, 35B)

| Strategy | pp4096 | tg64 | 80K ctx | Notes |
|---|---|---|---|---|
| **Nano IQ2 single-GPU `ncmoe=0`** | **1350** | **132** | ✅ | winner *because it fits 16 GB* |
| Mini Q3_K prefill worker (`ncmoe=2`, output/embd→CPU) | 1187 | 115 | — | best prefill-quality worker |
| 2-GPU expert split (`--override-tensor`, 21-20) | 544 | 59 | ❌ | +79% tg vs prod; **2nd place** |
| Balanced Q5_K `ncmoe=20` | 462 | 47 | ❌ | premium decode quality |
| Production Q5_K `ncmoe=32` | 364 | 33 | ⚠️ | legacy |

Enabling facts that **carry forward** to the distributed design:
- **The LAN is not the bottleneck.** KVM bridge ≈ **88.3 Gbps**, ping 0.06–0.17 ms; ggml-RPC
  tensor transfer tax is **5.8% prefill / 1.7% decode** (~40 MB/token in 3.6 ms). Cross-device
  distribution is *viable* here — the limiter is **P100 sm_60 compute**, not the network.
- **Cross-quant P/D split works.** Nano prefill → Balanced decode, KV is `q8_0` quant-agnostic,
  **7.9 s for 4K+200**, quality-checked (HellaSwag 78 vs 85; WikiText +14% PPL). Phase
  specialization is real and already a tool in the box.
- **Expert co-residency is the heterogeneous win.** The override-tensor split put 21 expert
  blocks on P100, 20 on RTX, attention/SSM on RTX — proof that **expert placement across
  devices** is the right primitive (this is what generalizes; see #270).

## The model-size gate (the single most important decision)

```
                 ┌─────────────────────────────────────────────┐
   request /     │  Does target model M fit one device's VRAM?  │
   target model  └───────────────┬───────────────┬─────────────┘
                                  │ YES           │ NO
                                  ▼               ▼
                     #149 winner: single-GPU   DISTRIBUTE (this roadmap):
                     small-quant, zero RPC,     placement(#270) + KV(#271)
                     phase-split P/D as option  + scheduler(#272)
```

**Corollary (the #149 lesson, kept):** before distributing, always check whether a *smaller quant
on one GPU* meets the quality bar — it beat 2-GPU distribution decisively for 35B. Distribution
earns its complexity only when the model genuinely exceeds one device *and* the quality floor
forbids shrinking it further.

## The three interlocking layers

A distributed serving stack is always the same three concerns. Hydra already owns primitive
versions of all three; scaling out means generalizing each.

| Layer | Owns | Today (Hydra) | Scale-out target | Issue | SOTA template |
|---|---|---|---|---|---|
| **Placement** | where weights/experts live | whole-model per node; `worker_type` P/D | layer/expert split across N devices, balanced | **#270** | prima.cpp Halda, Helix max-flow, SGLang EP+EPLB |
| **KV cache** | where reusable KV lives + how it's reused | flat `prefix/{hash}.kv` + M2 chunk dedup + Store/tmpfs | radix tree, multi-tier (device/host/remote), tier-aware | **#271** | SGLang RadixAttention, Mooncake, LMCache |
| **Scheduler** | which device serves a request | `WorkerSchedulerService` queue + warm leases | KV-aware + load-aware global router; no per-token stalls | **#272** | Mooncake Conductor, SGLang zero-overhead, P/D disagg |

They interlock: the **scheduler (#272)** routes by "who holds the prefix," which it reads from the
**KV index (#271)**, across devices laid out by the **placement planner (#270)**. Designing any one
in isolation under-serves the other two.

### Placement (#270) — parallelism strategy by network class
- **Tensor parallelism (intra-layer):** needs NVLink/InfiniBand sync bandwidth → **ruled out**
  across our virtio/LAN fabric; only valid *within* a single host.
- **Pipeline parallelism (inter-layer):** infrequent, small activation comms → the right fit for
  limited-bandwidth LANs and the "limited by GPU memory" case. Family: prima.cpp piped-ring +
  prefetch, Petals (100B+ over internet), Exo (405B across laptops + a phone).
- **Expert parallelism (EP) + EPLB:** distribute MoE experts across devices, load-balance by
  activation stats. **The direct generalization of #149's override-tensor split** and the
  favorite for this MoE model. SGLang/DeepSeek/Kimi run exactly this with PD disaggregation.
- **Placement as optimization:** Helix frames layer/partition placement on heterogeneous
  compute+bandwidth as **max-flow**, handling devices too small for the whole model; prima.cpp's
  **Halda** is the heuristic version (assign by compute × memory × link). Hydra builds the
  heuristic first, leaves max-flow as the scale-out target.
- **Substrate:** llama.cpp's RPC backend already distributes weights + KV across N local/remote
  `rpc-server` devices proportional to memory — #149 proved it works on this hardware.

### KV cache (#271) — from flat checkpoint to KV-centric layer
- Build a **radix tree** over Hydra's existing M2 content-addressed chunks → automatic
  longest-prefix reuse across all turns/sessions (vs today's exact system-prefix-only hit).
- Make the index **multi-tier and device-aware**: device VRAM → host RAM → Store/tmpfs (remote).
  **Hydra's Store RPC + tmpfs already is the remote tier** (Mooncake Store's role).
- Key on tokens independent of prefill quant (the cross-quant P/D split is proven). Feeds
  Phase-5 KV DAG **#107**.

### Scheduler (#272) — from "don't stall" to global router
- **Single-node now:** the engine runs the decode loop locally and streams
  (`EngineDecodeStreamAsync`); the coordinator prepares request N+1 while N decodes — never block a
  token on a control-RPC round-trip (SGLang zero-overhead discipline). *(Distinct from #149's
  5.8% ggml-backend tensor RPC — that's a different plane.)*
- **Cluster later:** route each request to the device holding the most reusable KV (reads #271),
  balancing reuse against load (Mooncake Conductor); place P/D roles per request; balance experts
  (EPLB, pairs with #270).

## Device-count milestones

| Stage | Topology | What turns on | Gate to advance |
|---|---|---|---|
| **D1 — today** | 1 GPU (RTX) | #149 winner: Nano single-GPU; Mini-prefill / Balanced-decode P/D as quality option. Scheduler "no-stall" discipline. | A target model exceeds one GPU, **or** a 2nd usable device appears. |
| **D2 — current box** | RTX + P100 VM | Phase-split P/D (proven, 7.9 s). Expert co-residency (#270 EP) **only** for a model that overflows one GPU. Radix cache (#271) single-node + Store tier. | A model needs >16 GB resident **and** small-quant fails the quality bar. |
| **D3 — first real cluster** | 3+ heterogeneous boxes, same LAN | Placement planner (#270, Halda-heuristic) earns its keep; KV-aware routing (#272) becomes the main lever; multi-tier KV pooling (#271). | Pipeline bubbles / load imbalance dominate → need global optimization. |
| **DN — scale-out** | N devices, varied GPUs/links | Helix-style max-flow placement; EPLB across devices; Mooncake-style KV-centric global scheduler. | — |

**Rule of thumb:** complexity is unlocked by *necessity*, never by default. Each stage must show
the previous stage's simpler option (smaller quant / single device / static split) has actually
run out, exactly as #149 showed for D2.

## What's settled vs open

**Settled by #149 (don't re-litigate):** for 2 devices + a model that fits 16 GB, single-GPU
small-quant wins; the P100 is a net loss as a *compute* peer; prefetch and `--no-mmap` give
nothing; the LAN is not the bottleneck; cross-quant P/D and expert co-residency both work.

**Open (this roadmap):** PP vs EP for a model that *doesn't* fit one GPU; EPLB balance on our
asymmetric RTX/P100 compute; the placement-planner heuristic; tier- and device-aware radix
indexing; KV-aware global routing at ≥3 devices. Each gets its own spike + go/no-go doc
(`docs/spike-engine-distributed.md`, `docs/spike-engine-radix-cache.md`, the scheduler doc).

## References
- Gate spike **#149** (this box's measured baseline) · Epic **#161** · KV DAG **#107** ·
  WorkerScheduler **#147** · mixed-P/D quality **#176**
- Placement: [Helix (max-flow, heterogeneous GPUs+network)](https://arxiv.org/pdf/2406.01566) ·
  [Parallax](https://gradient.network/parallax.pdf) ·
  prima.cpp (piped-ring + Halda + prefetch) https://gitee.com/magicor/prima.cpp ·
  [Petals](https://research.yandex.com/blog/petals-decentralized-inference-and-finetuning-of-large-language-models) ·
  [llama.cpp RPC backend](https://github.com/ggml-org/llama.cpp/blob/master/tools/rpc/README.md)
- Expert parallelism: [SGLang EP + EPLB](https://github.com/sgl-project/sglang/blob/main/docs/advanced_features/expert_parallelism.md) ·
  [DeepSeek 96×H100](https://www.lmsys.org/blog/2025-05-05-large-scale-ep/) ·
  [Kimi K2 128×H200](https://www.lmsys.org/blog/2025-07-20-k2-large-scale-ep/)
- KV-centric: [SGLang RadixAttention](https://github.com/sgl-project/sglang) ·
  [Mooncake](https://kvcache-ai.github.io/Mooncake/) · [LMCache](https://arxiv.org/html/2510.09665v2)
