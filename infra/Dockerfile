# ── Shared build stage: all .NET projects, one SDK pull ──────────────
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /build

# Version file + shared build props
COPY VERSION .
COPY src/core/Directory.Build.props core/

# Copy only source directories needed for .NET builds
COPY src/core/Hydra.Shared/ core/Hydra.Shared/
COPY src/core/Hydra.Store/ core/Hydra.Store/
COPY src/core/Hydra.Agent/ core/Hydra.Agent/
COPY src/Hydra.sln .

RUN dotnet restore core/Hydra.Store/Hydra.Store.csproj && \
    dotnet publish core/Hydra.Store -c Release -o /publish/store --no-restore
RUN dotnet restore core/Hydra.Agent/Hydra.Agent.csproj && \
    dotnet publish core/Hydra.Agent -c Release -o /publish/agent --no-restore

# ── Store target ────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS store
WORKDIR /app
COPY --from=build /publish/store .
EXPOSE 9500 9501
ENTRYPOINT ["dotnet", "Hydra.Store.dll"]

# ── Agent target ────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS agent
WORKDIR /app
COPY --from=build /publish/agent .
EXPOSE 9601 9611
ENTRYPOINT ["dotnet", "Hydra.Agent.dll"]

# ── Coordinator target ──────────────────────────────────────────────
FROM python:3.13-slim AS coordinator
WORKDIR /app
COPY VERSION .
COPY pyproject.toml .
COPY src/coordinator/ src/coordinator/
RUN pip install --no-cache-dir ".[monitoring]"
ENV PYTHONPATH=/app/src
EXPOSE 9000
ENTRYPOINT ["python", "-m", "coordinator.main"]
