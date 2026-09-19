# syntax=docker/dockerfile:1

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Restore against the project files alone, so a source-only change does not
# invalidate the (slow) restore layer.
COPY global.json KitchenCore.slnx ./
COPY src/KitchenCore.Shared/*.csproj src/KitchenCore.Shared/
COPY src/KitchenCore.Core/*.csproj   src/KitchenCore.Core/
COPY src/KitchenCore.Client/*.csproj src/KitchenCore.Client/
COPY src/KitchenCore.Server/*.csproj src/KitchenCore.Server/
COPY tests/KitchenCore.Tests/*.csproj tests/KitchenCore.Tests/
RUN dotnet restore src/KitchenCore.Server/KitchenCore.Server.csproj

COPY src/ src/
RUN dotnet publish src/KitchenCore.Server/KitchenCore.Server.csproj \
    -c Release -o /publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime

# git is a runtime dependency, not a build one: the data folder is synced by
# shelling out to the CLI. Whether it is ever used is decided at runtime by
# whether the mounted data folder is itself a repository.
RUN apt-get update \
    && apt-get install -y --no-install-recommends git ca-certificates curl \
    && rm -rf /var/lib/apt/lists/*

# Layout from the brief: binaries in /app/bin, data and config bind-mounted.
WORKDIR /app
COPY --from=build /publish ./bin

RUN mkdir -p /app/data/menu /app/data/shopping /app/config

ENV ASPNETCORE_HTTP_PORTS=8080 \
    KITCHENCORE_DATA_PATH=/app/data \
    KITCHENCORE_CONFIG_PATH=/app/config

EXPOSE 8080

HEALTHCHECK --interval=30s --timeout=3s --start-period=10s \
    CMD curl -fsS http://localhost:8080/healthz || exit 1

ENTRYPOINT ["dotnet", "/app/bin/KitchenCore.Server.dll"]
