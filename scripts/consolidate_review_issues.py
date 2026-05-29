#!/usr/bin/env python3
"""
One-time migration: consolidate 24 per-finding GitHub issues into 8 grouped issues.

For each group in reviews/groups.yml:
  1. Creates a consolidated issue with a checklist + full finding bodies
  2. Closes each old individual issue with a pointer to the new consolidated one
  3. Updates review files: replaces **Issue:** #old with **Issue:** #new

Idempotent: safe to re-run. Skips groups that already have a consolidated issue
(detected by checking if the first finding in the group already has the same issue
number as the others).

Usage:
    python scripts/consolidate_review_issues.py [--dry-run]
"""

import re
import subprocess
import sys
from pathlib import Path

import yaml

REPO_ROOT = Path(__file__).resolve().parent.parent
REVIEWS_DIR = REPO_ROOT / "reviews"
GROUPS_FILE = REVIEWS_DIR / "groups.yml"

DRY_RUN = "--dry-run" in sys.argv

SEVERITY_BADGE = {"0": "P0 Critical 🔴", "1": "P1 High 🟡", "2": "P2 Low ⚪"}


def gh(*args: str) -> str:
    result = subprocess.run(["gh", *args], capture_output=True, text=True, cwd=REPO_ROOT)
    if result.returncode != 0:
        raise RuntimeError(f"gh {' '.join(args)}\n  {result.stderr.strip()}")
    return result.stdout.strip()


def load_groups() -> dict:
    return yaml.safe_load(GROUPS_FILE.read_text())["groups"]


def parse_all_findings() -> dict[str, dict]:
    """Return {finding_id: finding_dict} across all review files."""
    header_re = re.compile(r"^### (\[M(\d+)-P([012])-(\d+)\] (.+))$", re.MULTILINE)
    findings: dict[str, dict] = {}

    for review_file in sorted(REVIEWS_DIR.glob("m*.md")):
        text = review_file.read_text()
        headers = list(header_re.finditer(text))

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

            findings[finding_id] = {
                "finding_id": finding_id,
                "milestone": milestone,
                "severity": severity,
                "seq": seq,
                "title": title,
                "status": status_m.group(1) if status_m else "unknown",
                "issue_number": int(issue_m.group(1)) if issue_m else None,
                "file_ref": file_m.group(1).strip() if file_m else "",
                "block": block,
                "file_path": review_file,
            }

    return findings


def build_consolidated_body(
    group_key: str, group_cfg: dict, findings: dict[str, dict], old_issue_numbers: list[int]
) -> str:
    finding_ids: list[str] = group_cfg["findings"]
    depends_on = group_cfg.get("depends_on")
    note = group_cfg.get("note")

    # Checklist
    checklist_lines = []
    for fid in finding_ids:
        f = findings.get(fid)
        if not f:
            continue
        badge = SEVERITY_BADGE.get(f["severity"], f"P{f['severity']}")
        file_ref = f["file_ref"]
        checklist_lines.append(
            f"- [ ] **{fid}** — {f['title']}  \n  {badge} · `{file_ref}`"
        )

    checklist = "\n".join(checklist_lines)

    # Dependency note
    dep_section = ""
    if depends_on:
        dep_section = f"\n> **Depends on:** resolving `{depends_on}` first.\n"
    elif note:
        dep_section = f"\n> **Note:** {note}\n"

    # Full finding details
    detail_blocks = []
    for fid in finding_ids:
        f = findings.get(fid)
        if not f:
            continue
        block = f["block"].strip()
        if block.endswith("---"):
            block = block[:-3].strip()
        detail_blocks.append(block)

    details = "\n\n---\n\n".join(detail_blocks)

    old_refs = ", ".join(f"#{n}" for n in sorted(old_issue_numbers))

    return (
        f"## Findings\n\n"
        f"{checklist}\n"
        f"{dep_section}\n"
        f"---\n\n"
        f"## Details\n\n"
        f"{details}\n\n"
        f"---\n"
        f"*Consolidated from individual issues: {old_refs}*"
    )


def update_issue_in_review(file_path: Path, finding_id: str, new_issue: int) -> bool:
    """Replace **Issue:** #old with **Issue:** #new for the given finding. Returns True if changed."""
    text = file_path.read_text()

    header_re = re.compile(rf"^### \[{re.escape(finding_id)}\] .+$", re.MULTILINE)
    header_m = header_re.search(text)
    if not header_m:
        print(f"    WARNING: header for {finding_id} not found")
        return False

    pre = text[: header_m.end()]
    post = text[header_m.end() :]

    new_post = re.sub(
        r"(\*\*Issue:\*\* )#\d+",
        rf"\g<1>#{new_issue}",
        post,
        count=1,
    )

    if new_post == post:
        # No existing **Issue:** — insert after **Status:** open
        new_post = re.sub(
            r"(\*\*Status:\*\* open\n)(\*\*Assigned:\*\*)",
            rf"\1**Issue:** #{new_issue}\n\2",
            post,
            count=1,
        )

    if new_post == post:
        print(f"    WARNING: could not update issue for {finding_id}")
        return False

    file_path.write_text(pre + new_post)
    return True


def main() -> None:
    if DRY_RUN:
        print("=== DRY RUN — no changes will be made ===\n")

    groups = load_groups()
    all_findings = parse_all_findings()

    created = 0
    skipped = 0

    for group_key, group_cfg in groups.items():
        finding_ids: list[str] = group_cfg["findings"]
        title: str = group_cfg["title"]
        labels: str = ",".join(group_cfg["labels"])

        # Gather the old individual issue numbers for findings in this group
        old_issue_numbers: list[int] = []
        for fid in finding_ids:
            f = all_findings.get(fid)
            if f and f["issue_number"] is not None:
                old_issue_numbers.append(f["issue_number"])

        # Check if already consolidated: all findings point to the same issue number
        unique_issues = set(old_issue_numbers)
        if len(unique_issues) == 1 and len(old_issue_numbers) == len(finding_ids):
            print(f"SKIP {group_key}: already consolidated at #{list(unique_issues)[0]}")
            skipped += 1
            continue

        print(f"\nCREATE {group_key}: {title}")
        print(f"  consolidating: {finding_ids}")
        print(f"  closing old issues: {old_issue_numbers}")

        if DRY_RUN:
            created += 1
            continue

        # Build consolidated issue body
        body = build_consolidated_body(group_key, group_cfg, all_findings, old_issue_numbers)

        # Create consolidated issue
        url = gh("issue", "create", "--title", title, "--body", body, "--label", labels)
        new_issue = int(url.rstrip("/").split("/")[-1])
        print(f"  → Created #{new_issue}: {url}")

        # Close old issues with pointer to new consolidated issue
        for old_num in old_issue_numbers:
            comment = (
                f"This finding has been consolidated into #{new_issue}: **{title}**.\n\n"
                f"All findings for this area are tracked there. Closing this individual issue."
            )
            gh("issue", "close", str(old_num), "--comment", comment)
            print(f"  → Closed #{old_num}")

        # Update review files with new consolidated issue number
        updated_files: set[Path] = set()
        for fid in finding_ids:
            f = all_findings.get(fid)
            if not f:
                continue
            if update_issue_in_review(f["file_path"], fid, new_issue):
                updated_files.add(f["file_path"])
                print(f"  → Updated {fid} in {f['file_path'].name} → #{new_issue}")

        created += 1

    print(f"\nDone: {created} {'would be ' if DRY_RUN else ''}created, {skipped} skipped.")


if __name__ == "__main__":
    main()
