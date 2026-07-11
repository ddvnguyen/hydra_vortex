# Instrumentor agent

A tiny local watcher (MiniCPM5-1B, 688 MB Q4_K_M) that does **no main work**.
Every sweep it: pulls pipeline vitals → fires a canary task through Paseo →
checks the canary landed → writes a 3-line PASS/WARN/FAIL report for you.
On FAIL it files/updates a single GitHub issue labeled `source:instrumentor`.

## Why a driver instead of a Paseo provider

The model's own card is clear: excellent first tool-call extraction
(~99% parseable, ~93% right tool) but weak exact arguments (~65%) and weak
natural stopping (~15%). A 1B model dropped into a full coding harness
(OpenCode/Pi with dozens of tools) will thrash. `instrumentor.py` instead
implements the card's recommended runtime contract exactly: 4 whitelisted
tools, temp 0, stop after the first complete `</function>`, schema validation,
tool execution outside the model. If the model derails, the driver files a
WARN report itself — a sweep always produces a report.

The model can only ever: read vitals, spawn a `canary-*` agent on your tier-3
provider, check/stop that canary, and write the report. Nothing else.

## Setup

```bash
# 1. Serve the model (persistent; systemd/tmux recommended)
./orchestration/instrumentor/serve-model.sh          # port 8090
# CPU-only box or busy GPUs:  INSTR_NGL=0 ./serve-model.sh
# Pin a GPU:                  INSTR_DEVICE=CUDA1 ./serve-model.sh

# 2. Test one sweep manually
cd ~/dev/hydra_vortex
CANARY_PROVIDER=<your-tier-3-provider> python3 orchestration/instrumentor/instrumentor.py
cat orchestration/state/instrumentor-report.md

# 3. Schedule it (system cron — every 2h at :15, offset from Paseo schedules)
crontab -e
15 */2 * * * cd ~/dev/hydra_vortex && CANARY_PROVIDER=<tier-3> \
  python3 orchestration/instrumentor/instrumentor.py >> orchestration/state/instrumentor.cron.log 2>&1
```

System cron (not a Paseo schedule) is deliberate: the Instrumentor's job is to
verify Paseo itself is healthy, so it must not depend on the thing it probes.

## Reading it

- Latest verdict: `orchestration/state/instrumentor-report.md`
- Trend: `orchestration/state/instrumentor-history.log` (one line per sweep)
- Failures reach your phone as GitHub issues labeled `source:instrumentor`.

## Config (env vars)

| Var | Default | Meaning |
|-----|---------|---------|
| `LLM_URL` | `http://127.0.0.1:8090/v1/chat/completions` | llama-server endpoint |
| `LLM_MODEL` | `minicpm5-1b-instrumentor` | served alias |
| `CANARY_PROVIDER` | `opencode` | Paseo provider for canary tasks — use tier-3 (free) |
| `MAX_STEPS` | `8` | hard cap on tool calls per sweep |
| `REPO_DIR` | git toplevel | repo path |

Occasionally set `CANARY_PROVIDER` to a tier-2 provider for one run to probe
the cloud path too — but don't schedule that: it spends real quota on pings.
