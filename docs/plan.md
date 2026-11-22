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
- The UI is served over HTTP on 8080. TLS comes from a reverse proxy for now; built-in TLS is in Phase 9 (the internal CA from Phase 6 is for agents only).
- The `docker compose` binary was added to the image in Phase 5.

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
- **Not yet built:** adding or removing endpoints in the UI. Remote endpoints arrive with the agent in Phase 6; until then the only endpoint is the configured or detected local one. *(Built in Phase 6.)*

### Phase 2: Containers

- List with filters (state, name, label, stack) and bulk start/stop/restart/remove.
- Detail page: overview, environment, mounts, ports, networks, labels, raw inspect JSON.
- Actions: start, stop, restart, kill, pause/unpause, rename, remove (optionally with volumes).
- Logs: live tail, search, timestamps, download.
- Stats: CPU, memory, network and I/O charts (Chart.js, per the guideline's chart rules).
- Exec console (xterm.js over SignalR).
- Create/recreate container form (image, ports, env, volumes, network, restart policy, resources).
- On Swarm, label containers that belong to a service task and link them to the service, so they aren't edited as standalone containers.

**Status: done.** Notes:
- **Pages** are `/endpoints/{id}/containers`, plus `/create`, and for each container `/{container}` (overview), `/logs`, `/stats`, `/console` and `/inspect`. Lists and overviews refresh live on engine events. A list holds its refresh while rows are selected.
- **Create and recreate.** The create form takes `docker run`-style text (`8080:80`, `data:/data:ro`, `KEY=value`) and pulls the image when it's missing or when asked. *Duplicate* prefills the form from an existing container, without Compose or Swarm labels. *Recreate* (optionally pulling first) rebuilds the container from its raw inspect JSON, so unmodelled settings survive. It renames the old container aside, creates and starts the new one, then removes the old. On failure it puts the original back. Editing an existing container's settings in place is *Duplicate* followed by removing the old container. There's no separate edit form.
- **Console** uses exec with a TTY over a hijacked connection (HTTP 101), bridged to xterm.js through SignalR. Sessions belong to the browser connection that opened them, close when it drops, and are capped at 8 per connection.
- **Swarm task containers** carry a badge and a notice, and have no recreate, rename or duplicate. Changes belong on the service, which the notice links on a manager.
- **Engine differences** found by the integration tests: Podman without cgroups (rootless without delegation, or nested) can't pause or report stats. The engine's message is shown, and the tests tolerate it. Podman's journald log driver can't follow logs without the journald events backend, and that error is shown too. Recreating under host networking or host IPC must drop `Hostname` and `ShmSize`.
- **CSP stays strict.** xterm.js builds `<style>` elements at runtime, so pages carry a per-response style nonce that a small shim stamps on them. htmx is configured not to copy `style` attributes when it swaps content.
- **Chart colours** are copper-light and gaslight blue, not the guideline's primary + success order. That pair fails the colour-vision check (deuteranopia ΔE 3.4), and success is a status colour. Raised as guideline feedback.
- **Pull progress** wasn't shown in Phase 2. Phase 3 added it to create; recreate still waits for the pull to finish.

### Phase 3: Images and registries

- List images, inspect them, view history, tag, remove, prune (dangling/unused).
- Pull with live layer progress; build from a Dockerfile/tarball upload (stretch goal).
- Registry credentials (Docker Hub, GHCR, private registries), stored encrypted and used for pulls.
- "Update available" check for images in use (compare digests).

**Status: done.** Notes:
- **Pages** are `/endpoints/{id}/images`, plus `/build`, and for each image `/{image}` (overview, tags, containers using it), `/history` and `/inspect`. Registry credentials are global, at `/registries`.
- **Pulls and builds run as background jobs** (`ImageJobs`), at most 4 at a time per endpoint, with live per-layer progress over SignalR. A second pull of the same reference joins the one already running. Finished jobs stay visible for 15 minutes, so a page opened later still shows how they ended. Docker's and Podman's pull formats differ and are both parsed in the client (`PullTracker`).
- **Build** takes a Dockerfile typed into the form or an uploaded `.tar`/`.tar.gz` context (up to 2 GB, staged in `TempPath`, which is emptied on start), with tags, pull and no-cache options.
- **Registry credentials** are encrypted with Data Protection (keys in the data volume) and attached to pulls, builds, recreate and update checks by matching the reference's registry. If the keys are lost, a credential is marked unreadable and must be entered again. Passwords are never shown again.
- **Update check** asks the engine for the registry's digest (`/distribution/{name}/json`, no pull) for every tag a container uses, and compares it with the image's repo digests. Docker records the index digest, Podman the index and the platform manifest, so either one counts as a match. Results are in memory per endpoint and keyed by image ID, so a re-pulled image starts out unchecked. Tags that can't be checked (built locally, removed from the registry, credentials rejected) show the reason, and the summary counts each outcome separately.
- **Update status in containers and stacks:** the container and stack lists badge what has an update available, and their detail pages show the result (the container's summary, each stack service's image, a notice saying how to update) with a "Check for updates" button for just that container or stack; the lists' button checks what they show. A container is checked by the reference it was created from, so one whose tag has since moved to another image (Docker then lists it by image ID; its config still names the tag) compares its own image with the registry. Swarm services are checked by the digest their spec is pinned to, since the image may not be on the manager; unpinned services say so. A scheduled check (Settings, off by default since checks can count against Docker Hub's pull rate limit; every 6 hours to weekly) checks every online endpoint when its last check is older than the interval, so also soon after a start, and open pages refresh when it ends.
- **Container create** pulls with live progress. **Recreate** still pulls without showing progress.

