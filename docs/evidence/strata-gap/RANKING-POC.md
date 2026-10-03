# RANKING-POC — Expert-profile ordering PoC on Strata (RTX 3060)

- **Task**: t0008 / #811 — do better expert-profile orderings beat the shipped one?
- **Date**: 2026-10-02/03 · **Branch**: `docs/811-ranking-poc` · **Status**: COMPLETE (P0–P3 all `status=ok`)
- **Verdict (one line)**: **No** — the draft atlas did not beat the shipped profile; it landed *below* both
  shipped and a random permutation at median level, and the measured same-profile noise floor
  (P3−P0: **1.9 pp hit / 0.95 tok/s** median, up to **11.0 pp / 13.7 tok/s** per turn) bars every
  finer-grained claim.

---

## 1. Question and design

Four cold-start arms, one GPU (RTX 3060, driver 595.91.07, Strata v0.1.29, model `gsq-rco-iq3_s`,
16K ctx), run sequentially, identical protocol:

| Arm | `--expert-profile` | Purpose |
|-----|--------------------|---------|
| P0 | shipped `expert-profile.bin` | baseline |
| P1 | `atlas.bin` (draft routing-heat atlas) | candidate |
| P2 | `shuffled.bin` (seed 1) | negative control: does *any* permutation move the needle? |
| P3 | shipped (repeat of P0) | **noise floor** — no P1-vs-P0 claim may be smaller than P3-vs-P0 |

Profiles (STRP v1, 24 576 ranked pairs, identical byte size 196 632):

| profile | sha256 | top-N overlap with shipped |
|---------|--------|----------------------------|
| shipped | `8f59b4aa8873209dff11c11e37bcda9529a1335b724a1afeea37bf6388975baf` | — |
| atlas | `b5cee3daedf417966e53146307326ac29fb7594dae11490fff6210bc22e1c79c` | top100=4, top512=24, top1000=79, top2456=404 |
| shuffled | `0e97f6202531fe53157df8b28908ca45d0f8c20cca7357c685bd4ad2059759f5` | top100=0 vs atlas (full permutation) |

**Atlas provenance (critical caveat)**: draft-grade — built from 2 probe requests × 256 tokens on
**Qwen3.8-Flash-Next-APEX-I-Mini**, i.e. *not* the model served here (GSQ-RCO IQ3_S). The routing heat
is expected to transfer only partially; this is the single largest limitation of the candidate.

## 2. Protocol

- 6 fixed prompts `prompts/rank-t{1..6}.txt` (sha256 in `prompts/rank-sha256sums.txt`),
  T1/T2/T5 = coding, T3/T4/T6 = general/expository; file sizes 4.2–4.7 KB each (≈1.0–1.2 Ktok apiece).
- Single chat session per arm, **cumulative messages** (standard OpenAI conversation → prompt-cache
  reuse), `temp=0`, `seed=42`, `max_tokens=256`, streaming, one request at a time.
- Measured `prompt_tokens` per turn (identical in all four arms):
  `1057 → 2344 → 3481 → 4721 → 6047 → 7332`.
- Client `.local/strata-research/convo3060.py` (OpenAI `/v1/chat/completions`).
- Arm lifecycle `.local/strata-research/arm-ranking.sh`: gates → H2D probe → link sampler →
  `server.py` (config copied from t0006 `strata-s1.json`; only `--expert-profile` and log path differ)
  → health + engine-load marker → startup-facts capture → conversation → `/metrics?requests=all`
  dump → exact-PID stop → engine-log copy → H2D re-probe → parser verdict.
- All harness code lives in `.local/strata-research/` (git-ignored scratch; **not** committed —
  committed code is only `tools/atlas/atlas_to_strata_profile.py`).

**Workload shape (applies to every arm)**: all 6×256 generated tokens were *reasoning* tokens
(`content_chars=0`, `finish=length` on every turn — the reasoning model never finished thinking
inside 256 tokens). The history fed to later turns therefore falls back to reasoning text. Routing
heat in this PoC is the model's *thinking-token* routing.

## 3. Capture method (source-verified)

Engine stderr → `Pn-engine.log`, one session, order-aligned:

