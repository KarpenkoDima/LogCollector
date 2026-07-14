FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

COPY Directory.Build.props global.json LogCollector.sln ./
COPY LogCollector.Core/LogCollector.Core.csproj LogCollector.Core/
COPY LogCollector.Application/LogCollector.Application.csproj LogCollector.Application/
COPY LogCollector.Infrastructure/LogCollector.Infrastructure.csproj LogCollector.Infrastructure/
COPY LogCollector.Host/LogCollector.Host.csproj LogCollector.Host/
RUN dotnet restore LogCollector.Host/LogCollector.Host.csproj

COPY LogCollector.Core/ LogCollector.Core/
COPY LogCollector.Application/ LogCollector.Application/
COPY LogCollector.Infrastructure/ LogCollector.Infrastructure/
COPY LogCollector.Host/ LogCollector.Host/
RUN dotnet publish LogCollector.Host/LogCollector.Host.csproj \
    --configuration Release \
    --no-restore \
    --output /publish \
    && mkdir /publish/data

FROM mcr.microsoft.com/dotnet/runtime:9.0-noble-chiseled AS runtime
WORKDIR /app
COPY --chown=$APP_UID:$APP_UID --from=build /publish/ ./

USER $APP_UID
EXPOSE 5140/udp
VOLUME ["/app/data"]

ENV DOTNET_ENVIRONMENT=Production \
    DOTNET_EnableDiagnostics=0 \
    DOTNET_GCConserveMemory=9 \
    SQLITE__CONNECTIONSTRING="Data Source=/app/data/logs.db;Cache=Shared"

ENTRYPOINT ["dotnet", "LogCollector.Host.dll"]
