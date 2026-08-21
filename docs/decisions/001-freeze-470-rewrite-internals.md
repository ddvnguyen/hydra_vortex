# Decision 001 — Freeze #470 contract, rewrite internals

## Problem

epic/470-merged-decode has been in flight for 4+ weeks. The fix:feat commit ratio is
37:1 — nearly all activity is repair, not new capability. Deploy-hold is still in effect.
The root cause is architectural: WorkerSchedulerService is a 4,723-line god-class and
`server-context.cpp` is ad hoc Hydra-specific code sitting inside upstream llama.cpp
sources, making every fix create new coupling. Continuing to patch this directly is
exactly what produced the 37:1 ratio.

## Decision

1. **Freeze the Hydra ↔ fork API and streaming contract** — no changes to RPC opcodes
   (`STATE_PUT`/`DECODE` framing), SSE streaming wire format, or the engine-mode
   switching interface.
2. **Two internal rewrites in parallel**, each on its own branch, each validated by a
   differential-parity harness before merge:
   - **PR #695** (`epic/591-rewrite-worker-scheduler` → `main`) — Hydra side. Replaces
     `WorkerSchedulerService` with `WorkerSchedulerV2` (event-driven state machine,
     `Hydra.StateMachine` DSL, `Hydra.Core.Scheduling` primitives). Gated by
     `HYDRA_SCHEDULER_IMPL` A/B toggle.
   - **`epic/610-server-hydra-extension`** — fork side. Extracts Hydra-specific server
     logic out of upstream `server-context.cpp` into fork-owned `hydra-server-context.cpp`
     behind a `server_hydra_extension` interface and `HYDRA_EXT_MODE` A/B toggle.
3. **Fold into the stabilization workstream (issue #697):**
   - **#469** — verification only (fork fix already landed via fork PR #60). A
     prerequisite for trusting #695's golden-trace baseline.
   - **#641** — fork-side fix. Confirmed by investigation to NOT be the same bug as
     #469 or the PR #695 parity mismatches. Fix via the `epic/610` rewrite.
4. **Freeze #514, #78, #95** until the rewrites land — no P0 flag, not blocking either
   rewrite, continuing to chase them now repeats the patching pattern.

## Alternatives considered

- **Keep patching epic/470 directly.** Rejected — this is exactly what produced the
  37:1 ratio. Further patches add coupling inside the same unstable surface.
- **Sequence the two rewrites instead of running them in parallel.** The user chose
  parallel: the two efforts live in separate repos and require separate agent attention.
  Sequential would halve throughput with no safety gain since neither rewrite touches the
  other's surface area.

## Consequences

- No new branches off `epic/470-merged-decode` itself except #697 stabilization work.
- Both rewrites must go through `docs/workflow/09-epic-branch.md`'s close-out gate with
  no CI-bypass exceptions. A past violation (PR #666, merged with failing Build & Test)
  is why this is called out explicitly.
- PR #695 is currently blocked on failing Build & Test CI and 3 of 16 parity scenarios
  (`warm_affinity_on`, `warm_affinity_verify_on`, `migration`) that don't yet
  byte-match. The fix for those scenarios is explicitly treated as the fix for #641.
- `epic/610` is 10 days stale and needs rebase (45 commits behind `hydra-fork`) and CI
  configuration before it can be validated.

Ref: #470, #697
