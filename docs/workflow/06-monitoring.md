# 6. Check monitoring (after deploy)

**Goal:** confirm the change didn't regress the running system. Details:
`CLAUDE.md` `## Monitoring & Observability`.

1. **Grafana** (http://localhost:3000) — Hydra dashboard. Check, around your change:
   request rate / sessions, store ops & bytes, **save/restore & migration latency**,
   llama tokens/s & KV usage, host/GPU temp + memory, service up/down table.
2. **Alerts** — Prometheus (http://localhost:9091) + `infra/prometheus/alerts.yml`.
   No new firing alerts. `monitor.yml` auto-creates/closes `monitoring` issues from
   alerts — **do not close a monitoring issue without root-causing it**.
3. **OTel Collector** (host log gateway, replaces Promtail + container-log-shipper
   in #363) — verify it is active:
   ```bash
   systemctl --user is-active infra-otel-collector
   curl -so/dev/null -w'%{http_code}\n' http://localhost:13133/
   ```
   If the collector is not running, restart it:
   `systemctl --user restart infra-otel-collector`. Health endpoint:
   `http://localhost:13133/`.
4. **Logs** — Grafana Explore (Loki); filter by `$trace_id` (now in
   structured metadata, not a label) to follow a request across
   Coordinator / Hydra.Head / Store.
5. If anything regressed or a new alert fired → `07-issue-and-close.md`.

→ Next: `07-issue-and-close.md`
