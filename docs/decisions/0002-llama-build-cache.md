# ADR 0002 — worktree-proof llama.cpp build cache (L1 stable / L2 test)

- **Status:** Accepted (2026-08-16)
- **Branch:** `tune-llamacpp-build-cache`
- **Implementation:** `scripts/llama-build.sh` (single local build entry point)
- **Related docs:** `DevelopmentRunBook.md` → "llama-engine / llama-server — build & package"; `docs/workflow/08-llama-fork.md`; `docs/workflow/05-deploy.md`

## Context

Fork builds are the most expensive thing a coding agent does, and they used to be
almost entirely uncached:

- Every agent task runs in a **fresh git worktree**. Each worktree has its own
  `src/llama-cpp` submodule checkout, so any build directory inside it starts
  empty. A fresh worktree == a cold build.
- ccache was **not wired consistently**. The `hydra-dev` preset and CI
  (`build-combo.sh`) used it, but the manual fallback cmake blocks in the runbook
  did not — so an agent falling back to a local deploy build got **no compile
  cache at all**.
- Local ccache (`~/.cache/ccache`) and CI ccache
  (`/mnt/WorkDisk/cache/hydra-ccache`) were **separate stores**. Local agents
  never inherited the ~94%-hit-rate warmth CI had already built up, and the local
  store sat on `/` (86% full).
- CI (`hydra-build.yml` on `ddvnguyen/llama.cpp`) already keeps a persistent
  ccache on the self-hosted runner, but `actions/checkout` wipes the runner
  workspace every run, so CI still pays the full **LTO relink** every time.

Goals:

1. Cheap local verification of a fork change **in any worktree** (out of the box).
2. Safe **flag / CUDA experimentation** that can never disturb the stable/CI cache.
3. An eviction policy that protects the stable cache and reclaims space from
   experiments, choosing what to evict by **use**, not just creation time.

## Decision

Adopt `scripts/llama-build.sh` as the single local build entry point, with a
**two-tier shared-cache model**: L1 stable / L2 test, one shared ccache store with
a 15G budget, and L2-first eviction.

### 1. Cache model

```
/mnt/WorkDisk/cache/
  hydra-ccache/                       shared ccache store (also CI's store), 15G budget
      ccache.conf                     max_size = 15G
  llama-build/stable/<profile>/       L1 build dirs - persistent, never pruned
  llama-build/test/<profile>[-<variant>]/   L2 build dirs - throwaway, prunable
```

- **One shared ccache store.** Stable local builds and CI write into the same
  store, so a fresh worktree inherits CI-warmed objects and vice versa.
- **Namespaces** (ccache >= 4.10) tag entries at write time:
  - stable local builds + CI -> namespace `l1`
  - test/experiment builds -> namespace `l2`
  This is what lets eviction clear `l2` without touching `l1`.
- **Build dirs live outside the worktree.** The in-tree `build-hydra-dev`,
  `build_sm86_sm120`, `build_sm60_v2` paths are **symlinks** to the active cache
  dir, so every existing `cmake --build build-*` command keeps working. The fork's
  `.gitignore` (`/*build*/`) keeps the symlinks untracked.
- **Deployed artifact always comes from CI** (`hydra-build.yml`). Local builds are
  verification only. This is a hard rule: an experiment binary never becomes the
  deployed artifact.
- **Offline submodule init.** `ensure_submodule` reuses the shared module repo
  (`git submodule update --init --reference $GIT_COMMON_DIR/modules/src/llama-cpp -N`)
  so a fresh worktree initializes in ~3s instead of re-cloning the fork (a full
  clone is slow enough to look like a network stall; the fork is public, so it was
  not an auth prompt). Falls back to a network clone only when the module has never
  been initialized on the host.

### 2. Why ccache is the worktree-proof layer (not the build dir)

- **ccache** keys on source content + full command line + compiler version, so it
  is **path-independent**: a fresh worktree at the same submodule SHA reuses
  objects. This is the only cache that survives a worktree switch by itself.
- **Ninja build dirs** are path- and mtime-bound: a fresh checkout resets source
  mtimes, so ninja rebuilds everything even at the same SHA, and its rules embed
  the absolute source path of the worktree that configured it.
