# Sockless Port

Self-hosted container management for Docker, Docker Swarm and Podman. Sockless Port runs as a container and manages containers, stacks, images, volumes and networks on the local engine. A lightweight agent will let it manage remote engines too.

> **Status:** early development. Phases 0 to 4 are done: it manages containers, images, volumes and networks on the local engine. The roadmap is in [docs/plan.md](docs/plan.md).

## Run it

```sh
DOCKER_GID=$(stat -c %g /var/run/docker.sock) docker compose -f docker/compose.yaml up -d --build
docker logs sockless-port      # copy the setup token
```

Open <http://localhost:9000>, enter the setup token and create the first administrator. The token is printed to the log on every start until an account exists, and stops working once setup is complete.

Data (the SQLite database and encryption keys) is kept in the `port-data` volume, mounted at `/data`.

The app runs as a non-root user, so it needs the group that owns the engine socket. `DOCKER_GID` sets it: on Linux, `stat -c %g /var/run/docker.sock` prints it. On Docker Desktop the socket inside containers belongs to group `0`, so use `DOCKER_GID=0`. Without the group the dashboard shows the endpoint as offline with "Permission denied" and says what to change.

### Which engine it manages

On first start Sockless Port looks for an engine in this order: `DOCKER_HOST`, `/var/run/docker.sock`, `/run/podman/podman.sock`, `$XDG_RUNTIME_DIR/podman/podman.sock`. It uses the first that answers. To choose one explicitly, set `Port__LocalEngine` (for example `unix:///run/podman/podman.sock` or `tcp://10.0.0.5:2375`). That setting also replaces the stored address on every start.

### Volume file browser

Browsing a volume's files starts a short-lived helper container that mounts the volume read-only, without a network. It uses `busybox:1.37`, pulled when missing. On hosts without internet access, set `Port__HelperImage` to an image the engine already has that provides `find` and `stat`.

### Podman

Mount the Podman socket in place of the Docker one:

| Mode | Socket to mount at `/var/run/docker.sock` |
|---|---|
| Rootful | `/run/podman/podman.sock` (enable it with `systemctl enable --now podman.socket`) |
| Rootless | `$XDG_RUNTIME_DIR/podman/podman.sock` (enable it with `systemctl --user enable --now podman.socket`) |

On SELinux hosts, add `:z` to the socket mount or run with `--security-opt label=disable`.

### Security

Access to the engine socket is equivalent to root on the host. Keep Sockless Port behind authentication, off the public internet, and preferably behind a TLS-terminating reverse proxy. Set `ASPNETCORE_FORWARDEDHEADERS_ENABLED=true` when a proxy sits in front of it.

## Develop

Needs the .NET 11 SDK. Node is not needed: the build downloads the Tailwind standalone CLI.

```sh
dotnet build
dotnet test
dotnet run --project src/Sockless.Port.Web
```

See [AGENTS.md](AGENTS.md) for the project layout and conventions.

## License

[MIT](LICENSE). Third-party components are listed in [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
