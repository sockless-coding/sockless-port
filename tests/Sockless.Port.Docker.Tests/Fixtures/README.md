# API fixtures

Real responses captured from throwaway engines, so the models and engine detection are tested against what engines actually send. Never capture from a personal or production engine: responses include container names, image names, labels and network details.

| File | Source |
|---|---|
| `docker-*.json`, `docker-events.jsonl` | `docker:dind` (Docker 29.10, API 1.57), after creating a volume, a network and two busybox containers, queried at API 1.51 |
| `docker-desktop-version.json` | Docker Desktop `/version` (version information only) |
| `swarm-manager-*.json` | `docker:dind` after `docker swarm init` |
| `podman-*.json` | `quay.io/podman/stable` (Podman 6.2) running `podman system service` |

To refresh, start the engines the same way (privileged containers exposing the API on 127.0.0.1) and query them with `curl`.
