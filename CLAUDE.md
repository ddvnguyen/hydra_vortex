# Hydra — Claude Handoff

## What Is This
Multi-GPU LLM inference system. Routes requests across **RTX 5060 Ti** (sm_120,
host), **RTX 3060** (sm_86, host — added in PR #373), and **Tesla P100** (sm_60,
KVM VM). All Hydra services run as containers on the host; Hydra.Core is the
single C# binary; only llama-server P100 lives in a KVM VM (192.168.122.21:8086).
Migrates ~800 MB KV cache state between GPUs without re-prefill, and the
2-GPU same-host pair (5060 Ti + 3060) is wired for the COMBINED engine
mode (expert-split, see "COMBINED engine mode" below).

## Read These First
1. `PROJECT_PLAN.md` — vision, structure, milestones (10 min)
2. `docs/architecture.md` — implemented design: routing, run modes, session lifecycle,
   chunked dedup, prefix checkpoints, n_past guard (10 min)
3. `specs/rpc-protocol.md` — binary wire format + all opcodes (5 min)
4. `## Task Lifecycle` (below) + `docs/workflow/` — how to work a task end-to-end
5. Active milestone: **Llama-Engine — P/D split mix-quant** (M-Perf done); see
   `docs/milestone-perf.md` for the completed perf track + `DevelopmentRunBook.md` for
   build/run/test. Live board: GitHub Project (`docs/GITHUB_PROJECT_SETUP.md`).

## Architecture
Client → Hydra.Core :9000 [C#] → Hydra Head [Go] (one per GPU node, RTX 5060 Ti +
RTX 3060 in containers, P100 as VM systemd) → llama-engine [C++ fork] per node →
Hydra Store RPC :9500 + tmpfs. The 3060 also exposes a ggml-RPC peer on `:9504`
for COMBINED mode. Full topology diagrams: `docs/diagrams.md`; full reference
(routing, run modes, session lifecycle, chunked dedup): `docs/architecture.md`.

The 5060 Ti + 3060 are **same-host** (CUDA0 + CUDA1) and run in the same
`pod_hydra-system`. The P100 is a **separate KVM VM**. Cross-host RPC
goes through the Hydra Store on the host; same-host RPC is direct.

## Language Decisions (FINAL — do not change)
| Service      | Language  | Reason                                      |
|--------------|-----------|---------------------------------------------|
| Hydra.Core   | C# .NET 10| System.IO.Pipelines, Socket.SendFileAsync   |
| Hydra Head   | Go        | Single binary, process management, OCI pull |
| llama-engine | C++ (fork)| Same binary as llama-server, +3 streaming state endpoints + COMBINED-mode filter for `--rpc-engine` / `--combined-ot-pattern` / `--ggml-rpc-port` (the COMBINED engine mode is only in `llama-engine`, NOT in `llama-server`) |

## Critical Facts (POC verified)
- P100 prefill: 110 tok/s → 80K context = 12 minutes. RTX handles large prefill.
- P100 decode: 28 tok/s — acceptable.
- RTX 5060 Ti: ~200 tok/s decode (Q3_K-mini 35B-A3B, mmap). SOLO primary.
- RTX 3060: ~60 tok/s decode (Q3_K-mini 35B-A3B, mmap). SOLO + COMBINED peer.
- Cross-GPU save/restore: WORKS. cache_n=2964 after restore. P/D split verified
  end-to-end 2026-07-01 with NIAH-8000: RTX prefill +3590, P100 KV restore +4,
  P100 decode +119.
