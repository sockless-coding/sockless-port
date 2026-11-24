# Security

What Sockless Port protects, what it can't, and how to run it safely.

## The socket is root on the host

Whoever can use the Docker or Podman socket can start a privileged container that mounts the host's root file system, so access to the socket is root on that host. A swarm manager's socket is root on every node of the swarm. Sockless Port holds that access for everyone who signs in, so:

- Keep it off the public internet. Put it behind a VPN or a reverse proxy with its own authentication, or at least TLS.
- Give people the least role they need (below). An operator can do anything the socket can, including starting a privileged container, so treat the operator role like shell access to the host.
- Rootless Docker or Podman limits the damage to the engine user's account. The container form and stacks warn about what rootless can't do (ports below 1024, cgroup limits).
- For people who only need to look, a read-only socket proxy (for example one that allows only `GET`) in front of a separate endpoint is a second line of defence. Viewer is enforced by Sockless Port; a proxy is enforced outside it.
- The agent relays its local socket to the server over mutual TLS and accepts only the server's certificate. Its port should still be reachable only from the server.

## Users and roles

Every page needs a signed-in user. **Administrators** manage users, endpoints, registries and settings, read the audit log, and have every role on every endpoint. Everyone else has a role per endpoint, set on the user's page:

| Role | May |
|---|---|
| No access | The endpoint doesn't exist for them (404). |
| Viewer | Read everything: lists, details, logs, stats, inspect JSON. |
| Operator | Also start, stop, create and remove containers, images, volumes, networks, pods and Compose stacks, open consoles, and browse and edit volume files. |
| Swarm operator | Also change services, nodes, secrets, configs, overlay networks and swarm stacks through a manager. |

A user's *role on all endpoints* also applies to endpoints added later; single endpoints can differ. Roles are read from the database for every request and every hub call, so a change applies at once. Disabling a user or setting their password ends their sessions at their next request.

Viewers can read containers' environment variables and inspect JSON, which may hold secrets passed as variables. Stack files and `.env`, volume files and consoles are for operators only. Secret values in a swarm are never shown to anyone.

There's always at least one enabled administrator, and administrators can't remove their own administrator role, disable or remove themselves.

## Audit log

