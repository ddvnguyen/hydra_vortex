#!/usr/bin/env bash
#
# llama-build.sh - worktree-proof llama.cpp fork build helper.
#
# Makes local fork builds cheap no matter which worktree the submodule is
# checked out in, and keeps experiments isolated from the stable/CI cache.
#
# Cache model (two tiers, one shared ccache budget):
#   L1 stable   CCACHE_NAMESPACE=l1  /mnt/WorkDisk/cache/llama-build/stable/<profile>
#               persistent, never pruned. Same ccache store as CI.
#   L2 test     CCACHE_NAMESPACE=l2  /mnt/WorkDisk/cache/llama-build/test/<profile>[/-<variant>]
#               throwaway. Evicted first when the shared store exceeds its cap.
#   ccache store: /mnt/WorkDisk/cache/hydra-ccache  (shared 15G budget, both tiers)
#
# Each build dir gets a build.meta (sha, source, cuda, flags, target, timestamp)
# so a later run can tell exactly what a cached binary was built with. The
# in-tree build dirs (build-hydra-dev, build_sm86_sm120, ...) are symlinks to
# the active cache dir, so the normal cmake --build build-* commands keep working.
#
# Usage:
#   llama-build.sh <profile> [options]          L1 stable build
#   llama-build.sh test <profile> [options]     L2 test/experiment build
#   llama-build.sh list                         show cache state
#   llama-build.sh prune                        evict L2 + drop old L2 build dirs
#   llama-build.sh --clear-l2                   nuke the whole L2 namespace + dirs
#
# Profiles: dev | dev-nofaq | deploy-sm86-sm120 | sm60
# Options:  --variant NAME   L2 only: separate build dir per experiment
#           --cuda VER       override CUDA toolkit version (e.g. 13.3)
#           --target NAME    override build target
#           --jobs N         parallel build jobs (default: nproc-8; env LLAMA_BUILD_JOBS)
#           -- -D...         extra CMake cache args, passed verbatim
#
set -euo pipefail

CACHE_ROOT="${LLAMA_CACHE_ROOT:-/mnt/WorkDisk/cache}"
CCACHE_DIR="$CACHE_ROOT/hydra-ccache"
BUILD_ROOT="$CACHE_ROOT/llama-build"
L1_DIR="$BUILD_ROOT/stable"
L2_DIR="$BUILD_ROOT/test"
LOCK_FILE="$CACHE_ROOT/llama-build.lock"
BUDGET_BYTES=$((15 * 1024 * 1024 * 1024))

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
SUBMOD="$ROOT/src/llama-cpp"

# ccache may live outside the default PATH (user-local install).
if ! command -v ccache >/dev/null 2>&1; then
  export PATH="$HOME/.local/bin:$PATH"
fi
if ! command -v ccache >/dev/null 2>&1; then
  echo "ERROR: ccache not found on PATH. Install it (conda install -c conda-forge ccache) or place it in ~/.local/bin." >&2
  exit 1
fi

log()  { echo "[llama-build] $*"; }
die()  { echo "ERROR: $*" >&2; exit 1; }

usage() {
  sed -n '2,24p' "${BASH_SOURCE[0]}"
  exit 0
}

