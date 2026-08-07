#!/usr/bin/env bash
# List valid OCI image tags for the deploy-heads `image-tag` picker.
#
# Queries GHCR (ghcr.io/ddvnguyen/llama-server) and keeps only suffixes that
# exist for BOTH arch refs (sm86-sm120-llama-engine-<s> and
# sm60-llama-engine-<s>) so a single picker selection works for all GPUs.
# Prints an `options:` block ready to paste into .github/workflows/deploy-heads.yml.
#
# Usage: bash scripts/ci/list-llama-image-tags.sh
set -euo pipefail

python3 - <<'PY'
import base64, json, os, urllib.request

REPO = "ddvnguyen/llama-server"
REGISTRY = "https://ghcr.io"
PREFIXES = ("sm86-sm120-llama-engine", "sm60-llama-engine")

def token():
    for path in (
        os.path.expanduser("~/.config/containers/auth.json"),
        "/root/.config/containers/auth.json",
    ):
        try:
            creds = json.load(open(path)).get("auths", {}).get("ghcr.io", {})
            if "auth" in creds:
                user, pwd = base64.b64decode(creds["auth"]).decode().split(":", 1)
                req = urllib.request.Request(
                    f"{REGISTRY}/token?scope=repository:{REPO}:pull&service=ghcr.io",
                    headers={"Authorization": "Basic "
                              + base64.b64encode(f"{user}:{pwd}".encode()).decode()},
                )
                return json.load(urllib.request.urlopen(req, timeout=15))["token"]
        except Exception:
            continue
    return ""

tok = token()
req = urllib.request.Request(
    f"{REGISTRY}/v2/{REPO}/tags/list?n=200",
    headers={"Authorization": f"Bearer {tok}"},
)
tags = set(json.load(urllib.request.urlopen(req, timeout=15)).get("tags", []))

valid = []
for prefix in PREFIXES:
    for t in tags:
        if t.startswith(prefix + "-"):
            suffix = t[len(prefix) + 1:]
            if suffix not in valid and all(f"{p}-{suffix}" in tags for p in PREFIXES):
                valid.append(suffix)

print("# deploy-heads `image-tag` options (valid suffixes for ALL GPUs)")
seen = set()
print("options:")
for s in ["current", "latest"] + sorted(valid):
    if s in seen:
        continue
    seen.add(s)
    print(f'          - "{s}"')
print()
print(f"# {len(valid)} valid build suffix(es)")
PY
