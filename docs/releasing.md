# Releasing

## Versions

Versions are `yyyy.m.rev`: the year, the month, and a revision that starts at 0 each month.

- The first release in November 2026 is `2026.11.0`, the next one that month is `2026.11.1`, and the first in December is `2026.12.0`.
- No `v` prefix, and no leading zeros (`2027.1.0`, not `2027.01.0`). That keeps every version valid semver and a valid .NET version, so versions compare as numbers.
- Builds that aren't releases report `0.0.0-dev`. Their agent install commands use the `latest` agent image.

## Making a release

1. Make sure `main` is green in CI.
2. On GitHub, go to **Releases → Draft a new release**. Create a new tag named after the version (for example `2026.11.0`) on `main`, write the notes, and click **Publish release**.
3. The **Release** workflow (`.github/workflows/release.yml`) checks the tag. Then it runs the full CI pipeline from that tag: build, unit tests, and integration tests against Docker, Swarm and rootful and rootless Podman. Then it builds and pushes both images for `linux/amd64` and `linux/arm64`.

The workflow stops before building anything if the tag doesn't match the format. If that happens, delete the release and its tag, then publish again with the right tag.

## Images

| Image | Tags |
|---|---|
| `ghcr.io/sockless-coding/sockless-port` | `2026.11.0`, `2026.11`, `latest` |
| `ghcr.io/sockless-coding/sockless-port-agent` | `2026.11.0`, `2026.11`, `latest` |

`2026.11` always points to the newest revision of that month. A release marked **pre-release** on GitHub pushes only its exact version tag, so `2026.11` and `latest` stay where they are.

The version is built into both binaries (`-p:Version=`). The server uses it to name the agent image in install commands, and to flag agents that are older than the server.

## The first release

GHCR creates both packages as private the first time they're pushed. After the first release, open each package's settings under the organisation's **Packages** and change its visibility to public. Also check that the packages are linked to this repository.
