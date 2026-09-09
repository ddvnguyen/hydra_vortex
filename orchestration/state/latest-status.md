# latest-status — rolling handoff

> **Updated: 2026-09-09 by W2a** (epic #697, lane family t-8d42fb7e63, per #750).
> Distilled from `PROJECT_STATUS.md` + open PR/issue posture. This is the
> ephemeral/session layer: update at session boundaries; durable decisions
> belong in `docs/decisions/`. Supersedes the 2026-08-27 session content
> that previously lived here (Track A / PR #695 re-baseline narrative — its
> outcomes are now reflected in `PROJECT_STATUS.md`).

## Milestone state

| MS | Status | Note |
|---|---|---|
| M0–M2 | ✅ done | core + routing + chunked dedup / prefix checkpoints |
| M-Perf | ✅ done | spec-decode → P/D streaming → pipeline (Tier-1) |
| **Llama-Engine (#470 merged-decode)** | **▶ now — deploy hold** | Phases 1–4 + 4.x landed; **Phase 5 (v3 segmented framing, real SSE) in progress**; Phase 6 E2E soak pending. `merged_decode` is advertised while the engine still rejects `prompt.messages` — do not deploy this engine build live. |
| M3–M5 | later | persistence re-spec, model mgmt, agentic obs |

Working branch: **`epic/697-final-verify`** — final-verify integration branch
for the #470/#591 lineage before `main`.

## Open PR posture (2026-09-09)

Into `epic/697-final-verify`:
- **#732** — #712 solo/cold route reuses session prefix KV
- **#731** — #713 recv-side EAGAIN retry + slot quarantine for KV restore
- **#739** — #738 debounce capability clearing (2 consecutive empty-cap polls)
- **#711** — failing repro + fix design for cold-atomic self-lease decode deadlock

Epic → main:
- **#707** — `epic/697-final-verify → main` (integrates #695 V2 goldens + minifleet evidence); awaits the four PRs above + final E2E/live-GPU verify.

Other lanes (not #697-gated):
- **#695** — v2 worker scheduler rewrite → `main`; re-baselined, 652/652 Tests.Core green, awaits owner CONFIRM (decision 6 — absolute gate).
- **#696** — `epic/470-merged-decode` → `main` (deploy hold, see above).
- #745/#748/#749 — #703 baseline-concurrency arms on baseline branches.

## Key in-flight facts

- Scheduler v2: 13/13 differential scenarios byte-match legacy goldens; toggle
  `HYDRA_SCHEDULER_IMPL=legacy|v2`, default still **legacy**.
- #718 warm-slot fast path merged into the epic branch (PR #719), not yet
  live-GPU verified.
- Dense-27B COMBINED: swap fixed (#537), decode still blocked by fork
  KV-restore (#78).
- P100 cold-expert mmap tax fixed (`no-mmap`, #470 item 3); Multiturn40kContext
  live rig 13 min PASS.

## Docs state (this PR, #750)

- `docs/cookbook/` operational recipes landed: `build.md`, `deploy.md`,
  `test-lane.md`, `monitoring.md` (plus the earlier `reconcile-epic-branch.md`,
  `paseo-delegate-and-verify.md`).
- `docs/decisions/`: added **003** (KV store on tmpfs, content-addressed, no
  shared FS). ADR assessment: 0001 (head/Go) + 001/002 (process) + 003 cover
  the "Key Design Decisions (do not relitigate)" list in CLAUDE.md — the
  remaining bullets (one-GPU-one-task invariant, llama-server via OCI) are
  invariants or already subsumed by 0001, not new ADRs.

## Next steps

1. Land #732/#731/#739/#711 into the epic branch (CI-green, docs+code).
2. Final E2E/live-GPU verify on the rig, then merge #707 → main (needs explicit
   user confirmation per CLAUDE.md).
3. #470 Phase 5 → Phase 6 soak to lift the deploy hold.
4. #695 owner CONFIRM is the blocking gate for the v2 default flip.
