# 2. Branch & implement

**Goal:** make the change on a branch, never on `main`.

1. **Branch:**
   - From a GitHub finding: `gh issue develop N --name fix/mN-Psev-seq`
     (e.g. `fix/m2-p2-060`).
   - Other work: `git checkout -b feat/<area>-<short>` (or `chore/…`, `docs/…`,
     `perf/…`) off the latest `main`.
2. **Scope:** follow the milestone doc for the area —
   `docs/milestone-perf.md`, `docs/milestone-3-production.md`, etc. Respect the
   `## Language Decisions`, `## Critical Facts`, and `## Key Design Decisions` in
   `CLAUDE.md` (do not relitigate them).
3. **Build / run while developing:** see `DevelopmentRunBook.md`
   ("Quick Start", "Running Tests", "llama-server build").
4. **Track sub-steps with todos**; keep exactly one in-progress.

→ Next: `03-test-verify.md`
