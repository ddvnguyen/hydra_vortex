# Stage 0c pre-registration (frozen before any run) — 2026-10-04

**Gate:** `G-0b` — the build-vs-itself KL floor = the **G-K2 denominator**
(`OPTION-C-DESIGN.md` §3.0 row *G-K2*, §Stage 0 item 4, §"G-0b instrument").
Stage 0 shipped everything else green and recorded `G-0b: NOT RUN — needs CI
llama-perplexity build` (`STAGE0-RESULTS.md` §7). The CI artifact landed in
`docs/evidence/strata-gap/stage0/PERPLEXITY-CI.md` (PR #825, branch
`docs/811-perplexity-ci`). This session measures the floor. Owner approved
Stage 0 + the CI `llama-perplexity` build on 2026-10-03/04.

Everything below is frozen **before any run**. Only §5's extent decision is
filled in after the pre-registered timing pass `T1` (that is what T1 exists
for) and committed **before** any measured pair; nothing else may change.

## 0. Instrument (binary, provenance, library path)

| Item | Value |
|---|---|
| binary | `.local/perplexity-ci/bin/llama-perplexity` (+ its `*.so*`, RPATH `$ORIGIN` — extracted as a directory, never moved alone) |
| `sha256` | `92594986686a4bbe23a2834695792ce32f8b18b9d57bf3c50e0ac87aba715fdd` |
| built from | fork `7a03f921d1cf0a83a89dcbb17bd75d567a7aa3b7`, workflow `3808d4e3`, run 37167416216, `runner_target=cloud`, `sm86-sm120`, CUDA 13.2, `GGML_NATIVE=OFF` (`PERPLEXITY-CI.md` §2/§5) |
| link | `LD_LIBRARY_PATH=<artifact>/bin:/opt/software/cuda/13.2.1/lib64` — required: `libcudart.so.13`/`libcublas.so.13` are **not** bundled (`PERPLEXITY-CI.md` §5) |
| relation to Stage 0 engine | export tree = fork commit tree `d03bd75b…` = the `.local/q2g` Stage 0 binary's tree (`PERPLEXITY-CI.md` §1), so the floor is measured on the same source family the stage binaries are built from |

**Only** this binary is executed, and only as `llama-perplexity` (perplexity /
KL flow). No builds, no podman, no CI triggers, **`llama-server` from this
artifact is never executed**.

## 1. Corpus, context, chunks

| Item | Value |
|---|---|
| corpus | `scripts/eval/wikitext-2-raw/wiki.test.raw` — the `kl-gate.sh` corpus named by `G-K2` |
| `sha256` | `173c87a53759e0201f33e0ccf978e510c2042d7f2cb78229d9a50d79b9e7dd08` |
| size | 1 290 590 bytes |
| token count | `n_chunk × 16384`, `n_chunk` read from run A's own log line `calculating perplexity over N chunks` (exact value recorded in the results after run A1; tokenization = this model's tokenizer with BOS, as `perplexity()` does) |
| `-c/--ctx-size` | **16384** (Stage 0 F0 config, `STAGE0-RESULTS.md` §1.1) |
| `-b/-ub` | **2048 / 2048** (Stage 0) |
| extent | `K = min(N_full, K_max)` chunks of 16384 tokens from the head of the corpus; `K_max` from the §5 formula evaluated on `T1`. `K` is passed as `--chunks K` on run A, so if `N_full ≤ K_max` the run is automatically the **full corpus**. |

Fixed token sequence: run B never reads the corpus — the token sequence is
read from run A's base file (`perplexity.cpp` `kl_divergence()`), so both legs
see byte-identical tokens (memory note *teacher-forced-vs-sampled-gates*:
fix the sequence, never sample).

## 2. Exact flags (Stage 0 F0 verbatim, minus what `llama-perplexity` cannot accept)

Shared by run A and run B:

```
-m /mnt/SSD/strata-models/GSQ-RCO/IQ3_S/Qwen3.8-Flash-Next-GSQ-RCO-IQ3_S-00001-of-00002.gguf
--split-mode layer -fit off -ngl 99
--n-cpu-moe 99 --override-tensor per_layer_token_embd=CPU
--moe-expert-cache-size 0
-c 16384 -np 1 --flash-attn on -t 6
--experimental-logs --load-mode none
-b 2048 -ub 2048
```