Every post that changes something is recorded: who, what, the target, the endpoint, the outcome (succeeded, failed with the engine's reason, or denied), and the client's address. Consoles opened, pulls started and jobs cancelled through the live connection are recorded too, and so are sign-ins (successful and failed, never the password), setup, and refused attempts at administration pages. Entries go to the database (Settings sets how long they're kept, 365 days by default) and to the server log, for shipping elsewhere.

## Sign-in

- Passwords are at least 12 characters, hashed by ASP.NET Core Identity. Five wrong passwords lock an account for five minutes.
- Sign-in and setup are rate-limited per client address (`Port__SignInAttemptsPerMinute`, 10 by default). Behind a reverse proxy, set `ASPNETCORE_FORWARDEDHEADERS_ENABLED=true` so the address is the client's.
- First-run setup needs a one-time token printed to the server log, so whoever reaches a fresh instance first can't claim it.
- Sessions end after the timeout in Settings without a request (12 hours by default). The cookie is `HttpOnly` and `SameSite=Strict`, and `Secure` over HTTPS.
- Single sign-on (OpenID Connect) uses the authorization code flow with PKCE. Users are matched by a login they linked before, or by an email address the provider says is verified. New users are created only with `Port__Oidc__CreateUsers=true`, and then have no access until an administrator grants some (or `Port__Oidc__DefaultRole`).

## Browser protections

- **CSRF:** every form and every htmx request carries an antiforgery token (htmx sends it as a header from `hx-headers` on `<body>`); posts without one are refused with 400. State only changes through `POST`. The live connection (SignalR) authenticates with the `SameSite=Strict` cookie, which browsers don't send from other sites.
- **CSP:** `default-src 'self'`, no inline scripts, no `eval` (htmx runs without it), and inline styles only through a per-response nonce for the terminal's generated styles. `frame-ancestors 'none'`. `form-action` allows the single sign-on provider when it's on.
- Everything is self-hosted: no CDN, so nothing third-party runs in the page.

## HTTPS

Prefer a reverse proxy that terminates TLS. Without one, turn on the built-in listener (`Port__Https__Port`, e.g. 8443, and publish that port). It serves your certificate (`Port__Https__Certificate`, PEM with `Port__Https__CertificateKey`, or PKCS#12), reloaded when the file changes, or one the internal CA issues for `Port__Https__HostNames` and renews 30 days before it expires. `Port__Https__RedirectHttp=true` redirects plain HTTP and adds HSTS; the health check stays on HTTP.

The internal CA's certificate is downloadable from Settings. Installing it in a browser trusts it for any name, and anyone with the data volume holds its key, so install it only on machines you manage. Its certificates for agents and for the UI can't stand in for the server towards agents: agents accept only the server's client certificate.

## Vulnerability scanning

Scans run Trivy on the server, as its non-root user, against an image tarball exported through the Engine API: the scanner never gets the socket, and nothing runs on the engine. Trivy is pinned by version and SHA-256 in the image, because it reads untrusted images and its own releases were once compromised (v0.69.4, March 2026); bump it deliberately. It gets a clean environment (none of Sockless Port's settings or secrets), and the exported image is deleted after each scan. Findings come from a downloaded database, so they're shown as data: text is escaped and only http(s) links are kept. Starting a scan needs the operator role, because each one copies a whole image to the server; scans are audited. Accepting a finding's risk hides it from counts and badges, so it needs a reason, may expire, and is audited; operators can accept for one image, and only administrators for every image, since that hides the finding on every endpoint.

## App templates

Template feeds are other people's work, so everything in them is treated as untrusted. A template never deploys by itself: it becomes a stack whose files the user sees, and which deploys only through the stack's review. Settings that give a service power over the host (privileged mode, the engine's socket and system folders, host namespaces, powerful capabilities, devices) are listed before saving. In the files it writes, every literal value has its `$` doubled and `.env` values are single-quoted, so nothing from a feed is interpolated by Compose. Notes are reduced to text, bold, code and http(s) links. Only administrators add feeds. Feeds may be on the local network, but what they point at (stack files and logos) is only fetched from public addresses, checked on every connection including redirects; downloads have size limits and timeouts. Logos are served from the server's own origin with a sandboxing policy, since an SVG opened on its own would otherwise be a page of this origin. Templates and stacks made from them are audited (`Templates: create stack`, `Template sources: add`, `refresh`, `remove`).

## Stacks from git repositories

Connecting a stack to a repository, changing its settings and its webhook need the operator role on the endpoint (swarm operator for swarm stacks), like editing its files. The server runs `git` as its non-root user, the way it runs Compose: an argument list, never a shell; a clean environment with a private home; no system or user git configuration; no prompts; and only the https, http and ssh transports (`GIT_ALLOW_PROTOCOL`), so neither a typed address nor a repository can use `file://` or `ext::`. Addresses with credentials in them are refused. Only the branch is fetched, without tags or submodules, and symbolic links are checked out as plain files, so a repository can't point the compose file or `.env` at the server's own files. Files read from it are limited to 1 MiB of UTF-8.

Tokens and private keys are encrypted with Data Protection, write-only in the UI, and never in the audit log. A token reaches git through its environment, as an `Authorization` header scoped to the repository's host, never on a command line. A key is written to a private folder for each run and deleted after. Keys Sockless Port generates (ECDSA P-256) stay encrypted in a hidden form field until the stack is saved; add them as read-only deploy keys. Passphrase-protected keys aren't supported. SSH host keys are trusted the first time (`accept-new`) and required after; the Repository tab shows their fingerprints, and forgetting them is audited.

The webhook (`POST /hooks/stacks/{id}`) is the only anonymous way to change anything. Its address has a random 128-bit part per stack, and the call must carry the stack's 256-bit secret: an HMAC-SHA256 signature of the body (GitHub, Gitea, Forgejo) or the secret itself (GitLab's `X-Gitlab-Token`), compared in constant time. Calls with a wrong secret are audited as denied; calls are rate-limited per address (30 a minute) and bodies limited to 5 MiB. A valid call can only queue a fetch and redeploy of that stack's configured branch: it can't choose a commit, a branch or files. Automatic redeploys only apply to a stack that's deployed, and are audited with their commit and outcome. Whoever can push to the branch can change what runs on the engine, which, with the socket, is root on its host: protect the branch accordingly, or leave polling and the webhook off and review each fetch before deploying.

Git addresses may be on the local network: operators can already run containers on the engine.

## Notifications

Only administrators manage channels and rules. Channel secrets (webhook header values, ntfy and Gotify tokens, SMTP passwords) are encrypted with Data Protection like registry passwords, are write-only in the UI, and never reach the audit log. Channel addresses may be on the local network, since administrators enter them. Notifications say what happened and where (container and image names, endpoint names, exit codes, vulnerability IDs, the start of an engine error), so send them only where those may be read. Delivery attempts, failures included, are kept in the notification history as long as the audit log.

## The data volume and backups

`/data` holds the database (users, endpoints, audit log, encrypted git tokens and keys), the Data Protection keys that encrypt registry passwords, git credentials and the CA's keys, the internal CA, and stack files. Git checkouts (`/data/git`) aren't backed up: they're fetched again. Anyone who can read it can decrypt the registry passwords and connect to every agent. A backup from Settings holds the same, so keep backups as safe as the server. Restoring one replaces all of it at the next start and keeps what it replaced in a `before-restore-*` folder; backups from a newer version are refused.
