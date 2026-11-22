# Sockless Port

Self-hosted container management for Docker, Docker Swarm and Podman. Sockless Port runs as a container and manages containers, stacks, images, volumes and networks on the local engine, and on remote engines through a lightweight agent. On a swarm manager it also manages services, nodes, secrets, configs and swarm stacks, and on Podman its pods.

> **Status:** early development. Phases 0 to 9 are done: Compose and swarm stacks, containers, images, volumes and networks, remote engines through agents, Docker Swarm, Podman pods, and users with roles per endpoint, an audit log, single sign-on, backups and built-in HTTPS. The roadmap is in [docs/plan.md](docs/plan.md).

## Run it

```sh
DOCKER_GID=$(stat -c %g /var/run/docker.sock) docker compose -f docker/compose.yaml up -d --build
docker logs sockless-port      # copy the setup token
```

Open <http://localhost:9000>, enter the setup token and create the first administrator. The token is printed to the log on every start until an account exists, and stops working once setup is complete.

Data (the SQLite database, encryption keys and stack files) is kept in the `port-data` volume, mounted at `/data`.

The app runs as a non-root user, so it needs the group that owns the engine socket. `DOCKER_GID` sets it: on Linux, `stat -c %g /var/run/docker.sock` prints it. On Docker Desktop the socket inside containers belongs to group `0`, so use `DOCKER_GID=0`. Without the group the dashboard shows the endpoint as offline with "Permission denied" and says what to change.

### Which engine it manages

On first start Sockless Port looks for an engine in this order: `DOCKER_HOST`, `/var/run/docker.sock`, `/run/podman/podman.sock`, `$XDG_RUNTIME_DIR/podman/podman.sock`. It uses the first that answers. To choose one explicitly, set `Port__LocalEngine` (for example `unix:///run/podman/podman.sock` or `tcp://10.0.0.5:2375`). That setting also replaces the stored address on every start.

### Stacks

Stacks are Compose projects. Each one's `compose.yaml` and `.env` are kept in `/data/stacks/<endpoint>/<name>/`, and the image's bundled `docker compose` runs from there against the engine. Projects started with `docker compose` elsewhere on the engine are listed too: they can be started, stopped and taken down by name, or adopted to be edited and redeployed from Sockless Port.

Compose runs inside the Sockless Port container, not on the engine's host, so:

- **Relative bind mounts** (`./data:/data`) resolve inside the data volume, which the engine can't see. Use absolute host paths or named volumes. The editor warns when a file has them.
- **`build:`** has no build context: only the two files are stored, and the image has no Buildx. Build and push images elsewhere, then use `image:`.
- **Interpolation** (`${VAR}`) uses the stack's `.env` only. Sockless Port's own environment is never passed to Compose.
- **Registry credentials** stored under Registries are used for the pulls Compose makes.

To use another Compose binary, set `Port__Compose` (for example `docker compose`, or a full path).

### Docker Swarm

When the endpoint is a swarm manager, the sidebar gets a Swarm section: the cluster (nodes, managers, resources and join tokens, shown only on request), services (create, edit, scale, restart, update the image, roll back, and logs of every task), nodes (drain, pause, promote, demote, labels, remove), secrets and configs. A worker only shows that it's one: managers know the cluster.

Stacks on a manager can be **swarm stacks**, deployed with `docker stack deploy` from the same editor and files as Compose stacks. The image bundles the Docker CLI for that (`Port__DockerCli` picks another). As with Compose, `${VAR}` comes from the stack's `.env` only, and the stored registry credentials are passed on (`--with-registry-auth`). The editor also warns about settings swarm ignores, such as `container_name:`, `restart:` and `depends_on:`.

Services created or changed here are pinned to the digest their tag points at, as `docker service create` does, so every node runs the same image.

To see a node's own containers, logs and terminals, enrol the agent on that node as an endpoint (the Docker Swarm install command runs one on every node). Node pages link to the endpoint that reaches them.

### Volume file browser

Browsing a volume's files starts a short-lived helper container that mounts the volume read-only, without a network. It uses `busybox:1.37`, pulled when missing. On hosts without internet access, set `Port__HelperImage` to an image the engine already has that provides `find` and `stat`.

### Podman

Mount the Podman socket in place of the Docker one:

| Mode | Socket to mount at `/var/run/docker.sock` |
|---|---|
| Rootful | `/run/podman/podman.sock` (enable it with `systemctl enable --now podman.socket`) |
| Rootless | `$XDG_RUNTIME_DIR/podman/podman.sock` (enable it with `systemctl --user enable --now podman.socket`) |

On SELinux hosts, add `:z` to the socket mount or run with `--security-opt label=disable`.

