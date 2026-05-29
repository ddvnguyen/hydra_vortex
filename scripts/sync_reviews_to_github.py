#!/usr/bin/env python3
"""
Sync open review findings to GitHub issues.

For each open finding in reviews/m*.md that doesn't yet have an **Issue:** #N line,
creates a GitHub issue and writes the issue number back into the review file.

Idempotent: safe to re-run; skips resolved findings and findings that already have
an issue number.

Usage:
    python scripts/sync_reviews_to_github.py [--dry-run]
"""

import re
import subprocess
import sys
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parent.parent
REVIEWS_DIR = REPO_ROOT / "reviews"

SEVERITY_LABEL = {"0": "p0-critical", "1": "p1-high", "2": "p2-low"}
SEVERITY_BADGE = {"0": "P0 Critical 🔴", "1": "P1 High 🟡", "2": "P2 Low ⚪"}

DRY_RUN = "--dry-run" in sys.argv


def gh(*args: str) -> str:
    result = subprocess.run(
        ["gh", *args], capture_output=True, text=True, cwd=REPO_ROOT
    )
    if result.returncode != 0:
        raise RuntimeError(f"gh {' '.join(args)}\n  stderr: {result.stderr.strip()}")
    return result.stdout.strip()


def parse_findings(text: str, file_path: Path) -> list[dict]:
    header_re = re.compile(
        r"^### (\[M(\d+)-P([012])-(\d+)\] (.+))$", re.MULTILINE
    )
    headers = list(header_re.finditer(text))
    findings = []

    for i, h in enumerate(headers):
        block_end = headers[i + 1].start() if i < len(headers) - 1 else len(text)
        block = text[h.start() : block_end]

        milestone = h.group(2)
        severity = h.group(3)
        seq = h.group(4)
        title = h.group(5).strip()
        finding_id = f"M{milestone}-P{severity}-{seq}"

        status_m = re.search(r"^\*\*Status:\*\* (\w+)", block, re.MULTILINE)
        issue_m = re.search(r"^\*\*Issue:\*\* #(\d+)", block, re.MULTILINE)
        file_m = re.search(r"^\*\*File:\*\* (.+)$", block, re.MULTILINE)

        findings.append(
            {
                "finding_id": finding_id,
                "milestone": milestone,
                "severity": severity,
                "seq": seq,
                "title": title,
                "status": status_m.group(1) if status_m else "unknown",
                "issue_number": int(issue_m.group(1)) if issue_m else None,
                "file_ref": file_m.group(1).strip() if file_m else "",
                "block": block,
                "file_path": file_path,
            }
        )

    return findings


def build_issue_body(f: dict) -> str:
    block = f["block"].strip()
    if block.endswith("---"):
        block = block[:-3].strip()

    badge = SEVERITY_BADGE.get(f["severity"], f"P{f['severity']}")
    source = f"reviews/m{f['milestone']}-review.md"

    return (
        f"{block}\n\n"
        f"---\n"
        f"*Auto-created from `{source}` · Finding `{f['finding_id']}` · {badge}*"
    )


def get_labels(f: dict) -> str:
    return ",".join(
        [
            "review-finding",
            SEVERITY_LABEL[f["severity"]],
            f"milestone-m{f['milestone']}",
        ]
    )


def insert_issue_number(file_path: Path, finding_id: str, issue_number: int) -> None:
    text = file_path.read_text()

    # Find the specific finding header, then insert **Issue:** after **Status:** open
    header_re = re.compile(
        rf"^### \[{re.escape(finding_id)}\] .+$", re.MULTILINE
    )
    header_m = header_re.search(text)
    if not header_m:
        print(f"    WARNING: header for {finding_id} not found — skipping write-back")
        return

    pre = text[: header_m.end()]
    post = text[header_m.end() :]

    new_post = re.sub(
        r"(\*\*Status:\*\* open\n)(\*\*Assigned:\*\*)",
        rf"\1**Issue:** #{issue_number}\n\2",
        post,
        count=1,
    )

    if new_post == post:
        print(f"    WARNING: could not insert issue number for {finding_id}")
        return

    file_path.write_text(pre + new_post)


def main() -> None:
    review_files = sorted(REVIEWS_DIR.glob("m*.md"))
    if not review_files:
        print("No review files found in reviews/.")
        sys.exit(1)

    if DRY_RUN:
        print("=== DRY RUN — no issues will be created ===\n")

    created = 0
    skipped = 0

    for review_file in review_files:
        print(f"\n=== {review_file.name} ===")
        text = review_file.read_text()
        findings = parse_findings(text, review_file)

        for f in findings:
            fid = f["finding_id"]

            if f["status"] == "resolved":
                print(f"  skip  {fid}  (resolved)")
                skipped += 1
                continue

            if f["issue_number"] is not None:
                print(f"  skip  {fid}  (already #{f['issue_number']})")
                skipped += 1
                continue

            issue_title = f"[{fid}] {f['title']}"
            body = build_issue_body(f)
            labels = get_labels(f)

            if DRY_RUN:
                print(f"  would create: {issue_title[:70]}")
                print(f"    labels: {labels}")
                created += 1
                continue

            print(f"  create {fid}: {issue_title[:65]}…")
            url = gh(
                "issue", "create",
                "--title", issue_title,
                "--body", body,
                "--label", labels,
            )

            issue_number = int(url.rstrip("/").split("/")[-1])
            print(f"    → #{issue_number}  {url}")

            insert_issue_number(review_file, fid, issue_number)
            created += 1

    print(f"\nDone: {created} {'would be ' if DRY_RUN else ''}created, {skipped} skipped.")


if __name__ == "__main__":
    main()
