# API fixtures

Real responses captured from throwaway engines, so the models and engine detection are tested against what engines actually send. Never capture from a personal or production engine: responses include container names, image names, labels and network details.

| File | Source |
|---|---|
| `docker-*.json`, `docker-events.jsonl` | `docker:dind` (Docker 29.10, API 1.57), after creating a volume, a network and two busybox containers, queried at API 1.51 |
| `docker-desktop-version.json` | Docker Desktop `/version` (version information only) |
| `swarm-manager-*.json` | `docker:dind` after `docker swarm init` |
| `podman-*.json` | `quay.io/podman/stable` (Podman 6.2) running `podman system service` |
| `docker-container-*.json`, `docker-container-logs*.bin` | `fixture-web` (ports, env, labels, a volume, a read-only bind, a network, limits, a health check; writes to stdout and stderr) and `fixture-tty` (TTY) on `docker:dind`. Logs are the raw response bodies (multiplexed, and raw for the TTY container) |
| `*-volumes-list.json`, `*-volume-inspect.json`, `*-network-inspect.json`, `*-networks-list.json`, `*-containers-mounts.json` | `docker:dind` and the Podman engine after creating `fixture-data` and `fixture-unused` volumes, a `fixture-net` bridge (subnet, gateway, IP range, MTU option, a label), `fixture-web` mounting the volume on that network, and `fixture-anon` with an anonymous volume. Nested Podman runs `fixture-anon` with host networking |
| `swarm-networks.json` | `GET /networks` on the swarm manager: `ingress` and `docker_gwbridge` |
| `swarm-inspect.json`, `swarm-nodes.json`, `swarm-services.json`, `swarm-service-inspect.json`, `swarm-tasks.json`, `swarm-secrets.json`, `swarm-configs.json`, `swarm-service-logs.bin` | A single-node `docker:dind` swarm (Docker 29.10) with `web` (2 replicas, a published port, an attachable overlay `backend`, the secret `db_password`, the config `app_conf` at `/etc/app.conf`, a volume, a constraint, a spread preference, limits, updated twice so it has a `PreviousSpec`), `agentish` (global), and the stack `demo` (`demo_api`, 2 replicas, and `demo_worker`, global). The log is the raw `/services/web/logs?details=true&timestamps=true` body. The join tokens are the throwaway swarm's |
| `swarm-tasks-rejected.json` | The tasks of `broken`, a service whose volume target isn't absolute, so every task is rejected |
| `podman-pods.json`, `podman-pod-*.json` | Libpod (`/v4.0.0/libpod`) on the Podman engine after creating `fixture-pod` (a label, `fixture-pod-web` and `fixture-pod-side`), `fixture-stopped` (created, never started), `fixture-empty` (no infra container) and `fixture-broken` (a container whose command doesn't exist, so the pod is degraded), with `fixture-solo` outside any pod. `podman-pod-containers.json` is Docker's container list at the same moment, `podman-pod-start-errors.json` the 409 of restarting `fixture-broken`, and `podman-pod-rm.json` a forced remove. Host networking only, so the pods publish no ports |
| `podman-container-inspect.json`, `podman-containers.json` | `fixture-web` on the Podman engine. Nested Podman has host networking and no cgroup, so it has no ports or stats |

To refresh, start the engines the same way (privileged containers exposing the API on 127.0.0.1) and query them with `curl`.
