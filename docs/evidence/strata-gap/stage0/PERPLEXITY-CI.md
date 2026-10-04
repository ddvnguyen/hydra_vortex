# Stage 0 G-0b — CI-built `llama-perplexity` (artifact, no OCI push)

**Why this exists.** `STAGE0-RESULTS.md` §7 records `G-0b: NOT RUN — needs CI llama-perplexity
build`; the G-K2 floor (`OPTION-C-DESIGN.md` §3.0) is a blocking gate for Stages 1 and 3. Owner
approved a CI-only build on 2026-10-04: no local builds, no podman, no GPU/rig use, nothing merged,
nothing deployed.

## 1. Source identity — which fork commit `.local/q2g/src` equals

The Stage 0 binary is `.local/q2g/src/build/bin/llama-server` (`arm-stage0.sh:38` → `BIN=...`), i.e.
the same export the Stage 0 harness ran. Verification, in order:

| Check | Result |
|---|---|
| `sha256sum .local/q2g/src/build/bin/llama-server` | `7be4f4fbb8ec3527f0b7eab80fa779c692a8b5a457223b5c9991b80a9a70beee` |
| matches `.local/q2g/CHECKPOINT.md` "ENGINE COMMIT REGISTER" | yes — register says binary `7be4f4fb…`, worktree HEAD `7a03f921d1cf0a83a89dcbb17bd75d567a7aa3b7` |
| `.local/q2g/src/VERSION` vs `git show 7a03f921d:VERSION` | `0.2.0` == `0.2.0` |
| `git ls-remote https://github.com/ddvnguyen/llama.cpp` | `7a03f921d…` = head of `refs/heads/fix/ean-contiguity-atlas-reset` (also contained in `refs/heads/feat/flash-next-colibri`) |
| **tree identity (decisive)** | export tree = commit tree = `d03bd75ba8bde938e1ccb8dabea3043d34e30d2b` |

Tree-identity method: a scratch index was built over the export
(`GIT_DIR=.local/treecheck-gitdir GIT_WORK_TREE=.local/q2g/src git add -A`), then `git write-tree`.
The first pass differed from `7a03f921d^{tree}` by exactly 3 paths — `build-xcframework.sh`,
`benches/dgx-spark/run-aime-120b-t8-x8-high.log`,
`examples/llama.swiftui/…/IDEWorkspaceChecks.plist` — all three present on disk with the commit's
mtime, skipped only because the export's own ignore rules match them (`.gitignore:46 /build*`,
`.gitignore:17 *.log`, `examples/llama.swiftui/.gitignore:2 xcshareddata`). After `git add -f` of
those three tracked-but-ignored paths the export tree hashed to
`d03bd75ba8bde938e1ccb8dabea3043d34e30d2b` = `git rev-parse 7a03f921d^{tree}` — **byte-identical,
zero modified, zero missing.**

**Conclusion: the export equals fork commit `7a03f921d1cf0a83a89dcbb17bd75d567a7aa3b7`
(`fix(atlas): EAN ggml_sqr contiguity + POST /atlas/reset… (hydra_vortex#806)`, 2026-09-27), VERSION 0.2.0.**

Lineage caveat (recorded, not resolved here): that commit is **not** contained in the default branch
`hydra-fork`. `git merge-base 7a03f921d origin/hydra-fork` = `65ef50a0` (2026-06-04); `7a03f921d` is
1837 commits ahead of it, `hydra-fork` is 219 commits ahead of it. The Stage 0 engine is therefore a
**flash-next-lineage** build, and the CI build had to be dispatched from that same commit.

## 2. Build flags — what the Stage 0 `llama-server` used vs what CI used

Stage 0 rig binary (`.local/q2g/src/build/CMakeCache.txt`, CUDA `/opt/software/cuda/13.2.1`):

