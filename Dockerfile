# Yardsyncs server: the API and the web portal in one image (the portal is served from the same address).
# Build:  docker build -t yardsyncs:latest .
# Run:    see deploy/ (docker compose with PostgreSQL + PostGIS, HTTPS proxy and nightly backups).

# ---- web portal
FROM node:24-alpine AS web
WORKDIR /src/web
COPY web/package.json ./
RUN npm install --no-audit --no-fund
COPY web/ ./
# vite writes into ../src/Yardsyncs.Api/wwwroot
RUN mkdir -p /src/src/Yardsyncs.Api && npm run build

# ---- server
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY Directory.Build.props Yardsyncs.slnx ./
COPY src/Yardsyncs.Domain/Yardsyncs.Domain.csproj src/Yardsyncs.Domain/
COPY src/Yardsyncs.Application/Yardsyncs.Application.csproj src/Yardsyncs.Application/
COPY src/Yardsyncs.Infrastructure/Yardsyncs.Infrastructure.csproj src/Yardsyncs.Infrastructure/
COPY src/Yardsyncs.Autogate/Yardsyncs.Autogate.csproj src/Yardsyncs.Autogate/
COPY src/Yardsyncs.Api/Yardsyncs.Api.csproj src/Yardsyncs.Api/
RUN dotnet restore src/Yardsyncs.Api/Yardsyncs.Api.csproj
COPY src/ src/
COPY db/ db/
COPY web/src/help/ web/src/help/
COPY --from=web /src/src/Yardsyncs.Api/wwwroot src/Yardsyncs.Api/wwwroot
# A version number like 1.4.0 or 1.4.0-rc.1 (shown by /health and /api/ops/status).
ARG VERSION=0.0.0-dev
RUN dotnet publish src/Yardsyncs.Api/Yardsyncs.Api.csproj -c Release -o /app --no-restore -p:InformationalVersion=$VERSION

# ---- runtime
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app ./
ENV ASPNETCORE_HTTP_PORTS=8080 \
    ASPNETCORE_ENVIRONMENT=Production \
    Logging__Json=true \
    Database__MigrateOnStartup=true \
    DOTNET_gcServer=false
EXPOSE 8080
# The image's non-root user.
USER $APP_UID
ENTRYPOINT ["dotnet", "Yardsyncs.Api.dll"]