profile_flags() {
  local profile="$1" cuda="$2" nvcc="$3"
  case "$profile" in
    dev|dev-nofaq|deploy-sm86-sm120)
      # FA_ALL_QUANTS is the biggest compile-time multiplier (fattn-vec/tile
      # instances for every K/V combo). dev-nofaq drops it: the default FA
      # kernel set (f16/f16, q4_0/q4_0, q8_0/q8_0, bf16/bf16) still covers the
      # symmetric q8_0 KV cache Hydra uses, so iteration compiles much faster.
      # Keep it ON for dev/deploy so Q5_1/Q4_1 KV experiments stay build-ready.
      local fa_all_quants="-DGGML_CUDA_FA_ALL_QUANTS=ON"
      if [ "$profile" = "dev-nofaq" ]; then
        fa_all_quants="-DGGML_CUDA_FA_ALL_QUANTS=OFF"
      fi
      CMAKE_ARGS=(
        -DCMAKE_CUDA_ARCHITECTURES="86;120"
        -DCMAKE_CUDA_COMPILER="$nvcc"
        -DCMAKE_C_COMPILER_LAUNCHER=ccache
        -DCMAKE_CXX_COMPILER_LAUNCHER=ccache
        -DCMAKE_CUDA_COMPILER_LAUNCHER=ccache
        -DGGML_CUDA=ON
        # FORCE_CUBLAS=OFF (approved): use the custom int8-tensor-core MMQ
        # kernels for Q4/Q5 model quants instead of forcing FP16 cuBLAS.
        # build.md: custom kernels are the default on GPUs with int8 tensor
        # cores (sm_86/sm_120) and generally faster for quantized GEMMs.
        -DGGML_CUDA_FORCE_CUBLAS=OFF
        -DGGML_CUDA_FA=ON
        "$fa_all_quants"
        -DGGML_CUDA_GRAPHS=ON
        -DGGML_CUDA_NCCL=ON
        -DGGML_RPC=ON
        -DGGML_NVML=ON
        -DGGML_NATIVE=ON
        -DCMAKE_BUILD_TYPE=Release
        -DBUILD_SHARED_LIBS=ON
        -DCMAKE_BUILD_RPATH='$ORIGIN'
        -DCMAKE_INSTALL_RPATH='$ORIGIN'
        -DCMAKE_BUILD_WITH_INSTALL_RPATH=ON
        -DLLAMA_BUILD_EXAMPLES=OFF
        -DLLAMA_BUILD_TESTS=OFF
      )
      if [ "$profile" = "deploy-sm86-sm120" ]; then
        CMAKE_ARGS+=(-DCMAKE_INTERPROCEDURAL_OPTIMIZATION=ON)
      fi
      ;;
    sm60)
      CMAKE_ARGS=(
        -DCMAKE_CUDA_ARCHITECTURES="60"
        -DCMAKE_CUDA_COMPILER="$nvcc"
        -DCMAKE_CUDA_HOST_COMPILER=/usr/bin/g++-14
        -DCMAKE_C_COMPILER_LAUNCHER=ccache
        -DCMAKE_CXX_COMPILER_LAUNCHER=ccache
        -DCMAKE_CUDA_COMPILER_LAUNCHER=ccache
        -DGGML_CUDA=ON
        -DGGML_CUDA_FORCE_CUBLAS=ON
        -DGGML_CUDA_FORCE_MMQ=OFF
        -DGGML_CUDA_FA_ALL_QUANTS=OFF
        -DGGML_RPC=ON
        -DGGML_NVML=ON
        -DGGML_NATIVE=ON
        -DCPACK_INCLUDE_COMMANDS=ON
        -DCMAKE_BUILD_TYPE=Release
        -DBUILD_SHARED_LIBS=ON
        -DCMAKE_BUILD_RPATH='$ORIGIN'
        -DCMAKE_INSTALL_RPATH='$ORIGIN'
        -DCMAKE_BUILD_WITH_INSTALL_RPATH=ON
        -DCMAKE_INTERPROCEDURAL_OPTIMIZATION=ON
        -DLLAMA_BUILD_EXAMPLES=OFF
        -DLLAMA_BUILD_TESTS=OFF
      )
      ;;
    *) die "unknown profile '$profile' (dev | dev-nofaq | deploy-sm86-sm120 | sm60)" ;;
  esac
}

meta_set() { # meta_set <dir> <key> <value>
  local dir="$1" key="$2" val="$3"
  grep -v "^$key=" "$dir/build.meta" 2>/dev/null > "$dir/build.meta.tmp" || true
  printf '%s=%s\n' "$key" "$val" >> "$dir/build.meta.tmp"
  mv "$dir/build.meta.tmp" "$dir/build.meta"
}

meta_get() { # meta_get <dir> <key>
  local dir="$1" key="$2"
  sed -n "s/^$key=//p" "$dir/build.meta" 2>/dev/null | head -1
}

join() { local IFS="$1"; shift; echo "$*"; }

store_size() { du -sb "$CCACHE_DIR" 2>/dev/null | cut -f1 | grep -E '^[0-9]+$' || echo 0; }

