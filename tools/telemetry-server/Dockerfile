# Build from the repository root:  docker build -f tools/telemetry-server/Dockerfile -t alice-telemetry .
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY Directory.Build.props ./
COPY src/Alice.Cli/Telemetry/TelemetryWire.cs src/Alice.Cli/Telemetry/
COPY tools/telemetry-server/ tools/telemetry-server/
RUN dotnet publish tools/telemetry-server/Alice.TelemetryServer.csproj -c Release -o /out

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /out .
ENV ASPNETCORE_URLS=http://+:8080 \
    ALICE_TELEMETRY_DB=/data/telemetry.db \
    ALICE_TELEMETRY_RETENTION_DAYS=180
VOLUME /data
EXPOSE 8080
USER app
ENTRYPOINT ["dotnet", "Alice.TelemetryServer.dll"]
