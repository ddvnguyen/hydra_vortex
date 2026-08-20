---
description: Deploy safely and monitor after release. Use immediately before and after deploying a change to production.
---

# ship-and-monitor

## Purpose

Ensure safe rollout and post-deploy verification.

## Instructions

1. Confirm observability is in place: logs, metrics, and at least one alert covering the new behavior.
2. Confirm the rollback procedure is identified and executable.
3. Use a staged rollout or feature flag — do not enable globally in one step.
4. After deploying, monitor error rates, latency, and relevant metrics for at least 30 minutes.
5. If a metric degrades beyond threshold, roll back immediately — investigate after, not before.
6. Post a deployment note: what shipped, when, and what to watch.

## Checklist

- [ ] Observability confirmed
- [ ] Rollback plan executable
- [ ] Staged rollout or feature flag used
- [ ] Post-deploy monitoring window completed
- [ ] Deployment note posted

## Example

Input:
Ship a new rate-limiter to production.

Expected behavior:
- Canary to 5% of traffic first
- Watch error rate and latency for 30 minutes
- Roll back on threshold breach without waiting to diagnose first
