# ── Shared build stage: all .NET projects, one SDK pull ──────────────
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /build

# Copy only source directories needed for .NET builds
COPY src/Hydra.Shared/ src/Hydra.Shared/
COPY src/Hydra.Store/ src/Hydra.Store/
COPY src/Hydra.Agent/ src/Hydra.Agent/
COPY Hydra.sln .

RUN dotnet restore src/Hydra.Store/Hydra.Store.csproj && \
    dotnet publish src/Hydra.Store -c Release -o /publish/store --no-restore
RUN dotnet restore src/Hydra.Agent/Hydra.Agent.csproj && \
    dotnet publish src/Hydra.Agent -c Release -o /publish/agent --no-restore

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
COPY pyproject.toml .
COPY src/coordinator/ src/coordinator/
COPY src/python_shared/ src/python_shared/
RUN pip install --no-cache-dir ".[monitoring]"
ENV PYTHONPATH=/app/src
EXPOSE 9000
ENTRYPOINT ["python", "-m", "coordinator.main"]