- Prompt-cache reuse: FIXED for qwen35moe via the fork patch (recurrent/hybrid context
  checkpoints, port of ik_llama.cpp#1762). Follow-up turns now reuse cached KV
  (`restored context checkpoint`) instead of full re-prefill — verified live 2026-06-04
  (turn-2 cached_tokens 1229/1251). Was: "SSM truncation BROKEN; --cache-prompt useless."
- n_tokens MUST be > n_past or cache is nuked. Coordinator must guard this.
- KV state at 60-80K context: ~800 MB.

## COMBINED engine mode (2-GPU 5060 Ti + 3060)
The same-host pair can act as **one logical engine** in two modes: **COMBINED-OT**
(expert-split, FFN tensors routed to the peer's ggml-RPC backend, MoE profile default)
and **COMBINED-static** (layer-split via `--combined-split-mode layer`, registers the
peer device before model load, Dense profile — avoids recurrent-layer state corruption
across the RPC boundary that COMBINED-OT has). Switch profiles with
`bash scripts/set-profile.sh {moe|dense}`. Full mode details, env vars, and the
moe/dense profile table: `docs/combined-engine-mode.md`.

## Hydra Head (Go node agent)
Single Go binary per GPU node (`src/head/`) managing 4 sub-services: llama-server,
node_exporter, nvidia_exporter, promtail — replaces the old Agent containers. The
llama-engine binary is pulled from ghcr.io via `crane`; the RTX 5060 Ti + 3060 share
one fat image (`llama-server:sm86-sm120-engine`), P100 uses a separate sm_60 image.
Deploy: `scripts/deploy-hydra-head.sh`. Full source map, service config, OCI images,
and deprecated-infra table: `docs/hydra-head.md`.

## llama.cpp Fork (hydra-state-streaming branch)
Three new endpoints added to tools/server/server.cpp:
- GET /slots/{id}/state      → stream binary KV state out
- PUT /slots/{id}/state      → stream binary KV state in
- GET /slots/{id}/state/meta → metadata (n_past, state_size)

These eliminate disk round-trips. Hydra.Core pipes stream directly llama↔Store.
Without these patches, nothing else in the system makes sense.
Build RTX: GGML_CUDA_FORCE_CUBLAS=ON, sm_120. Build P100: sm_60.

## Milestones
Core M0–M2 built; **M-Perf done** (2026-06). Roadmap **restructured 2026-06** around the
Tier-1 performance track; with M-Perf complete the active track is now **Llama-Engine —
P/D split mix-quant**. M3/M4/M5 are kept but reframed as a later **Production phase** (not
active now). Tracked in the GitHub Project "Hydra Vortex" + native Milestones
(`docs/GITHUB_PROJECT_SETUP.md`); detail in `docs/milestone-*.md`.

| MS          | Goal                                                       | Status  |
|-------------|------------------------------------------------------------|---------|
| M0          | llama fork + Store + Agent + System test                   | ✅ done  |
| M1          | Coordinator + routing + session + migration                | ✅ done  |
| M2          | Chunked dedup + prefix checkpoints                         | ✅ done  |
| Phase 0     | Stabilize: green CI, restore obs, rebase onto remote       | ✅ done  |
| M-Perf      | Heterogeneous perf: spec-decode → P/D streaming → pipeline | ✅ done  |
| Llama-Engine| **P/D split mix-quant** (RTX precise prefill / P100 quant decode, worker policy, pipelined prefill, dynamic quant swap) | ▶ now   |
| M3          | Persistence (NVMe write-behind, **C# re-spec**) + obs harden | Production (later) |
| M4          | Model mgmt & multi-modal (dist, dynamic load, vision/…)    | Production (later) |
| M5          | LLM obs & agentic (Langfuse, A/B testing, agentic)         | Production (later) |
| Phase 5     | Semantic KV: KV DAG + git-aware prefix cache (#107)        | planned |

Phase 5 (Store v2 "Semantic KV", #107) design: `docs/kv-dag-architecture.md` (KV DAG, git-aware
reuse, content-defined chunking; quantization excluded), decomposed as issues #107-A … #107-I.

## Task Lifecycle (MANDATORY)
Every unit of work follows this loop. Each step's detail is in `docs/workflow/` —
**open the linked doc when you reach that step**. **GitHub Projects is the single
source of truth** (issues = work items, PRs link via `Closes #N`, board status is
automatic — no cross-linking). Commands live in `DevelopmentRunBook.md`.

1. **Pick up** — choose from the **GitHub Project board** (`gh project item-list` /
   GitHub MCP), filtered by Milestone (currently Llama-Engine — P/D split mix-quant), or
   `gh issue list --label review-finding --state open`; set the item's Status →
   In Progress. → `docs/workflow/01-pickup.md`
2. **Branch & implement** — never on `main`; `fix/…` from the issue or `feat/…`;
   follow the milestone doc. → `docs/workflow/02-implement.md`
3. **Test / verify** — unit (`dotnet test src/core/Tests.Shared/ && dotnet test src/core/Tests.Core/`) + E2E
   (`pytest tests/system`) green before PR. **"E2E verify" means deploying the
   current working tree to the live env and confirming behavior — it is never
   an instruction to merge the PR.** → `docs/workflow/03-test-verify.md`
4. **Commit & PR** — conventional commits + `Co-Authored-By`; `gh pr create …
   Closes #N` (this link auto-moves the Project item). **Merging a PR always
   requires the user's explicit confirmation or explicit request — never merge
   as a side effect of a verify/test request.** → `docs/workflow/04-commit-pr.md`
5. **Deploy** (if runtime/fork) — build sm_120/sm_60; push the fork + bump the
   `src/llama-cpp` submodule pointer. → `docs/workflow/05-deploy.md`
6. **Check monitoring** — Grafana :3000 + alerts; no regressions.
   → `docs/workflow/06-monitoring.md`
7. **Issue + close-out** — new problem → `gh issue create --label review-finding`
   (auto-added to the Project); finished item's Status → Done (auto on PR-merge/close).
   → `docs/workflow/07-issue-and-close.md`

## GitHub Workflow (MANDATORY for all coding agents)

The full development cycle: **feature → issue → implement → review → merge → deploy → monitoring → (problem → issue)**

### Findings → Issues
Review findings are tracked **directly as GitHub issues** labelled `review-finding`
(grouped per milestone, e.g. `[M2] …`). There is **no** `reviews/` markdown tree,
`sync_reviews_to_github.py`, or `reviews.yml` — those were removed. File findings
with `gh issue create --label review-finding`; list them with
`gh issue list --label review-finding --state open`.

Title convention: `[M{n}] short title`, or `[M{n}-P{sev}-{seq}]` for a specific
finding. P0 = correctness/data-loss, P1 = behavioural bug, P2 = minor/perf.

### Fix → Branch → PR
1. Pick the finding: `gh issue list --label review-finding --state open`
2. Branch from it: `gh issue develop N --name fix/mN-Psev-seq`
3. Implement the fix.
4. Open the PR: `gh pr create --title "fix: [MN-Psev-seq] short title" --body "Closes #N"`

### Monitoring issues
Auto-created by `monitor.yml` (Prometheus alerts) and `ci.yml` failure handlers.
Do not manually close a monitoring issue without investigating the root cause.

## Planning (GitHub Projects)
Roadmap/planning lives in the **GitHub Project v2 "Hydra Vortex"** (same repo as code).
Milestones = native GitHub **Milestones** (`Phase 0`, `M-Perf`, `Llama-Engine — P/D split
mix-quant`, `M3`, `M4`, `M5`);
work items = **issues** with Status / Priority fields on the board. PRs link to issues
via `Closes #N`; built-in workflows auto-add items and set **Status → Done** on
merge/close — **no manual cross-linking**. Drive it with `gh project` / `gh issue`
(Bash) or the **GitHub MCP** (configured in `.mcp.json` / `opencode.json`). Board
layout + setup: `docs/GITHUB_PROJECT_SETUP.md`.


## Starting Point
Core M0–M2 and **M-Perf are done**. Start from the **GitHub Project board**, filtered to
the active Milestone (currently **Llama-Engine — P/D split mix-quant**), and follow the
**Task Lifecycle** above. M3/M4/M5 are deferred to a later Production phase. Build/run/test
commands are in `DevelopmentRunBook.md`.

## Key Design Decisions (do not relitigate)
- **One GPU = one compute task at a time** (invariant). "Dual-role" (SOLO /
  COMBINED-peer) is a *capability the engine switches between*, NOT two workloads
  running on one GPU at once. Concurrency exclusivity is a **Hydra Core scheduling
  guarantee** (only borrow a *free* peer GPU for COMBINED), not a low-level lock —
  this dissolves the #21 race class by construction. See `docs/architecture-principles.md` (P1–P3).
- **Plan bold**: prefer architecture-level solutions over local patches; accept
  architectural change when the long-term net is positive; judge every decision
  against the roadmap (layer swap, single-GPU P/D mix-quant). See `docs/architecture-principles.md` (P4–P5).
- No Ray until possible M4+ (2 nodes, not needed)
- Store backed by tmpfs not S3/MinIO (sendfile + zero-copy)
- Full KV state only (delta export impossible — SSM truncation broken)
- Content-addressed chunking at Store level, not llama.cpp level (M2)
- No shared filesystem between nodes (Hydra Store RPC replaces NFS/virtiofs)
- llama.cpp fork minimal: only 3 endpoints in server.cpp, no core changes
- Hydra Head in Go: single binary per GPU node, 4-service management (llama + exporters + promtail)
- llama-server distributed via OCI registry (ghcr.io), pulled at startup — no shared mounts
- 2-layer YAML config for hydra-head: global.yaml + per-node overrides

## Hardware
- RTX 5060 Ti 16 GB sm_120, CUDA 13.2 — host machine, i7-12700K, 64 GB
  (CUDA0 — primary; COMBINED-mode head when activated)
- RTX 3060 12 GB sm_86, CUDA 13.2 — same host, i7-12700K, 64 GB
  (CUDA1 — added in PR #373; SOLO decode worker + COMBINED peer of the
  5060 Ti; exposes its ggml-RPC backend on :9504)
- Tesla P100 16 GB sm_60, CUDA 12.9 — KVM VM at 192.168.122.21
  (llama-engine only; cross-quant decode peer of the 5060 Ti in the
  legacy P/D split path; model: Q5_K-balanced 35B-A3B)
- tmpfs 30 GB at /mnt/llm-ram (compose-managed inside Store container)
- Model: Qwopus3.6-35B-A3B-v1-APEX-MTP-I-Balanced.gguf (qwen35moe arch, MTP
  spec-decode, vision mmproj). Same-host GPUs load the Q3_K-mini quant
  (12 GB fits on the 3060); P100 loads the Q5_K-balanced quant.
  Cross-quant P/D split is gated on `HYDRA_COORD_ALLOW_CROSS_MODEL_KV_REUSE=true`.

## Monitoring & Observability
Prometheus + Loki + Grafana + Promtail run as Quadlet systemd user services;
Hydra services also run via podman compose. Grafana :3000, Prometheus :9091,
Loki :3100. Start everything: `bash scripts/start-env.sh` (or
`start-infra.sh` / `start-hydra.sh` / `deploy-hydra-head.sh all` individually).
Full metrics endpoints, log pipeline, alert rules, and dashboard panel list:
`docs/monitoring-observability.md`.

## Coding Agent Rules
1. **Ask for decisions** via the `question`/`AskUserQuestion` tool when there are
   multiple viable options — don't pick silently.
2. **Track tasks** with `todowrite`/`TaskCreate` always, one `in_progress` at a time.
3. **Use sub-agents aggressively** (2-3 in parallel) for research or multi-file work
   that would take >30s serially — not for trivial single-file edits.
4. **End with a final result block** (`---` + summary) after completing work.

Full rationale, examples, and tool-name mapping: `docs/agent-coding-rules.md`.
