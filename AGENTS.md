# Agent instructions

Sockless Port is a self-hosted container management UI for Docker, Docker Swarm and Podman. It's built with ASP.NET Core on .NET 11 / C# 15. The roadmap is in [docs/plan.md](docs/plan.md); check which phase you're working in before adding features.

## Layout

| Path | What |
|---|---|
| `src/Sockless.Port.Docker` | Thin Engine API client: transports (unix socket, named pipe, TCP/TLS, edge tunnels), version negotiation, models, streams, engine detection, the agent's wire protocol (`Agent/AgentProtocol.cs`) and the edge tunnel's multiplexer (`Tunnel/`). No ASP.NET dependencies; AOT-compatible (the agent shares it) |
| `src/Sockless.Port.Agent` | The remote agent: a Native AOT minimal API that relays the Engine API to its local socket over mutual TLS, plus enrolment, renewal, `/agent/info` and the `update` command that replaces its own container (`AgentUpdater`); in edge mode it dials the server (`TunnelClient`). No Core or Web dependencies |
| `src/Sockless.Port.Core` | Options, EF Core model (SQLite), Identity, first-run setup, endpoints (`Engines/`: connections, state, monitor, local detection, `EndpointRegistry`), agents (`Agents/`: internal CA, enrolment, edge tunnels, certificate renewal, updates, install commands), and per resource: `Containers/`, `Images/`, `Volumes/` (create form, usage, file browser), `Networks/` (create form, usage), `Registries/`, `Stacks/` (files, Compose, Docker CLI and git runners, validation, jobs, diff, adoption, git-backed stacks: checkouts, SSH keys, polling and webhooks), `Swarm/` (service form and spec builder, writes that pin digests, inventory, units), `Pods/` (actions, which pod a container is in), `Vulnerabilities/` (Trivy runner, scan queue, stored results, schedule), `Monitoring/` (live resource use of every running container, and its recorded history for trends), `Overview/` (each endpoint's summary for the overview across endpoints), `Moves/` (moving a stack or a container to another endpoint: checks, volume and image copies, the moves themselves), `Maintenance/` (scheduled prunes, the history of scheduled runs, and the schedule that runs backups and prunes), `Templates/` (feeds and the built-in list, compose files made from templates, risks, logos), `Notifications/` (channels, rules, alerts from events and background work, delivery, history), `Work/` (the background tasks list shown in the top bar), and administration: `Access/` (roles per endpoint, users, single sign-on matching), `Audit/`, `Settings/`, `Backup/` (backups, their encryption and the scheduled ones), `Https/` (the UI's certificate) |
| `src/Sockless.Port.Web` | Razor Pages UI, htmx, SignalR hub, Tailwind styles, static assets |
| `tests/*` | xUnit v3 on Microsoft.Testing.Platform (`dotnet test`) |
| `build/Tailwind.targets` | Downloads the pinned Tailwind 3 standalone CLI to `.tools/` and builds `wwwroot/css/app.css` |
| `build/codemirror/` | Source of the checked-in CodeMirror bundle (`wwwroot/lib/codemirror/`); rebuilt by hand with npm when bumping it |
| `docker/` | Server `Dockerfile`, agent `Dockerfile.agent` and `compose.yaml` |

## Commands

```sh
dotnet build                      # also builds the CSS
dotnet test
dotnet run --project src/Sockless.Port.Web   # http://localhost:5080, data in src/Sockless.Port.Web/data
dotnet ef migrations add <Name> --project src/Sockless.Port.Core --output-dir Data/Migrations
docker build -f docker/Dockerfile -t sockless-port .
docker build -f docker/Dockerfile.agent -t sockless-port-agent .

# Integration tests against a real engine (skipped unless the variables are set):
SOCKLESS_PORT_TEST_ENGINE=unix:///var/run/docker.sock SOCKLESS_PORT_TEST_EXPECT=docker \
  dotnet test -- --filter-trait "Category=Integration"
# Add SOCKLESS_PORT_TEST_AGENT_IMAGE=<an agent image on that engine> for the agent update test.
```

