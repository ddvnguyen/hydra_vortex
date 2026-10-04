# Colibri ledger re-check — `epic/atlas-v2-colibri-1120` + fork `feat/flash-next-colibri`

**Date:** 2026-10-03 · **Epic:** #811 (Strata gap analysis) · **Status:** READ-ONLY state check
(no merge, no push to an existing branch, no build, no GPU, no `gh workflow run`, nothing written to `/tmp`).

Ledger line being re-checked (2026-09-25):
> "Owner directive: merge Colibri upstream work (epic/atlas-v2-colibri-1120 Atlas V2 port;
> fork branch feat/flash-next-colibri) and deploy atlas-web when finished."

Since then the track pivoted: nine docs/evidence PRs (#812–#822) landed on
`epic/811-strata-gap-analysis` and Option C (`docs/evidence/strata-gap/OPTION-C-DESIGN.md`,
PR #821) became the port plan. This note records what the two named branches actually are
today, what "deploy atlas-web" means, how Option C would collide with them, and the smallest
safe sequence.

**Heads measured at** `origin/epic/811-strata-gap-analysis = 9b2a8df54` (it advanced from
`2b0c8a64c` → `9b2a8df54` *during* this check, i.e. another agent is pushing to the epic branch
right now — every number below is a point-in-time sample).

---

## 1. Parent repo — `epic/atlas-v2-colibri-1120`

### 1.1 Does it exist on `origin`? **No.**

```console
$ git ls-remote --heads origin epic/atlas-v2-colibri-1120
(no output)
$ gh api repos/ddvnguyen/hydra_vortex/branches/epic/atlas-v2-colibri-1120
gh: Branch not found (HTTP 404)
$ gh pr list --state all --head  epic/atlas-v2-colibri-1120   → []
$ gh pr list --state all --base epic/atlas-v2-colibri-1120   → []
$ gh run list --branch epic/atlas-v2-colibri-1120            → []
```

The branch exists **only as a local branch** in this clone, at
`3479fd97710306fa9d3523b9159b77a25089667f`
(`2026-09-24 23:55 +0700  docs(atlas-web): pin upstream 9d5d05de7f v1.12.0 and record provenance`).
It has never been pushed, has no PR (open *or* closed) and has never had a CI run.

The branch the ledger almost certainly means is its **published successor**:

```console
$ git ls-remote --heads origin epic/atlas-v2-colibri-1120-rebuild
b1a5278c93de69a7d870c32cf2264affb2ca208e  refs/heads/epic/atlas-v2-colibri-1120-rebuild
```

`b1a5278c9 = fix(atlas-web): architect round-1 findings on F4c+F5 (B1, B2, N1-N3)`, open as
**PR #803 → `epic/flash-next-colibri-p100`**.

### 1.2 Ahead / behind

| branch (sha) | vs `origin/main` (`96d38613`) | vs `origin/epic/811-strata-gap-analysis` (`9b2a8df54`) |
|---|---|---|
| `epic/atlas-v2-colibri-1120` **local** `3479fd977` | **behind 0, ahead 2730** | **behind 36, ahead 2446** |
| `origin/epic/atlas-v2-colibri-1120-rebuild` `b1a5278c9` | behind 0, ahead 294 | behind 48, ahead 22 |
| `origin/feat/flash-next-colibri` `21e858990` | behind 0, ahead 286 | behind 48, ahead 14 |

`git rev-list --left-right --count A...B`, left = base-only.

The "ahead 2730" is not 2730 edits: merge-base with `main` **is** `origin/main`
(`96d38613`, 2026-09-06) and `5542d1d7a merge: upstream-colibri v1.12.0 @ 9d5d05de7f into
epic/atlas-v2-colibri-1120` folded the whole `JustVugg/colibri` repository history in.
Content-wise the local branch differs from `main` by **139 paths**, and from
`origin/epic/atlas-v2-colibri-1120-rebuild` by only **37 paths** (962 insertions, 226 170
deletions — the deletions are almost entirely `docs/leader-handoff-state.md`).

### 1.3 Mergeability dry-run (`git merge-tree`, nothing merged)

```console
$ git merge-tree --write-tree --name-only origin/epic/811-strata-gap-analysis epic/atlas-v2-colibri-1120
7325a67584b2529f2d393a06c1289ee09156a848      # merge tree produced
PROJECT_STATUS.md                              # ← ONLY conflict
CONFLICT (content): Merge conflict in PROJECT_STATUS.md
Auto-merging atlas-web/src/Galaxy.tsx
rc=1

$ git merge-tree --write-tree --name-only origin/epic/811-strata-gap-analysis origin/epic/atlas-v2-colibri-1120-rebuild
fe54b130811ce1de43af3acc8e9ecc966c9211b2
PROJECT_STATUS.md
docs/PORTS_AND_ENV.md
src/llama-cpp                                   # ← submodule conflict
CONFLICT (content): Merge conflict in PROJECT_STATUS.md
CONFLICT (content): Merge conflict in docs/PORTS_AND_ENV.md
Failed to merge submodule src/llama-cpp
CONFLICT (submodule): Merge conflict in src/llama-cpp
rc=1

$ git merge-tree --write-tree --name-only origin/main epic/atlas-v2-colibri-1120          → rc=0 (clean)
$ git merge-tree --write-tree --name-only origin/main origin/epic/atlas-v2-colibri-1120-rebuild → rc=0 (clean)
$ git merge-tree --write-tree --name-only origin/main origin/epic/flash-next-colibri-p100 → rc=0 (clean)
```

So both Colibri branches merge into `main` **cleanly**; both conflict with the *epic* branch.
The local branch's only epic conflict is `PROJECT_STATUS.md` (expected — 11 docs PRs have been
editing that file). `-rebuild` additionally conflicts on `docs/PORTS_AND_ENV.md` **and** the
`src/llama-cpp` submodule gitlink (see §2.4 — that one is not a normal conflict).

### 1.4 Which one should be used — and why the local one must not be pushed

`git diff --name-status epic/atlas-v2-colibri-1120 origin/epic/atlas-v2-colibri-1120-rebuild`
(37 paths). Direction: 1120 → rebuild.

**Only in the local 1120 branch (absent from `main` *and* from rebuild)** — i.e. stray
artifacts dragged in from a dirty tree:

| path | in `main` | in local 1120 | in `-rebuild` |
|---|---|---|---|
| `.evidence-e1` | NO | **YES** | NO |
| `docs/leader-handoff-state.md` | NO | **YES** | NO |
| `orch-new5-probe.txt` | NO | **YES** | NO |
| `tools/atlas/out/experts.json` (+ `.atlas-full.json`, `pins`, `lop.json`) | NO | **YES** | NO |
| `docs/evidence/prefill/prefill_poc_{pre,post}.npy` | — | **YES** | NO |

**Only in `-rebuild`** — i.e. the real, later port work:

| path | meaning |
|---|---|
| `atlas-web/LICENSE-colibri`, `atlas-web/NOTICE` | Colibri upstream attribution (licence requirement) |
| `atlas-web/scripts/proxy-smoke.ts` | the smoke test issue #802 asks CI to run |
| `atlas-web/{server/server.ts,src/Brain.tsx,src/Galaxy.tsx}` | architect round-1 fixes (F4c crash-safe atlas entries, F5 Galaxy turn picker) |
| `.gitleaksignore` | secret-scan allowlist |

⇒ **`epic/atlas-v2-colibri-1120` is superseded by `epic/atlas-v2-colibri-1120-rebuild`.**
Pushing the local branch would publish four scratch/evidence paths and lose the licence
attribution plus the round-1 fixes.

### 1.5 Open PRs on the Colibri parent line + CI

| PR | head → base | state | mergeable | CI (`statusCheckRollup`) |
|---|---|---|---|---|
| **#800** | `epic/flash-next-colibri-p100` → `main` | OPEN | MERGEABLE / CLEAN | Build & Test ✅, Gitleaks ✅, Resolve runner ✅, Deploy hydra-head ⏭ skipped |
| **#803** | `epic/atlas-v2-colibri-1120-rebuild` → `epic/flash-next-colibri-p100` | OPEN | MERGEABLE / CLEAN | Build & Test ✅, Resolve runner ✅, Deploy hydra-head ⏭ skipped |
| **#808** | `feat/atlas-p4-debt` → `feat/flash-next-colibri` | OPEN | MERGEABLE / CLEAN | *(no checks recorded)* |
| **#779** | `771-colibri-expert-atlas` → `main` | OPEN | MERGEABLE / CLEAN | Build & Test ✅, Gitleaks ✅, Resolve runner ✅ |
| #805 | `chore/bump-llama-cpp-417620251` → `feat/flash-next-colibri` | MERGED | — | — |

`-rebuild` branch CI: 4 runs on `epic/atlas-v2-colibri-1120-rebuild`, all `conclusion: success`
(2026-09-25 → 2026-09-26). Local `epic/atlas-v2-colibri-1120`: **zero runs**.

---

## 2. Fork — `feat/flash-next-colibri`

### 2.1 Where the fork lives

* `.gitmodules` still declares `submodule.src/llama-cpp.url = https://github.com/ggml-org/llama.cpp`
  (`branch = master`) — that is the **upstream**, not our fork, and `git ls-remote` on it for
  `refs/heads/feat/flash-next-colibri` returns nothing.
* The fork is **`ddvnguyen/llama.cpp`**, named by the parent's own CI:

  ```yaml
  # .github/workflows/deploy-heads.yml:127  and  dev-test.yml:112
  uses: ddvnguyen/llama.cpp/.github/workflows/hydra-build.yml@hydra-fork
  ```
* The submodule checkout in this worktree carries the real remotes:
  `hydra-fork → https://github.com/ddvnguyen/llama.cpp`, `gs → https://github.com/GenerelSchwerz/llama.cpp`.
* Note: this worktree's `src/llama-cpp` is **not** empty (78 entries, checked out at
  `7a03f921d` on branch `fix/ean-contiguity-atlas-reset`, with 2 locally modified files) —
  contrary to the brief's premise. `src/ik-llama-cpp` is the one that is deleted.

