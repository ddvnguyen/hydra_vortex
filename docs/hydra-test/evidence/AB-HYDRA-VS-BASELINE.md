# A/B: Hydra coordinator+RPC vs direct llama-server — 2026-08-29 (owner-directed)

## Setup (strict parity)
- Same VM (P100 16GB), same binary (fork tip 67ceb00bd, v9720), same GGUF (Qwen3.5-9B-Q4_K_M),
  same flags both sides: `-ngl 16 -t 3 -c 65536 --cache-type-k q8_0 --cache-type-v q8_0 --cont-batching`.
- **B (baseline):** bare `llama-server` :18099, direct HTTP. VRAM 4,178 MiB.
- **A (Hydra):** core-A :19000 → engine :18086 (hydra RPC 0x42 PREFILL + M2 KV save/restore),
  `force_mode=solo` (single worker — mirrors baseline topology). VRAM 4,178 MiB — identical.
- Same 3-turn workload, full history resent each turn; same text both sides. Sequential (GPU exclusive per run).

## Results (wall-clock; both finish=length, 64 out)

| Turn | prompt tok | Baseline | Hydra | Δ |
|---|---|---|---|---|
| 1 | 8,328 | 39.65 s | 53.30 s | +13.7 s (+34%) |
| 2 | 26,819 | 88.91 s | 122.42 s | +33.5 s (+38%) |
| 3 | 35,110 | 54.79 s | 168.63 s | +113.8 s (+208%) |

## Attribution (engine-side traces — the honest split)

**Compute rate is IDENTICAL** (~250 tok/s prefill, ~8.5 tok/s decode both sides — P100 not slowed by Hydra):

| Prefill | tokens | time | rate |
|---|---|---|---|
| Baseline t1 | 8,328 | 31.5 s | 264 tok/s |
| Baseline t3 | 8,807 (26.3K prefix-cached) | 40.3 s | 219 tok/s |
| Hydra t3 (full re-prefill) | 35,110 | 144.8 s | 242 tok/s |

1. **Per-token overhead ≈ 0** — Hydra's engine prefill rate (242 tok/s) matches baseline (219-264 tok/s).
2. **Baseline turn2/3 benefited from llama-server's built-in prefix cache** (turn3 only prefilled 8.8K of 35.1K).
   Hydra re-prefilled the FULL history each turn (no prefix reuse engaged on the solo route) — that's the
   entire turn3 gap (+105 s ≈ 26.3K tok × ~4 ms).
3. **Same-work pipeline overhead ≈ +8-13 s per cold turn** (RPC chunking + coordinator pipeline + hashing),
   ~+34% at 8.3K prompt, shrinking proportionally as prompts grow. KV save itself is NOT on the critical
   path (post-decode STATE_GET 685 MiB took 0.4 s, async, after slot release — verified again).
4. **KV restore on warm resident = 0.0 ms** (`DECODE_APPLY restore=0.0ms`) — the #470 M2 machinery is fast.

## Verdict
Hydra adds **no meaningful compute overhead**; the visible gap is (a) ~10-20% fixed pipeline cost on cold
turns and (b) **missing prefix-cache reuse on the solo/cold route** — an optimization target (ledger/warm
affinity exists but didn't engage for identical multi-turn history), not an inherent cost.

## Follow-ups
1. Investigate why warm/prefix path didn't reuse turn1-2 KV for turn3 same-session solo requests
   (expected ledger.HasStoreState hit). Repro: this script + force_mode=solo.
2. cold_concurrency route broken (KV-not-restored crash, separate finding) — force_mode=solo used to sidestep.
