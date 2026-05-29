#!/usr/bin/env python3
"""
Sync open review findings to GitHub issues, grouped by feature area.

Reads reviews/groups.yml to determine which findings belong together.
Creates one consolidated issue per group (not one per finding).
Findings not listed in any group fall back to per-finding behavior.

Idempotent: safe to re-run; skips resolved findings and findings that already
have an issue number.

Usage:
    python scripts/sync_reviews_to_github.py [--dry-run]
"""

import re
import subprocess
import sys
from pathlib import Path

try:
    import yaml
    _YAML = True
except ImportError:
    _YAML = False

REPO_ROOT = Path(__file__).resolve().parent.parent
REVIEWS_DIR = REPO_ROOT / "reviews"
GROUPS_FILE = REVIEWS_DIR / "groups.yml"

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


def load_groups() -> dict:
    if not _YAML or not GROUPS_FILE.exists():
        return {}
    return yaml.safe_load(GROUPS_FILE.read_text()).get("groups", {})


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


def build_group_body(group_cfg: dict, group_findings: list[dict]) -> str:
    depends_on = group_cfg.get("depends_on")
    note = group_cfg.get("note")

    checklist_lines = []
    for f in group_findings:
        badge = SEVERITY_BADGE.get(f["severity"], f"P{f['severity']}")
        checklist_lines.append(
            f"- [ ] **{f['finding_id']}** — {f['title']}  \n"
            f"  {badge} · `{f['file_ref']}`"
        )
    checklist = "\n".join(checklist_lines)

    dep_section = ""
    if depends_on:
        dep_section = f"\n> **Depends on:** resolving `{depends_on}` first.\n"
    elif note:
        dep_section = f"\n> **Note:** {note}\n"

    detail_blocks = []
    for f in group_findings:
        block = f["block"].strip()
        if block.endswith("---"):
            block = block[:-3].strip()
        detail_blocks.append(block)

    details = "\n\n---\n\n".join(detail_blocks)

    source_files = ", ".join(
        sorted({f["file_path"].name for f in group_findings})
    )

    return (
        f"## Findings\n\n"
        f"{checklist}\n"
        f"{dep_section}\n"
        f"---\n\n"
        f"## Details\n\n"
        f"{details}\n\n"
        f"---\n"
        f"*Auto-created from `{source_files}` by sync_reviews_to_github.py*"
    )


def build_single_body(f: dict) -> str:
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


def write_issue_number(file_path: Path, finding_id: str, issue_number: int) -> None:
    text = file_path.read_text()
    header_re = re.compile(
        rf"^### \[{re.escape(finding_id)}\] .+$", re.MULTILINE
    )
    header_m = header_re.search(text)
    if not header_m:
        print(f"    WARNING: header for {finding_id} not found — skipping write-back")
        return

    pre = text[: header_m.end()]
    post = text[header_m.end() :]

    # Update existing **Issue:** if present
    new_post = re.sub(
        r"(\*\*Issue:\*\* )#\d+", rf"\g<1>#{issue_number}", post, count=1
    )
    # Or insert after **Status:** open
    if new_post == post:
        new_post = re.sub(
            r"(\*\*Status:\*\* open\n)(\*\*Assigned:\*\*)",
            rf"\1**Issue:** #{issue_number}\n\2",
            post,
            count=1,
        )

    if new_post == post:
        print(f"    WARNING: could not write issue number for {finding_id}")
        return

    file_path.write_text(pre + new_post)