| Flag | Stage 0 rig build | CI combo (build-combo.sh, sm86-sm120) | same? |
|---|---|---|---|
| `CMAKE_CUDA_ARCHITECTURES` | `86;120` | `86;120` | yes |
| CUDA toolkit | 13.2.1 (nvcc) | 13.2 (cloud: `cuda-toolkit-13-2`) | yes (13.2 line) |
| `CMAKE_BUILD_TYPE` | `Release` | `Release` | yes |
| `GGML_CUDA` | `ON` | `ON` | yes |
| `GGML_CUDA_GRAPHS` / `GGML_CUDA_NCCL` / `GGML_CUDA_FA` | `ON` / `ON` / `ON` | `ON` / `ON` / `ON` | yes |
| `BUILD_SHARED_LIBS` | `ON` | `ON` (+ RPATH `$ORIGIN`) | yes |
| `LLAMA_BUILD_TOOLS` | `ON` (target `llama-perplexity` exists) | `ON` (default, not disabled) | yes |
| `GGML_NATIVE` | `ON` | `OFF` (cloud runner; combo sets ON only for `runner_target=local`) | **no** |
| `GGML_CUDA_FORCE_CUBLAS` | `OFF` | `ON` | **no** |
| `GGML_RPC` | `OFF` | `ON` | **no** |
| `GGML_CUDA_FA_ALL_QUANTS` | `OFF` | `ON` | **no** |
| IPO (`CMAKE_INTERPROCEDURAL_OPTIMIZATION`) | not set | `OFF` (cloud; `local` would set ON) | yes (both off) |
| `LLAMA_BUILD_EXAMPLES` | `ON` | `OFF` (does not affect tools) | n/a |

The CI build deliberately reuses the **llama-server combo's configure flags verbatim** (task
instruction: "same configure flags as the llama-server combo … see build-combo.sh"), so that the
floor binary is built exactly like every future CI-built stage binary. The four deltas against the
rig's local build are recorded above for the reader: they matter only if the G-K2 floor from this
binary is compared against the *rig* binary rather than against another combo build, which is not
what G-0b does (G-0b is build-vs-itself).

Runtime flags for the future G-0b run are unchanged from Stage 0 (`arm-stage0.sh:131-138`) — see
`STAGE0-RESULTS.md` §1.1.

## 3. Why the cloud runner (and not the self-hosted `hydra-rtx-host-fork`)

Dispatched with `runner_target=cloud` (`ubuntu-latest`), `runner=cloud`, because at dispatch time
(2026-10-04 07:11 +07) the rig host could not take a CUDA build:

- `/` (root, where `$HOME` lives) was **100 % full, 2.2 GB free**;
- the self-hosted runner work dir `/home/ddv/actions-runner-fork/_work` sits on `/mnt/containers`,
  **94 % full, 6.4 GB free** — not enough for a fresh `build_hydra_*` tree plus ccache;
- a live `llama-server` (other agent, GPU1 / RTX 3060, port 8093, `.local/wt-stage0b/…`) was running,
  and a from-scratch `nproc`-parallel compile on the rig would have contended for host CPU/RAM with
  that measurement session.

No GPU was used, no podman was invoked (`Login to ghcr.io` step skipped — see §5), and nothing was
deployed. Cost: zero (public repo → GitHub-hosted minutes are free).

## 4. Fork PR, workflow change, dispatch

- Branch: `ci/perplexity-artifact`, created from `7a03f921d` (+2 commits, both CI-only).
- Fork PR (base `hydra-fork`, **not to be merged**): https://github.com/ddvnguyen/llama.cpp/pull/160
- Workflow: existing `.github/workflows/hydra-build.yml` (chosen over a new `hydra-tools.yml`
  because `workflow_dispatch` only registers workflows that already exist on the default branch —
  `hydra-build.yml` is on `hydra-fork`, a brand-new workflow file could not be dispatched from a
  non-default branch without merging it first).

Diff summary (`7a03f921d..3808d4e3`, 2 files, +80/-3 net):

