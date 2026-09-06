# ADR 0002 — Leader contract: human ↔ muse-spark-1.2-contributor (baseline running)

- **Status:** Accepted (2026-08-21)
- **Context PRs:** `baseline-dual-rtx-llamacpp-dsh` branch (based on `dab35bae3` post #677), `docs/decisions/002-supersede-hermes-fleet-contract.md` (history `f8b322c73`), `AGENTS.md` current operating model
- **Supersedes:** the already-superseded v2.1.1 hermes fleet contract (`deepseek-v4-flash-only`, 5-min tick, `hermes-lead-template` refs) — this ADR does not restore it
- **Parties:** human `ddvnguyen` ↔ agent `muse-spark-1.2-contributor` (opt-in contributor provider per `AGENTS.md` delegate models)
- **Scope:** `llama.cpp baseline` on 2×RTX (5060 Ti sm_120 + 3060 sm_86) — running authority for the baseline stack only

## Context

The hermes fleet contract was superseded on 2026-08-21 (see history `f8b322c73:002-supersede-hermes-fleet-contract.md`): its referenced files (`orchestration/hermes-lead-template/`, `Lead.goals.md`, `Lessons.md`) never existed on disk and its model constraint (`deepseek-v4-flash-only`) had already been overridden by real delegation (`hy3` default through 2026-08-30, `mimo-v2.5` fallback, `muse-spark-1.2-contributor` opt-in). The durable operating model is `AGENTS.md` (Claude as leader, Paseo delegates, model chosen per task, zero-trust verification, 30-min heartbeat) + `CLAUDE.md` (§Architecture, §Task Lifecycle 01→07, §4 Merging requires explicit user confirmation) and `PROJECT_STATUS.md` single source of truth.

Charter is `CLAUDE.md` + `AGENTS.md`; where `orchestration/LEAD_CHARTER.md` / `GOALS.md` / `ARCHITECTURE.md` exist (legacy worktrees) they are read-only. This worktree (`majestic-toad`) has no such files on `dab35bae3` — `CLAUDE.md` is the charter.

A baseline is needed: bare-bone upstream `ggml-org/llama.cpp` `llama-server` on the two local RTX cards, in a container, hooked to a harness (`dsh` / `pi`) for metrics, before Hydra-specific COMBINED/P-D logic.

## Decision

Sign as **leader contract** under **Option A (Markdown ADR + Signed-off-by)** — the default recommended path.

- **Term:** standing until superseded. Revocable by removing this ADR file and updating `PROJECT_STATUS.md`; any merge to `main` still requires explicit user confirmation per `CLAUDE.md §4` (no auto-merge on CI green).
- **Authority — "running" for baseline:** plan / route / gate and **run** the baseline stack. Leader may: switch `src/llama-cpp` submodule to upstream `ggml-org/llama.cpp`, build locally with `--parallel 8`, create/run `infra/llama-baseline/` container (`podman compose -f infra/llama-baseline/docker-compose.baseline.yml up`), manage `--ctx-size 98304 → 65536` fallback (yarn RoPE `scale 4`, `yarn-orig-ctx 32768`, `cache-type-k/v q8_0`, `flash-attn on`), and drive harness runs (`dsh` / `pi` via `OPENAI_BASE_URL=http://localhost:8080/v1`). No authority over Hydra production deploy (hydra-system pod, P100 VM `192.168.122.21`, Grafana :3000 alerts) beyond documenting baseline results.
- **Responsibilities:** keep `PROJECT_STATUS.md` in sync (milestones / verified facts), enforce `docs/workflow/01→07` for baseline changes, monitor baseline endpoint (`:8080` or `:18080` if side-by-side with Hydra).
- **Invariants (recorded, baseline bypasses Hydra specifics):** `one GPU = one task` (Hydra invariant — vanilla `llama-server` pooling uses one process across both GPUs via `tensor_split`, so this invariant is intentionally not enforced for the bare-bone baseline), Store on tmpfs `/mnt/llm-ram` content-addressed (Hydra — baseline has no Store), RPC opcodes `0x40–0x46`, `n_tokens > n_past` or cache nuked, COMBINED `moe` (expert-split) / `dense` (layer-split) via `scripts/set-profile.sh {moe|dense}` (baseline uses upstream `--split-mode layer --tensor-split 25,40` instead of fork `--combined-*` flags).

**Signing:** `Signed-off-by: muse-spark-1.2-contributor <2026-08-21>` trailer on the implementing commit (DCO-style). No GPG (Option B) unless operator opts in; no HMAC token (Option C — rejected as over-engineered for governance doc).

## Consequences

- Durable, versioned, reversible record of who signed what and when; `git revert` of this file revokes.
- `AGENTS.md` remains the bootstrap; this ADR is the signature — no duplication.
- Baseline work proceeds under this contract; if a different orchestration or deploy model is wanted later, supersede via new ADR (not silent edit).
- No runtime secret or infra dependency added.

## Alternatives considered

- **Option B (GPG signed commit/tag + detached .asc):** stronger cryptographic non-repudiation, but requires local GPG key and distribution with no existing infra — opt-in only.
- **Option C (HMAC token like `.hydra-head-token`):** over-engineered for a governance doc; appropriate for runtime auth, not this.
- **Re-adopt hermes fleet:** rejected — superseded per `002-supersede`, files never existed, model constraint already overridden in practice.

## Baseline annex — 2×RTX vanilla `llama.cpp`

- **Hardware:** 5060 Ti sm_120 16GB `01:00.0` + 3060 sm_86 12GB `02:00.0`, driver `595.84` CUDA 13.2, toolkit `/opt/software/cuda/13.2`.
- **Model:** `Qwopus3.6-27B-Coder-Compat-MTP-Q5_K_M.gguf` 19.5GB (`/mnt/WorkDisk/LLM-Models` + `/mnt/SSD` mirror) — does not fit solo.
- **Pooling:** single `llama-server` with `CUDA_VISIBLE_DEVICES=0,1`, `devices: [nvidia.com/gpu=all]`, `--n-gpu-layers 65 --tensor-split 25,40 --split-mode layer` (stock upstream, mirrors Hydra `models.json: dense-27b-combined 65/[25,40]/layer`; not fork `--combined-*`).
- **Ctx:** try `98304` first (yarn 4× over `32768`), fallback `65536` on `CUDA OOM` — both `q8_0` KV, `flash_attn on`.
- **Harness hook:** `OPENAI_BASE_URL=http://localhost:8080/v1` (or `:18080` side-by-side), `OPENAI_API_KEY=dummy`; `infra/llama-baseline/bench-baseline.sh` captures TTFT/TPOT/prefill vs `tests/bench/baselines/`.
- **Build:** host `cmake -S src/llama-cpp -B src/llama-cpp/build -DGGML_CUDA=1 -DLLAMA_CURL=ON && cmake --build src/llama-cpp/build --parallel 8` (CUDA 13.2).

Ref: sign-leader-contract / baseline-dual-rtx-llamacpp-dsh
