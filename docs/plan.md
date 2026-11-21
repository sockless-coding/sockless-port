# Sockless Port: implementation plan

Status: draft, 2026-11-21

A self-hosted container management UI for Docker, Docker Swarm and Podman. It runs as a container and manages containers, stacks (Compose and Swarm), images, volumes and networks on the local engine. On Swarm it also manages services, nodes, secrets and configs. A lightweight agent installed on other hosts lets it manage remote engines too.

## Guidelines

The UI follows the Sockless guidelines (`sockless-guidelines`):

- Variant: **Workshop**. Surface guide: `design/sockless/surfaces/web-app.md`. Tokens: `design/sockless/tokens/workshop.css`, copied into the project as-is.
- Dark only, tokens and no raw hex, copper-light for interactive elements, brass for warnings, verdigris for success, rust for danger. Status is never shown by colour alone.
- Shell: 256 px sidebar and a 64 px top bar, locked to the viewport. Only the content area scrolls.
- WCAG 2.2 AA, visible `:focus-visible` rings, a skip link, landmarks, and support for `prefers-reduced-motion`.
- Destructive actions ask for confirmation and name the target, e.g. "Remove container `nginx-1`?".
- Tailwind, htmx, fonts, xterm.js and the editor are all self-hosted (no CDN), because admin tools often run offline.

## Architecture

```
                  ┌───────────────────────────── host A ─────────────────────────────┐
 browser ──HTTPS──▶ sockless-port (container)                                         │
                  │  ASP.NET Core: Razor Pages + htmx + SignalR                       │
                  │  ├─ Endpoint registry (SQLite)                                    │
                  │  ├─ Docker client ──unix socket──▶ docker.sock / podman.sock      │
                  │  └─ Docker client ──mTLS──┐                                       │
                  └───────────────────────────┼───────────────────────────────────────┘
                                              │
                  ┌───────────── host B ──────▼────────────┐
                  │ sockless-port-agent (container, AOT)   │
                  │  ├─ API relay ─▶ docker/podman socket  │
                  │  ├─ Stack runner (docker compose)      │
                  │  └─ Host info                          │
                  └────────────────────────────────────────┘
```

**Main decision:** the agent exposes the **same Docker Engine API**, relayed over mutual TLS, plus a few extra endpoints of its own (stacks, host info). The server talks to local and remote engines through one Docker client. A remote endpoint is just a different transport, so every feature works on remote hosts without extra code.

**Engines and capabilities:** Podman serves a Docker-compatible API (`podman.socket`), so the same client covers it. Each endpoint detects its engine once it connects: Podman shows up as `Podman Engine` in `/version` components, and Swarm shows up as `Swarm.LocalNodeState` / `ControlAvailable` in `/info`. The endpoint records a set of **capabilities**, for example `Swarm`, `SwarmManager`, `Pods` and `Build`. The UI shows or hides navigation and actions based on these capabilities, never on engine-name checks scattered through pages. Where an API behaves differently on Podman, the client handles it in one place behind the capability.

### Technology

| Concern | Choice | Why |
|---|---|---|
| Runtime | .NET 11, C# 15 | Requirement |
| UI | ASP.NET Core Razor Pages, htmx (`hx-boost`), Tailwind 3 (standalone CLI, no Node at runtime) | Matches the guideline's reference implementation |
| Live data | SignalR | Logs, stats, events, pull progress, exec terminal |
| Terminal | xterm.js (self-hosted) | Container exec/attach |
| Editor | CodeMirror 6 (self-hosted, prebuilt bundle) | Compose and env editing; lighter than Monaco |
| Docker access | Our own thin typed client over `HttpClient` (unix socket, named pipe, TCP+TLS), with source-generated `System.Text.Json` | Docker.DotNet lags behind Engine API versions. A thin client keeps the agent AOT-friendly and lets one client serve both transports |
| Stacks | Bundled `docker` CLI + `docker compose` v2 plugin, run as a process with `DOCKER_HOST` pointed at the endpoint | Reimplementing Compose is out of scope. `docker compose` works against Podman's socket, and `docker stack deploy` covers Swarm |
| Storage | SQLite + EF Core, in the `/data` volume | Single-container deployment |
| Secrets | ASP.NET Data Protection, keys persisted to `/data/keys` | Registry passwords, agent client keys |
| Auth | Cookie auth, local users (hashed with `PasswordHasher`), first-run setup page; OIDC later | |
| Agent | ASP.NET Core minimal API, Native AOT, chiselled/distroless image | "Lite": small image, low memory |
| Tests | xUnit v3; integration tests against real engines: Docker, Docker in Swarm mode, rootful and rootless Podman | |

### Solution layout

```
src/
  Sockless.Port.Web/        Razor Pages, hubs, auth, static assets (tokens, Tailwind build)
  Sockless.Port.Core/       Endpoint registry, stacks, audit, EF Core model, services
  Sockless.Port.Docker/     Engine API client, models, transports (shared with the agent)
  Sockless.Port.Agent/      The remote agent
tests/
  Sockless.Port.Docker.Tests/
  Sockless.Port.Core.Tests/
  Sockless.Port.Web.Tests/  (WebApplicationFactory, plus Playwright smoke tests later)
docker/
  Dockerfile                server image (includes the docker compose plugin)
  Dockerfile.agent          agent image
  compose.yaml              how users run it
```

