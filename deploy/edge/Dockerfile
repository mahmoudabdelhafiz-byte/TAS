# Yardsyncs lane agent (edge mode): runs at the terminal when Yardsyncs is hosted in the cloud.
# Build (from the repository root):  docker build -f deploy/edge/Dockerfile -t yardsyncs-edge:latest .
# Run: see deploy/edge/README.md.

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY Directory.Build.props ./
COPY src/Yardsyncs.Domain/Yardsyncs.Domain.csproj src/Yardsyncs.Domain/
COPY src/Yardsyncs.Application/Yardsyncs.Application.csproj src/Yardsyncs.Application/
COPY src/Yardsyncs.Edge/Yardsyncs.Edge.csproj src/Yardsyncs.Edge/
RUN dotnet restore src/Yardsyncs.Edge/Yardsyncs.Edge.csproj
COPY src/Yardsyncs.Domain/ src/Yardsyncs.Domain/
COPY src/Yardsyncs.Application/ src/Yardsyncs.Application/
COPY src/Yardsyncs.Edge/ src/Yardsyncs.Edge/
ARG VERSION=0.0.0-dev
RUN dotnet publish src/Yardsyncs.Edge/Yardsyncs.Edge.csproj -c Release -o /app --no-restore -p:InformationalVersion=$VERSION

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app ./
ENV ASPNETCORE_URLS=http://0.0.0.0:8090 \
    ASPNETCORE_ENVIRONMENT=Production \
    Edge__DataDirectory=/data \
    DOTNET_gcServer=false
VOLUME /data
EXPOSE 8090
ENTRYPOINT ["dotnet", "Yardsyncs.Edge.dll"]
