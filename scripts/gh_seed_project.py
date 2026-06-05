#!/usr/bin/env python3
"""Seed GitHub issues for the Hydra Vortex roadmap (GitHub Projects workflow).

Idempotent: an issue is created only if its exact title doesn't already exist.
Each roadmap item gets its native **Milestone** + a priority label + the `task` label.
M3.1.x are linked as **sub-issues** of the M3 persistence umbrella (best-effort).

Findings are NOT created here — they already exist as issues (`review-finding`).
Board membership is handled by the Project's built-in **auto-add** workflow once the
Project v2 "Hydra Vortex" exists; this script only manages the issues themselves.

Requires: `gh` authenticated with `repo` scope. Run: `python scripts/gh_seed_project.py`.
"""
import json
import subprocess
import sys

REPO = "ddvnguyen/hydra_vortex"

# (title, body, milestone, priority_label, sub_of_title|None)
ROADMAP = [
    ("M-Perf.1 · DeviceProfiler measurement spike",
     "BUILD FIRST. Measure P100 decode w/ target+draft resident, draft acceptance "
     "(MoE+SSM), iperf3 NAT bandwidth, SSM cache-prompt bug scope. Gates the track. "
     "See docs/milestone-perf.md.", "M-Perf — Heterogeneous Performance", "p1-high", None),
    ("M-Perf.2 · Heterogeneous speculative decoding",
     "Draft Qwen3-0.5B on P100 via llama.cpp RPC; SpecOrchestrator + ngram-mod fallback. "
     "No fork. 1.4-1.8x lossless decode.", "M-Perf — Heterogeneous Performance", "p1-high", None),
    ("M-Perf.3 · Streaming chunked-prefill KV (P/D)",
     "Layer-chunked KV streamed as prefill layers finish. Needs FORK: per-layer state "
     "endpoints + attention-KV-only SSM workaround. TTFT 80K down >=3x.",
     "M-Perf — Heterogeneous Performance", "p1-high", None),
    ("M-Perf.4 · Pipeline scaffolding",
     "Coordinator -> asyncio dataflow of Stages. Foundation for deferred Tier-2 PRP/Halda.",
     "M-Perf — Heterogeneous Performance", "p2-low", None),

    ("M3 · NVMe write-behind persistence (C# re-spec)",
     "SQLite metadata + write-behind tmpfs->NVMe + startup recovery, re-spec'd for the "
     "C# Store. Enables reboot survival (store is volatile tmpfs today).",
     "M3 — Persistence & Real Obs", "p1-high", None),
    ("M3.1.1 · SQLite metadata (C# Microsoft.Data.Sqlite)",
     "Session + chunk metadata table; backup tracking.",
     "M3 — Persistence & Real Obs", "p2-low", "M3 · NVMe write-behind persistence (C# re-spec)"),
    ("M3.1.2 · Write-behind task (tmpfs->NVMe, BackgroundService)",
     "Periodic copy of unbacked chunks tmpfs->NVMe; mark backed_up.",
     "M3 — Persistence & Real Obs", "p2-low", "M3 · NVMe write-behind persistence (C# re-spec)"),
    ("M3.1.3 · Startup recovery (restore hot sessions)",
     "On boot, restore top-N recent sessions NVMe->tmpfs within 30s.",
     "M3 — Persistence & Real Obs", "p2-low", "M3 · NVMe write-behind persistence (C# re-spec)"),
    ("M3 · Harden monitoring/obs for real",
     "Real metrics on Grafana: save/restore/migration latency, draft acceptance, cache-hit.",
     "M3 — Persistence & Real Obs", "p2-low", None),

    ("M4 · Model distribution from Store",
     "Raw model PUT to Store + agent GET-if-missing on startup; no manual scp.",
     "M4 — Model Management & Multi-Modal", "p2-low", None),
    ("M4 · Dynamic model load/swap",
     "Hot-swap / load models on demand without a full stack restart.",
     "M4 — Model Management & Multi-Modal", "p2-low", None),
    ("M4 · Vision / embedding / audio model support",
     "Serve multimodal models alongside the LLM.",
     "M4 — Model Management & Multi-Modal", "p2-low", None),
    ("M4 · systemd lifecycle (ramdisk->store->agents->coordinator)",
     "Boot-ordered systemd units; builds on the P100 rootless systemctl --user work.",
     "M4 — Model Management & Multi-Modal", "p2-low", None),

    ("M5 · Langfuse tracing",
     "Optional/feature-flagged request tracing: tokens, routing, migration spans.",
     "M5 — LLM Obs & Agentic", "p2-low", None),
    ("M5 · A/B testing",
     "A/B routing experiments across model/quant/draft/policy variants.",
     "M5 — LLM Obs & Agentic", "p2-low", None),
    ("M5 · Agentic system",
     "Agentic orchestration over the Coordinator (tool use, multi-turn, sub-agents).",
     "M5 — LLM Obs & Agentic", "p2-low", None),
]


def gh(*args, check=True):
    r = subprocess.run(["gh", *args], capture_output=True, text=True)
    if check and r.returncode != 0:
        print(f"  ! gh {' '.join(args)} -> {r.stderr.strip()[:200]}")
    return r


def existing_issues():
    r = gh("issue", "list", "--repo", REPO, "--state", "all", "--limit", "300",
           "--json", "number,title")
    return {i["title"]: i["number"] for i in json.loads(r.stdout or "[]")}


def node_id(num):
    r = gh("issue", "view", str(num), "--repo", REPO, "--json", "id")
    return json.loads(r.stdout or "{}").get("id") if r.returncode == 0 else None


def link_sub_issue(parent_num, child_num):
    pid, cid = node_id(parent_num), node_id(child_num)
    if not (pid and cid):
        return False
    q = ("mutation($p:ID!,$c:ID!){addSubIssue(input:{issueId:$p,subIssueId:$c})"
         "{subIssue{number}}}")
    r = gh("api", "graphql", "-f", f"query={q}", "-f", f"p={pid}", "-f", f"c={cid}", check=False)
    return r.returncode == 0


def main():
    if gh("auth", "status", check=False).returncode != 0:
        sys.exit("gh not authenticated.")
    have = existing_issues()
    nums = {}
    for title, body, milestone, prio, _ in ROADMAP:
        if title in have:
            print(f"  = {title}")
            nums[title] = have[title]
            continue
        r = gh("issue", "create", "--repo", REPO, "--title", title, "--body", body,
               "--milestone", milestone, "--label", "task", "--label", prio)
        if r.returncode == 0:
            num = r.stdout.strip().rstrip("/").split("/")[-1]
            print(f"  + #{num} {title}")
            nums[title] = int(num)
        # else gh() already printed the error

    print("-- linking sub-issues --")
    for title, _, _, _, sub_of in ROADMAP:
        if sub_of and title in nums and sub_of in nums:
            ok = link_sub_issue(nums[sub_of], nums[title])
            print(f"  {'linked' if ok else 'skip  '}: {title} -> {sub_of}")

    print("\nDone. Issues exist with milestones + labels. The Project's auto-add "
          "workflow will pull them onto the board once Project 'Hydra Vortex' exists.")


if __name__ == "__main__":
    main()