## Phases

Each phase can ship on its own and ends with a working, containerised build.

### Phase 0: Foundations

- Solution, `Directory.Build.props` (net11.0, nullable, warnings as errors, central package management), `.editorconfig`.
- `Sockless.Port.Web` with the Workshop shell: sidebar, top bar, skip link, theme tokens, fonts, Tailwind build wired into MSBuild.
- Auth: first-run admin setup, login and logout, then authorisation on every page.
- SQLite + EF Core migrations, Data Protection keys in `/data`.
- Server `Dockerfile` (multi-stage, non-root user with the docker group GID configurable), `compose.yaml`, health endpoint. Also document running under Podman (rootful: mount `/run/podman/podman.sock`; rootless: mount `$XDG_RUNTIME_DIR/podman/podman.sock`, with the SELinux `:z` label or `--security-opt label=disable`).
- CI (GitHub Actions): build, test, build the image.
- `AGENTS.md`/`CLAUDE.md` in this repo that routes to the guidelines.

**Done when:** `docker compose up` serves an empty, themed, logged-in dashboard.

**Status: done.** Notes:
- First-run setup needs a one-time token that is printed to the container log, so the first visitor can't claim a fresh instance.
- Auth uses ASP.NET Core Identity on SQLite. Lockout after 5 failed attempts covers brute force per account; a per-IP rate limit stays in Phase 9.
- A strict CSP (`'self'` only, no inline script or style, htmx without eval) is already in place.
- The UI is served over HTTP on 8080. TLS comes from a reverse proxy for now; built-in TLS arrives with the internal CA in Phase 6.
- The `docker compose` plugin is not in the image yet; it's added in Phase 5.

### Phase 1: Docker client and local endpoint

- `Sockless.Port.Docker`: transports (unix socket, Windows named pipe, TCP+TLS), API version negotiation (`/_ping`, `/version`), error mapping, streaming responses (multiplexed stdout/stderr frames, JSON-lines progress).
- An endpoint model with the local socket as the default endpoint. It auto-detects `/var/run/docker.sock`, `/run/podman/podman.sock` or a configured path. An endpoint switcher in the top bar shows each endpoint's engine (Docker, Swarm, Podman) as a word badge, not colour alone.
- Engine and capability detection (see Architecture). The capability set is cached per endpoint and refreshed on reconnect.
- CI runs the client's integration tests against a matrix: Docker, Docker in Swarm mode, and Podman rootful and rootless.
- Dashboard: engine info, counts (containers running/stopped, images, volumes, networks), disk usage (`/system/df`).
- Docker events stream → SignalR → live refresh of lists.

**Status: done.** Notes:
- **API versions:** the minimum is **1.41**, not 1.44, so Podman's compatible API works across versions. Ubuntu 24.04 ships Podman 4.9, which reports 1.41; Podman 6.2 reports 1.44. Requests use at most 1.51, and newer engines are asked for 1.51.
- **Endpoints** are stored in SQLite. The local endpoint is detected on first start (`DOCKER_HOST`, then the well-known Docker and Podman sockets, then named pipes on Windows); `Port:LocalEngine` overrides detection. Pages live under `/endpoints/{id}`, and the last endpoint visited is remembered in a cookie.
- **The monitor** keeps each endpoint connected with exponential backoff (1 s to 30 s), streams events, and pings every 30 s so a silently dead connection is noticed. Endpoint state is a `closed` hierarchy: Connecting, Online or Offline. Offline carries a reason that says what to check (e.g. the missing `group_add`).
- **Podman differences** found by the integration tests: volume events have an empty `Actor.ID`, and removal is reported as `remove` instead of `destroy`. Both are normalised in the client.
- **Disk usage** loads separately (it can be slow) with its own refresh button. Podman reports no build cache, so that row is hidden.
- **Not yet built:** adding or removing endpoints in the UI. Remote endpoints arrive with the agent in Phase 6; until then the only endpoint is the configured or detected local one.

### Phase 2: Containers

