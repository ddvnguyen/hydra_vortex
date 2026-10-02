#!/usr/bin/env python3
"""Rebuild t0006 prompt cells with the REAL tokenizer counts + add warmup.txt.

Why: cells were sized by chars/4. On this mixed content the Qwen3.5 BPE runs
~2.6-3.7 chars/token, so "p4k" tokenized to 6301 and "p12k" to ~19k (prompt +
256 > ctx 16384 -> server rejected with 400 on S0-1). It also adds an
independent warmup.txt so the warm-up request no longer primes the measured
p1k cell (S0-1 warmup used p1k.txt verbatim).

Actions per over-target cell (p4k -> <=4096, p12k -> <=12288):
  1. copy the original to prompts/archive-2026-10-02-char-est/ (sha kept)
  2. cut at the largest sentence boundary whose prefix fits the target
  3. recount, write, update manifest.json (actual_tokens), SOURCES.md,
     sha256sums.txt
p1k stays (1115 tok, +8.9% of 1K - within noise, documented).
"""
import hashlib
import json
import pathlib
import re
import shutil
import sys

PROMPTS = pathlib.Path("/mnt/WorkDisk/workspace/worktree/1q3ry0vb/baseline-flash-next"
                       "/docs/evidence/strata-3060-ab/prompts")
TPATH = pathlib.Path("/mnt/SSD/strata-models/packs/iq3_s/tokenizer")
_ST_PATH = pathlib.Path("/mnt/WorkDisk/strata/src-v0.1.29/tools/strata_tokenizer.py")
import importlib.util  # noqa: E402

_spec = importlib.util.spec_from_file_location("strata_tokenizer", _ST_PATH)
assert _spec and _spec.loader
ST = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(ST)

TARGETS = {"p4k": 4096, "p12k": 12288}
BOUNDARY = re.compile(r"[.!?\n。！？;；]")


def load_tokenizer():
    vocab = json.loads((TPATH / "vocab.json").read_text(encoding="utf-8"))
    toks = [None] * len(vocab)
    for t, i in vocab.items():
        toks[i] = t
    merges = (TPATH / "merges.txt").read_text(encoding="utf-8").split("\n")
    types = json.loads((TPATH / "token_type.json").read_text(encoding="utf-8"))
    return ST.Tokenizer(toks, merges, types)


def ntok(tok, text):
    return len(tok.encode(text))


def boundaries(text):
    # candidate cut points: every sentence end, sorted ascending, excluding very end
    pts = sorted({m.end() for m in BOUNDARY.finditer(text)})
    return [p for p in pts if 100 < p < len(text) - 100]


def trim_to(tok, text, target):
    """Longest sentence-boundary prefix with token count <= target, as close as
    possible to target (>= 97%)."""
    b = boundaries(text)
    lo, hi = 0, len(b) - 1
    best = 0
    while lo <= hi:
        mid = (lo + hi) // 2
        c = ntok(tok, text[:b[mid]])
        if c <= target:
            best = mid
            lo = mid + 1
        else:
            hi = mid - 1
    # try to grow one boundary if we are short of 97% (non-monotonic BPE edges)
    if best + 1 < len(b) and ntok(tok, text[:b[best + 1]]) <= target:
        best += 1
    cut = b[best]
    out = text[:cut].rstrip()
    if not out.endswith("\n"):
        out += "\n"
    return out