- `strata serve: prompt …` — `src/program/generate.cpp:4322`
- `decode expert cache hit rate …` — `:4331` (printed only when `req_look > 0`; all four arms came
  out **aligned: 6 serve / 6 hit / 6 decode-timing / 6 prefill / 6 convo** anyway)
- `strata decode timing: … CPU experts / VRAM hits / PCIe …` — `:4209–4217`
- startup facts — `:1866/:2164/:2169/:2223`
- `/metrics?requests=all` per-arm snapshot in `logs/Pn.metrics.json` (`totals.requests=6`,
  `totals.reused=17 625` — exact match with the sum of per-turn reuse, cross-check passed)

**Not captured**: the stdout summary block (speculation + `adaptive tier … swapped (every 4 rounds)`
lines, `generate.cpp:4944/:4976`) goes to a PIPE read by `server.py`'s protocol loop — it appears in
neither the engine log nor `Pn-server.out`. **Adaptive expert-swap counts are therefore unobservable
in this PoC**; the per-turn hit-rate trajectory is the only adaptation proxy (`adapt_every=4`
default, so adaptation *can* act from T4/T5 onward).

## 4. Integrity gates (all arms, all passed)

| Arm | H2D pre/post (GB/s) | link_bad | Mem after | ports 8090/8091 | stop |
|-----|---------------------|----------|-----------|------------------|------|
| P0 | 6.11 / 6.12 | 0 | 67.1 GiB | down (pre-gate) | exact PID, GPU 1 MiB |
| P1 | 6.12 / 6.11 | 0 | 67.9 GiB | down (pre-gate) | exact PID, GPU 1 MiB |
| P2 | 6.11 / 6.12 | 0 | 69.6 GiB | down (pre-gate) | exact PID, GPU 1 MiB |
| P3 | 6.10 / 6.11 | 0 | 78.5 GiB | down (pre-gate) | exact PID, GPU 1 MiB |

Every arm additionally required `MemAvailable ≥ 67 GiB` before start (never lowered) and ended with
8092 down. No arm ever ran under memory pressure (see §7 incident).

Startup facts per arm:

| Arm | slots | pre-filled | auto slots | borrow | prompt chunk |
|-----|-------|------------|------------|--------|--------------|
| P0 | 2638 | 2638/2638, slot 0 verified | 2041 | 2313 (4.43 GiB) | 8192 |
| P1 | 2647 | 2647/2647 | 2041 | 2323 | 8192 |
| P2 | 2656 | 2656/2656 | 2041 | 2330 | 8192 |
| P3 | 2638 | 2638/2638, slot 0 verified | 2041 | 2313 (4.43 GiB) | 8192 |

Profile line (each arm): `profile …: 24576 ranked pairs, built for 24576 slots`; policy line:
`expert cache N slots, ~5.06 GiB; policy is PROFILE, ranked by routing frequency, no eviction`.
Slot count is sized from free VRAM at load → **±9/±18 slots (±0.7 %) variance across P0/P1/P2
(confound, biases P1/P2 slightly *toward* larger caches); P0 vs P3 is exactly 2638 = clean pair.**

## 5. Results

### 5.1 Per-turn hit rate (%) / decode tok/s (engine `serve` line)

| Arm | T1 | T2 | T3 | T4 | T5 | T6 | **median hit** | **median tps** | micro-avg hit | draft accept | Σ decode ms |
|-----|----|----|----|----|----|----|----------------|----------------|---------------|--------------|-------------|
| P0 shipped | 54.9/31.8 | 60.9/39.6 | 56.6/24.4 | 46.7/39.3 | 49.5/34.6 | 54.8/36.6 | **54.85** | **35.6** | 53.96 | 76.2 % | 45 925 |
| P1 atlas | 50.8/34.4 | 55.1/36.2 | 52.5/33.5 | 60.3/27.5 | 47.4/29.7 | 50.8/32.5 | **51.65** | **33.0** | 52.91 | 71.4 % | 47 980 |
| P2 shuffled | 38.0/31.4 | 58.2/34.0 | 55.2/27.7 | 59.3/35.6 | 43.6/26.3 | 55.0/34.4 | **55.10** | **32.7** | 51.69 | 71.8 % | 49 294 |
| P3 shipped (drift) | 54.9/36.5 | 61.5/39.1 | 55.8/37.1 | 57.7/36.6 | 49.9/34.0 | 60.9/22.9 | **56.75** | **36.55** | 56.71 | 72.0 % | 46 194 |

