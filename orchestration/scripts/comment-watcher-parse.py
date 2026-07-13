#!/usr/bin/env python3
"""Parse issues JSON from stdin, find @hydra commands in latest comments."""
import sys
import re
import json

repo = sys.argv[1] if len(sys.argv) > 1 else ""
cursor = sys.argv[2] if len(sys.argv) > 2 else ""

pattern = re.compile(r'@hydra\s+/(plan|skip-pm|approve|implement|merge)\b')

try:
    issues = json.load(sys.stdin)
except Exception:
    issues = []

results = []
latest_ts = cursor

for issue in issues:
    num = issue['number']
    comments = issue.get('comments', [])
    if not comments:
        continue
    last = comments[-1]
    body = last.get('body', '')
    created = last.get('createdAt', '')

    # Skip if older than or equal to cursor
    if cursor and created and created <= cursor:
        continue

    m = pattern.search(body)
    if m:
        cmd = m.group(1)
        results.append(f'HUMAN_CMD issue={num} repo={repo} command=/{cmd} ts={created}')

    # Track latest comment timestamp across all issues
    if created and (not latest_ts or created > latest_ts):
        latest_ts = created

if results:
    print('FOUND')
    for r in results:
        print(r)
    print(f'CURSOR={latest_ts}')
else:
    print('NONE')
