# Build context: spx-v8/
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY Directory.Build.props global.json SpxQuant.sln ./
COPY src/ src/
COPY tests/ tests/
COPY config/ config/
RUN dotnet restore SpxQuant.sln
# Tests run inside the image build: a failing invariant (e.g. the read-only guard) fails the image.
RUN dotnet test SpxQuant.sln -c Release --no-restore
RUN dotnet publish src/SpxQuant.Api/SpxQuant.Api.csproj -c Release --no-restore -o /out

FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app
COPY --from=build /out ./
COPY config/ ./config/
RUN mkdir -p /data/audit && chown -R $APP_UID /data
USER $APP_UID
# appsettings.json binds loopback for local dotnet run; inside the container bind all interfaces
# (compose publishes the port on host 127.0.0.1 only). Plain "Urls" overrides appsettings.
ENV Urls=http://0.0.0.0:5080 \
    Platform__CalendarPath=config/calendar/us-equity-2026.json \
    Platform__ReplaySnapshotPath=config/replay/synthetic-0dte-2026-11-02.json \
    Platform__AuditLogPath=/data/audit/audit.jsonl
EXPOSE 5080
ENTRYPOINT ["dotnet", "SpxQuant.Api.dll"]