Prompt-cache reuse was byte-identical across arms (`reused = 0, 1052, 2339, 3476, 4716, 6042`;
prefill 6.4–7.7 s/turn, 162–188 tok/s) — the conversation structure is a controlled constant;
only generated-token *identity* diverges (see §6).

### 5.2 Noise floor — P3 − P0 (same profile, same slots, identical protocol)

| | T1 | T2 | T3 | T4 | T5 | T6 | **median** |
|---|----|----|----|----|----|----|-----------|
| Δ hit pp | +0.0 | +0.6 | −0.8 | **+11.0** | +0.4 | +6.1 | **+1.9** |
| Δ tps | +4.7 | −0.5 | **+12.7** | −2.7 | −0.6 | **−13.7** | **+0.95** |

**Noise floor (n=1 repeat): median 1.9 pp / 0.95 tok/s; per-turn envelope up to 11.0 pp / 13.7 tok/s.**
Total decode time drifted only +0.6 % (45 925 → 46 194 ms), so the aggregate is stable while
single turns swing wildly.

### 5.3 Deltas vs baseline P0 (compare against §5.2 before believing any of them)

| | T1 | T2 | T3 | T4 | T5 | T6 | **median** | floor | exceeds floor? |
|---|----|----|----|----|----|----|-----------|-------|----------------|
| P1−P0 hit pp | −4.1 | −5.8 | −4.1 | +13.6 | −2.1 | −4.0 | **−3.2** | 1.9 | yes (1.7×) — *deficit* |
| P1−P0 tps | +2.6 | −3.4 | +9.1 | −11.8 | −4.9 | −4.1 | **−2.6** | 0.95 | yes (2.7×) — *deficit* |
| P2−P0 hit pp | **−16.9** | −2.7 | −1.4 | +12.6 | −5.9 | +0.2 | **+0.25** | 1.9 | no → indistinguishable |
| P2−P0 tps | −0.4 | −5.6 | +3.3 | −3.7 | −8.3 | −2.2 | **−2.9** | 0.95 | yes (3.1×) — *deficit* |

## 6. Verdict (leader rules applied: no P1-vs-P0 claim smaller than the P3-vs-P0 floor)

1. **Does the atlas beat the shipped profile? NO.** Median P1−P0 = **−3.2 pp hit / −2.6 tok/s**,
   both magnitudes exceed the median noise floor (1.9 pp / 0.95 tok/s) — the point estimate is a
   *deficit*, not a benefit. The per-turn story is barred: 6 of 12 per-turn deltas sit inside the
   per-turn drift envelope (P3 itself moved +11.0 pp at T4 and −13.7 tok/s at T6), so only
   6-turn medians are discussable. Statistically honest framing: one drift sample, margin ≈1.7×
   the floor — enough to deny any "atlas wins" claim, **not** enough for a high-confidence
   "atlas is worse" headline beyond "point estimate worse, no evidence of benefit".
2. **Does the atlas beat a random permutation? NO.** P1−P2 median = −3.45 pp hit — the heat-informed
   atlas landed *below* shuffled. (No direct P1↔P2 drift pair exists; P0↔P3 floor used as proxy.)
   Meanwhile shuffled ≡ shipped on hit rate (P2−P0 = +0.25 pp ≪ 1.9 pp floor): **permuting the
   shipped ranking did not move median hit rate at all** — at this cache size (all 2638 slots
   pre-filled, no eviction), ordering appears to matter little for hit rate.
3. **Does any difference persist to T6, or wash out (adaptive tier, `adapt_every=4`)?**
   **Inconclusive at turn granularity.** Same-profile per-turn drift (up to 11 pp) makes trajectories
   unusable; early-turn atlas deficits (T1–T3: −4.1/−5.8/−4.1 pp) are directionally consistent but
   T4's +13.6 mirrors P3's own T4 drift (+11.0) — i.e. consistent with noise. Adaptive swaps are
   unobservable (§3), so wash-out cannot be confirmed or denied directly. Decode-speed deltas
   (P1 and P2 both ≈ −3 tok/s vs P0 while P3 is +0.95) also exceed the median floor, but draft
   acceptance drifted too (76.2 % → 72.0 % between P0 and P3), so decode deltas are entangled with
   spec-decode variance and are reported as secondary observations only.