```console
$ git ls-remote --heads https://github.com/ddvnguyen/llama.cpp feat/flash-next-colibri
63877737e2cc41395ba8228ceece4d041a1107f5  refs/heads/feat/flash-next-colibri
```

### 2.2 Ahead / behind vs its base (GitHub compare API — no clone)

```console
$ gh api repos/ddvnguyen/llama.cpp/compare/baseline-flash-next...feat/flash-next-colibri
{"status":"ahead","ahead_by":526,"behind_by":0,"total_commits":526,
 "merge_base":"cdd11021b45bb48407caf44d4dbc4f4cf3ab27a9"}
$ gh api .../compare/hydra-fork...feat/flash-next-colibri   → {"status":"diverged","ahead_by":1840,"behind_by":219}
$ gh api .../compare/master...feat/flash-next-colibri       → {"status":"ahead","ahead_by":1619,"behind_by":0}
```

⇒ against its real base (`baseline-flash-next` @ `cdd11021b`) the branch is a **strict
descendant: +526 / −0**. The `master` figure shows ~1619 of those commits are an **upstream
llama.cpp sync**, which matters for §4.

### 2.3 PR + CI on the fork

```console
$ gh pr list -R ddvnguyen/llama.cpp --state all --head feat/flash-next-colibri   → []
```