over_budget() {
  local size; size="$(store_size)"
  [ "$size" -gt "$BUDGET_BYTES" ]
}

# Eviction: shared budget is enforced L2-first. The L2 namespace is always the
# first victim; only when L2 is empty and the store is still over budget do we
# fall back to evicting least-used entries store-wide (last access time beats
# creation time: ccache touches entry files on use, so mtime reflects use).
evict() {
  if ! over_budget; then return 0; fi
  log "shared ccache store over ${BUDGET_BYTES} bytes - evicting L2 namespace first"
  ccache --evict-namespace l2 || true
  if ! over_budget; then return 0; fi
  log "still over budget - evicting least-used entries store-wide (last-access order)"
  # entry dirs live at $CCACHE_DIR/<0-f>/<0-f>/<hash>
  local list
  list="$(find "$CCACHE_DIR" -mindepth 3 -maxdepth 3 -type d 2>/dev/null | while read -r d; do
    # max(atime, mtime) = last access/use
    read -r at mt < <(stat -c '%X %Y' "$d")
    local lastused; lastused=$(( at > mt ? at : mt ))
    printf '%s %s\n' "$lastused" "$d"
  done | sort -n)"
  [ -z "$list" ] && return 0
  local size lastused d s
  size="$(store_size)"
  while [ "$size" -gt "$BUDGET_BYTES" ]; do
    read -r lastused d <<< "$(printf '%s\n' "$list" | head -1)" || break
    if [ -z "$d" ]; then break; fi
    list="$(printf '%s\n' "$list" | tail -n +2)"
    s="$(du -sb "$d" 2>/dev/null | cut -f1 || echo 0)"
    rm -rf "$d"
    size=$(( size - s ))
    log "evicted $(basename "$d") ($s bytes)"
  done
}

ccache_env() { # ccache_env <namespace>
  export CCACHE_DIR CCACHE_MAXSIZE=15G CCACHE_NAMESPACE="$1"
  mkdir -p "$CCACHE_DIR"
  # Declare the shared budget in the store config so every consumer sees it.
  if [ ! -f "$CCACHE_DIR/ccache.conf" ] || ! grep -q '^max_size' "$CCACHE_DIR/ccache.conf"; then
    { echo "# shared budget across L1 (stable/CI) + L2 (test) llama-build tiers"
      echo "max_size = 15G"
    } > "$CCACHE_DIR/ccache.conf"
  fi
}

ensure_submodule() {
  if [ ! -d "$SUBMOD/.git" ] && [ ! -f "$SUBMOD/.git" ]; then
    # Prefer reusing the shared module repo (common gitdir) so a fresh worktree
    # does not re-clone the fork over the network. --reference shares its object
    # store; -N (no-fetch) keeps it offline when the pinned SHA is already there.
    local module_ref
    module_ref="$(git -C "$ROOT" rev-parse --git-common-dir 2>/dev/null)/modules/src/llama-cpp"
    if [ -n "${module_ref#*/modules/}" ] && [ -d "$module_ref" ]; then
      log "submodule not initialized - reusing shared module repo ($module_ref)"
      if ! git -C "$ROOT" submodule update --init --reference "$module_ref" -N src/llama-cpp 2>/dev/null; then
        log "offline init failed - falling back to network submodule update"
        git -C "$ROOT" submodule update --init src/llama-cpp
      fi
    else
      log "submodule not initialized - running git submodule update --init"
      git -C "$ROOT" submodule update --init src/llama-cpp
    fi
  fi
  [ -d "$SUBMOD" ] || die "src/llama-cpp missing after submodule init"
}