On Podman the sidebar gets **Pods**: each pod with its containers, and start, stop, restart and remove (which removes the pod's containers too). Container lists say which pod a container is in and filter by pod. A container in a pod isn't offered *Recreate*, because Docker's API, which Sockless Port uses for containers, can't put the new one back in the pod.

Rootless engines (Docker or Podman) are badged on the dashboard. The container form says that they can't publish ports below 1024 and that limits need cgroups v2 with the controllers delegated to the engine's user. When an engine call fails for one of these reasons, the error says what to do.

### Users and roles

The first administrator is created at setup. Under **Users**, administrators add people and give each a role per endpoint: viewer (reads everything), operator (also changes containers, images, volumes, networks, pods and Compose stacks, and opens consoles) or swarm operator (also changes the swarm). A role "on all endpoints" also covers endpoints added later. Every change is in the **Audit log**, with who made it, the target, the endpoint and the outcome.

Each user chooses below the menu whether times are shown in their browser's time zone or in UTC; Settings has the default.

### Single sign-on

Sign-in through an OpenID Connect provider (Keycloak, Authentik, Entra ID, …) works next to passwords:

```yaml
environment:
  Port__Oidc__Authority: https://auth.example.com/realms/main
  Port__Oidc__ClientId: sockless-port
  Port__Oidc__ClientSecret: ${OIDC_SECRET}
  # Optional: Port__Oidc__DisplayName, Port__Oidc__Scopes__0, Port__Oidc__CreateUsers=true,
  # Port__Oidc__DefaultRole=Viewer, Port__Oidc__DisablePasswordLogin=true
```

Register `https://<host>/signin-oidc` as the redirect URI. Users are matched by their verified email address, so add them under Users with it first, or set `Port__Oidc__CreateUsers=true` to create them on their first sign-in (with no access until you grant some).

### HTTPS

A reverse proxy that terminates TLS is the usual setup; set `ASPNETCORE_FORWARDEDHEADERS_ENABLED=true` behind one. Sockless Port can also serve HTTPS itself:

```yaml
ports:
  - "9443:8443"
environment:
  Port__Https__Port: "8443"
  Port__Https__HostNames__0: port.example.com   # for the internal CA's certificate
  # Or your own: Port__Https__Certificate=/certs/tls.crt, Port__Https__CertificateKey=/certs/tls.key
  # Port__Https__RedirectHttp: "true", Port__Https__PublicPort: "9443"
```

Without a certificate of yours, the internal CA (the one that signs agent certificates) issues one and renews it. Download the CA certificate from Settings to trust it in browsers.

### Vulnerability scanning

Images can be scanned for known vulnerabilities with [Trivy](https://github.com/aquasecurity/trivy), which the image includes. Scan one image from its Vulnerabilities tab, a container's image from its overview, or everything containers use from the Images page; Settings can scan daily or weekly. Each image is copied from its engine to the Sockless Port server and scanned there, so it works the same for remote agents and Podman, and the result is shared by every endpoint with the same image. Packages installed in a running container after it started aren't part of the image, so they aren't covered.

A finding you've reviewed can be marked as an accepted risk, with the reason and an optional expiry: it stays in the scan but no longer counts, and its comment thread keeps the reasoning for whoever looks next.

Trivy downloads its vulnerability databases into `/data/scanner` when they're due. Without internet access, point it at a mirror (`Port__Scanner__DatabaseRepositories__0`, `Port__Scanner__JavaDatabaseRepositories__0`), or copy the databases in and set `Port__Scanner__SkipDatabaseUpdate=true`.

### Backups

Settings downloads a backup of the data volume (database, encryption keys, internal CA and stack files) as one `.tar.gz`, taken while everything runs. Restoring one is checked on upload and applied at the next start; the compose file's `restart: unless-stopped` brings Sockless Port back by itself. A backup can decrypt the stored registry passwords and reach every agent, so keep it safe.

### Security

Access to the engine socket is equivalent to root on the host. Keep Sockless Port off the public internet, give people the least role they need, and read [docs/security.md](docs/security.md) for what Sockless Port protects and what it can't.

## Develop

Needs the .NET 11 SDK. Node is not needed: the build downloads the Tailwind standalone CLI, and the CodeMirror bundle is checked in (rebuild it from `build/codemirror/` when bumping it). Stacks need `docker-compose` or the `docker compose` plugin on the PATH, and swarm stacks the `docker` CLI.

```sh
dotnet build
dotnet test
dotnet run --project src/Sockless.Port.Web
```

See [AGENTS.md](AGENTS.md) for the project layout and conventions.

## License

[MIT](LICENSE). Third-party components are listed in [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
