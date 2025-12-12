# ── Stage 1: Build ────────────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

# Copy project files first — Docker caches this layer until any .csproj changes.
# Restore runs only when dependencies change, not on every source edit.
COPY LogCollector.sln                                              .
COPY LogCollector.Core/LogCollector.Core.csproj                   LogCollector.Core/
COPY LogCollector.Application/LogCollector.Application.csproj     LogCollector.Application/
COPY LogCollector.Infrastructure/LogCollector.Infrastructure.csproj LogCollector.Infrastructure/
COPY LogCollector.Host/LogCollector.Host.csproj                   LogCollector.Host/
RUN dotnet restore LogCollector.Host/LogCollector.Host.csproj

# Copy source and publish.  Tests are excluded via .dockerignore.
COPY . .
RUN dotnet publish LogCollector.Host/LogCollector.Host.csproj \
        --configuration Release \
        --no-restore \
        --output /app/publish

# ── Stage 2: Runtime ──────────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/runtime:9.0 AS runtime

# Dedicated non-root system account.
# The container never needs root — no CAP_NET_BIND_SERVICE, no sudo.
# Port 514 is handled by Docker's host-side iptables rule (see docker-compose.yml).
RUN groupadd --system logcollector \
 && useradd  --system --gid logcollector --no-create-home logcollector

# Data directory for the SQLite sink.
# Must be writable by the service account; bind-mount or named volume is expected here.
RUN mkdir -p /var/log/logcollector \
 && chown logcollector:logcollector /var/log/logcollector

WORKDIR /app
COPY --from=build /app/publish .

USER logcollector

# The service listens on 5140/udp inside the container.
# Docker maps host port 514 → container port 5140 (see docker-compose.yml).
# This avoids the need for CAP_NET_BIND_SERVICE inside the container.
EXPOSE 5140/udp

ENV DOTNET_ENVIRONMENT=Production

ENTRYPOINT ["dotnet", "LogCollector.Host.dll"]