- **"Rebuild only what changed"** therefore comes from two cooperating things:
  1. **Reuse the build dir.** Ninja re-executes only the rules whose compile
     command or inputs changed; unchanged rules keep their objects.
  2. **Scope flags narrowly.** `GGML_CUDA_FA_ALL_QUANTS` is a compile-definition on
     the `ggml-cuda` target only (`ggml/src/ggml-cuda/CMakeLists.txt`), so flipping
     it recompiles just that target. A **global** flag change (arch, `-O`, nvcc
     version) inherently recompiles every affected TU — no build framework soundly
     reuses a TU compiled with old global flags, and none should.

### 3. Configure-on-change

Each build dir carries a `build.meta` file recording the exact inputs:

```
sha=<submodule HEAD>
source=<source path it was configured from>
tier=stable|test   profile=<profile>   cuda=<version>
target=<target>    flags=<full cmake arg list>    built_at=<timestamp>
```

`llama-build.sh` reconfigures **only when** the requested submodule SHA, tier,
CUDA version, target, and full flag list differ from `build.meta` (or the recorded
source path no longer exists). Identical inputs -> reuse the dir and let ninja do
its incremental thing. Any change -> reconfigure in place.

### 4. Eviction: shared 15G budget, L2 first, last-use not creation time

ccache 4.10.2 exposes a global `max_size` but **no per-namespace size limit**
(verified with `ccache -p`), so the wrapper enforces the shared budget after every
build and on `prune`:

1. Sum the whole store (`du -sb`). If <= 15G, done.
2. Over budget -> **clear namespace `l2` first** (`ccache --evict-namespace l2`).
   Experiments are always the first eviction victim.
3. Still over -> evict least-used entries store-wide, ordered by **last access/use**
   = `max(atime, mtime)` from `stat` (ccache touches entry files on use, so mtime
   reflects use). Creation time is deliberately not the primary signal.

`prune` also drops L2 build dirs that are not the current active symlink target;
`--clear-l2` nukes the whole L2 namespace and L2 build dirs. Prune never touches
L1 or the active target.

## Consequences

**Positive**

- Fresh-worktree verification is cheap: local compiles hit the CI-warmed store;
  L1 build dirs persist like a never-wiped CI workspace; a same-flags rebuild
  skips configure entirely.
- Flag / CUDA experiments live in L2 and can never evict or dirty L1/CI.
- Auditable: `build.meta` + `llama-build.sh list` show exactly what any cached
  binary was built with (flags, CUDA, SHA, target, timestamp).
- All three build paths (CI, iteration, deploy-flags) share one warm ccache pool.

**Costs / follow-ups**

- ccache >= 4.10 required for `--evict-namespace` (host has 4.10.2).
- CI sets `CCACHE_MAXSIZE=10G` per job in `hydra-build.yml` (fork-side), which
  overrides the 15G store config during CI runs. Aligning CI's cap to 15G is a
  **fork PR**, out of scope here.
- A shared build dir serializes concurrent agents building the same profile
  (single `flock`). Acceptable on a single-host setup.
- Reuse assumes a **clean** submodule checkout; a dirty checkout's objects could
  leak into a later worktree's build. The SHA check mitigates, and agents are
  told to build from committed state.

## Alternatives considered

- **CCACHE_LOGFILE per-entry hit-counts** (usage *frequency*, not just recency).
  Rejected: the log format is not a stable API, adds a parser, and the
  filesystem's last-access metadata already satisfies the requirement.
- **SHA-keyed build dirs** (one dir per submodule SHA). Rejected: a submodule
  bump would always start cold; L1 fixed-path + ninja incremental reuse is better.
- **Two separate ccache stores with independent caps.** Rejected: the requirement
  was a *shared* 15G budget with L2 as the eviction victim; a single store +
  namespaces delivers exactly that.
- **Thin LTO** (`-flto=thin`) to speed relinks. Rejected: clang-only, this is gcc.
  Full IPO stays for deploy (decode perf is an existing locked decision); `dev`
  iteration builds skip IPO.
- **Bazel / Buck remote caching.** Rejected: configuration-granular (any config
  change cascades to everything downstream), a full build-system migration, and no
  better than ccache + reused ninja dirs for this single-host C++/CUDA case.
- **sccache.** Same whole-TU caching model as ccache; adds nothing on a single host.

## References

- `DevelopmentRunBook.md` -> "llama-engine / llama-server — build & package" (usage)
- `docs/workflow/08-llama-fork.md` -> "Verify a fork change builds" (agent flow)
- `docs/workflow/05-deploy.md` -> "llama.cpp fork change" (deploy path: always CI)
- `scripts/llama-build.sh` (implementation)