It is a PR **base**, not a head — `#157 feat/flash-next-colibri-f1f3 → feat/flash-next-colibri`
is MERGED (F1–F3: NextN geometry on the colibri line). There is **no PR from it anywhere**.

CI state on the tip `63877737e`:

```console
$ gh run list -R ddvnguyen/llama.cpp --branch feat/flash-next-colibri --limit 10
Python Type-Check          2026-09-27  head=63877737e  conclusion=cancelled
.github/workflows/build-cann.yml  2026-09-27  head=63877737e  conclusion=failure
.github/workflows/build-cann.yml  2026-09-27  head=6ac553350  conclusion=failure
.github/workflows/build-cann.yml  2026-09-26  head=417620251  conclusion=failure
Hydra Build & Push         2026-09-24  head=6b7dc6a98  conclusion=success
```

⇒ **the fork branch tip is not green** (one `cancelled`, three consecutive `build-cann`
failures; the last successful engine build is `6b7dc6a98`, 2026-09-24).

### 2.4 Unpublished fork commits — **the important one**

The parent's submodule pointer on the epic branch (and on `baseline-flash-next`) names a
commit that is **not fetchable from any public remote**:

```console
$ git ls-tree origin/epic/811-strata-gap-analysis src/llama-cpp
160000 commit 0f766227d7a3fa8f69f38605d791b2dec63df8cb	src/llama-cpp
$ git ls-tree origin/baseline-flash-next          src/llama-cpp
160000 commit 0f766227d7a3fa8f69f38605d791b2dec63df8cb	src/llama-cpp
$ git log origin/epic/811-strata-gap-analysis -1 --format='%h %ci %s' -- src/llama-cpp
b456bf4f7 2026-09-26 00:59:45 +0700 chore(llama-cpp): bump submodule to 0f766227d (atlas artifact routes)
```

