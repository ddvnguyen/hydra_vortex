# ADR 0001 — hydra-head: a per-node Go agent

- **Status:** Accepted (2026-06-13)
- **Context PRs:** #233 (hydra-head), #230 (Core health monitor), #211 (router mode)
- **Supersedes:** the manual Quadlet/systemd + hand-invoked `llama-server` deploy path

## Context

Deploying Hydra to a node is not reproducible today. A node is brought up by installing N
Quadlet unit files in the right order against the right host state, plus a hand-managed
`llama-server` invocation. There is no single source of truth, and experimenting with new
`llama-server` params is fragile — a small change can silently break a node's startup.

We want a node bring-up that is declarative, versioned, and self-verifying, and a clean
boundary between *who keeps a node alive* and *who decides what it runs*.

## Decision

Adopt **hydra-head**, a single Go binary per GPU node that supervises `llama-server` plus the
node's telemetry sidecars (node_exporter, nvidia_exporter, promtail).

### Responsibility split (the core of this decision)

| Concern | Owner |
|---|---|
| Which model/preset is active, llama config/params, routing | **Hydra.Core (C#)** |
| Detecting a *stuck* node (request-level stall) | **Hydra.Core (C#)** |
| Reproducible node baseline (binary, port, GPU, preset definitions) | **hydra-head (Go)** |
| Launch `llama-server` (router mode) and restart on **crash** | **hydra-head (Go)** |
| Execute a restart on **stuck** when Core asks | **hydra-head (Go)** |

- **head does not decide the model or runtime config.** Its config (`global.yaml` +
  `node-*.yaml`) is the *reproducible node baseline*: binary path, port, GPU device, and the
  *set* of router presets/param-sets. Core selects the *active* preset at runtime.
- **Crash vs stuck.** head detects *crashes* locally (process exit → restart with backoff). A
  *stuck* `llama-server` can still answer `/health` 200, so head cannot reliably detect it
  alone. Core has the request-level view (token progress stalls), so **Core is the "stuck"
  detector** and calls head's restart endpoint; head executes it. This extends
  `HealthMonitorService` (#230).

### How the pieces compose

```
head restarts a wedged llama  →  llama returns as an empty router-mode server
   →  Core health monitor (#230) sees "empty /slots + /health OK = router/loading",
      keeps the node routable  →  next request re-loads the model (router mode, #211)
```

### Language

Go is added **only** for the node agent. This amends *Language Decisions (FINAL)* in
`CLAUDE.md` to a third entry:

| Service | Language | Reason |
|---|---|---|
| hydra-head | Go | node-local supervision + reproducible OCI deploy; **not** coordination |

Coordination/queue logic stays in **C# Hydra.Core** and does not move to Go.

## Consequences

**Positive**
- Node bring-up becomes *one binary + two YAMLs + an OCI image ref*, checksum-verified, with a
  `verify_and_start.sh` preflight (OS/GPU/CUDA/binary/config).
- Param experimentation is version-controlled (edit a preset, restart via API) instead of
  re-templating systemd units.
- Clean liveness/runtime boundary; the two health loops (head=process, Core=routing/stuck) are
  complementary, not duplicative.

**Costs / follow-ups (tracked against #233)**
- A third language (Go) in the build/test/CI matrix.
- #233 must fix execution gaps before merge: the `cmd.Wait()` data race in the supervisor, the
  silent param-drop in `BuildLlamaArgs`, the incomplete migration (old agent/quadlet/deploy
  files still present; CI still on the old path), and the Go toolchain mismatch
  (`go.mod` 1.25 vs deploy script 1.23.4). Plus a Core→head restart endpoint for "stuck".

## Alternatives considered

- **Keep Quadlet/systemd.** Rejected as the primary owner: it is exactly the non-reproducible,
  fragile path this ADR replaces, and it cannot restart `llama-server` on a *model-level*
  liveness signal.
- **Fold the node agent into Hydra.Core (C#).** Keeps two languages, but couples node-local
  supervision to the coordinator's lifecycle and deploy cadence. Rejected to keep node bring-up
  independently deployable.