```diff
# .github/workflows/hydra-build.yml
+      build_llama_perplexity:
+        description: "Build llama-perplexity (artifact upload ONLY, no OCI push) - Stage 0 G-0b KL floor. Needs arch_sm86_sm120 + execution_mode=matrix."
+        type: boolean
+        default: false
...
+          if [ "${{ inputs.build_llama_perplexity }}" = "true" ]; then
+            if [ "${{ inputs.arch_sm86_sm120 }}" != "true" ]; then
+              echo "::error::build_llama_perplexity needs arch_sm86_sm120 (CUDA 13.2, cuda_arch 86;120)."
+              exit 1
+            fi
+            if [ "${{ inputs.execution_mode }}" != "matrix" ]; then
+              echo "::error::build_llama_perplexity only supports execution_mode=matrix."
+              exit 1
+            fi
+            combos+=('{"arch":"sm86-sm120","binary":"llama-perplexity","cuda_version":"13.2","cuda_arch":"86;120","mode":"artifact"}')
+          fi
...
-      - name: Login to ghcr.io
+      - name: Login to ghcr.io
+        if: matrix.mode != 'artifact'
...
-            "$CUDA_PATH" "${{ env.IMAGE_REPO }}" "${{ needs.resolve.outputs.sha }}" "${{ inputs.runner_target }}" "${{ needs.resolve.outputs.pr }}"
+            "$CUDA_PATH" "${{ env.IMAGE_REPO }}" "${{ needs.resolve.outputs.sha }}" "${{ inputs.runner_target }}" "${{ needs.resolve.outputs.pr }}" \
+            "${{ matrix.mode }}"
+
+      - name: Upload binary artifact
+        if: matrix.mode == 'artifact'
+        uses: actions/upload-artifact@v6
+        with:
+          name: ${{ matrix.binary }}-${{ matrix.arch }}-${{ needs.resolve.outputs.sha }}
+          path: staging_${{ matrix.arch }}/${{ matrix.binary }}/
+          if-no-files-found: error
+          retention-days: 14
...
+      - name: Deploy reminder
+        if: matrix.mode != 'artifact'
...
-    timeout-minutes: ${{ inputs.runner_target == 'cloud' && 45 || 60 }}
+    timeout-minutes: ${{ inputs.build_llama_perplexity == true && 120 || (inputs.runner_target == 'cloud' && 45 || 60) }}

# .github/workflows/scripts/build-combo.sh
+MODE="${10:-image}" # "image" (default) or "artifact" (no push, stage + upload)
+case "$MODE" in image|artifact) ;; *) echo "::error::Invalid MODE '$MODE' ..."; exit 1 ;; esac
...
+if [ "$MODE" = "artifact" ]; then
+  echo "=== [$ARCH/$BINARY] artifact mode: stage + exit, NO OCI push ==="
+  mkdir -p "${STAGING_DIR}/bin"
+  cp "$BUILD_DIR/bin/$BINARY" "${STAGING_DIR}/bin/"
+  cp "$BUILD_DIR/bin/"*.so* "${STAGING_DIR}/bin/" 2>/dev/null || true
+  ldd "${STAGING_DIR}/bin/$BINARY" > "${STAGING_DIR}/ldd.txt" 2>&1 || true
+  { ... source_commit/runner_target/cmake_args/build info ... } > "${STAGING_DIR}/build-info.txt"
+  exit 0
+fi
```

`image` mode (the default) is untouched: the registry same-hash gate, `podman build/push` and the
`--version` probe only run in `image` mode. In `artifact` mode the binary is **never executed**
(owner rule: do not run `llama-perplexity`).

Dispatch (owner-approved):

```bash
gh workflow run hydra-build.yml --repo ddvnguyen/llama.cpp --ref ci/perplexity-artifact \
  -f build_llama_engine=false -f build_llama_server=false -f build_llama_perplexity=true \
  -f arch_sm86_sm120=true -f arch_sm60=false \
  -f runner_target=cloud -f execution_mode=matrix -f runner=cloud
```

## 5. Run and artifact

- Run: https://github.com/ddvnguyen/llama.cpp/actions/runs/37167416216 (`workflow_dispatch` on `refs/heads/ci/perplexity-artifact`)
- Built from source commit: `7a03f921d1cf0a83a89dcbb17bd75d567a7aa3b7`
  (workflow head `3808d4e3c15a54a9f082542d9e20d02d3ab0b219`; only `.github/workflows/*` differ)
- Steps of note: `Provision toolchain (cloud)` = success, `Login to ghcr.io` = **skipped**,
  `Build, package, push` = configure + build only, `Upload binary artifact` = the artifact.