def sha256(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def main():
    tok = load_tokenizer()
    archive = PROMPTS / "archive-2026-10-02-char-est"
    archive.mkdir(exist_ok=True)
    report = []

    for cell, target in TARGETS.items():
        f = PROMPTS / ("%s.txt" % cell)
        original = f.read_text(encoding="utf-8")
        count = ntok(tok, original)
        if count <= target:
            report.append("%s: %d tok <= %d, kept" % (cell, count, target))
            continue
        dest = archive / f.name
        if not dest.exists():
            shutil.copy2(f, dest)
        trimmed = trim_to(tok, original, target)
        new_count = ntok(tok, trimmed)
        assert new_count <= target, (cell, new_count, target)
        old_sha = sha256(dest)
        f.write_text(trimmed, encoding="utf-8")
        report.append("%s: %d -> %d tok (target %d), old_sha=%s new_sha=%s"
                      % (cell, count, new_count, target, old_sha[:16], sha256(f)[:16]))

    # warmup: independent content (t0005 bench paragraph), never a measured cell
    warm = PROMPTS / "warmup.txt"
    if not warm.exists():
        warm.write_text(
            "Write a detailed technical explanation of how a mixture-of-experts language model "
            "routes tokens to experts, why load balancing matters, and how expert weights can be "
            "cached across CPU RAM and GPU VRAM. Be thorough and concrete.\n", encoding="utf-8")
    report.append("warmup.txt: %d tok (independent, was t0005 bench prompt)" % ntok(tok, warm.read_text()))

    # recount everything for the manifest
    manifest_path = PROMPTS / "manifest.json"
    manifest = json.loads(manifest_path.read_text())
    for cell in manifest["cells"]:
        if cell["kind"] == "prefill_decode":
            n = ntok(tok, (PROMPTS / cell["file"]).read_text(encoding="utf-8"))
        else:
            n = [ntok(tok, (PROMPTS / tf).read_text(encoding="utf-8")) for tf in cell["files"]]
        cell["actual_tokens"] = n
        report.append("%s actual_tokens=%s" % (cell["id"], n))
    manifest_path.write_text(json.dumps(manifest, indent=1) + "\n")

    # cumulative prefix conversation must fit ctx - max_new - template margin
    pcell = [c for c in manifest["cells"] if c["id"] == "prefix"][0]
    t1, t2, t3 = pcell["actual_tokens"]
    cum3 = t1 + t2 + t3 + 80  # rough chat-template + filler margin
    report.append("prefix cumulative turn3 ~%d tokens (ctx budget 16128)" % cum3)
    assert cum3 < 16128, "prefix conversation would not fit ctx 16384"

    # regenerate sha256sums.txt
    lines = []
    for f in sorted(PROMPTS.glob("*.txt")):
        lines.append("%s  %s" % (sha256(f), f.name))
    (PROMPTS / "sha256sums.txt").write_text("\n".join(lines) + "\n")

    print("\n".join(report))

    # append deviation note to SOURCES.md
    sources = PROMPTS / "SOURCES.md"
    note = ("\n## 2026-10-02: cell rebuild with real tokenizer counts (t0006 S0-1 abort)\n\n"
            "The cells were first sized by chars/4. The Qwen3.5 BPE on this content runs "
            "~2.6-3.7 chars/token, so p4k tokenized to 6301 and p12k to ~19k; p12k + 256 "
            "completion exceeded ctx 16384 and the server rejected it with HTTP 400 "
            "(arm S0-1 aborted, void). p4k and p12k were trimmed at sentence boundaries to "
            "<= 4096 / <= 12288 tokens with the pack tokenizer "
            "(/mnt/SSD/strata-models/packs/iq3_s/tokenizer); originals kept under "
            "archive-2026-10-02-char-est/ with their old sha256. p1k stays at 1115 tokens "
            "(+8.9% of 1K, within noise). warmup.txt added (independent content = t0005 bench "
            "prompt): the S0-1 warm-up used p1k.txt verbatim and primed the measured p1k cell "
            "(1110/1115 tokens reused on rep0) - fixed for all later arms. sha256sums.txt "
            "regenerated; this is a deviation from the pre-registered cell composition, "
            "documented for the README.\n")
    with sources.open("a", encoding="utf-8") as f:
        f.write(note)
    print("SOURCES.md updated, sha256sums.txt regenerated")


if __name__ == "__main__":
    main()