Reachability evidence (all against GitHub, read-only):

| test | result |
|---|---|
| `gh api repos/ddvnguyen/llama.cpp/git/commits/0f766227…` | **404 Not Found** |
| same endpoint for `6b7dc6a98…` (known-good control) | **200** |
| `gh api repos/ddvnguyen/llama.cpp/commits/0f766227…` | **422 "No commit found for SHA"** |
| `ggml-org/llama.cpp`, `JustVugg/colibri`, `ddvnguyen/hydra_vortex`, `GenerelSchwerz/llama.cpp` | all **404** |
| `git ls-remote` of all **221** fork refs (heads + tags + 115 `refs/pull/*`) | `0f766227` is not a tip of any |
| first **100** commits of `feat/flash-next-colibri` and of `417620251` | **not present** |
| local `merge-base --is-ancestor 0f766227 {417620251, 6b7dc6a98, cdd11021b}` | **NO / NO / NO** |
| local `merge-base 0f766227 417620251` | `ee13fdaa6` (the SHA the 2026-09-23 memory note calls the fold point) |
| `rev-list --count 417620251..0f766227` / `0f766227..417620251` | **3** unpublished / **11** published |

Locally the object survives only inside the submodule store, as the head of a **local**
branch `feat/flash-next-colibri` checked out at
`/mnt/WorkDisk/workspace/worktree/1q3ry0vb/fork-flash-next-colibri`:

```
0f766227d feat(atlas): engine-hosted GET /experts.json + /expert-ranks.json artifact routes
3840b2de3 fix(atlas): resolve split-shard GGUF geometry in load_impl
0abcf9287 fix(colibri): restore symbols dropped in cross-lineage cherry-pick
```

`git diff --shortstat 417620251 0f766227` → `10 files changed, 35 insertions(+), 905 deletions(-)`
i.e. the *published* branch is a superset in content; and the published line carries
**same-subject, different-SHA** commits (`380d7c910` = the `/experts.json` route,
`1f3f099cb` = the split-shard fix).

