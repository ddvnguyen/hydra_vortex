# hydra_vortex — autonomic orchestration

Self-driving development loop on top of [Paseo](https://paseo.sh):

```
GitHub issue → plan → breakdown/handoff → develop → PR → deploy → test → monitor → new issues
```

The human maintains `GOALS.md` and `ARCHITECTURE.md` and approves big changes.
Everything else runs unattended, supervised by Paseo schedules, with token
quota (5h rolling windows) handled by checkpoint + scheduled resume + local
draft fallback.

## Layout

```
orchestration/
├── README.md            ← you are here
├── GOALS.md             ← HUMAN-ONLY. Project goals & priorities.
├── ARCHITECTURE.md      ← HUMAN-ONLY. Architecture ground truth & boundaries.
├── LEAD_CHARTER.md      ← System charter for the team-lead agent.
├── QUOTA.md             ← Rate-limit / resume / tier-fallback protocol.
├── MONITOR_CHARTER.md   ← Charter for the monitoring agent.
├── providers.yaml       ← Provider tiers and routing rules.
├── state/               ← Task checkpoints written by agents (gitignore-able).
├── instrumentor/        ← Tiny local watchdog (MiniCPM5-1B) — see its README.
│   ├── instrumentor.py  ← Driver: canary probes + PASS/WARN/FAIL reports.
│   ├── serve-model.sh   ← llama-server launcher (688 MB Q4_K_M, port 8090).
│   └── README.md
└── scripts/
    ├── bootstrap.sh     ← Creates all Paseo schedules (idempotent).
    ├── teardown.sh      ← Removes the schedules.
    ├── labels.sh        ← Creates the GitHub label state machine (gh CLI).
    ├── vitals.sh        ← Deterministic pipeline health snapshot.
    └── quota-resume.sh  ← Helper: schedule a run-once resume at quota reset.
```

## Install

1. Prerequisites on the daemon machine: `paseo` daemon running, `claude`,
   `opencode`, `pi` CLIs authenticated, `gh` CLI authenticated against the repo,
   Paseo orchestration skills installed (`npx skills add getpaseo/paseo`).

2. Commit this folder to the repo root as `orchestration/`.

3. Point the agents at the charter. Append to `CLAUDE.md` and `AGENTS.md`:

   ```
   ## Orchestration
   If you are the team lead, read orchestration/LEAD_CHARTER.md before acting.
   All agents: orchestration/GOALS.md and orchestration/ARCHITECTURE.md are the
   source of truth and are read-only for agents.
   ```

4. Create the GitHub labels:

   ```bash
   ./orchestration/scripts/labels.sh
   ```

5. Edit the variables at the top of `scripts/bootstrap.sh` (repo path, timezone,
   providers, intervals), then:

   ```bash
   ./orchestration/scripts/bootstrap.sh
   ```

6. Fill in `GOALS.md` and `ARCHITECTURE.md`. The morning triage will start
   picking up issues labeled `status:ready`.

7. (Recommended) Set up the Instrumentor watchdog — a 1B local model that
   probes the pipeline with canary tasks and reports PASS/WARN/FAIL to you.
   See `instrumentor/README.md` (serve the model, test one sweep, add a cron
   line). It runs on system cron, not Paseo, so it can tell you when Paseo
   itself is broken.

## Day-to-day

- Watch/steer from the Paseo mobile or desktop app.
- Approve permission requests: `paseo permit ls` / `paseo permit allow <id>`.
- Big changes arrive as proposals in the lead's chat — reply "approved" or "rejected".
- Add work by filing GitHub issues with the `status:ready` label.
- Check quota: plan usage is visible in the Paseo app; checkpoints live in
  `orchestration/state/`.

## Kill switch

```bash
./orchestration/scripts/teardown.sh   # stop all schedules
paseo ls                              # then stop any running agents
```

> Note: Paseo evolves quickly. If any `paseo schedule` flag in the scripts
> errors, check `paseo schedule --help` for the current syntax and adjust —
> the design does not change, only the flag spelling might.