### Phase 4: Volumes and networks

- Volumes: list, create (driver and options), inspect, show which containers use each one, remove, prune. Optional file browser through a short-lived helper container.
- Networks: list, create (bridge/overlay/macvlan, subnet/gateway), inspect, connect/disconnect containers, remove, prune.

**Status: done.** Notes:
- **Pages** are `/endpoints/{id}/volumes` (plus `/create`, and for each volume `/{name}`, `/files` and `/inspect`) and `/endpoints/{id}/networks` (plus `/create`, and for each network `/{id}` and `/inspect`). Lists and overviews refresh live on engine events. Container pages link their volumes and networks.
- **Usage** comes from one container list (`Mounts` and `NetworkSettings.Networks` are in it), so lists need no inspect per container. Stopped containers count as users, as they do for removal and prune.
- **Volume prune** offers "unused anonymous" and "all unused". A plain prune keeps named volumes only from API 1.42, so below that only "all unused" is offered. Docker labels anonymous volumes and Podman doesn't, so a 64-character hex name counts as anonymous too. Volume and network prunes accept a label filter, which the tests use to keep parallel runs apart.
- **Volume sizes** come from `/system/df?type=volume` and load separately (on the list, behind "Show sizes"), because the engine measures every volume. Podman ignores the type and computes everything.
- **File browser** (read-only): a POST starts a helper container from `Port:HelperImage` (default `busybox:1.37`, pulled when missing) that mounts the volume read-only with no network and runs `sleep` for 30 minutes. Pages list with `find … -exec stat` through a non-TTY exec, and download single files or folders (as tar) through the archive API. GET requests only use an open helper; a helper is reused for 25 minutes, removed after 5 minutes idle or on close, and released before the volume is removed or pruned. Stopped leftovers are swept on first use. Links are shown, not followed, and only regular files download (Podman's archive API follows links, Docker's doesn't). Helpers carry `sockless-port.helper`, don't count as users, and are badged in the container list. Editing or uploading files isn't built.
- **Network create** offers the drivers the engine lists in `/info` (bridge, macvlan, ipvlan), and overlay only on a swarm manager. Subnet, gateway and range are checked before the engine sees them, including host bits after the prefix. Macvlan and ipvlan take a host interface (`parent`). A second network with the same name is refused, since Docker would allow it and then can't address either by name. Attachable is only for overlay; the other overlay options stay in Phase 7.
- **Connect and disconnect** are on the network page, with aliases and a fixed IPv4 or IPv6 address. Containers on host, none or another container's network can't join, so they aren't offered. Built-in networks (`bridge`, `host`, `none`, `docker_gwbridge`, `ingress`; Podman's default is listed as `bridge` too) can't be removed.
- **Engine differences** found by the integration tests: Podman renames driver options (`com.docker.network.driver.mtu` becomes `mtu`), reports no endpoint IDs, answers "in use" errors with 500 instead of 403, and nested Podman runs containers with host networking, so the tests put them on `bridge` explicitly.

### Phase 5: Stacks (Compose)

- Stack storage: `/data/stacks/<endpoint>/<name>/compose.yaml` + `.env`.
- Editor with YAML validation (`docker compose config`), plus a diff before redeploy.
- Deploy, update (pull + up), stop, down (optionally removing volumes), and a per-stack service and container view.
- Detect stacks created outside the UI (by the `com.docker.compose.project` label) and offer to adopt them.
- On Podman, the same flow runs through `docker compose` against the Podman socket. Known Compose gaps on Podman are listed in the UI when they come up.
- Stack model designed with a `Kind` (Compose, Swarm) from the start, so Phase 7 adds Swarm stacks without a migration rewrite.
- Later: git-backed stacks (repo + path + branch, manual or webhook redeploy).

