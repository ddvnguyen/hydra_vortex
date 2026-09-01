# Agent Rehearsal — Hydra TEST v1b Criterion (a)

## Purpose

Validates that a real agentic conversation flows through the Hydra TEST rig
(:19000 / :19001) end-to-end, with all requests returning 200 and completion
tokens > 0. Also verifies prod (:9000) is untouched.

## Steps

| # | Name | Core | What it tests |
|---|------|------|---------------|
| 1 | System-prompt task briefing | core-A | System message processing, task acknowledgment |
| 2 | Tool-call-style JSON request | core-A | Structured JSON output (tool-call mimicry) |
| 3 | Multi-turn follow-up | core-A | Prior-context reference in a new turn |
| 4 | Concurrent burst (3+3) | core-A + core-B | Parallel cross-core, sequential within core (cold_atomic self-lease limit: 1 in-flight per core) |
| 5 | Session-continuation | core-A | Context retention across 4 turns (asserts task name in reply) |
| 6 | Prod-isolation probe | prod | Byte-compare prod /v1/models before and after; skip if unreachable |

## Configuration

- **model**: `qwen3.5-9b-test` (max_tokens ≤ 64 per step)
- **timeout**: 120s per request
- **evidence**: `docs/hydra-test/evidence/agent-rehearsal-<timestamp>.jsonl`

## Running

```bash
bash scripts/hydra-test/agent-rehearsal.sh
```

Exit 0 = all steps passed. Exit 1 = any step failed (5xx, timeout, prod contamination).

## Evidence format (JSONL)

Each line:
```json
{"step":"step1_system_briefing","core":"core-A","http_status":200,"completion_tokens":16,"latency_s":5.48,"detail":"ok","ts":"20260829T120000Z"}
```
