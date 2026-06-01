#!/usr/bin/env python3
"""Seed a Plane Cloud workspace with the Hydra Vortex roadmap (the "Revolution plan").

Idempotent: re-running reuses existing project / modules / work items by name, so it
is safe to run repeatedly as the roadmap evolves. No third-party deps (urllib only).

Setup (see docs/PLANE_SETUP.md):
    export PLANE_API_KEY=plane_api_xxxxxxxx          # Plane profile > Personal Access Tokens
    export PLANE_WORKSPACE_SLUG=your-workspace-slug  # from your Plane workspace URL
    python scripts/plane_seed_roadmap.py

Milestones map to Plane *modules*; concrete tasks map to *work items* linked to them.
"""
import json
import os
import sys
import urllib.error
import urllib.request

BASE = os.environ.get("PLANE_BASE_URL", "https://api.plane.so").rstrip("/")
KEY = os.environ.get("PLANE_API_KEY")
SLUG = os.environ.get("PLANE_WORKSPACE_SLUG")

PROJECT_NAME = "Hydra Vortex"
PROJECT_IDENTIFIER = "HYDRA"

# (module_name, module_status, module_desc, [ (item_name, priority, desc, done) ])
ROADMAP = [
    ("Phase 0 — Stabilize", "in-progress",
     "Foundation: green CI/CD, restore obs, rebase local onto remote.", [
        ("Phase 0 · Reverse obs-strip + restore slot-wait", "high",
         "DONE — CoordinatorConfig nodes[] migration committed (2ce42a0); obs metrics + slot-wait restored; all unit tests green.", True),
        ("Phase 0 · Green main CI + rebase onto remote", "high",
         "Triage CI #67/#68/#69; rebase local 6 commits onto main/main (verified conflict-free, identical trees); user pushes.", False),
        ("Phase 0 · Refresh roadmap docs + auto-memory", "medium",
         "Update PROJECT_PLAN.md milestone table + CLAUDE.md; reviews/ workflow is gone (findings are GitHub issues now).", False),
     ]),
    ("M-Perf — Heterogeneous Performance", "in-progress",
     "Tier-1 performance track (~6-8 wk): spike -> spec-decode -> P/D streaming -> pipeline.", [
        ("M-Perf.1 · DeviceProfiler measurement spike", "urgent",
         "BUILD FIRST. Measure P100 decode w/ target+draft resident, draft acceptance (MoE+SSM), iperf3 NAT bandwidth, SSM cache-prompt bug scope. Gates the whole track.", False),
        ("M-Perf.2 · Heterogeneous speculative decoding", "high",
         "Draft Qwen3-0.5B on P100 via llama.cpp RPC; SpecOrchestrator + ngram-mod fallback. No fork. Research-verified 1.4-1.8x lossless decode.", False),
        ("M-Perf.3 · Streaming chunked-prefill KV (P/D)", "high",
         "Layer-chunked KV streamed as prefill layers finish. Needs FORK: per-layer state endpoints + attention-KV-only SSM workaround. Target TTFT 80K down >=3x.", False),
        ("M-Perf.4 · Pipeline scaffolding", "medium",
         "Refactor Coordinator -> asyncio dataflow of Stages. Foundation for deferred Tier-2 (prima.cpp PRP / Halda).", False),
     ]),
    ("M3 — Persistence & Real Obs", "planned",
     "NVMe write-behind persistence (re-spec'd for C# Store) + harden observability.", [
        ("M3 · NVMe write-behind persistence (C# re-spec)", "high",
         "SQLite metadata + write-behind tmpfs->NVMe + startup recovery, re-spec'd for the C# Store. Enables reboot survival (store is volatile tmpfs today).", False),
        ("M3 · Harden monitoring/obs for real", "medium",
         "Real metrics on Grafana: save/restore/migration latency, draft acceptance, cache-hit. The obs-for-real payoff.", False),
     ]),
    ("M4 — Model Management & Multi-Modal", "planned",
     "Model distribution + dynamic loading + multimodal model support.", [
        ("M4 · Model distribution from Store", "medium",
         "Raw model PUT to Store + agent GET-if-missing on startup; no manual scp.", False),
        ("M4 · Dynamic model load/swap", "medium",
         "Hot-swap / load models on demand without a full stack restart.", False),
        ("M4 · Vision / embedding / audio model support", "low",
         "Serve multimodal models (vision, embedding, audio) alongside the LLM.", False),
     ]),
    ("M5 — LLM Obs & Agentic", "planned",
     "Langfuse tracing, A/B testing, agentic orchestration.", [
        ("M5 · Langfuse tracing", "low",
         "Optional/feature-flagged request tracing: token counts, routing decisions, migration spans.", False),
        ("M5 · A/B testing + agentic system", "low",
         "A/B routing experiments; agentic orchestration layer.", False),
     ]),
]