Releases: publishing a GitHub release whose tag is the version (`yyyy.m.rev`, e.g. `2026.11.0`) builds, tests and pushes both images; see [docs/releasing.md](docs/releasing.md).

`SOCKLESS_PORT_TEST_EXPECT` is `docker`, `swarm` or `podman`. CI runs all three (Podman both rootful and rootless). Locally, you can use throwaway engines, never your own: run `docker:dind` (add `docker swarm init` for swarm) or `quay.io/podman/stable` with `podman system service`, privileged, with the API published on 127.0.0.1. Web tests use `Port:LocalEngine` pointing at a socket that doesn't exist, so they never touch the developer's engine.

## Engines

- Every engine call goes through `DockerClient`. Pages get it from `EndpointPageModel.Connection.Client`, which is null unless the endpoint is online. Catch engine failures and show `EngineErrors.Describe(...)`.
- Decide what to show from `EngineDescriptor.Capabilities` (`Swarm`, `SwarmManager`, `Pods`, `Rootless`), never from `EngineKind`.
- Pods use libpod's own API (`DockerClient.Pods.cs`, paths under `DockerClient.LibpodPrefix`, never through `Path()`). Docker's container list doesn't say which pod a container is in: use `PodMembership`. Containers in a pod can't be recreated through Docker's API (the new one would leave the pod).
- Engine messages that name a known problem (rootless ports, missing cgroups, short image names) get a hint from `EngineHints`, appended by `EngineErrors.ForCall`; add new ones there, not in pages.
- Engine differences (Podman vs Docker) are handled inside `Sockless.Port.Docker` (e.g. `Streams/EventNormalizer.cs`), never in pages.
- Edge agents are a `TunnelAddress`: every connection is a stream through the agent's tunnel (`AgentTunnels`), with the same mutual TLS as a direct agent. Code that needs a raw connection uses `DockerTransport.ConnectRawAsync`, never a socket of its own; the CLIs reach tunnels through `EngineBridge`. Don't trust anything for arriving through a tunnel.
- New API models go in `Docker/Models` and must be registered in `DockerJsonContext`. Check them against real responses: fixtures in `tests/Sockless.Port.Docker.Tests/Fixtures`, captured only from throwaway engines.
- Container operations that need care (recreate, create bodies) live in `Core/Containers` and work on raw inspect JSON (`JsonObject`), so settings the UI doesn't model survive. Engine quirks found by the integration tests are handled there too (e.g. no `Hostname` with host networking, no `ShmSize` with host IPC).
- Helper containers Sockless Port starts for itself (the volume file browser) carry the `sockless-port.helper` label, are started only from a POST, mount what they need read-only with no network, and end by themselves (`sleep`). Two write: the volume editor's save helper and a move's target helper (`VolumeBrowser.StartCopyHelperAsync`), each removed as soon as its write ends. The agent update helper (`AgentUpdates`, `AgentUpdate` in `Docker/Agent`) is the exception to all of this: it runs the new agent image with the agent's socket, may start from the automatic schedule, and replaces the agent's container on its host; it's removed once the server has read its outcome. They never count as a resource's users (`ContainerLabels.IsHelper`), and are released before removing or pruning what they mount.
- Form posts that call the engine go through `EndpointPageModel.TryEngineAsync`: failures become a flash message, success a confirmation, then post/redirect/get.
- Swarm pages derive from `SwarmPageModel` and read through `Manager` (the client only on a swarm manager). Swarm updates name the object's version, and service edits apply the form onto the raw spec (`ServiceSpecBuilder`), so unmodelled settings survive. Write services through `SwarmServices`, which pins images to digests and passes registry credentials.
- Stacks run the Compose CLI through `ComposeCli`, never a shell: arguments as a list, a clean environment (only `DOCKER_HOST`, a private `DOCKER_CONFIG` with registry credentials, and what the OS needs), and a project folder or the project name. Swarm stacks run `docker stack` through `DockerCli` the same way, with the stack's `.env` as the run's variables. Long runs are `StackJobs` (one per stack), streamed to the page like image jobs. Never stop, redeploy or take down the project Sockless Port runs in (`ComposeProject.IsSelf`).
- Git-backed stacks run `git` through `GitCli` the same way (no shell, a clean environment, only https/http/ssh; secrets through the environment or private files, never arguments). Fetches are stack jobs (`Fetch`, `Sync`), so they queue behind the stack's other work. Compose runs in the checkout (`StackSource`); the stack folder keeps a copy of the files for the review and diff. Webhooks and polling only queue syncs through `GitStackTriggers`.
- Live updates: the monitor forwards engine events to `EngineHub`, and `site.js` turns them into the htmx body events `engine-changed` (current endpoint's resources changed), `endpoint-state` (current endpoint's connection changed) and `endpoints-changed` (any endpoint's state). Make a fragment live with `hx-get` + `hx-trigger="engine-changed from:body"`. Pages that show several endpoints (the overview) mark them with `data-overview-row="{id}"`: they're watched too, and each fires `endpoint-changed-{id}` on its engine events and state changes. Lists across endpoints (`/containers`, `/stacks`) mark each endpoint they show with `<span hidden data-watch-endpoint="{id}">` for the same events; they read endpoints in parallel through `EndpointReads` (a time limit each, problems named above the list), and their posts carry `endpointId:id`, checked and audited per endpoint. htmx trigger filters (`[...]`) need eval, which the CSP forbids.

## Background tasks

- Work that takes more than a moment (pulls, scans, checks, removals of many things, prunes, backups) reports to `Work.WorkTracker`: `Start(...)` a `WorkItem`, `Report(done, total)` progress when it can tell, and end it with `Succeed`/`Fail`/`Canceled`. Hold it in a `using`, so an exception doesn't leave it running. Services take `WorkTracker? work = null` just before `Alerts? alerts = null`.
- Titles say what's happening ("Pulling nginx:1.27"); a `Subject` lets `WorkDisplay.Link` point at the page it's about. Work without an endpoint is for administrators only.
- The top bar's list refreshes on the `work-changed` body event; don't push progress to browsers yourself.

## Notifications

- Raise alerts through `Notifications.Alerts`, never by sending yourself. An alert's key decides when it fires again: it fires once until `ClearAsync` with the same key. Use a fresh key (or one with a hash of its content) for things that should fire each time.
- Services that produce alerts take `Alerts? alerts = null` as their last constructor parameter, so tests can build them without it.

## Access and audit

See [docs/security.md](docs/security.md). Roles are per endpoint (`EndpointRole`: viewer, operator, swarm operator); administrators have all of them everywhere and alone see `/users`, `/audit`, `/settings`, `/registries` and endpoint management (folder conventions in `Program.cs`).

- `UseUserAccess` reads the user's access from the database for every request (`HttpContext.Access()`) and signs out removed, disabled or re-passworded users. The hub reads it again for every call (`EngineHub.RequireAsync`).
- `EndpointPageModel` checks the role: `ReadPermission` to open the page (operator for forms that only change things, consoles, volume files, stack files), `WritePermission` for every non-GET, checked after `LoadAsync` (swarm pages and swarm stacks need `ManageSwarm`). An endpoint the user can't see is 404. New endpoint pages get this for free; override the two properties when a page needs more.
- Hide actions the user can't use with `sp-can="write"` (the page's write permission), `"operate"`, `"swarm"` or `"admin"` on any element. It hides; the pages still check every post.
- Every page post is audited by `AuditFilter`: the action from the page's folder and handler (or the form's `action`), the target from `EndpointPageModel.AuditTarget` (else route values and `ids`/`name`), the outcome from flash errors, model state and denials. Use `AuditTrail.Target`/`Action` to name things better, `[NoAudit]` for posts that change nothing (validation, preferences), and `AuditTrail.WriteAsync` for changes outside page posts. Never put secrets in an audit entry.
- Times go through `Format.Timestamp`, which follows the user's local/UTC preference (`TimeZones`); never format timestamps yourself.

