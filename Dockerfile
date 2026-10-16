# Yardsyncs server: the API and the web portal in one image (the portal is served from the same address).
# Build:  docker build -t yardsyncs:latest .
# Run:    see deploy/ (docker compose with PostgreSQL + PostGIS, HTTPS proxy and nightly backups).

# ---- web portal
FROM node:24-alpine AS web
WORKDIR /src/web
COPY web/package.json ./
RUN npm install --no-audit --no-fund
COPY web/ ./
# vite writes into ../src/Tas.Api/wwwroot
RUN mkdir -p /src/src/Tas.Api && npm run build

# ---- server
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY Directory.Build.props TAS.slnx ./
COPY src/Tas.Domain/Tas.Domain.csproj src/Tas.Domain/
COPY src/Tas.Application/Tas.Application.csproj src/Tas.Application/
COPY src/Tas.Infrastructure/Tas.Infrastructure.csproj src/Tas.Infrastructure/
COPY src/Tas.Api/Tas.Api.csproj src/Tas.Api/
RUN dotnet restore src/Tas.Api/Tas.Api.csproj
COPY src/ src/
COPY db/ db/
COPY --from=web /src/src/Tas.Api/wwwroot src/Tas.Api/wwwroot
# A version number like 1.4.0 or 1.4.0-rc.1 (shown by /health and /api/ops/status).
ARG VERSION=0.0.0-dev
RUN dotnet publish src/Tas.Api/Tas.Api.csproj -c Release -o /app --no-restore -p:InformationalVersion=$VERSION

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
ENTRYPOINT ["dotnet", "Tas.Api.dll"]
