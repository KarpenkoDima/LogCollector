# syntax=docker/dockerfile:1.7

# Один Dockerfile обслуживает два сценария:
#   runtime     — минимальный production image;
#   development — SDK image для dotnet watch.
#
# Версия .NET задаётся одним ARG. Для security patch пересобирайте с --pull.
ARG DOTNET_VERSION=9.0

# ─────────────────────────────────────────────────────────────────────────────
# 1. Restore: сначала только solution и project-файлы
# ─────────────────────────────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/sdk:${DOTNET_VERSION}-noble AS restore
WORKDIR /src

ENV DOTNET_CLI_TELEMETRY_OPTOUT=1 \
    DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1 \
    NUGET_XMLDOC_MODE=skip

COPY LogCollector.sln ./
COPY LogCollector.Core/LogCollector.Core.csproj LogCollector.Core/
COPY LogCollector.Application/LogCollector.Application.csproj LogCollector.Application/
COPY LogCollector.Infrastructure/LogCollector.Infrastructure.csproj LogCollector.Infrastructure/
COPY LogCollector.Host/LogCollector.Host.csproj LogCollector.Host/

# Restore отделён от COPY исходников: изменение .cs не инвалидирует NuGet layer.
RUN --mount=type=cache,id=logcollector-nuget,target=/root/.nuget/packages \
    dotnet restore LogCollector.Host/LogCollector.Host.csproj --nologo

# ─────────────────────────────────────────────────────────────────────────────
# 2. Publish: только проекты, нужные production host
# ─────────────────────────────────────────────────────────────────────────────
FROM restore AS publish

COPY LogCollector.Core/ LogCollector.Core/
COPY LogCollector.Application/ LogCollector.Application/
COPY LogCollector.Infrastructure/ LogCollector.Infrastructure/
COPY LogCollector.Host/ LogCollector.Host/

RUN --mount=type=cache,id=logcollector-nuget,target=/root/.nuget/packages \
    dotnet publish LogCollector.Host/LogCollector.Host.csproj \
      --configuration Release \
      --no-restore \
      --output /app/publish \
      --nologo \
      -p:UseAppHost=false \
 && mkdir -p /app/publish/data

# ─────────────────────────────────────────────────────────────────────────────
# 3. Development: SDK + dotnet watch
# ─────────────────────────────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/sdk:${DOTNET_VERSION}-noble AS development
WORKDIR /src

ENV DOTNET_CLI_TELEMETRY_OPTOUT=1 \
    DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1 \
    DOTNET_USE_POLLING_FILE_WATCHER=true \
    DOTNET_WATCH_SUPPRESS_LAUNCH_BROWSER=true \
    DOTNET_WATCH_SUPPRESS_EMOJIS=true \
    NUGET_PACKAGES=/opt/nuget/packages

# UID/GID в dev Compose заменяются на UID/GID пользователя Linux. Каталог NuGet
# остаётся доступным для записи независимо от выбранного UID.
RUN mkdir -p /opt/nuget/packages /app/data \
 && chmod 0777 /opt/nuget/packages /app/data

COPY . .
RUN dotnet restore LogCollector.Host/LogCollector.Host.csproj --nologo

USER app
ENTRYPOINT ["dotnet", "watch", "--project", "LogCollector.Host/LogCollector.Host.csproj", "run", "--no-launch-profile"]

# ─────────────────────────────────────────────────────────────────────────────
# 4. Runtime: console worker, поэтому aspnet image не нужен
# ─────────────────────────────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/runtime:${DOTNET_VERSION}-noble-chiseled AS runtime
WORKDIR /app

# В официальном .NET image уже есть непривилегированный пользователь app.
# Каталог data создаётся в publish-stage и получает того же владельца.
COPY --from=publish --chown=app:app /app/publish/ ./

ENV DOTNET_ENVIRONMENT=Production \
    DOTNET_EnableDiagnostics=0 \
    DOTNET_CLI_TELEMETRY_OPTOUT=1

# Приложение внутри контейнера слушает 5140/udp. Стандартный host port 514
# перенаправляет Docker, поэтому CAP_NET_BIND_SERVICE внутри не требуется.
EXPOSE 5140/udp

USER app
STOPSIGNAL SIGTERM
ENTRYPOINT ["dotnet", "LogCollector.Host.dll"]
