# Third-Party Notices

Hydra is licensed under **AGPL-3.0** (see [LICENSE](LICENSE)). It depends on the
third-party components listed below, each under its own license. All listed
licenses are permissive (MIT / Apache-2.0 / BSD / PostgreSQL) and compatible
with distribution of Hydra under AGPL-3.0.

This file is informational. Each component remains governed by its own license;
consult the upstream project for the authoritative license text.

## Forked component

| Component | Upstream | License | Notes |
|-----------|----------|---------|-------|
| llama.cpp (`hydra-state-streaming` fork) | https://github.com/ggml-org/llama.cpp | MIT | Vendored as a git submodule under `src/llama-cpp`. The fork adds 3 streaming-state endpoints to `tools/server/server.cpp`; all upstream MIT terms continue to apply to that code. |

## .NET / NuGet (Hydra.Core, Hydra.Shared)

| Package | License |
|---------|---------|
| Microsoft.ML.Tokenizers | MIT |
| Microsoft.ML.Tokenizers.Data.O200kBase | MIT |
| OpenAI (.NET SDK) | MIT |
| prometheus-net | MIT |
| prometheus-net.AspNetCore | MIT |
| Npgsql | PostgreSQL License |
| Serilog | Apache-2.0 |
| ~~Serilog.Sinks.Console~~ | (removed in #363 — Loki push is the new primary path) |
| ~~Serilog.Sinks.Grafana.Loki~~ | (removed in #363 — replaced by OTel pipeline) |
| Serilog.Sinks.OpenTelemetry | Apache-2.0 |
| OpenTelemetry.Exporter | Apache-2.0 |
| OpenTelemetry.Exporter.OpenTelemetryProtocol | Apache-2.0 |
| OpenTelemetry.Extensions.Hosting | Apache-2.0 |

### Test-only (.NET)

| Package | License |
|---------|---------|
| Microsoft.NET.Test.Sdk | MIT |
| xunit | Apache-2.0 |
| xunit.runner.visualstudio | Apache-2.0 |

## Python (coordinator / tooling)

| Package | License |
|---------|---------|
| fastapi | MIT |
| uvicorn | BSD-3-Clause |
| httpx | BSD-3-Clause |
| pydantic | MIT |
| pydantic-settings | MIT |
| structlog | MIT / Apache-2.0 |
| aiofiles | Apache-2.0 |
| aiosqlite (extra: persistence) | MIT |
| prometheus-client (extra: monitoring) | Apache-2.0 |
| langfuse (extra: langfuse) | MIT |

### Test-only (Python)

| Package | License |
|---------|---------|
| pytest | MIT |
| pytest-asyncio | Apache-2.0 |
| ruff | MIT |

## Go (Hydra.Head)

| Package | License |
|---------|---------|
| github.com/google/go-containerregistry | Apache-2.0 |
| go.opentelemetry.io/otel | Apache-2.0 |
| go.opentelemetry.io/otel/log | Apache-2.0 |
| go.opentelemetry.io/otel/sdk | Apache-2.0 |
| go.opentelemetry.io/otel/sdk/log | Apache-2.0 |
| go.opentelemetry.io/otel/trace | Apache-2.0 |
| go.opentelemetry.io/otel/exporters/otlp/otlplog/otlploghttp | Apache-2.0 |
| gopkg.in/yaml.v3 | MIT |

(Added in #363: the OTel Go SDK + `otlploghttp` exporter are
the new log pipeline for Hydra.Head and its child processes.
Hydra.Head no longer depends on `samber/slog-loki` /
`samber/slog-multi` — those were not adopted.)

## Container images

| Image | License | Notes |
|-------|---------|-------|
| otel/opentelemetry-collector-contrib | Apache-2.0 | The OTel Collector gateway (Quadlet `infra-otel-collector.container`); pulls from Docker Hub in `start-infra.sh`. |
| grafana/loki | AGPL-3.0 | The Loki log backend (Quadlet `infra-loki.container`). Grafana changed the Loki license from Apache-2.0 to AGPL-3.0 in v2.9.0; the local Loki 3.x instance is internally-facing (no external network exposure of its API), so the AGPL terms do not extend Hydra's distribution license. |
| ~~grafana/promtail~~ | (removed in #363) | Promtail was EOL on 2026-03-02. The hydra-head image no longer bundles it. |

> Versions are pinned in the respective `*.csproj` and `pyproject.toml` files.
> If you add a dependency, please update this file. Avoid introducing
> dependencies under licenses incompatible with AGPL-3.0 (e.g. proprietary
> or strong-copyleft-incompatible terms).