## UI: follow the Sockless guidelines

The UI follows the Sockless theme, **Workshop** variant, surface **web app**. The guidelines live in the `sockless-guidelines` repository (`design/sockless/`). Read, in order:

1. `principles.md`
2. `surfaces/web-app.md`
3. `components.md` and the foundations when you need exact values
4. `accessibility.md` before adding any new text/background pair

Rules to follow without re-reading:

- Dark only. No light mode.
- Tokens, never raw hex, in component CSS. Tokens are in `wwwroot/css/workshop.css`, copied verbatim from `tokens/workshop.css` (theme v1.0.0); update it by copying again, never by editing it. If a token is missing, say so instead of inventing a colour.
- In Tailwind, use the role colours from `tailwind.config.js`. Opacity modifiers (`bg-accent/10`) work only on roles that have an `-rgb` triplet (accent, accent-2, success, warning, danger, info).
- Copper-light (`accent`) is for interactive and active elements. Brass (`accent-2`/`warning`) is secondary and warning, verdigris (`success`) is success, rust (`danger`) is danger.
- Status is never colour alone: badges always carry a word.
- Muted text is for placeholders and large text only.
- Every animation stops under `prefers-reduced-motion: reduce`.
- Icons: Heroicons outline only, via `<icon name="..." />` (files in `src/Sockless.Port.Web/Icons/`, taken from `heroicons@2.2.0/24/outline`).
- Destructive actions confirm first and name the target.
- Self-host everything. The CSP (`Infrastructure/SecurityHeaders.cs`) allows only `'self'`: no CDNs, inline scripts, inline styles or `hx-on`/eval.