**Practical conclusion**: this PoC found *no* benefit from the draft atlas, and no sensitivity to
permutation at median level. Before investing in better atlases, two things must change: (a) build
the atlas from probes on the *served* model/routing, and (b) get n≥3 arms per profile (or a
variance-reduced design: interleaved turns, A/A arms in the same run) — a single-run median floor
of 1.9 pp with a 11 pp per-turn envelope cannot resolve effects of the size we care about.

## 7. Incident log (P3 delay)

At 00:19 the P3 pre-arm gate failed (`MemAvailable 64.9 < 67`). Cause: owner's `cachyos-dev`
libvirt VM ballooned 26.5 → 30.5 GB while host swap churned (`MemAvailable` 73 → 54 GiB).
The VM was **not touched**, the gate was **not lowered**; an auto-retry watcher (gate ≥67 GiB ×
3 samples + swap-quiet, 1 h cap) was started but expired unused. P3 was eventually run at 06:02
after manual confirmation of 3 × 20 s samples (mem ≥67 GiB, `si=so=0`) → `status=ok`.
**No arm ever ran under swap churn.**

Earlier at 00:06, P0's first attempt exposed a harness bug (client read delta key `reasoning`
instead of Strata's `reasoning_content`, so `ttft` stayed null and the arm aborted pre-measurement).
Fixed in `convo3060.py`; attempt-1 artifacts archived as `P0-attempt1-*` and are excluded from all
tables. P0 was rerun cold.

## 8. Caveats

- **n=1 per arm**; the noise floor itself is one repeat → no confidence intervals. Claim rule used:
  median delta must exceed the median floor; margins < 2× floor = inconclusive.
- **Content divergence**: despite `temp=0 seed=42`, generated tokens differ across arms (GPU
  reduction nondeterminism / near-tie argmax flips). Prompt-token *structure* is identical
  (256 tok/turn) but routing heat follows the actual tokens → part of every delta is workload
  divergence. P3 captures this combined with system noise — which is exactly what a floor should do.
- **Atlas/model mismatch**: APEX-I-Mini-derived heat vs GSQ-RCO workload (§1) — likely the dominant
  reason the candidate failed to transfer.
- **Slot-count variance** ±0.7 % (§4) affects P0/P1/P2 comparisons; P0 vs P3 is exact.
- **Adaptive tier swaps unobservable** (§3); adaptation may partially mask static-order effects
  from T4 onward.
- **Reasoning-only outputs** (§2): findings characterize thinking-token routing under `max_tokens`
  truncation, not finished-answer traffic.
- Single GPU / single model / 16K context / `v0.1.29`; generalization to other setups untested.

## 9. Evidence inventory

```
docs/evidence/strata-gap/
├── RANKING-POC.md                    (this file)
├── prompts/rank-t{1..6}.txt          fixed conversation prompts
├── prompts/rank-sha256sums.txt
└── ranking/
    ├── configs/rank-p{0..3}.json     arm configs (copied from .local; only --expert-profile differs)
    ├── arms/P{0..3}.convo.json       per-turn client trace (content/reasoning/ttft/tps/finish)
    ├── arms/P{0..3}.rank.json        parsed turns + startup facts + medians
    ├── arms/P0-attempt1-*            aborted first attempt (harness bug; excluded)
    └── logs/
        ├── P{0..3}-arm.out           lifecycle transcript + verdict line
        ├── P{0..3}-engine.log        engine stderr (capture source, order-aligned)
        ├── P{0..3}-startup.txt       startup facts
        ├── P{0..3}.metrics.json      /metrics?requests=all snapshot (cross-check)
        ├── P{0..3}.h2d.txt / .link.tsv / .summary.txt / -server.out
        └── watcher.out               P3 auto-retry watcher log (expired unused)
```

Committed code: `tools/atlas/atlas_to_strata_profile.py` (STRP v1 writer, `--shuffle SEED`).
Harness scripts (`.local/strata-research/`) and `.bin` profile files are intentionally **not**
committed. Model/gates/config provenance: configs derive from t0006 `strata-s1.json`
(`git show docs/811-t0006-strata-vs-fork-evidence:docs/evidence/strata-3060-ab/arms/strata-s1.json`).
