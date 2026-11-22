# syntax=docker/dockerfile:1
# Sockless Port server image. Build from the repository root:
#   docker build -f docker/Dockerfile -t sockless-port .

ARG DOTNET_VERSION=11.0

# ---- build (runs on the build machine's architecture, cross-publishes for the target) ----
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:${DOTNET_VERSION} AS build
ARG TARGETARCH
ARG VERSION=0.0.0-dev
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

# ---- docker compose: a pinned release, checked against its published SHA-256 ----
# Bump deliberately: update the version and both checksums (from the release's .sha256 files).
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:${DOTNET_VERSION} AS compose
ARG TARGETARCH
ARG COMPOSE_VERSION=5.7.0
ARG COMPOSE_SHA256_AMD64=7758048ad38e580f22559603312874fec8bb638d021a9e64182385fc17cc12a2
ARG COMPOSE_SHA256_ARM64=15861535d14e26555736b49376c95e14bfd94285366c8446bfac7156a972e76f
RUN set -eu;     case "$TARGETARCH" in       amd64) arch=x86_64; sum="$COMPOSE_SHA256_AMD64" ;;       arm64) arch=aarch64; sum="$COMPOSE_SHA256_ARM64" ;;       *) echo "No Compose build for $TARGETARCH" >&2; exit 1 ;;     esac;     curl -fsSL -o /docker-compose "https://github.com/docker/compose/releases/download/v${COMPOSE_VERSION}/docker-compose-linux-${arch}";     echo "${sum}  /docker-compose" | sha256sum -c -;     chmod 755 /docker-compose

# ---- Trivy, for vulnerability scans: a pinned release, checked against its published SHA-256 ----
# Bump deliberately: update the version and both checksums (from the release's trivy_<version>_checksums.txt).
# Never take "latest": v0.69.4 was a compromised release (GHSA-69fq-xp46-6x23).
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:${DOTNET_VERSION} AS trivy
ARG TARGETARCH
ARG TRIVY_VERSION=0.76.2
ARG TRIVY_SHA256_AMD64=bc0fc726e017b8927a62b2d3531d7a84ee01769daf5c99ed866dced54089fdbf
ARG TRIVY_SHA256_ARM64=ab9d51f86e6a5fb936f605faff6b593ac51bc378fff4d3e8b9d58358b9c367c3
RUN set -eu;     case "$TARGETARCH" in       amd64) arch=64bit; sum="$TRIVY_SHA256_AMD64" ;;       arm64) arch=ARM64; sum="$TRIVY_SHA256_ARM64" ;;       *) echo "No Trivy build for $TARGETARCH" >&2; exit 1 ;;     esac;     curl -fsSL -o /tmp/trivy.tar.gz "https://github.com/aquasecurity/trivy/releases/download/v${TRIVY_VERSION}/trivy_${TRIVY_VERSION}_Linux-${arch}.tar.gz";     echo "${sum}  /tmp/trivy.tar.gz" | sha256sum -c -;     tar -xzf /tmp/trivy.tar.gz -C / trivy;     chmod 755 /trivy

# ---- docker CLI, for swarm stacks (docker stack deploy): the official image, pinned by digest ----
# Bump deliberately: update the tag and the index digest (docker buildx imagetools inspect docker:<version>-cli).
FROM docker:29.10.0-cli@sha256:34507eb5069d8be82c95dd3b6987d4250fd5fee49b82e2509b7d7a7030740d6d AS docker-cli

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

# Stacks run through the standalone Compose binary (Port:Compose defaults to docker-compose).
COPY --from=compose /docker-compose /usr/local/bin/docker-compose
# Swarm stacks run through the Docker CLI's stack commands (Port:DockerCli defaults to docker).
COPY --from=docker-cli /usr/local/bin/docker /usr/local/bin/docker
# Images are scanned with Trivy (Port:Scanner:Command defaults to trivy); its databases go to /data/scanner.
COPY --from=trivy /trivy /usr/local/bin/trivy

WORKDIR /app
COPY --from=build /app .
USER app
EXPOSE 8080
# Only with Port__Https__Port=8443 (built-in HTTPS, off by default).
EXPOSE 8443

HEALTHCHECK --interval=30s --timeout=5s --start-period=20s --retries=3 \
  CMD ["dotnet", "Sockless.Port.Web.dll", "healthcheck"]

ENTRYPOINT ["dotnet", "Sockless.Port.Web.dll"]
