# Milestone M4 — Model Management & Multi-Modal

> New milestone from the 2026-06 restructure. Absorbs the old M3.4 (model
> distribution + systemd) and extends Hydra beyond a single hardcoded LLM.
> Prerequisite: M-Perf landed, M3 persistence underway.

## Goal
Serve and manage **multiple models** — distributed from the Store, loaded on demand,
across modalities — without manual `scp` or full-stack restarts.

## Tasks

### M4.1 — Model distribution from Store
- Store accepts large model files via raw PUT (immutable; not chunked).
- `hydra-upload-model` CLI: read GGUF from disk → PUT to Store under `model/<key>`.
- Core startup: if the local model file is missing, GET from Store → local tmpfs.
- Config: `model_key` per node.
- **Done when:** a new agent node starts with no manual model copy.

### M4.2 — Dynamic model load / swap
- Core API to load, unload, or swap the active model on a node
  without restarting the core or llama-server process where avoidable.
- Track resident models per node; route requests to a node that has the model.
- **Done when:** switching a node's model is a single API call, no redeploy.

### M4.3 — Multi-modal model support
- Serve **vision**, **embedding**, and **audio** models alongside the LLM.
- Core routing extended to model *type* (chat / embedding / vision / audio),
  not just GPU placement.
- Per-type endpoints on the OpenAI-compatible surface
  (`/v1/embeddings`, vision-in-chat, audio transcription).
- **Done when:** at least one model of each new modality is reachable end-to-end.

### M4.4 — systemd lifecycle (re-homed from old M3.4)
- `hydra-ramdisk` → `llama-servers` → `hydra-core` units,
  booted in order. (Builds on the in-flight P100 rootless `systemctl --user` work.)
- **Done when:** `sudo reboot` brings the full stack up automatically.

## Out of scope (for now)
- Training/fine-tuning; quantization pipelines; model registry UI.
