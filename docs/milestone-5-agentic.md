# Milestone M5 — LLM Observability & Agentic

> New milestone from the 2026-06 restructure. Absorbs the old M3.3 (Langfuse) and
> adds experimentation + agentic orchestration. Lowest priority of the new
> milestones; Langfuse is optional/feature-flagged.

## Goal
Deep LLM-level observability and the ability to run controlled experiments and
agentic workflows on top of the inference fabric.

## Tasks

### M5.1 — Langfuse tracing (re-homed from old M3.3)
- Optional: enabled only if `LANGFUSE_PUBLIC_KEY` is set.
- Per completion: generation span (model, prompt/completion tokens, latency),
  routing metadata (node, reason, cache_hit, session_id), migration span if any.
- `coordinator/langfuse.py`, async Langfuse SDK.
- **Done when:** full request traces with token counts + routing visible in Langfuse.

### M5.2 — A/B testing
- Route a configurable fraction of traffic across variants (model, quant, draft
  config, routing policy) and compare latency/quality/acceptance.
- Results surfaced as Prometheus metrics + Langfuse tags.
- **Done when:** an A/B split runs and its metrics are comparable side by side.

### M5.3 — Agentic system
- Orchestration layer that composes multi-step LLM workflows over the Coordinator
  (tool use, multi-turn, sub-agents), reusing session affinity + KV migration.
- **Done when:** a representative multi-step agentic task runs end-to-end on Hydra.

## Out of scope (for now)
- Building a general agent framework from scratch; eval harnesses beyond A/B.