active_targets_in() { # active_targets_in <root> : symlink targets under <root>
  local root="$1"
  find "$SUBMOD" -maxdepth 1 -name 'build*' -type l 2>/dev/null | while read -r l; do
    local t; t="$(readlink -f "$l" 2>/dev/null || true)"
    case "$t" in "$root"/*) echo "$t" ;; esac
  done | sort -u
}

symlink_in_tree() { # symlink_in_tree <target-dir>
  local target="$1" in_tree_name="$2" link="$SUBMOD/$in_tree_name"
  if [ -e "$link" ] && [ ! -L "$link" ]; then
    log "WARNING: $link is a real directory; leaving it (not using shared cache for it)"
    return 0
  fi
  ln -sfn "$target" "$link"
}

cmd_list() {
  local size
  size="$(store_size)"
  printf 'ccache store : %s (%s bytes, budget %s)\n' "$CCACHE_DIR" "$size" "$BUDGET_BYTES"
  CCACHE_DIR="$CCACHE_DIR" ccache -s 2>/dev/null | grep -E "Cacheable|Hits|Misses|Cache size" || true
  echo
  printf 'L1 build dirs (%s)\n' "$L1_DIR"
  for d in "$L1_DIR"/*; do
    [ -d "$d" ] || continue
    printf '  %-45s %s bytes  sha=%s cuda=%s target=%s built=%s\n' \
      "$(basename "$d")" "$(du -sb "$d" | cut -f1)" \
      "$(meta_get "$d" sha)" "$(meta_get "$d" cuda)" "$(meta_get "$d" target)" \
      "$(meta_get "$d" built_at)"
  done
  echo
  printf 'L2 build dirs (%s)\n' "$L2_DIR"
  for d in "$L2_DIR"/*; do
    [ -d "$d" ] || continue
    printf '  %-45s %s bytes  sha=%s cuda=%s target=%s built=%s\n' \
      "$(basename "$d")" "$(du -sb "$d" | cut -f1)" \
      "$(meta_get "$d" sha)" "$(meta_get "$d" cuda)" "$(meta_get "$d" target)" \
      "$(meta_get "$d" built_at)"
  done
}

cmd_prune() {
  exec 9>"$LOCK_FILE"; flock 9
  evict
  # Drop L2 build dirs that are not the current in-tree active target.
  local keep; keep="$(active_targets_in "$L2_DIR")"
  for d in "$L2_DIR"/*; do
    [ -d "$d" ] || continue
    case " $keep " in *" $d "*) log "keeping active $d" ;; *)
      log "removing L2 build dir $d"
      rm -rf "$d" ;;
    esac
  done
}

cmd_clear_l2() {
  exec 9>"$LOCK_FILE"; flock 9
  log "clearing L2 namespace"
  ccache --evict-namespace l2 || true
  log "removing L2 build dirs ($L2_DIR)"
  rm -rf "$L2_DIR"
}

cmd_build() {
  local tier="$1" profile="$2"
  shift 2

  local variant="" cuda_override="" target_override="" jobs="" extra_args=()
  while [ $# -gt 0 ]; do
    case "$1" in
      --variant) variant="${2:?--variant needs a name}"; shift 2 ;;
      --cuda)    cuda_override="${2:?--cuda needs a version}"; shift 2 ;;
      --target)  target_override="${2:?--target needs a name}"; shift 2 ;;
      --jobs)    jobs="${2:?--jobs needs a number}"; shift 2 ;;
      --)        shift; extra_args=("$@"); break ;;
      *)         die "unknown option '$1' (see usage)" ;;
    esac
  done

  # Build parallelism. Default: nproc - 8 so the host keeps ~8 threads free for
  # concurrent work (live engine, MoE CPU offload, agents) — e.g. 12 jobs on the
  # 20-thread i7-12700K. Override with --jobs N or LLAMA_BUILD_JOBS.
  local default_jobs=$(( $(nproc) - 8 ))
  [ "$default_jobs" -lt 1 ] && default_jobs=1
  jobs="${jobs:-${LLAMA_BUILD_JOBS:-$default_jobs}}"

  [ "$tier" = "test" ] || tier="stable"
  local namespace; [ "$tier" = "stable" ] && namespace="l1" || namespace="l2"

  local cuda; cuda="${cuda_override:-$([ "$profile" = sm60 ] && echo 12.9 || echo 13.2)}"
  local in_tree_name target
  case "$profile" in
    dev)               in_tree_name="build-hydra-dev" ;;
    dev-nofaq)         in_tree_name="build-hydra-dev-nofaq" ;;
    deploy-sm86-sm120) in_tree_name="build_sm86_sm120" ;;
    sm60)              in_tree_name="build_sm60_v2" ;;
    *) die "unknown profile '$profile'" ;;
  esac
  target="${target_override:-$([ "$profile" = sm60 ] && echo llama-server || echo llama-engine)}"

  ensure_submodule
  ccache_env "$namespace"

  local cuda_path="/opt/software/cuda/$cuda"
  [ -x "$cuda_path/bin/nvcc" ] || die "CUDA toolkit not found at $cuda_path"

  local build_dir
  if [ "$tier" = "stable" ]; then
    build_dir="$L1_DIR/$profile"
  else
    build_dir="$L2_DIR/$profile${variant:+-$variant}"
  fi

  # Snapshot the effective flag set so reconfigure-on-change is exact.
  local cmake_args=()
  profile_flags "$profile" "$cuda" "$cuda_path/bin/nvcc"
  cmake_args=("${CMAKE_ARGS[@]}" "${extra_args[@]}")

  local sha source
  sha="$(git -C "$SUBMOD" rev-parse HEAD 2>/dev/null || echo unknown)"
  source="$SUBMOD"

  mkdir -p "$build_dir"
  symlink_in_tree "$build_dir" "$in_tree_name"

  local need_configure=1
  if [ -f "$build_dir/build.ninja" ] && [ -f "$build_dir/CMakeCache.txt" ] && [ -f "$build_dir/build.meta" ]; then
    local m_sha m_source m_flags m_cuda m_target m_tier
    m_sha="$(meta_get "$build_dir" sha)"
    m_source="$(meta_get "$build_dir" source)"
    m_cuda="$(meta_get "$build_dir" cuda)"
    m_target="$(meta_get "$build_dir" target)"
    m_tier="$(meta_get "$build_dir" tier)"
    m_flags="$(meta_get "$build_dir" flags)"
    if [ "$m_sha" = "$sha" ] && [ "$m_cuda" = "$cuda" ] && [ "$m_target" = "$target" ] \
       && [ "$m_tier" = "$tier" ] && [ -d "$m_source" ] && [ "$m_flags" = "$(join ' ' "${cmake_args[@]}")" ]; then
      need_configure=0
    fi
  fi

  exec 9>"$LOCK_FILE"; flock 9
  if [ "$need_configure" -eq 1 ]; then
    log "configuring [$tier/$profile] CUDA $cuda target $target into $build_dir"
    cmake -S "$SUBMOD" -B "$build_dir" -G Ninja "${cmake_args[@]}"
  else
    log "reusing existing configuration in $build_dir (sha/flags unchanged)"
  fi

  log "building target '$target' with $jobs parallel jobs (ccache namespace=$namespace, store=$CCACHE_DIR)"
  cmake --build "$build_dir" --target "$target" -j"$jobs"

  # Refresh build.meta on success.
  meta_set "$build_dir" sha        "$sha"
  meta_set "$build_dir" source     "$source"
  meta_set "$build_dir" tier       "$tier"
  meta_set "$build_dir" profile    "$profile"
  meta_set "$build_dir" cuda       "$cuda"
  meta_set "$build_dir" target     "$target"
  meta_set "$build_dir" flags      "$(join ' ' "${cmake_args[@]}")"
  meta_set "$build_dir" built_at   "$(date -Is)"

  ccache -s | grep -E "Cacheable|Hits|Misses" || true
  echo
  log "binary: $build_dir/bin/$target"
  log "cache:  $CCACHE_DIR (namespace $namespace, shared 15G budget, L2 evicted first)"
  log "deploy: trigger hydra-build.yml CI on ddvnguyen/llama.cpp (see docs/workflow/05-deploy.md); local build is verification only"

  evict
}

main() {
  [ $# -eq 0 ] && usage
  case "$1" in
    list)        cmd_list ;;
    prune)       cmd_prune ;;
    --clear-l2)  cmd_clear_l2 ;;
    test)        [ $# -ge 2 ] || usage; cmd_build test "$2" "${@:3}" ;;
    dev|dev-nofaq|deploy-sm86-sm120|sm60) cmd_build stable "$1" "${@:2}" ;;
    -h|--help)   usage ;;
    *)           die "unknown command '$1'" ;;
  esac
}

main "$@"