### Verification (binary NOT executed)

| Item | Value |
|---|---|
| artifact name | `llama-perplexity-sm86-sm120-3808d4e` |
| artifact file | `.local/perplexity-ci/bin/llama-perplexity` (660 MB artifact total: `bin/` + `ldd.txt` + `build-info.txt`) |
| `file` | `ELF 64-bit LSB pie executable, x86-64, version 1 (SYSV), dynamically linked, interpreter /lib64/ld-linux-x86-64.so.2, BuildID[sha1]=07c3af8965fb139e9694369f3f8a79a40c6c5b77, for GNU/Linux 3.2.0, not stripped` |
| `sha256` | `92594986686a4bbe23a2834695792ce32f8b18b9d57bf3c50e0ac87aba715fdd` |
| size / mode | `15968` bytes, `-rwxr-xr-x` |
| `ldd` (this host, `LD_LIBRARY_PATH=.../bin:/opt/software/cuda/13.2.1/lib64`) | **0 `not found`** — resolves `libllama-perplexity-impl.so`, `libllama-common.so.0`, `libllama.so.0`, `libggml-{base,cuda,cpu,rpc}.so.0` from the artifact dir, `libcuda.so.1` from the driver, `libcudart.so.13`/`libcublas.so.13`/`libcublasLt.so.13` from `/opt/software/cuda/13.2.1/lib64` |
| `ldd` without that CUDA path | `libcudart.so.13 => not found`, `libcublas.so.13 => not found` only — i.e. the artifact does **not** bundle the CUDA runtime; the rig must put CUDA 13.2.1 on `LD_LIBRARY_PATH` (as `arm-stage0.sh` already does) |
| `ldd.txt` produced in CI | same set; there `libcudart`/`libcublas` resolved via `/usr/local/cuda/targets/...` and `libcuda.so.1 => not found` (no driver on the runner — expected) |
| `cuobjdump --list-elf bin/libggml-cuda.so` | 380 cubins, alternating `libggml-cuda.*.sm_86.cubin` and `libggml-cuda.*.sm_120a.cubin` → `CMAKE_CUDA_ARCHITECTURES=86;120` confirmed in the payload |
| `build-info.txt` | `mode=artifact`, `binary=llama-perplexity`, `arch=sm86-sm120`, `cuda_arch=86;120`, `cuda_version=13.2`, `runner_target=cloud`, `source_ref=ci/perplexity-artifact`, `source_commit=3808d4e3c15a54a9f082542d9e20d02d3ab0b219`, `fork_version=0.2.0`, `built_at=2026-10-04T02:13:01Z`, full `cmake_args=` line preserved |
| source identity of that commit | `git diff --name-only 7a03f921d 3808d4e3` = `.github/workflows/hydra-build.yml` + `.github/workflows/scripts/build-combo.sh` **only** — every source file compiled is byte-identical to `7a03f921d` (the export's tree, §1) |
| binary executed? | **no** — only `file`, `ldd`, `sha256sum`, `cuobjdump` were run against it |

## 6. Caveats / follow-ups

- G-0b is still **NOT RUN** — this note only delivers the binary. The KL floor itself must be
  measured on the rig (dump + compare, `scripts/hydra-kl-gate.sh` with logs redirected out of
  `/tmp/opencode/`, `HYDRA_PIN_FILE` left unset — see `BIASED-ROUTING-DESIGN.md` §4.2).
- The artifact is a **shared** build (`BUILD_SHARED_LIBS=ON`, RPATH `$ORIGIN`): it must be extracted
  as a directory (binary + `*.so*` together), never moved as a lone binary.
- The PR against `hydra-fork` shows the whole flash-next lineage divergence in its three-dot diff;
  review `7a03f921d..ci/perplexity-artifact` instead. **Do not merge it.**
- `runner_target=cloud` implies `GGML_NATIVE=OFF`; a `local` runner build would use
  `GGML_NATIVE=ON`. If a future stage binary is built with a different `runner_target`, the floor
  should be re-measured with a binary of the same provenance.