**Status: done.** Notes:
- **Pages** are `/endpoints/{id}/stacks`, plus `/create` (and `/create?adopt={name}`), and for each stack `/{name}` (overview: job, services and containers) and `/{name}/editor`. Lists and overviews refresh live on engine events and when a job ends. Container pages link their Compose stack.
- **Compose** is the standalone `docker-compose` 5.7.0 binary, pinned in the image and checked against its release SHA-256 (`Port:Compose` picks another; from source, `docker-compose` then `docker compose` are tried). It runs as a child process with an argument list and a **clean environment**: only `DOCKER_HOST` for the endpoint, a private `DOCKER_CONFIG` holding the stored registry credentials (deleted after the run), and what the OS needs. Sockless Port's own variables never reach `${…}` interpolation. TCP+TLS endpoints get their certificates in that folder, ready for Phase 6.
- **Storage:** a `Stacks` table (endpoint, name, `Kind` = Compose or Swarm, adopted, saved/deployed times) and the files in `/data/stacks/{endpoint}/{name}/`. A successful deploy copies the files to `.deployed/`, which the review diffs against; a down removes that copy.
- **Editor:** CodeMirror 6 (self-hosted bundle, source in `build/codemirror/`) over plain text areas that remain the form fields, so it works without scripts. Validation runs `docker compose config` on a temporary copy; errors that name a line show in the editor's gutter. It also warns about what's valid but won't work here: `build:` (no context, no Buildx), relative bind mounts (they resolve inside the data volume) and, on rootless engines, ports below 1024. A new `Rootless` capability comes from `/info` `SecurityOptions`.
- **Review before deploy:** "Review and deploy" shows a line diff of both files against what was last deployed, and Deploy carries a hash of the reviewed content: if the files changed after the review, the review is shown again instead. Diff lines carry +/− and a hidden word, tinted gaslight (added) and rust (removed), not success green.
- **Jobs** (`StackJobs`): deploy (`up --detach --remove-orphans`), pull and update (`--pull always`), start, stop, restart, take down (optionally `--volumes`) and remove (take down, then delete the files; or forget the stack and leave its containers running). One job per stack, at most 4 per endpoint, kept 15 minutes after they end. Output streams over SignalR; failures name Compose's last error line and add hints for known problems (`ComposeHints`).
- **External projects** (containers with `com.docker.compose.project`, not managed here) are listed with an External badge. Compose can start, stop, restart and take them down by project name, without the file. **Adopting** uses the project's own compose file and `.env` when the label's path is readable from the server; otherwise it rebuilds a file from the containers, leaving out what the image sets, doubling `$` so values aren't interpolated, and declaring volumes and networks the project didn't create as external.
- **Self-protection:** the project Sockless Port runs in (found from its own mounts and cgroup) is badged and can't be stopped, redeployed or taken down from the UI.
- **Engine differences** found by the integration tests: Podman records image names fully qualified (`docker.io/library/busybox:1.37`), and words a refused pull as "requested access to the resource is denied"; both are handled. Known Podman gaps (short names, rootless ports, cgroup limits, SELinux relabelling) are recognised in Compose's output and explained with the error.
- **Not built:** Swarm stacks (Phase 7, the `Kind` column is ready), git-backed stacks, extra files next to the compose file (`env_file:`, configs), and an unsaved-changes warning in the editor.

### Phase 6: Remote agent

- `Sockless.Port.Agent`: an AOT minimal API that relays `/v1.xx/*` to the local socket, plus `/agent/stacks/*` (runs compose locally from files the server pushes) and `/agent/info`.
- **Enrolment:** the server creates an endpoint and shows a single-use, time-limited `docker run …` command containing an enrolment token. On first start the agent generates a key pair, sends a CSR with the token, and receives a client/server cert signed by the server's internal CA. After that, all traffic uses mTLS with pinned certificates.
- **Direct mode (first):** the server connects to the agent's port. This needs the agent's host to be reachable from the server.
- **Edge mode (later):** the agent dials out to the server over a WebSocket tunnel, for hosts behind NAT or firewalls. The server's transport layer gains a "tunnel" transport, and nothing above it changes.
- Endpoint health, agent version reporting, and an "agent update available" prompt.
- `Dockerfile.agent` and a one-line install script, with variants for Docker, Podman (rootful/rootless) and Swarm (a `docker service create --mode global` command, so every node runs an agent).

