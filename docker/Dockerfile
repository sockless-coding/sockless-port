# syntax=docker/dockerfile:1
# Sockless Port server image. Build from the repository root:
#   docker build -f docker/Dockerfile -t sockless-port .

ARG DOTNET_VERSION=11.0

# ---- build (runs on the build machine's architecture, cross-publishes for the target) ----
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:${DOTNET_VERSION} AS build
ARG TARGETARCH
ARG VERSION=0.1.0-dev
WORKDIR /src

# Restore first so the layer is cached until a project or package version changes.
COPY global.json Directory.Build.props Directory.Packages.props ./
COPY build/ build/
COPY src/Sockless.Port.Docker/Sockless.Port.Docker.csproj src/Sockless.Port.Docker/
COPY src/Sockless.Port.Core/Sockless.Port.Core.csproj src/Sockless.Port.Core/
COPY src/Sockless.Port.Web/Sockless.Port.Web.csproj src/Sockless.Port.Web/
RUN dotnet restore src/Sockless.Port.Web/Sockless.Port.Web.csproj -a $TARGETARCH

COPY src/ src/
RUN dotnet publish src/Sockless.Port.Web/Sockless.Port.Web.csproj \
      --no-restore -a $TARGETARCH -c Release -o /app \
      -p:Version=$VERSION -p:UseAppHost=false

# ---- runtime ----
FROM mcr.microsoft.com/dotnet/aspnet:${DOTNET_VERSION} AS runtime
LABEL org.opencontainers.image.title="Sockless Port" \
      org.opencontainers.image.description="Container management for Docker, Docker Swarm and Podman" \
      org.opencontainers.image.source="https://github.com/sockless-coding/sockless-port" \
      org.opencontainers.image.licenses="MIT"

# The base image's non-root "app" user (UID 1654) owns the data volume. Access to the
# Docker/Podman socket comes from adding the socket's group at run time (compose: group_add).
RUN mkdir -p /data && chown app:app /data
VOLUME /data

ENV ASPNETCORE_HTTP_PORTS=8080 \
    Port__DataPath=/data \
    DOTNET_CLI_TELEMETRY_OPTOUT=1

WORKDIR /app
COPY --from=build /app .
USER app
EXPOSE 8080

HEALTHCHECK --interval=30s --timeout=5s --start-period=20s --retries=3 \
  CMD ["dotnet", "Sockless.Port.Web.dll", "healthcheck"]

ENTRYPOINT ["dotnet", "Sockless.Port.Web.dll"]
