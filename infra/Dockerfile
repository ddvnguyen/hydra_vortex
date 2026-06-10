# ── Shared build stage: all .NET projects, one SDK pull ──────────────
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /build

# Version file + shared build props
COPY VERSION .
COPY src/core/Directory.Build.props core/

# Copy only source directories needed for .NET builds
COPY src/core/Hydra.Shared/ core/Hydra.Shared/
COPY src/core/Hydra.Core/ core/Hydra.Core/
COPY src/Hydra.sln .

RUN dotnet restore core/Hydra.Core/Hydra.Core.csproj && \
    dotnet publish core/Hydra.Core -c Release -o /publish/core --no-restore

# ── Core target (Store + Coordinator + Agent, single binary) ───────────
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS core
WORKDIR /app
COPY --from=build /publish/core .
EXPOSE 9000 9500 9501
ENTRYPOINT ["dotnet", "Hydra.Core.dll"]