**Status: done (direct mode).** Notes:
- **Agent** (`Sockless.Port.Agent`): a Native AOT minimal API in a chiselled image (about 45 MB, non-root, no shell). It relays the Engine API to the local socket unchanged, with bodies streaming both ways. Hijacked connections (exec and attach with a TTY) are spliced below Kestrel's HTTP layer, because Kestrel won't upgrade a request that has a body and exec start always sends one. Besides the relay, it serves only `/agent/enrol/*`, `/agent/info` (version, uptime) and `/agent/healthz` (no client certificate needed, for the container health check, and says nothing about the host).
- **Stacks need no agent routes.** Instead of the planned `/agent/stacks/*`, the server runs Compose itself against the agent's relay (`DOCKER_HOST=tcp://…`, `DOCKER_TLS_VERIFY`, and the CA and client certificate in the run's private folder). Stack files stay on the server, and the agent stays a plain relay. Covered by an integration test through a real agent.
- **Enrolment** runs in direct mode: the server connects to the agent. The *Add agent* page creates the endpoint and shows the install command for each variant, with a token (`spa1.{id}.{secret}`) that works once, within the hour. Until enrolment the agent serves a throwaway self-signed certificate. The agent proves it holds the token with an HMAC over its CSR, bound to a server nonce. The server signs only the key, choosing the name, usage and host names itself, and proves the token back over the certificates it issued. From then on, both sides use mutual TLS. The agent accepts only a client certificate from that CA named `sockless-port-server`, and the server pins the agent's certificate. Enrolling again (endpoint settings, optionally at a new address) revokes the old certificate at once.
- **Internal CA** (`InternalCa`): keys in `/data/pki`, encrypted with Data Protection like registry passwords. The CA lasts 20 years, agent certificates 5 years, and the server's client certificate 1 year (renewed 30 days before it expires). Agents accept any client certificate the CA signed. Agent certificates aren't renewed yet; re-enrol before they expire.
- **Endpoints** can now be added, renamed, re-enrolled and removed (`EndpointRegistry`). Removing one deletes its saved stacks. The local endpoint is detected at start and can only be renamed. The `IsLocal` column was replaced by the endpoint's kind (migrations `AddAgentEndpoints`, `DropEndpointIsLocal`), and an upgrade keeps the existing local endpoint local.
- **Health and versions:** agent endpoints go through the same monitor as the local one (backoff, events, ping). The endpoint list and page show the agent's version. When it differs from the server's, an "Update available" badge and the update command for the agent's variant appear; the identity lives in the agent's volume, so updating needs no re-enrolment. Install commands name the agent image of the server's version (`latest` for development builds). CI builds both images and pushes them on version tags.
- **Exec** opens its own connection for every engine, not just agents, because a hijacked connection belongs to the engine afterwards. The integration suite passes against Docker with this change.
- **Install script:** none. The per-variant one-line commands on the *Add agent* page are the install path. A `curl … | sh` served by the server would travel over plain HTTP while the UI has no built-in TLS.
- **Not built:** edge mode (agent dials out through a WebSocket tunnel), renewal of agent certificates, and per-node drill-down for Swarm (Phase 7).

### Phase 7: Swarm

Shown only on endpoints with the `Swarm` capability. Write actions need `SwarmManager`.

- Cluster overview: nodes, managers and leader, cluster-wide resource totals, join tokens (shown only on request, with rotate).
- Nodes: list, inspect, availability (active/pause/drain), role (promote/demote), labels, remove.
- Services: list, detail (tasks per node, replicas, update status), create/edit (image, replicas or global, ports, mounts, networks, constraints, placement preferences, resources, update/rollback config), scale, force update, rollback, remove, and logs aggregated across tasks.
- Swarm stacks: deploy, update and remove through `docker stack deploy` with the same editor and storage as Compose stacks, plus a view of services grouped by stack (`com.docker.stack.namespace`).
- Secrets and configs: list, create, inspect metadata (secret values are never shown again), remove. Each one shows the services that use it.
- Overlay networks: Phase 4 network pages gain the attachable/ingress/encrypted options.
- Per-node container views: when agents run on every node (global mode), the endpoint lets you drill into a node's own containers, logs and exec. Without agents, it shows only what the manager API exposes.

