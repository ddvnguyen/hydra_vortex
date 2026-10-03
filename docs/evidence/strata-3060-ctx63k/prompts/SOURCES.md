# ctx63k prompt provenance

p63k.txt - header `# ctx63k benchmark prompt cell p63k`, then real hydra-repo `docs/*.md` sources (path-sorted, each once, `## source:` marker per section), joined with `\n\n---\n\n`, sentence-boundary trimmed with the pack tokenizer to <= 63000 tokens (actual 62990). Distinct source pool from the t0006 p12k cell (strata README/bench-results/DETAILS) so this cell shares no bytes with earlier evidence.
warmup.txt - verbatim from t0006 prompts (branch docs/811-t0006-strata-vs-fork-evidence).
Sources used (16):
- docs/GITHUB_PROJECT_SETUP.md
- docs/PORTS_AND_ENV.md
- docs/RUNBOOK.md
- docs/agent-coding-rules.md
- docs/architecture-principles.md
- docs/architecture.md
- docs/build-environment.md
- docs/combined-engine-mode.md
- docs/combined-mode-sched-registration.md
- docs/combined-reservation-design.md
- docs/cuda-modules.md
- docs/design-colibri-expert-atlas.md
- docs/design-decode-profiler.md
- docs/design-direct-push-logging.md
- docs/handoff-csharp-coordinator.md
- docs/milestone-4-models.md
