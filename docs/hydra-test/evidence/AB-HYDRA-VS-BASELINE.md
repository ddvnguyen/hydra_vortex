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

---

# 6-turn A/B with TTFT/decode breakdown — 2026-08-29 (owner-directed)

Same strict parity as above (same binary/GGUF/flags/ctx; force_mode=solo on Hydra side; sequential exclusive runs).
Streaming client, TTFT = first visible delta (incl. reasoning_content), decode = remainder. 64 out tokens per turn.
Harness caveat: conversation grows one user turn at a time (assistant ACK appended AFTER each response).

## Client wall-clock

| Turn | Prompt tok | BASE ttft | BASE decode | BASE total | HYDRA ttft | HYDRA decode | HYDRA total |
|---|---|---|---|---|---|---|---|
| 1 | 8,323 | 31.3 s | 7.5 s | 38.7 s | 32.4 s | 7.5 s | 39.9 s |
| 2 | 14,532 | 25.3 s | 8.8 s | 34.0 s | 60.2 s | 8.7 s | 68.9 s |
| 3 | 20,291 | 24.8 s | 10.7 s | 35.5 s | 90.0 s | 10.1 s | 100.1 s |
| 4 | 26,050 | 26.2 s | 11.7 s | 38.0 s | 134.8 s | 11.9 s | 146.7 s |
| 5 | 31,359 | 25.5 s | 13.0 s | 38.5 s | 215.1 s | 13.0 s | 228.1 s |
| 6 | 37,118 | 29.0 s | 14.8 s | 43.8 s | **503 FAIL** | — | — |

## Attribution (engine traces, exact arithmetic)

- Decode rate: IDENTICAL both sides (8.6 → 4.3 tok/s as ctx grows) — pure P100 attention scaling, zero Hydra effect.
- Baseline TTFT stays FLAT ~25 s across turns: llama-server prefix cache prefills only ~6.2K NEW tokens per turn.
- Hydra TTFT grows super-linearly because it re-prefills the FULL history every turn:
  - t2: 14,532 tok / 60.2 s ≈ 241 tok/s; t3: 20,291/90.0 ≈ 225; t4: 26,050/134.8 ≈ 193; t5: 31,359/215 ≈ 146 tok/s.
  - Rate also DEGRADES beyond ~20K (225→146 tok/s), i.e. RPC-chunked prefill cost grows with context — second-order overhead on top of redundancy.
- Same-work cold-turn overhead (turn1, both sides cold): 32.4 vs 31.3 s TTFT ≈ +1.1 s (+3.5%) — coordinator+RPC+hash cost is small per request.
- Turn1 rate check: 8,323 tok in ~31.4 s ≈ 265 tok/s — matches baseline rate exactly at equal work.

## Where Hydra loses (ranked)

1. **No prefix reuse on solo/cold route (dominant, ~70-80% of the gap).** Every turn re-prefills the full
   history (t5: 31.4K tok = ~5x the 6.2K the baseline actually computed). The ledger/warm path exists but
   did not engage for consecutive same-session solo requests. Fix direction: store-level prefix check
   (SyncMissing-style) before full PREFILL on the solo route.
2. **RPC-chunked prefill degrades with context size (secondary).** Effective prefill rate drops 265 → ~146
   tok/s from 8K to 31K context on the Hydra path; baseline holds ~250 tok/s at the same sizes (its turn2+
   prefill is smaller, but turn1 at 8.3K matched). Suspect per-chunk RPC round-trip + hash/logits staging
   grows with KV size. Worth profiling PREFILL chunk boundaries.
3. **KV save is NOT the problem** — confirmed again: post-decode STATE_GET 292-685 MiB streams in ~0.2-0.4 s,
   async, after slot release. Restore on resident pool: 0.0 ms.
4. **P0 BUG — turn6 (37.1K) deterministic 503.** Pre-prefill KV restore (STATE_GET→state_seq_set_data) loses
   the tail of the stream: `hydra_recv_a: recv failed (EAGAIN)` is treated as clean EOF →
   `state_seq_set_data: unexpectedly reached end of buffer` → corrupted KV pool → `llama_decode failed at
   batch 33792` → `prefill_engine_terminal_error`. Reproduced 3/3 runs. EAGAIN on a live fd must retry, not
   EOF. Fix in fork: distinguish EAGAIN from EOF in hydra_recv_a retry loop.

## Verdict

Hydra compute remains neutral; its losses are (1) missing prefix reuse — the single biggest, very fixable
optimization target, (2) RPC prefill chunking that scales poorly with context, (3) a hard P0 failure at
~37K context on the shared-prefix restore path. Baseline stays flat ~25 s TTFT at every turn via prefix cache.
