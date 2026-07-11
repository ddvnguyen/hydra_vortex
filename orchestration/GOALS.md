# Project goals — hydra_vortex

> HUMAN-ONLY FILE. Agents read this at the start of every task and must never
> edit it. If a task conflicts with this file, agents stop and ask.

## Mission

hydra_vortex runs and improves the **local LLM inference + observability stack**
on this machine: a `llama.cpp` server (RTX 5060 Ti 16GB, host) offloading tensor
work to a Tesla P100 16GB RPC node, fronted by the **Bifrost** OpenAI-compatible
gateway, traced by **Langfuse**, and monitored by **Prometheus + Grafana**
(GPU, node, and custom RPC-bandwidth exporters). The autonomic loop keeps the
stack healthy, surfaces regressions as GitHub issues, and implements fixes
through the issue-driven dev cycle.

## Current priorities (ordered)

1. Stabilize the Bifrost → llama.cpp → P100 RPC inference path end-to-end
   (correct `--tensor-split`, no P100 idle, no KV-cache thrash).
2. Close observability gaps: ensure every exporter (GPU host, GPU VM, node
   host, node VM, RPC bandwidth) is scraped and has a Grafana panel + alert.
3. Make tracing trustworthy: Bifrost OTel → Langfuse traces flowing for every
   request; fix drops / auth mismatches.
4. Add Prometheus alerting rules for the known failure modes (KV pressure,
   P100 idle, VRAM near-full, RPC bandwidth saturation, red CI).
5. Keep the orchestration itself observable: live dashboard (`:8098`) wired to
   Prometheus/Grafana, Instrumentor canary probe green.

## Non-goals (do NOT work on these)

- No cloud-provider migration of the inference path (local-only by design).
- No UI/frontend rewrite of Langfuse or Grafana.
- No new external SaaS dependencies beyond what Bifrost already supports.
- No changes to PRODUCTION targets — there is no production; staging = this host.

## Quality bar

- Every config change is validated before merge: `docker compose config`
  (or `podman-compose config`), `promtool check rules`, and a `curl` health/
  metrics check against the affected endpoint.
- Dashboards/changes get a failing-check-first discipline where possible:
  a broken scrape target or invalid rule is reproduced, then fixed.
- Tier-3 (local/free model) output is always draft quality and must be
  reviewed by a tier-1/tier-2 agent before merge (see providers.yaml).

## Definitions of done

- Feature/config: implemented, validated (`docker compose config` + `promtool`
  + `curl` probe), deployed to the running stack on this host, clean 24h
  monitoring soak (no new `source:monitoring` issue referencing it).
- Bugfix: reproduced with a failing check first, then fixed, check passes.
- Docs: accurate against the live stack; no stale port/label references.