- List with filters (state, name, label, stack) and bulk start/stop/restart/remove.
- Detail page: overview, environment, mounts, ports, networks, labels, raw inspect JSON.
- Actions: start, stop, restart, kill, pause/unpause, rename, remove (optionally with volumes).
- Logs: live tail, search, timestamps, download.
- Stats: CPU, memory, network and I/O charts (Chart.js, per the guideline's chart rules).
- Exec console (xterm.js over SignalR).
- Create/recreate container form (image, ports, env, volumes, network, restart policy, resources).
- On Swarm, label containers that belong to a service task and link them to the service, so they aren't edited as standalone containers.

### Phase 3: Images and registries

- List images, inspect them, view history, tag, remove, prune (dangling/unused).
- Pull with live layer progress; build from a Dockerfile/tarball upload (stretch goal).
- Registry credentials (Docker Hub, GHCR, private registries), stored encrypted and used for pulls.
- "Update available" check for images in use (compare digests).

### Phase 4: Volumes and networks

- Volumes: list, create (driver and options), inspect, show which containers use each one, remove, prune. Optional file browser through a short-lived helper container.
- Networks: list, create (bridge/overlay/macvlan, subnet/gateway), inspect, connect/disconnect containers, remove, prune.

### Phase 5: Stacks (Compose)

- Stack storage: `/data/stacks/<endpoint>/<name>/compose.yaml` + `.env`.
- Editor with YAML validation (`docker compose config`), plus a diff before redeploy.
- Deploy, update (pull + up), stop, down (optionally removing volumes), and a per-stack service and container view.
- Detect stacks created outside the UI (by the `com.docker.compose.project` label) and offer to adopt them.
- On Podman, the same flow runs through `docker compose` against the Podman socket. Known Compose gaps on Podman are listed in the UI when they come up.
- Stack model designed with a `Kind` (Compose, Swarm) from the start, so Phase 7 adds Swarm stacks without a migration rewrite.
- Later: git-backed stacks (repo + path + branch, manual or webhook redeploy).

### Phase 6: Remote agent

- `Sockless.Port.Agent`: an AOT minimal API that relays `/v1.xx/*` to the local socket, plus `/agent/stacks/*` (runs compose locally from files the server pushes) and `/agent/info`.
- **Enrolment:** the server creates an endpoint and shows a single-use, time-limited `docker run …` command containing an enrolment token. On first start the agent generates a key pair, sends a CSR with the token, and receives a client/server cert signed by the server's internal CA. After that, all traffic uses mTLS with pinned certificates.
- **Direct mode (first):** the server connects to the agent's port. This needs the agent's host to be reachable from the server.
- **Edge mode (later):** the agent dials out to the server over a WebSocket tunnel, for hosts behind NAT or firewalls. The server's transport layer gains a "tunnel" transport, and nothing above it changes.
- Endpoint health, agent version reporting, and an "agent update available" prompt.
- `Dockerfile.agent` and a one-line install script, with variants for Docker, Podman (rootful/rootless) and Swarm (a `docker service create --mode global` command, so every node runs an agent).

### Phase 7: Swarm

Shown only on endpoints with the `Swarm` capability. Write actions need `SwarmManager`.

- Cluster overview: nodes, managers and leader, cluster-wide resource totals, join tokens (shown only on request, with rotate).
- Nodes: list, inspect, availability (active/pause/drain), role (promote/demote), labels, remove.
- Services: list, detail (tasks per node, replicas, update status), create/edit (image, replicas or global, ports, mounts, networks, constraints, placement preferences, resources, update/rollback config), scale, force update, rollback, remove, and logs aggregated across tasks.
- Swarm stacks: deploy, update and remove through `docker stack deploy` with the same editor and storage as Compose stacks, plus a view of services grouped by stack (`com.docker.stack.namespace`).
- Secrets and configs: list, create, inspect metadata (secret values are never shown again), remove. Each one shows the services that use it.
- Overlay networks: Phase 4 network pages gain the attachable/ingress/encrypted options.
- Per-node container views: when agents run on every node (global mode), the endpoint lets you drill into a node's own containers, logs and exec. Without agents, it shows only what the manager API exposes.

### Phase 8: Podman extras

Shown only on endpoints with the `Pods` capability. These use Podman's libpod API (`/v4.0.0/libpod/*`) through the same transport.

- Pods: list, inspect, start/stop/restart/remove, and a view of containers grouped by pod.
- Notes on rootless Podman in the UI: port binding below 1024, cgroup limits for resource stats.
- Later: Quadlet/systemd unit awareness, `podman kube play` for Kubernetes YAML.

### Phase 9: Hardening and administration

- Users and roles (admin, operator, viewer), with access set per endpoint.
- Audit log of every mutating action (who, what, target, endpoint).
- OIDC login.
- Settings: time display (local/UTC, per the guideline), session timeout, backup and restore of `/data`.
- Security review: CSRF on every htmx POST, CSP, the risks of mounting the socket (documented), rate-limited login.

## Risks and notes

- **Socket access is root on the host.** This is documented clearly. The server and agent run as non-root users in the docker group; a read-only socket mode (via a proxy) is an option for viewer-only setups.
- **Compose binary versions** are pinned in both images and updated deliberately.
- **Docker API drift:** the client targets a minimum API version (for example 1.44) and negotiates down. CI tests against current and previous engine versions.
- **Windows hosts:** named pipe support is in the client, but the images target Linux first.
- **Podman differences:** the compatible API is close to Docker's but not identical. Examples are event fields, stats on rootless cgroups v1, and some network options. Each known difference is handled in the client behind a capability, and the Podman CI jobs catch regressions.
- **Swarm manager access:** a manager's socket controls the whole cluster, so Swarm write actions get their own role permission in Phase 9.

## Decisions

- "Images" is the scope originally written as "isos".
- Swarm is in scope (Phase 7).
- Podman is in scope: the core features through the compatible API, plus pods in Phase 8.
- Product name: **Sockless Port**.