Run A adds: `-f <corpus> --kl-divergence-base <base> --chunks K`
Run B adds: `--kl-divergence --kl-divergence-base <base>`  (no `-f`: tokens come from the base file)

**Deltas vs `STAGE0-RESULTS.md` §1.1 and why each is forced by the tool
(perplexity example, not server example):**

| Stage 0 flag | disposition | reason |
|---|---|---|
| `--host 127.0.0.1 --port 8093` | dropped | server-only (`LLAMA_EXAMPLE_SERVER`); perplexity binds no port |
| `--alias gsq-rco-iq3_s` | dropped | server-only |
| `--jinja` | dropped | not accepted by `LLAMA_EXAMPLE_PERPLEXITY` (0 matches in the artifact's own `--help`) |
| `--spec-type none` | dropped | not accepted by this example; speculative decode stays **off by default** (no draft path, default `speculative.types` empty) |
| `--ple-prefetch` | dropped | server-only |
| `--parallel 1` | passed as `-np 1`, then overridden by the tool | `perplexity()`/`kl_divergence()` recompute `n_parallel = max(1, n_batch/n_ctx) = 1` anyway |
| `--profile-decode` | not used | server-only; no profiling arm in G-0b |
| everything else (`-t 6`, `-ngl 99`, `--n-cpu-moe 99`, `--override-tensor per_layer_token_embd=CPU`, `--moe-expert-cache-size 0`, `-fit off`, `--split-mode layer`, `--flash-attn on`, `-c 16384`, `-b/-ub 2048`, `--load-mode none`, `--experimental-logs`) | **kept verbatim** | model/GPU config mirror, owner requirement |

`--override-kv` is **banned** (owner rule) and is not used. Spares: `-t 6`
⇒ `nproc(20) − 6 = 14 ≥ 2` (pre-registered spare rule,
`leader-handoff-state.md:756-757`). Disclosed covariate: the KLD/PPL
reduction spawns `hardware_concurrency()−1 = 19` worker threads transiently
(`perplexity.cpp`) — CPU-only phase, no GPU work.

## 3. Environment (zero-change)

```bash
env -u HYDRA_PIN_FILE -u HYDRA_E1_DRYRUN -u HYDRA_E1_TIMING -u HYDRA_E0_STATS \
    CUDA_VISIBLE_DEVICES=1 CUDA_DEVICE_ORDER=PCI_BUS_ID \
    TMPDIR=<worktree>/.local/tmp \
    LD_LIBRARY_PATH=<artifact>/bin:/opt/software/cuda/13.2.1/lib64
```

- Mirrors `scripts/hydra-kl-gate.sh` step 1's strip, but with **all** log
  paths out of `/tmp/opencode/` (the script hardcodes `/tmp/opencode`, so the
  flow is re-run by `run-stage0c.sh` with logs under
  `docs/evidence/strata-gap/stage0c/` — that is the override the owner
  required). Host env was verified to contain **no** `HYDRA_*`/`LLAMA_*`
  variables pre-arm (recorded in `prearm.log`).
- `TMPDIR` under `.local/tmp`; **nothing is written under `/tmp`**.
- `CUDA_VISIBLE_DEVICES=1` (RTX 3060) only; GPU0 (5060 Ti) never touched; no
  pinning (`taskset`), no ports, no owner-VM/8086/8091 contact.
- One GPU = one compute task: every run is strictly sequential.

## 4. Procedure (order frozen)

1. **pre-arm gates + h2d pre** (`prearm.log`): both GPUs ≤ 5 MiB, 8091 not
   listening, no `llama-server`/`llama-perplexity` alive,
   `MemAvailable ≥ 67 GiB` (**pre-arm only**), spare ≥ 2, h2d probe
   **6.00–6.30 GB/s** (rerun once if invalid).
2. **T1 timing pass** (not one of the n pairs): `T1A` = run A with
   `--chunks 1`, `T1B` = run B against it. Purpose: wall time + base-file
   size ⇒ §5 extent decision.
3. **§5 extent decision** appended to this file and committed **before any
   measured run**.
4. **Measured pairs (n = 3), sequential:** `P1 = A1,B1` · `P2 = A2,B2` ·
   `P3 = A3,B3`. Each `A_i` is a fresh `--kl-divergence-base` dump from a
   cold process; each `B_i` is an independent forward pass of the *same
   binary* against its own `A_i` — i.e. exactly what G-K2 later does with two
   different builds, with the two builds being identical here.
5. **Zero-change plumbing control `C1`**: repeat run B against `A1` with the
   identical flags and `env` (all `HYDRA_*` unset) — nothing at all differs
   from `B1`; any difference is pure run-to-run instrument noise.
6. h2d post probe after every run; final GPU state (`final.log`).

h2d probes: pre **and** post each run (Stage 0 parity). 1 Hz link sampler
(`sample_link.sh`, copied alongside) runs across every run → `gen1 ∧ util≥20`
must be 0 samples.

**Log/artifact handling:** every run writes
`docs/evidence/strata-gap/stage0c/<label>.log` (header: date, pid, full
command, env strip proof, nproc/load1/MemAvailable, GPU state) + `<label>.link.tsv`
+ `<label>.h2d.txt`. Base files live in `.local/stage0c/` (git-ignored):
`A2`/`A3` are deleted right after their pair, `A1` after `C1`, the timing
base `T1A` is kept (small).

## 5. Wall-time budget and extent rule (the only post-T1 fill-in)

- **Budget:** ≤ **4 h** of measured wall time for steps 4–5 (the timing pass
  is extra). If the rule below yields a `K_max` too small to be a sensible
  floor (`K_max < 4`), **STOP and report** rather than shrink further.
- From `T1`: `L_A`, `c_A = wall(T1A) − L_A`, `L_B`, `c_B = wall(T1B) − L_B`
  (load times parsed from the tool's own log lines; if not parseable, fall
  back to `L = 0`, `c = wall`, which under-estimates `K_max` — conservative),
  and `s = |base file bytes| / 1 chunk`.
- `pair(K) = L_A + K·c_A + L_B + K·c_B`; control `= L_B + K·c_B`;
  `total(K) = 3·pair(K) + control ≤ 240 min` ⇒ time bound on `K`.
- disk bound: `K·s ≤ 60 GB` (WorkDisk had 95 GB free pre-arm; keep ≥ 30 GB).
- `K_max = min(time bound, disk bound)`, integer floor; **extent
  `K = min(N_full, K_max)`** via `--chunks K` on every run A.

## 6. Metrics, floor, G-K2 bar, and the failure readings

Recorded from every run B log: **`Mean KLD ± σ`**, **`99.0% KLD`**, `Median`,
`Maximum`, **`Same top p`**, plus `Mean PPL(base)` as a sanity line; run A's
`Final estimate: PPL` is recorded per `A_i` (E1 reported 0.031% spread over 8
dumps).

- **floor (per pair) = `Mean KLD` of `B_i` vs `A_i`** (full-vocab, uint16-
  quantized base — never a truncated top-k, never PPL, never a char
  position; memory note rule "never mix truncated top-k vs full-vocab").
- **G-K2 bar = 2 × max(Mean KLD over the n pairs)** — conservative, E1
  precedent (`§41`: the 0.01 gate sits 10.6× above the **observed max** floor
  "correct practice at n=4 vs median"). `2 × median` is reported alongside
  for reference, the max is the bar.
- **Non-determinism beyond the E1 precedent** if any of:
  cross-pair `max − min` Mean KLD **> 7.04e-4**, or cross-pair
  `max/min` **> 4.0** (E1 §40: four disarmed pairs 0.000235 / 0.000939 /
  0.000467 / 0.000249, abs spread 7.04e-4, ratio 4.0), or
  `|Mean(C1) − Mean(B1)| > 1e-4`.
- **Deterministic-in-practice** if `|Mean(C1) − Mean(B1)| ≤ 1e-5`.
- **Floor > 0.01** (the E1 gate) ⇒ report it as the finding: the instrument
  cannot carry a 2×-floor gate at E1's threshold. A floor of exactly 0 also
  gets reported as the finding (2 × 0 is an unsatisfiable bar for G-K2 and
  the design must be told).

Every number is reported raw, with min/median/max + spread; no outlier is
discarded (E1 outlier discipline: report the tail, never promote it silently).

## 7. Abort conditions (owner rules; STOP and report, no workarounds)

crash / non-zero exit · invalid h2d link (outside 6.00–6.30 GB/s after one
rerun) · `gen1 ∧ util≥20` sample · any pre-arm guard failure · free disk
< 30 GB · `MemAvailable < 67 GiB` at pre-arm · any sign of touching GPU0,
port 8091/8086, or the owner's cachyos-dev VM · exact-pid kills only.

---

## 8. EXTENT DECISION — filled from T1, committed **before any measured run** (2026-10-04)

T1 executed per §4/§5 (`T1A` = run A `--chunks 1`, `T1B` = run B against it); both `rc=0`,
h2d 6.11–6.12 GB/s (band 6.00–6.30), `gen1 ∧ util≥20` = 0 samples, no crash. Raw:
`T1A.log`, `T1B.log`, `T1A.h2d.txt`, `T1B.h2d.txt`, `T1*.link.tsv`. T1 is **not** one of the
n pairs (extent differs) and is reported separately.

Measured inputs:

| quantity | value | source |
|---|---|---|
| `wall(T1A)` / `wall(T1B)` | 237.2 s / 236.9 s | `T1A.log` / `T1B.log` `rc= wall_s=` |
| `c_A` | 116.14 s internal pass + 1.54 s `process_logits`+write = **117.7 s/chunk** | `T1A.log` |
| `c_B` | 115.89 s internal pass + ≈5 s KLD reduction/stats = **120.9 s/chunk** | `T1B.log` |
| `L_A` / `L_B` | `wall − c` = **119.5 s / 116.0 s** | §5 rule |
| `s` (base file per chunk) | **4 068 109 324 B (4.07 GB)** | `ls -l .local/stage0c/T1A.kld` |
| KLD positions per chunk | 8191 (`n_ctx − 1 − n_ctx/2`) | `perplexity.cpp` |

Rule evaluation: time bound `3·pair(K) + control(K) ≤ 240 min` ⇒ **K ≤ 16**;
disk bound `K·s ≤ 60 GB` ⇒ **K ≤ 14**.

### ⇒ `K_max = 14`, extent `K = min(N_full, 14)` chunks = 229 376 corpus tokens head,
### 114 674 KLD positions per pair. Estimated total measured wall = **3.48 h** (budget 4 h),
### base-file peak 57.0 GB (95 GB free → ≥ 38 GB remains, ≥ 30 GB rule holds).

No other pre-registered item changes. P1/P2/P3 and C1 run at this extent.

---

## 9. AMENDMENT — control `C1` moved to immediately after `B1` (2026-10-04 10:30 +07)

**When:** after `A1` completed (`rc=0`, wall 1767.5 s, base 56 953 530 276 B = 57.0 GB),
**before** `C1`/`A2`/`B2`/`A3`/`B3`. Nothing about the metric, extent, flags, env or pair
definition changes.

**Why (disk arithmetic in §4 was infeasible as written):** §4 said "keep `A1` until after
`C1`, delete `A2`/`A3` after their pair". Two live bases at once = 114 GB while the
worktree had 95 GB free pre-arm (41 GB free after `A1`), which also breaks §5's
`K·s ≤ 60 GB` base-file rule. With `s = 4.07 GB/chunk` and `K = 14`, **exactly one base
file can exist at any time**.

**Resolution (no other option preserves `C1`):** execute `C1` (repeat run B against the
same `A1`, identical flags, all `HYDRA_*` unset) **right after `B1`**, then delete `A1.kld`,
then run `P2` and `P3`. The control is unaffected in substance — it is a byte-identical
re-run of `B1` against the same base; its only difference from the registered order is
that it sits adjacent to `B1` instead of after `P3`, which *reduces* (not increases)
environmental drift between `B1` and `C1` and is the same adjacency the E1 isolation legs
used. Every reading is reported raw, so any residual time-drift caveat is visible.
