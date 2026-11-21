# Agent instructions

Sockless Port is a self-hosted container management UI for Docker, Docker Swarm and Podman. It's built with ASP.NET Core on .NET 11 / C# 15. The roadmap is in [docs/plan.md](docs/plan.md); check which phase you're working in before adding features.

## Layout

| Path | What |
|---|---|
| `src/Sockless.Port.Docker` | Thin Engine API client: transports (unix socket, named pipe, TCP/TLS), version negotiation, models, streams, engine detection. No ASP.NET dependencies; AOT-compatible (the agent will share it) |
| `src/Sockless.Port.Core` | Options, EF Core model (SQLite), Identity, first-run setup, endpoints (`Engines/`: connections, state, monitor, local detection) |
| `src/Sockless.Port.Web` | Razor Pages UI, htmx, SignalR hub, Tailwind styles, static assets |
| `tests/*` | xUnit v3 on Microsoft.Testing.Platform (`dotnet test`) |
| `build/Tailwind.targets` | Downloads the pinned Tailwind 3 standalone CLI to `.tools/` and builds `wwwroot/css/app.css` |
| `docker/` | Server `Dockerfile` and `compose.yaml` |

## Commands

```sh
dotnet build                      # also builds the CSS
dotnet test
dotnet run --project src/Sockless.Port.Web   # http://localhost:5080, data in src/Sockless.Port.Web/data
dotnet ef migrations add <Name> --project src/Sockless.Port.Core --output-dir Data/Migrations
docker build -f docker/Dockerfile -t sockless-port .

# Integration tests against a real engine (skipped unless the variables are set):
SOCKLESS_PORT_TEST_ENGINE=unix:///var/run/docker.sock SOCKLESS_PORT_TEST_EXPECT=docker \
  dotnet test -- --filter-trait "Category=Integration"
```

`SOCKLESS_PORT_TEST_EXPECT` is `docker`, `swarm` or `podman`. CI runs all three (Podman both rootful and rootless). Locally, you can use throwaway engines, never your own: run `docker:dind` (add `docker swarm init` for swarm) or `quay.io/podman/stable` with `podman system service`, privileged, with the API published on 127.0.0.1. Web tests use `Port:LocalEngine` pointing at a socket that doesn't exist, so they never touch the developer's engine.

## Engines

- Every engine call goes through `DockerClient`. Pages get it from `EndpointPageModel.Connection.Client`, which is null unless the endpoint is online. Catch engine failures and show `EngineErrors.Describe(...)`.
- Decide what to show from `EngineDescriptor.Capabilities` (`Swarm`, `SwarmManager`, `Pods`), never from `EngineKind`.
- Engine differences (Podman vs Docker) are handled inside `Sockless.Port.Docker` (e.g. `Streams/EventNormalizer.cs`), never in pages.
- New API models go in `Docker/Models` and must be registered in `DockerJsonContext`. Check them against real responses: fixtures in `tests/Sockless.Port.Docker.Tests/Fixtures`, captured only from throwaway engines.
- Live updates: the monitor forwards engine events to `EngineHub`, and `site.js` turns them into the htmx body events `engine-changed` (current endpoint's resources changed), `endpoint-state` (current endpoint's connection changed) and `endpoints-changed` (any endpoint's state). Make a fragment live with `hx-get` + `hx-trigger="engine-changed from:body"`. htmx trigger filters (`[...]`) need eval, which the CSP forbids.

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

## Code conventions

- C# 15 features where they help: prefer `union`/`closed` hierarchies over marker interfaces, and `with(...)` collection arguments.
- Warnings are errors. Nullable is on.
- Package versions live only in `Directory.Packages.props`.
- Every page requires sign-in by default (fallback policy). Opt out explicitly with `AllowAnonymousToPage`.
- POST, never GET, for anything that changes state. Antiforgery is on for every form and htmx request.
- New navigation entries go in `Infrastructure/Navigation.cs`, and only once the page exists.
