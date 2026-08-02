#!/usr/bin/env python3
"""Fail if PROJECT_STATUS.md claims a repo path that does not exist.

CLAUDE.md declares PROJECT_STATUS.md "the single source of truth" and says
"Never let PROJECT_STATUS.md drift from the actual codebase". That was an
honour-system rule, and it drifted: the doc claimed `EngineConfigApplier` was
"✅ Implemented" at a path PR #488 had deleted ten days earlier, and nothing
noticed. This makes the rule mechanical.

Scans every backtick-quoted path rooted at a real top-level directory and
asserts it exists. A trailing `:<line>` is stripped before checking, so
`Foo.cs:1209` validates `Foo.cs`.

Usage:  python3 scripts/check_status_paths.py [file ...]
Exit:   0 = all claimed paths resolve, 1 = at least one is missing.
"""

from __future__ import annotations

import re
import sys
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parent.parent

# Only treat a backticked string as a path claim if it starts with one of
# these — avoids matching prose, identifiers, and shell snippets.
PATH_ROOTS = ("src/", "infra/", "scripts/", "tests/", "docs/", "specs/", ".github/")

# `path/to/file.ext` or `path/to/file.ext:123`
CLAIM = re.compile(r"`([A-Za-z0-9_./-]+(?::\d+)?)`")

DEFAULT_TARGETS = ["PROJECT_STATUS.md"]


def claimed_paths(text: str) -> list[tuple[int, str]]:
    """Return (line_number, path) for every path-looking backticked span."""
    found: list[tuple[int, str]] = []
    for lineno, line in enumerate(text.splitlines(), start=1):
        for raw in CLAIM.findall(line):
            if raw.startswith(PATH_ROOTS):
                found.append((lineno, raw.split(":", 1)[0]))
    return found


def check(target: Path) -> list[str]:
    """Return a list of human-readable failures for one file."""
    if not target.exists():
        return [f"{target}: file not found"]

    failures = []
    for lineno, path in claimed_paths(target.read_text(encoding="utf-8")):
        if not (REPO_ROOT / path).exists():
            rel = target.relative_to(REPO_ROOT)
            failures.append(f"{rel}:{lineno}: claims `{path}` — does not exist")
    return failures


def main(argv: list[str]) -> int:
    targets = argv[1:] or DEFAULT_TARGETS
    failures: list[str] = []
    checked = 0

    for name in targets:
        target = REPO_ROOT / name
        failures.extend(check(target))
        if target.exists():
            checked += len(claimed_paths(target.read_text(encoding="utf-8")))

    if failures:
        print(f"{len(failures)} stale path claim(s) found:\n", file=sys.stderr)
        for f in failures:
            print(f"  {f}", file=sys.stderr)
        print(
            "\nEither fix the path or mark the entry as removed "
            "(drop the backticked path so it is no longer a claim).",
            file=sys.stderr,
        )
        return 1

    print(f"OK — {checked} claimed path(s) resolve across {len(targets)} file(s).")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
