# ADR 0003 — Leader contract: human ↔ baseline-flash-next leader (Qwen3.8-Flash-Next baseline)

- **Status:** Accepted (2026-09-13)
- **Supersedes:** nothing. Extends **ADR 0002** (`docs/decisions/0002-leader-contract.md`) to the `baseline-flash-next` track; ADR 0002 stays in force for the 2×RTX vanilla `Qwopus3.6` baseline.
- **Parties:** human `ddvnguyen` ↔ agent **`baseline-flash-next-leader`** (leader of the `baseline-flash-next` track)
- **Workspace:** `/mnt/WorkDisk/workspace/worktree/1q3ry0vb/baseline-flash-next` on branch `baseline-flash-next`
- **Charter:** `CLAUDE.md` §Task Lifecycle 01→07, `AGENTS.md`; this ADR is the signature.

## Context

The project now has a second baseline thread: **Qwen3.8-Flash-Next (`qwen4exp`)** on the
`baseline-flash-next` fork lineage, tracked by **#763** (separate from #762 MoE-cache
profiling and #761 MTP port — do not conflate). Per `PROJECT_STATUS.md` (addenda
2026-09-06 → 2026-09-12):

- **Fork decision (user-approved, Option B):** Hydra's fork commits were rebased onto
  `GenerelSchwerz/llama.cpp` branch `qwen4exp-mtp` to adopt its CUDA MoE expert cache +
  grouped MoE drafting/MTP. Submodule pointer moved `1d3c4a8e3 → efd26f235`; our 5
  Hydra #747 commits (`--parallel-ctx-threshold`) + UM `cudaMemAdvise/prefetch`
  preserved with zero conflicts.
- **Fork PR target (per user):** `ddvnguyen/llama.cpp` branch **`baseline-flash-next`**
  — not `master`, not `hydra-fork`, not `fork/763-qwen4exp-mtp`.
- **Lineage conflict resolved:** `cdd11021b` (hand-rolled qwen4exp NextN/MTP) vs the gs
  lineage's independent draft head are the same code; gs adopted as primary, cdd has
  no unique delta. Merge `b6b34cc0f` tree byte-identical to `efd26f235`. PR
  **ddvnguyen/llama.cpp#120** open for human review (not merged).
- **E2E on the resolved tree (`efd26f235`) — PASS:** production-shaped
  `Qwen3.8-27B-UD-Q5_K_S.gguf` + `--spec-type draft-mtp` (dual-GPU RPC, UM on):
  prefill 134.7 tok/s, decode **32.57 tok/s**, MTP acceptance 41.5%; and the true
  `qwen4exp` pairing (target `qwen3.8-flash-next-apex-mini` + sidecar
  `mtp-Qwen3.8-Flash-Next-shared-Q8_0.gguf`) validated both flagged risks —
  load-time `borrow_shared_tensor` and `is_mem_shared=false` (no double-borrow
  corruption), 63.9% draft acceptance, coherent output.
- **Baseline harness in place:** `infra/llama-baseline/` params-file architecture
  (arm `090-udq5-148000-parallel1-cache-ram-16g.yml` is the compose DEFAULT PIN),
  `run-with-params.sh`, `concurrent-decode-test.sh`, `multiturn-growth-test.sh`,
  podman compose baseline stack.

This is a distinct track from ADR 0002's `Qwopus3.6` 2×RTX vanilla baseline. It
needs its own running authority so the leader can build, run, and gate
`Qwen3.8-Flash-Next` baseline work without touching Hydra production.

## Decision

Sign as **leader contract** for the `baseline-flash-next` track under **Option A
(Markdown ADR + `Signed-off-by`)** — same default path as ADR 0002.

- **Term:** standing until superseded. Revocable by removing this ADR file and
  updating `PROJECT_STATUS.md`; any merge to `main` still requires explicit user
  confirmation per `CLAUDE.md §4` (no auto-merge on CI green).