def main() -> None:
    review_files = sorted(REVIEWS_DIR.glob("m*.md"))
    if not review_files:
        print("No review files found in reviews/.")
        sys.exit(1)

    if DRY_RUN:
        print("=== DRY RUN — no issues will be created ===\n")

    # Load all findings across all review files
    all_findings: dict[str, dict] = {}
    for review_file in review_files:
        for f in parse_findings(review_file.read_text(), review_file):
            all_findings[f["finding_id"]] = f

    # Load groups config
    groups = load_groups()

    # Build finding_id → group_key index
    finding_to_group: dict[str, str] = {}
    for group_key, group_cfg in groups.items():
        for fid in group_cfg.get("findings", []):
            finding_to_group[fid] = group_key

    created = 0
    skipped = 0

    # --- Process grouped findings ---
    processed_groups: set[str] = set()
    for group_key, group_cfg in groups.items():
        finding_ids: list[str] = group_cfg.get("findings", [])
        title: str = group_cfg["title"]
        labels: str = ",".join(group_cfg["labels"])

        group_findings = [
            all_findings[fid] for fid in finding_ids if fid in all_findings
        ]
        open_unissued = [
            f for f in group_findings
            if f["status"] != "resolved" and f["issue_number"] is None
        ]
        already_issued = [
            f for f in group_findings if f["issue_number"] is not None
        ]

        # If any finding already has an issue number, propagate it to the others
        if already_issued:
            existing_numbers = {f["issue_number"] for f in already_issued}
            if len(existing_numbers) == 1 and not open_unissued:
                print(f"skip  {group_key}  (all findings → #{list(existing_numbers)[0]})")
                skipped += 1
                processed_groups.add(group_key)
                continue

            if len(existing_numbers) == 1 and open_unissued:
                # Propagate the existing issue number to unissued findings
                existing_num = list(existing_numbers)[0]
                print(f"propagate  {group_key}  #{existing_num} → {[f['finding_id'] for f in open_unissued]}")
                if not DRY_RUN:
                    for f in open_unissued:
                        write_issue_number(f["file_path"], f["finding_id"], existing_num)
                processed_groups.add(group_key)
                skipped += 1
                continue

        if not open_unissued:
            print(f"skip  {group_key}  (no open unissued findings)")
            skipped += 1
            processed_groups.add(group_key)
            continue

        print(f"\ncreate group  {group_key}")
        print(f"  title: {title[:65]}")
        print(f"  findings: {[f['finding_id'] for f in open_unissued]}")

        if DRY_RUN:
            print(f"  labels: {labels}")
            created += 1
            processed_groups.add(group_key)
            continue

        body = build_group_body(group_cfg, open_unissued)
        url = gh("issue", "create", "--title", title, "--body", body, "--label", labels)
        issue_number = int(url.rstrip("/").split("/")[-1])
        print(f"  → #{issue_number}  {url}")

        for f in open_unissued:
            write_issue_number(f["file_path"], f["finding_id"], issue_number)
            print(f"  → wrote #{issue_number} to {f['finding_id']}")

        created += 1
        processed_groups.add(group_key)

    # --- Process ungrouped findings (fallback: one issue per finding) ---
    for review_file in review_files:
        for f in parse_findings(review_file.read_text(), review_file):
            fid = f["finding_id"]
            if fid in finding_to_group:
                continue  # already handled above

            if f["status"] == "resolved":
                skipped += 1
                continue

            if f["issue_number"] is not None:
                skipped += 1
                continue

            issue_title = f"[{fid}] {f['title']}"
            body = build_single_body(f)
            labels = ",".join([
                "review-finding",
                SEVERITY_LABEL[f["severity"]],
                f"milestone-m{f['milestone']}",
            ])

            print(f"\ncreate  {fid}: {issue_title[:65]}")

            if DRY_RUN:
                print(f"  labels: {labels}")
                created += 1
                continue

            url = gh("issue", "create", "--title", issue_title, "--body", body, "--label", labels)
            issue_number = int(url.rstrip("/").split("/")[-1])
            print(f"  → #{issue_number}  {url}")
            write_issue_number(review_file, fid, issue_number)
            created += 1

    print(f"\nDone: {created} {'would be ' if DRY_RUN else ''}created, {skipped} skipped.")


if __name__ == "__main__":
    main()