def _req(method, path, body=None):
    url = f"{BASE}{path}"
    data = json.dumps(body).encode() if body is not None else None
    req = urllib.request.Request(url, data=data, method=method)
    req.add_header("X-API-Key", KEY)
    req.add_header("Content-Type", "application/json")
    try:
        with urllib.request.urlopen(req) as r:
            raw = r.read().decode()
            return r.status, (json.loads(raw) if raw else {})
    except urllib.error.HTTPError as e:
        return e.code, {"error": e.read().decode()[:400]}
    except urllib.error.URLError as e:
        sys.exit(f"network error reaching {BASE}: {e}")


def _results(payload):
    if isinstance(payload, list):
        return payload
    if isinstance(payload, dict):
        return payload.get("results", payload.get("items", []))
    return []


def get_or_create_project():
    _, data = _req("GET", f"/api/v1/workspaces/{SLUG}/projects/")
    for p in _results(data):
        if p.get("name") == PROJECT_NAME or p.get("identifier") == PROJECT_IDENTIFIER:
            print(f"  = project exists: {p['name']} ({p['id']})")
            return p["id"]
    st, data = _req("POST", f"/api/v1/workspaces/{SLUG}/projects/",
                    {"name": PROJECT_NAME, "identifier": PROJECT_IDENTIFIER})
    if st >= 300:
        sys.exit(f"project create failed [{st}]: {data}")
    print(f"  + created project: {PROJECT_NAME} ({data['id']})")
    return data["id"]


def find_done_state(pid):
    _, data = _req("GET", f"/api/v1/workspaces/{SLUG}/projects/{pid}/states/")
    for s in _results(data):
        if s.get("group") == "completed":
            return s["id"]
    return None


def get_modules(pid):
    _, data = _req("GET", f"/api/v1/workspaces/{SLUG}/projects/{pid}/modules/")
    return {m["name"]: m["id"] for m in _results(data)}


def ensure_module(pid, cache, name, desc, status):
    if name in cache:
        print(f"  = module exists: {name}")
        return cache[name]
    st, data = _req("POST", f"/api/v1/workspaces/{SLUG}/projects/{pid}/modules/",
                    {"name": name, "description": desc, "status": status})
    if st >= 300:
        print(f"  ! module '{name}' failed [{st}]: {data}")
        return None
    print(f"  + module: {name}")
    cache[name] = data["id"]
    return data["id"]


def list_work_items(pid):
    """Return (endpoint_name, {name: id}). Handles the issues->work-items rename."""
    for ep in ("work-items", "issues"):
        st, data = _req("GET", f"/api/v1/workspaces/{SLUG}/projects/{pid}/{ep}/")
        if st < 300:
            return ep, {i["name"]: i["id"] for i in _results(data)}
    return "work-items", {}


def create_work_item(pid, ep, name, desc_html, priority, state_id):
    body = {"name": name, "description_html": desc_html, "priority": priority}
    if state_id:
        body["state"] = state_id
    st, data = _req("POST", f"/api/v1/workspaces/{SLUG}/projects/{pid}/{ep}/", body)
    if st >= 300:
        print(f"  ! work item '{name}' failed [{st}]: {data}")
        return None
    print(f"    + {name}")
    return data["id"]


def link_module(pid, module_id, issue_id):
    for sub in ("module-issues", "module-work-items"):
        st, _ = _req(
            "POST",
            f"/api/v1/workspaces/{SLUG}/projects/{pid}/modules/{module_id}/{sub}/",
            {"issues": [issue_id]},
        )
        if st < 300:
            return True
    return False


def main():
    if not KEY or not SLUG:
        sys.exit("Set PLANE_API_KEY and PLANE_WORKSPACE_SLUG first (see docs/PLANE_SETUP.md).")
    print(f"Seeding Plane workspace '{SLUG}' at {BASE}")
    pid = get_or_create_project()
    done_state = find_done_state(pid)
    modules = get_modules(pid)
    ep, existing = list_work_items(pid)
    for mod_name, status, mod_desc, items in ROADMAP:
        mid = ensure_module(pid, modules, mod_name, mod_desc, status)
        for name, prio, desc, done in items:
            if name in existing:
                print(f"    = {name}")
                iid = existing[name]
            else:
                iid = create_work_item(pid, ep, name, f"<p>{desc}</p>", prio,
                                        done_state if done else None)
            if iid and mid:
                link_module(pid, mid, iid)
    print(f"\nDone. Open Plane -> project '{PROJECT_NAME}' to view the roadmap.")


if __name__ == "__main__":
    main()