- **Authority — "running" for the `baseline-flash-next` track:**
  - **Fork / submodule:** manage `src/llama-cpp` on the `baseline-flash-next`
    lineage (`ddvnguyen/llama.cpp` branch `baseline-flash-next`); push the fork
    **before** bumping the parent pointer (per `docs/workflow/02-implement.md`);
    author/review fork PRs targeting `baseline-flash-next`. Never merge to `main`
    without explicit user confirmation.
  - **Build:** host builds for RTX (CUDA 13.2.2, `CMAKE_CUDA_ARCHITECTURES=86;120`)
    and P100 `sm_60` builds (`/opt/software/cuda/12.9`, `CMAKE_CUDA_ARCHITECTURES=60`),
    `--parallel 8`.
  - **Run:** `infra/llama-baseline/` podman compose + `run-with-params.sh` arms;
    load/run Qwen3.8-Flash-Next artifacts (production-shaped `Qwen3.8-27B-*` and
    `qwen3.8-flash-next-apex-mini` + MTP sidecar); drive the harnesses
    (`bench-baseline.sh`, `concurrent-decode-test.sh`, `multiturn-growth-test.sh`,
    `dsh` / `pi` via an OpenAI-compatible endpoint).
  - **P100:** build/deploy `sm_60` binaries and run a **bounded** smoke through the
    VM `ggml-rpc-server` (§7/§8 of `docs/arms/p100-flash-next-exploration.md`).
- **Out of authority:** Hydra **production** (`hydra-system` pod, `:8080`/`:8081`),
  the P100 production `llama-engine` (`:8086`, RPC `:9502`) and
  `ik-llama-minicpm` (`:8090`) beyond documenting results, and `main` merges.
- **Responsibilities:** keep `PROJECT_STATUS.md` in sync (milestones / verified
  facts) for this track; enforce `docs/workflow/01→07` for baseline changes; use
  the params-file architecture for new configs; monitor the baseline endpoint
  (`:18080` when side-by-side with Hydra, else the compose default).
- **Invariants (recorded; baseline intentionally bypasses Hydra specifics):**
  `one GPU = one task` (Hydra — vanilla pooling / RPC split is exempt, as in ADR
  0002); Store on tmpfs `/mnt/llm-ram` content-addressed (Hydra — baseline has no
  Store); RPC opcodes `0x40–0x46`; `n_tokens > n_past` or cache nuked; production
  `pin 130` (`Qwen3.8-27B-UD-Q5_K_S`, arch `qwen35`) is **not** the `qwen4exp`
  path and must not be conflated with this track.
- **Hardware / production safety:** P100 use requires the drain-verify → test →
  restore discipline (`docs/arms/p100-flash-next-exploration.md` §Residual risk 5);
  host GPU runs must not starve or disrupt the Hydra stack or sibling tasks.

**Signing:** `Signed-off-by: baseline-flash-next-leader <2026-09-13>` trailer on the
implementing commit (DCO-style). No GPG (Option B) unless operator opts in; no HMAC
token (Option C — rejected as over-engineered for a governance doc, per ADR 0002).

## Consequences

- Durable, versioned, reversible record of who signed what and when; `git revert`
  of this file revokes.
- ADR 0002 remains the signature for the 2×RTX `Qwopus3.6` baseline; the two
  contracts are scoped to different tracks and do not overlap.
- `PROJECT_STATUS.md` "Leader Contract" section must list both ADRs.
- `AGENTS.md` remains the bootstrap; this ADR adds no runtime secret or infra
  dependency.

## Alternatives considered

- **Extend ADR 0002 in place:** rejected — different track, model, fork lineage,
  and hardware (P100); silent edit would blur the signed scope.
- **Option B (GPG) / Option C (HMAC):** same rejection as ADR 0002 (opt-in /
  over-engineered).
- **No contract (bare `AGENTS.md`):** rejected — loses the explicit, revocable
  authority record the project uses for baseline running rights.

Ref: sign-leader-contract / baseline-flash-next