**Status: done.** Notes:
- **Pages** are under `/endpoints/{id}/swarm`: the cluster, `services` (and `/create`, and per service an overview, `/edit`, `/logs`, `/inspect`), `nodes` (overview, `/inspect`), `secrets` and `configs` (list, `/create`, detail). The sidebar's Swarm section uses `NavItem.Requires`: the cluster on any swarm engine, the rest on managers only. A worker explains that it's one instead of failing (`_SwarmCallout`). The dashboard's swarm card links the cluster.
- **Client:** `DockerClient.Swarm.cs` and `Models/SwarmModels.cs`, checked against captured fixtures. Every update names the object's version, so a change made meanwhile fails ("update out of sequence") instead of overwriting it. Service logs are read with `details=true`; `ServiceLogLine` takes the task, node and service from each line's attributes.
- **Cluster:** node, manager and reachable-manager counts (with how many failures the managers survive), CPUs and memory of the ready nodes, and what running tasks have reserved of them. Join tokens never appear on a GET: "Show join commands" is a POST that answers `no-store`. Worker and manager tokens rotate separately.
- **Services:** the form takes the CLI's formats (`8080:80/udp host`, `data:/data:ro`, `node.role==manager`, `512M`, `1m30s`). An edit applies the form onto the service's raw spec (`ServiceSpecBuilder`), so what it doesn't show (healthchecks, restart policy, log driver, tmpfs mounts, mount options, network aliases, custom secret targets, port names) survives; an unchanged form gives back the same spec. The mode can't change, and jobs aren't edited here. `SwarmServices` pins a new tag to its digest through the engine's distribution endpoint and passes the stored registry credentials (`X-Registry-Auth`), as `docker service create` does; when the registry can't be asked, the tag is kept with a warning. Scale, restart (force update), update image (resolve the tag again) and rollback (`rollback=previous`) are on the service's page. Tasks show per slot, current ones first, then the last few that ended, with their error. Task changes on other nodes send no events to the manager, so lists poll every 3–5 seconds while something converges and every 30 seconds otherwise.
- **Nodes:** availability (active, pause, drain), promote and demote (with a warning when it's the node this endpoint talks to), labels, and remove (forced when the node is still up). The node this endpoint talks to can't be removed from here.
- **Secrets and configs:** created from text (CRLF becomes LF) or an uploaded file, up to 500 KB and 1000 KB. A secret's value is never shown or rendered back into the form; a config's is shown as text when it is UTF-8, and downloadable. Lists and details show the services using each one, and where their tasks find it.
- **Swarm stacks:** `StackKind.Swarm`, chosen when a stack is created (the default on managers). They run through the Docker CLI (`DockerCli`; the image copies `docker` from the `docker:29.10.0-cli` image, pinned by digest): deploy is `stack deploy --prune --with-registry-auth --detach=true --resolve-image changed`, update the same with `always`, take down is `stack rm`, and restart force-updates each service through the API. `docker stack deploy` interpolates only from its environment, so the stack's `.env` is passed as the run's variables (`StackEnv`, read as Compose reads it); names the run needs itself (`PATH`, `DOCKER_*`, …) can't be replaced, and the editor warns about them. Validation runs `docker compose config` (line numbers, the existing warnings) and then `docker stack config`, which knows swarm's schema; Compose's "only applied to Swarm" warnings are dropped, and settings swarm ignores (`build:`, `container_name:`, `restart:`, `depends_on:`, …) are warned about. Swarm stacks don't start or stop. External swarm stacks (services with `com.docker.stack.namespace`) are listed, restarted and removed by name. Stack pages list the services with their state; services link back to their stack and warn on edit that the next deploy resets them. Self-protection checks the stack's tasks' containers.
- **Overlay networks:** the create form adds encrypted (the `encrypted` driver option) and ingress next to attachable; ingress can't be attachable or internal. Lists and pages badge them.
- **Per-node views:** `EngineDescriptor` now carries the swarm node and cluster IDs, so a node is matched to the endpoint that reaches it (this one, or an agent enrolled on it). Node pages link that endpoint's containers and dashboard, and task rows link containers there; without one, they offer *Add an agent endpoint* prefilled with the node's name and address.
- **Not built:** adopting swarm stacks (rebuilding a compose file from services), enrolling every agent of a global service at once, editing jobs, the service form's restart policy, healthcheck and log driver (kept as they are), unlocking an autolocked swarm, and rotating the swarm CA.

### Phase 8: Podman extras

Shown only on endpoints with the `Pods` capability. These use Podman's libpod API (`/v4.0.0/libpod/*`) through the same transport.

- Pods: list, inspect, start/stop/restart/remove, and a view of containers grouped by pod.
- Notes on rootless Podman in the UI: port binding below 1024, cgroup limits for resource stats.
- Later: Quadlet/systemd unit awareness, `podman kube play` for Kubernetes YAML.

**Status: done.** Notes:
- **Client:** `DockerClient.Pods.cs` and `Models/PodModels.cs`, checked against fixtures captured from nested Podman 6.2. Libpod paths start with `/v4.0.0/libpod` (Podman's `MinAPIVersion` since Podman 4) and skip the compatible API's version prefix. Agents relay them unchanged, so pods work on remote Podman too. Start and stop of a pod already in that state answer 304, which counts as success. A partial failure answers 409 with a list of per-container errors and no message; `ErrorResponse` reads that list. Remove is always forced: an unforced remove of a running pod fails, but only after it has removed the pod's stopped containers, the infra container included. The client can also create a pod and a container in one, for the integration tests.
- **Pages** are under `/endpoints/{id}/pods`: the list (each pod with its containers and their states, the grouped view; search, a state filter, bulk start, stop, restart and remove), and per pod an overview (containers with the infra one last, shared namespaces, network, published ports, exit policy, the command that created it, labels) and `/inspect`. The sidebar entry needs `Pods`. On other engines the pages explain that pods are a Podman feature (`_PodsCallout`). Pod events arrive through the compatible `/events` as `Type: pod`, so the pages stay live like the others.
- **Containers:** Docker's container list doesn't say which pod a container is in, even on Podman, so `PodMembership` reads it from the pod list. The container list badges the pod (linked) and its infra container, and filters by pod. A container's pages show its pod. *Recreate* isn't offered for containers in a pod, and a post is refused, because Docker's create API can't put the new container in the pod. An image update notice says to recreate with `podman run --pod` instead.
- **Self-protection:** a pod holding the container Sockless Port runs in can be started, but not stopped, restarted or removed.
- **Rootless notes:** the dashboard badges rootless engines. The container form notes that they can't publish ports below 1024, and that limits need the cgroup controllers delegated to the engine's user. `EngineDescriptor` now carries the cgroup version: rootless on cgroups v1 has no cgroups at all, which the limits note and the stats page say up front. `/info`'s limit flags (`MemoryLimit`, `CpuCfsQuota`, …) aren't used: nested Podman reports `CpuCfsQuota: false` yet accepts `--cpus`. Failures are explained where they happen instead. `EngineHints` (rootless ports, rootlessport, a container without a cgroup, other cgroup failures, short image names, SELinux relabelling, ports in use) is appended to every engine error by `EngineErrors.ForCall`, so stats, starts and creates explain them. `ComposeHints` now builds on it.
- **Dashboard:** the containers card counts pods on engines that have them.
- **Not built:** creating pods from the UI, pause and unpause of pods, pod stats, Quadlet/systemd units and `podman kube play` (left for later, as planned), and recreating a pod's container through libpod.

### Phase 9: Hardening and administration

- Users and roles (admin, operator, viewer), with access set per endpoint.
- Audit log of every mutating action (who, what, target, endpoint).
- OIDC login.
- Settings: time display (local/UTC, per the guideline), session timeout, backup and restore of `/data`.
- Security review: CSRF on every htmx POST, CSP, the risks of mounting the socket (documented), rate-limited login.
- Built-in TLS for the UI (moved from Phase 0/6): an opt-in HTTPS listener with a certificate you supply, or one issued by the internal CA, with the CA downloadable from Settings.

**Status: done.** Notes:
- **Roles** are per endpoint, not global: none, viewer, operator and swarm operator, each including the one before. A user has a role on all endpoints (which also covers endpoints added later) and can have another on single endpoints (`EndpointGrants`). Administrators (`IsAdmin`) have every role everywhere and alone manage users, endpoints, registries, settings and the audit log; the setup user and every user from before (migration `AddAccessAuditSettings`) are administrators. Swarm writes (services, nodes, secrets, configs, overlay networks, swarm stacks, join tokens) need the swarm operator role, as the risks below asked.
- **Enforcement:** access is read from the database on every request and every hub call, so role changes apply at once; disabling a user or setting their password changes their security stamp, which ends their sessions at the next request. `EndpointPageModel` checks a read permission (operator for create forms, consoles, volume files and stack files) and a write permission for every post; endpoints the user can't see are 404, refused posts 403. Views hide actions with `sp-can`. There's always one enabled administrator, and nobody can demote, disable or remove themselves.
- **Audit log** (`/audit`): a global page filter records every post (who, action, target, endpoint, outcome and why, client address), including posts refused for the role; the hub records consoles, pulls and cancellations; sign-ins, failed sign-ins (never the password), setup and refused administration posts are recorded too. Filters by user, endpoint, outcome and text; kept for a configurable number of days (365 by default), and also written to the server log.
- **OIDC:** authorization code with PKCE, `response_mode=query` so the correlation cookies can be `SameSite=Lax` and it works over plain HTTP. A login is matched to a linked user, else to one with the same verified email (then linked), else created when `Port:Oidc:CreateUsers` allows it, with `DefaultRole` (none unless set). The callback continues with a page that moves on by itself, because a redirect chain that started at the provider wouldn't carry the `SameSite=Strict` session cookie. The CSP's `form-action` allows the provider. Passwords can be hidden from the login page (`DisablePasswordLogin`), but still work at `?local=true` for recovery.
- **Settings** (`/settings`): default time display, session timeout (idle, sliding; from the next sign-in) and audit retention, stored in a `Settings` table. Each user picks local or UTC below the menu (their account page has it too). Local times are rendered by the server in the browser's zone, which `site.js` puts in a cookie with the current offset as a fallback for hosts without the zone's rules.
- **Backup and restore:** a `POST` streams a `.tar.gz` of the database (`VACUUM INTO`, consistent while running), Data Protection keys, internal CA and stack files, with a manifest. A restore is checked on upload (manifest, only regular files under those folders, no path tricks, at most 4 GB unpacked, no migrations this server doesn't know), staged in `/data/restore-pending`, and applied at the next start before the database opens; what it replaced goes to `before-restore-{time}`. "Restart and restore" stops the server; the restart policy starts it again.
- **Security review:** antiforgery on every form and htmx request (tests post without a token), the strict CSP (unchanged, plus the provider in `form-action`), sign-in and setup rate-limited per address (`Port:SignInAttemptsPerMinute`, 10), the account lockout, and [docs/security.md](security.md) on the socket's risks, roles, cookies, CSP, HTTPS and backups.
- **Built-in TLS:** `Port:Https:Port` adds an HTTPS listener next to the HTTP ones (which are re-added from `urls`/`http_ports`, since listening in code replaces them). The certificate is asked for on every handshake: a configured PEM or PKCS#12 file, reloaded when it changes, or one the internal CA issues for `Port:Https:HostNames` (397 days, renewed 30 days before; `CN=sockless-port-ui`, server authentication only, so agents don't accept it as the server). `RedirectHttp` adds the redirect and HSTS, except for `/healthz`. The CA certificate downloads from Settings.
- **Not built:** groups or roles from the OIDC provider's claims, OIDC sign-out at the provider, encrypted backups, scheduled backups, per-user API tokens, and closing consoles and streams that are already open when a user loses access (new calls are refused at once).

### Phase 10: Vulnerability scanning

- Scan images for known vulnerabilities (CVEs and other advisories) in their operating system packages and in language packages (Go binaries, Java, Node.js, Python, Ruby, Rust, .NET…).
- Show the result per image (a Vulnerabilities tab with every finding), and as a badge on the image and container lists and the container's overview.
- Scan on request (one image, one container's image, every image in use) and on a schedule.
- Work the same on local, agent and Podman endpoints, and offline with a mirrored or copied database.

**Status: done.** Notes:
- **Scanner:** Trivy, bundled in the image as a pinned release checked against its SHA-256 (`Port:Scanner:Command` picks another), run like Compose: an argument list, a clean environment (a private home plus the proxy variables), the process tree killed on cancel. Only the `vuln` scanner runs; layer analysis uses Trivy's in-memory cache, so nothing but the databases stays on the data volume.
- **Where it runs:** the image is exported from its engine (`GET /images/{id}/get`, through the agent like any other call, with the image's platform from API 1.48 so Docker's containerd store exports what it has) into a private folder under `TempPath`, scanned on the server, and deleted. Nothing runs on the engine and no helper container needs the socket. The cost is copying each new image ID to the server once.
- **Databases** (`{DataPath}/scanner`, not backed up) are downloaded in their own step under a lock, only when Trivy's `NextUpdate` has passed, and scans then run with `--skip-db-update --skip-java-db-update`, so scans never race over a download. A failed download is a warning while an older database is there. `Port:Scanner:DatabaseRepositories` / `JavaDatabaseRepositories` name mirrors; `SkipDatabaseUpdate` never downloads (scans add `--offline-scan`).
- **Queue:** `ImageScans` scans one image at a time from a bounded queue (500). Queued, running (with the phase and bytes copied) and failed scans are in memory; open pages refresh on each phase through the engine hub, like engine events. An image already queued isn't queued twice. Each scan has a time limit (`Port:Scanner:TimeoutMinutes`, 30).
- **Results** are stored in SQLite (`ImageScans`) per image ID, which names the same content on every engine, so an image is scanned once wherever it runs: counts per severity and of fixable findings for lists, the OS and whether it's end of life, the scanner and database versions, and the findings as JSON (at most 10,000, worst first, titles shortened, only http(s) links). Results of images no online endpoint has are deleted once they're 30 days old.
- **Pages:** the image's Vulnerabilities tab (`/endpoints/{id}/images/{image}/vulnerabilities`) shows the summary and every finding, filtered by severity, by "has a fix" and by text, at most 1000 rows at a time. The image list has a Vulnerabilities column and a "Scan images in use" button, the container list a badge under the image, and the container overview the result with a "Scan" button. Badges name the worst severity with its count ("3 critical": rust for critical and high, brass for medium, neutral below; "No known vulnerabilities" in verdigris). Viewers see results; scans need the operator role and are audited like other posts.
- **Schedule** (Settings, off by default, daily or weekly): every 15 minutes, the images containers use on every online endpoint are queued when their result is older than the interval; a failed scan waits an interval too. Settings also shows the scanner's version and how current its databases are, or why it can't run.
- **Not built:** scanning what was installed in a container after it started (its writable layer, via `/containers/{id}/export`), badges for stacks and swarm services (a service's image may not be on the manager), ignore lists or VEX, SBOM export, scanning images in a registry without pulling them, and notifications for new critical findings.
- **Later: a scanner of our own** (on hold; Trivy first). Not from NVD: NVD names upstream products and versions, but distros backport fixes, so matching distro packages against it gives many false results; it's only useful for CVSS and descriptions. The plan would be:
  - **Data:** OSV.dev's bulk downloads (one JSON format for npm, PyPI, Go, Maven, NuGet, crates.io, RubyGems, Packagist, and Debian, Ubuntu, Alpine and several RPM distros; check each distro's coverage first), distro feeds where OSV has gaps, and CISA KEV and EPSS to rank findings. Synced daily into SQLite, with a mirror or file import for offline servers.
  - **Inventory:** our own reader for image layers (handling deleted files): `/etc/os-release`, dpkg (`status` and `status.d/`), apk, rpm (three storage formats), and language manifests (lock files, `*.dist-info`, `*.deps.json`, Go build info, `pom.properties` in nested jars).
  - **Matching:** version comparison per ecosystem (dpkg, apk, rpm, semver, Python, Maven), Debian and Ubuntu source package names, and statuses such as not affected, won't fix and end of life.
  - **Why:** no third-party binary; pure .NET, so the agent could scan on its own host; and OS package databases are small files that the archive API can read from a running container, which would catch packages installed after it started.
  - **Phases:** OSV sync and Debian, Ubuntu and Alpine packages (with per-container scans) first; then npm, Python, Go and .NET; then RPM distros, Java, KEV and EPSS. Keep Trivy until ours gives the same results on real images.

## Risks and notes

- **Socket access is root on the host.** This is documented clearly. The server and agent run as non-root users in the docker group; a read-only socket mode (via a proxy) is an option for viewer-only setups.
- **Compose binary versions** are pinned in both images and updated deliberately.
- **Docker API drift:** the client targets a minimum API version (for example 1.44) and negotiates down. CI tests against current and previous engine versions.
- **Windows hosts:** named pipe support is in the client, but the images target Linux first.
- **Podman differences:** the compatible API is close to Docker's but not identical. Examples are event fields, stats on rootless cgroups v1, and some network options. Each known difference is handled in the client behind a capability, and the Podman CI jobs catch regressions.
- **Swarm manager access:** a manager's socket controls the whole cluster, so Swarm write actions get their own role permission in Phase 9.
- **The scanner is third-party code that reads untrusted images.** It's pinned by version and checksum and bumped deliberately (Trivy's v0.69.4 release was compromised in March 2026, GHSA-69fq-xp46-6x23). It runs as the server's non-root user with a clean environment, and only ever sees an exported tarball, never the socket.

## Decisions

- "Images" is the scope originally written as "isos".
- Swarm is in scope (Phase 7).
- Podman is in scope: the core features through the compatible API, plus pods in Phase 8.
- Product name: **Sockless Port**.
- Vulnerability scanning uses **Trivy** (Phase 10), on the server, from exported images.