## Browser code

- Libraries and page scripts load once in `_Layout.cshtml`. A page's behaviour is a component: `SocklessPort.component('[data-x]', el => { …; return cleanup; })` in its own file under `wwwroot/js/`. It mounts when the element arrives (initial load or htmx swap), and its cleanup runs when the element is swapped out. Use this, not `DOMContentLoaded`, because hx-boost never reloads the page.
- Streams (logs, stats) use `SocklessPort.connection.stream(...)`. Terminals use hub methods plus `terminalOutput`/`terminalExit` messages. Show hub errors with `SocklessPort.message(err)`.
- Destructive buttons carry `data-confirm="Remove web?"` (or `data-confirm-bulk="Remove {list}?"` in bulk forms). `site.js` shows the styled dialog.
- CSP: no inline `style` attributes anywhere. `<style>` elements created by libraries get the per-response nonce from `csp-nonce.js`. htmx is configured not to settle the `style` attribute.
- Tailwind scans `wwwroot/js` too. Component classes used only from JS are kept, but only if they appear literally in a scanned file.
- Content is `max-w-5xl`, as the guideline says, except on lists: a page whose main content is a table sets `ViewData["Wide"] = true` and gets `max-w-screen-2xl`. Forms and detail pages stay narrow.
- Tables go in `.table-wrap`. One wider than its column gets a stand-in scrollbar from `table-scroll.js` that sticks to the bottom of the content area while the table is on screen; don't add scrolling of your own.
- Use the `hidden` attribute to hide things. A global `[hidden] { display: none !important }` keeps component display rules from defeating it.
- Charts: series colours are `accent` then `info` (copper-light, gaslight). That pair passes colour-vision checks; `success` is reserved for status.

## Code conventions

- C# 15 features where they help: prefer `union`/`closed` hierarchies over marker interfaces, and `with(...)` collection arguments.
- Warnings are errors. Nullable is on.
- Package versions live only in `Directory.Packages.props`.
- Every page requires sign-in by default (fallback policy). Opt out explicitly with `AllowAnonymousToPage`.
- POST, never GET, for anything that changes state. Antiforgery is on for every form and htmx request.
- New navigation entries go in `Infrastructure/Navigation.cs`, and only once the page exists.