> **[hypothesis]** the three local commits were rewritten (rebase / re-cherry-pick) into
> `380d7c910` + `1f3f099cb` before PRs #157/#158 merged, and the original SHAs were never
> pushed — or were force-pushed away. Either way `0f766227` is unreachable today.
>
> **Consequence:** `git clone --recurse-submodules` of `epic/811-strata-gap-analysis` (or of
> `baseline-flash-next`) **fails**, and the `04-commit-pr.md` submodule-reachability gate
> would fail for any `epic/811 → main` PR. This is the exact P0 failure mode that doc records
> (PR #290). It is pre-existing — not introduced by the Colibri work — but it must be cleared
> before the epic can leave the epic branch.

### 2.5 How it relates to the fork build the rig runs (`.local/q2g/src`)

`.local/q2g/src` is a source **export with no `.git`** (`[ -e .local/q2g/src/.git ] → NO`).
Markers found:

```console
$ cat .local/q2g/src/VERSION
0.2.0
$ sed -n '1,8p' .local/q2g/src/build/common/build-info.cpp
int LLAMA_BUILD_NUMBER = 1234;
char const * LLAMA_COMMIT = "f2fe73fc0";
⇒ llama_build_info() == "b1234-f2fe73fc0"
```

* **`f2fe73fc0` is not a fork commit.** It is the **parent** repo's
  `baseline-flash-next` HEAD — `f2fe73fc0  2026-09-27 18:27:42 +0700  feat(atlas): P4 debt —
  Galaxy exact per-turn heat, P100 doc sync, pinfile engine-id`. Because the export has no
  `.git`, `build-info.cmake` walked up into the enclosing `hydra_vortex` checkout. The
  export's file mtimes (`Sep 27 18:27`) match that commit exactly.
  **The stamp therefore names the wrong repository.**
* **Actual fork content of the export = `7a03f921d`** (`fix(atlas): EAN ggml_sqr contiguity +
  POST /atlas/reset?scope=ean|all`, 2026-09-27, an ancestor of the published tip
  `63877737e`). Verified byte-for-byte:

  | file | export vs `7a03f921d` | export vs `0f766227` |
  |---|---|---|
  | `tools/server/server-context.cpp` | **IDENTICAL** | differs |
  | `src/llama-context.cpp` | **IDENTICAL** | — |
  | `tools/expert-atlas/analyze_edge0.py` | **IDENTICAL** | — |

  plus the export contains `POST /atlas/reset` (3 files), which `0f766227` predates.
* The **live P100 engine** documented in `docs/PORTS_AND_ENV.md:107` is build
  `b11331-6b7dc6a98f` → fork **`6b7dc6a98`** (2026-09-24), i.e. **older than both the q2g
  export (`7a03f921d`) and the branch tip (`63877737e`)**.

```
fork:  … ─ cdd11021b(baseline) ─ ee13fdaa6 ─┬─ 6b7dc6a98 ─ 380d7c910 ─ 417620251 ─ 7a03f921d ─ … ─ 63877737e (tip)
                                            └─ 0abcf9287 ─ 3840b2de3 ─ 0f766227   ← local-only, is the parent's gitlink
parent gitlinks: main=5fff12845 · epic/811 & baseline-flash-next=0f766227(dangling) ·
                 epic/flash-next-colibri-p100 & -rebuild=6b7dc6a98 · origin/feat/flash-next-colibri=417620251
rig:            .local/q2g/src ≈ 7a03f921d   ·   live P100 = 6b7dc6a98
```

---

## 3. What "deploy atlas-web" means

### 3.1 There is no CI path

```console
$ grep -rIn 'atlas' .github/workflows/     → (no matches)
$ grep -rIn -E 'bun|vite|atlas' .github/   → only unrelated `ubuntu-latest` / `auto` hits
```

There is **no `deploy-atlas*.yml`, no bun/vite job, no `gh workflow run` target** for
atlas-web. `ci.yml` / `deploy-core.yml` / `deploy-heads.yml` / the fork's `hydra-build.yml`
cover Hydra Core, Hydra Head and llama-engine only.

### 3.2 What is actually deployed, where

`atlas-web/` is a **separate bun + Vite service** (owner ruling `d-075e1e3b9c`, `atlas-web/README.md`):

```console
$ grep -A4 '"scripts"' atlas-web/package.json
  "build": "tsc -b && vite build",
  "serve": "bun run server/server.ts"
```

| instance | where | port | how |
|---|---|---|---|
| host dev | this host | `8619` | `bun run build && bun run serve` (`ATLAS_WEB_PORT`, `ATLAS_ENGINES`) |
| **production** | **P100 VM `192.168.122.21`** | **`8620`** | **systemd *user* unit `atlas-web.service` (bun), linger on, cloudflared target; proxies engine `127.0.0.1:8086` on-VM** |

Source: `docs/PORTS_AND_ENV.md:80-86` and `:106-107` (re-verified 2026-09-27);
`PROJECT_STATUS.md:110` marks the row **"✅ Deployed"**. `hydra-atlas-engine.service` is
dead/orphaned — do not start or cite it (`:83-85`).

### 3.3 Secrets / hosts needed

* SSH to `192.168.122.21` (the P100 KVM VM), `systemctl --user` + lingering for the `atlas-web`
  unit, `bun 1.4.2` on the VM, `ATLAS_ENGINES` pointing at the on-VM engine.
* **No GitHub secret, no OCI image, no `podman build`** — atlas-web ships as built `dist/` +
  `server/server.ts`, not as a container.

### 3.4 CI-only rule — does it apply?

The owner rule (`ci-cd-only-no-local-builds`, `CLAUDE.md` → never build locally) names
**Hydra Core / Hydra Head / llama-engine** and their three workflows. atlas-web is *not* in
that set — but it also has no CI at all, so **"deploy atlas-web" is an out-of-band manual
host → VM step, and it is currently un-gated by CI** (open issue **#802** *"CI: wire atlas-web
proxy smoke test into Build & Test"*).

> **[hypothesis]** the ledger's "when finished" means: once the Colibri 1.12.0 port has
> merged, rebuild `dist/` from the merged tree and restart the P100 user unit. Doing it
> before the merge would deploy a tree that no branch holds.

---

## 4. Interaction risk with the Strata / Option C work

Option C's touch list (fork files only — `docs/evidence/strata-gap/OPTION-C-DESIGN.md` §2 rows
1–18 and §3 Stages 1–3) intersected with
`gh api repos/ddvnguyen/llama.cpp/compare/baseline-flash-next...feat/flash-next-colibri → .files`
(300 paths):

| fork file | Option C stage | touched by `feat/flash-next-colibri`? |
|---|---|---|
| `ggml/src/ggml-cuda/moe-cache.cu` | **S1** streaming, **S3** admission/eviction (§1.2/§1.3) | **YES** |
| `ggml/src/ggml-backend.cpp` | **S2** boundary events / async copy | **YES** |
| `ggml/src/ggml-cuda/ggml-cuda.cu` | **S2** `:7179-7190`, `:4404-4412`, `:8243` | **YES** |
| `ggml/src/ggml-cpu/ggml-cpu.c` | §1.4 CPU pool | **YES** |
| `src/llama.cpp` | §2 r2 buft override `:318-355` | **YES** |
| `common/arg.cpp` | flags `:2876-2895`, `-t`, `--profile-decode` | **YES** |
| `src/llama-model-loader.cpp` | §2 r2 first-match-wins | **YES** |
| `src/llama-context.cpp` / `src/llama-graph.cpp` / `src/llama-model.cpp` | §1.7 / A-hops | **YES** |
| `src/models/qwen4exp.cpp` | A2 node order | no |
| `tools/server/*` | — | no (identical to base) |

**10 of 11 cited fork files overlap.**

But the overlap is *not* evenly distributed — the 300-path list is dominated by the
**upstream llama.cpp sync** (`ahead 1619 / behind 0` vs `master`, §2.2), so most hits are
upstream drift both sides already share. The **Colibri-authored** commits inside
`moe-cache.cu` are:

```
6b7dc6a98 fix(cuda): version-guard 7-arg cuStreamGetCaptureInfo for CUDA 12.x (sm_60 build)
a2c46384b moe-cache : ledger records routed expert ids and miss mask
ab8424b1b moe-cache : add env-gated per-plan ledger (GGML_CUDA_MOE_LEDGER)
042f0a5f5 cuda(moe): make grouped frequency half-life env-tunable
```

and inside `ggml-backend.cpp` only `9bae00957 llama: add opt-in decode boundary overlap`
(2026-09-11) plus merges.

> **Risk: MEDIUM (semantic, not textual).** Option C §1.3 explicitly plans to reuse
> `expert_frequency` at `moe-cache.cu:2777-2799` with the lazy shift at `:3119-3125` — i.e.
> **exactly the region `042f0a5f5` made env-tunable and `ab8424b1b`/`a2c46384b` instrumented.**
> Stage 3 also rewrites the victim-pick / `INVALID_STATE` block. Nothing conflicts *today*
> (different branches, and Option C is still DESIGN), but whichever lands second must
> reconcile the Colibri ledger hooks with the new admission policy, and Option C's
> identical-token gate **G-K1** needs a declared answer to "is `GGML_CUDA_MOE_LEDGER` on or
> off in the control arm".
>
> **[hypothesis]** Option C should be declared to start from a fork base that already contains
> the Colibri telemetry (`≥ 6b7dc6a98`, i.e. the same pin `epic/flash-next-colibri-p100` and
> `-rebuild` use) so the gates measure the fork as it will ship, not a pre-telemetry tree.

**Parent-repo overlap: none.** Option C touches zero parent-repo paths; `-rebuild`'s 136 paths
(`atlas-web/*`, docs, configs) and Option C's fork paths are disjoint. The only shared parent
artifacts are `PROJECT_STATUS.md` and `docs/PORTS_AND_ENV.md`, which every epic PR fights over
regardless.

---

## 5. Recommendation

### 5.1 Still mergeable as-is?

| target | verdict |
|---|---|
| **local `epic/atlas-v2-colibri-1120`** | **NO — do not push, do not merge.** Never pushed, zero CI, superseded by `-rebuild`, and carries 4+ scratch paths (`.evidence-e1`, `docs/leader-handoff-state.md`, `orch-new5-probe.txt`, `tools/atlas/out/*`) absent from `main`. Merge-tree vs the epic is resolvable (only `PROJECT_STATUS.md`) but merging it would *regress* the port (loses `LICENSE-colibri`/`NOTICE`/`proxy-smoke.ts`/round-1 fixes). |
| **`origin/epic/atlas-v2-colibri-1120-rebuild` (PR #803)** | **YES with one resolution.** MERGEABLE, CI green ×4, but `merge-tree` conflicts on `PROJECT_STATUS.md`, `docs/PORTS_AND_ENV.md` **and the `src/llama-cpp` gitlink** (`6b7dc6a98` vs `0f766227`). |
| **fork `feat/flash-next-colibri`** | **Not as-is.** No PR exists; tip CI is `cancelled` + `build-cann` `failure`; and the parent pins 3 unpublished commits (§2.4). |
| **`epic/811 → main` later** | **Blocked** until the dangling `0f766227` gitlink is re-bumped (§2.4). |

### 5.2 Smallest safe sequence

1. **Retire `epic/atlas-v2-colibri-1120`.** Do not push it, do not open a PR from it.
   Owner confirms the ledger line is re-pointed to `epic/atlas-v2-colibri-1120-rebuild` /
   PR **#803**. *(owner confirm)*
2. Merge **#803** → `epic/flash-next-colibri-p100`. CI green, `MERGEABLE`.
   Per `09-epic-branch.md` an epic-internal PR needs CI + review only — but it changes the
   submodule pin, so run the `04-commit-pr.md` reachability check on `6b7dc6a98` first
   (already verified present in `ddvnguyen/llama.cpp`). Resolve the 3 merge-tree conflicts.
   *(reviewer + CI gate)*
3. Merge **#800** → `main`. Already `MERGEABLE`/CLEAN with Build & Test + Gitleaks green.
   **`04-commit-pr.md`: explicit user confirmation required for the final epic → main PR.**
   *(owner confirm)* — `#779` (`771-colibri-expert-atlas → main`) is a separate, older line;
   it should be closed as superseded or explicitly sequenced after #800 *(owner decision)*.
4. **Fork side:** decide what to do with the 3 unpublished commits
   (`0abcf9287`, `3840b2de3`, `0f766227`) held in
   `/mnt/WorkDisk/workspace/worktree/1q3ry0vb/fork-flash-next-colibri`:
   (a) confirm their published equivalents `380d7c910` / `1f3f099cb` are what we want and
   **delete the local branch**, or (b) push them. Then **re-bump `src/llama-cpp`** on
   `epic/811-strata-gap-analysis` (and `baseline-flash-next`) from `0f766227` to a published
   SHA — `63877737e` (tip) or `7a03f921d` (what the rig actually runs).
   Get the fork tip green (`build-cann` failing, `Python Type-Check` cancelled) before any
   fork → `baseline-flash-next` PR. *(owner decision + CI gate)*
5. **Option C** starts only after step 4, on a fork base `≥ 6b7dc6a98`, with G-K1's
   `GGML_CUDA_MOE_LEDGER` state declared. *(Option C §5 go/no-go)*
6. **`deploy atlas-web`** last — after step 3: `bun run build` from the merged tree, install /
   restart the P100 VM user unit `atlas-web.service` on `:8620`. No CI path exists; note the
   gap (issue **#802**). *(owner confirm + manual host→VM step)*

### 5.3 Reasons to drop / supersede

* `epic/atlas-v2-colibri-1120`: never pushed, no CI, dirty-tree artifacts, and its content is
  a strict subset of `-rebuild` minus the licence attribution — keeping it alive invites
  someone to publish a bad tree.
* The ledger's fork half ("merge `feat/flash-next-colibri`") is **not actionable as written**:
  there is no such PR to merge, the branch is a PR *base*, its tip is red, and the parent-side
  pin for it points at an unreachable commit. What *is* actionable is steps 2–4 above.
* The dangling `0f766227` gitlink is the one genuinely blocking finding — it breaks fresh
  clones of the epic branch today, independent of Colibri.

---

### Commands used (all read-only except `git fetch` / `git worktree add` / `git merge-tree`)

```bash
git fetch origin --prune
git ls-remote --heads origin <branch>;  git ls-remote --heads https://github.com/ddvnguyen/llama.cpp
git rev-parse <ref>;  git rev-list --left-right --count <base>...<branch>
git merge-tree --write-tree --name-only <base> <branch>     # dry-run, nothing merged
git diff --name-status/--shortstat <a> <b>;  git ls-tree <ref> src/llama-cpp
gh pr list --state all --head/--base <branch>;  gh pr view <n> --json statusCheckRollup,mergeable,mergeStateStatus
gh run list --branch <branch>;  gh run list -R ddvnguyen/llama.cpp --branch feat/flash-next-colibri
gh api repos/<owner>/<repo>/compare/<base>...<head>
gh api repos/ddvnguyen/llama.cpp/git/commits/<sha>          # reachability probe
grep -rIn 'atlas' .github/workflows/;  cat atlas-web/package.json
```
