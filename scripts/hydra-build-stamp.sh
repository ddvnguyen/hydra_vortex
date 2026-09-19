#!/bin/bash
# Build provenance stamp (standing rule §27.5, amended §28.3): every build
# writes the source lineage into the build dir; every rig leg logs it.
#
# The stamp records the NEWEST OBJECT MTIME in the build dir, not the
# stamp-writing time: any relink after stamping invalidates the stamp.
# Measurement build dirs are IMMUTABLE after stamping: never relink objects
# into a stamped dir; fresh dir per base, always.
#
# Usage: hydra-build-stamp.sh <build-dir>   # run from the fork checkout after build
#        hydra-build-stamp.sh --show <build-dir>
#        hydra-build-stamp.sh --check <build-dir>  # exit 0 fresh, 1 stale/missing/CONTAMINATED
set -u
check() {
  local DIR="$1" f
  f="$DIR/PROVENANCE"
  [ -f "$f" ] || { echo "PROVENANCE missing in $DIR"; return 1; }
  grep -q "^tree=CONTAMINATED" "$f" && { echo "PROVENANCE contaminated: $DIR"; return 1; }
  local stamped newest
  stamped=$(grep -m1 "^objects=" "$f" | cut -d= -f2)
  newest=$(find "$DIR" \( -name "*.o" -o -name "*.so*" -o -type f -path "*/bin/*" \) -printf "%T@\n" 2>/dev/null | sort -rn | head -1 | cut -d. -f1)
  [ -n "$stamped" ] && [ -n "$newest" ] && [ "$newest" -le "$stamped" ] || { echo "PROVENANCE stale: objects newer than stamp in $DIR"; return 1; }
  cat "$f"
  return 0
}
[ "${1:-}" = "--show" ] && { cat "${2:?}/PROVENANCE" 2>/dev/null || echo "PROVENANCE missing in $2"; exit 0; }
[ "${1:-}" = "--check" ] && { check "${2:?}"; exit $?; }
DIR="${1:?usage: $0 <build-dir> | --show <build-dir> | --check <build-dir>}"
SHA=$(git rev-parse HEAD 2>/dev/null || echo UNKNOWN)
[ -n "$(git status --short 2>/dev/null)" ] && DIRTY="dirty" || DIRTY="clean"
BR=$(git rev-parse --abbrev-ref HEAD 2>/dev/null || echo UNKNOWN)
NEWEST=$(find "$DIR" \( -name "*.o" -o -name "*.so*" -o -type f -path "*/bin/*" \) -printf "%T@\n" 2>/dev/null | sort -rn | head -1 | cut -d. -f1)
{
  echo "sha=$SHA"
  echo "branch=$BR"
  echo "tree=$DIRTY"
  echo "objects=${NEWEST:-0}"
  echo "date=$(date -u +%FT%TZ)"
  echo "src=$(git rev-parse --show-toplevel 2>/dev/null || echo UNKNOWN)"
} > "$DIR/PROVENANCE"
cat "$DIR/PROVENANCE"
