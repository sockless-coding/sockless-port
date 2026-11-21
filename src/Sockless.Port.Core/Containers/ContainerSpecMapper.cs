using System.Globalization;
using Sockless.Port.Docker.Models;

namespace Sockless.Port.Containers;

/// <summary>Fills a <see cref="ContainerSpec"/> from an existing container, for "Duplicate".</summary>
public static class ContainerSpecMapper
{
    public static ContainerSpec FromInspect(ContainerInspect container)
    {
        var config = container.Config;
        var host = container.HostConfig;

        return new ContainerSpec
        {
            Name = $"{container.DisplayName}-copy",
            Image = config?.Image ?? "",
            Command = config?.Cmd is { Count: > 0 } cmd ? ContainerSpecBuilder.JoinShellWords(cmd) : null,
            Environment = Join(config?.Env),
            Ports = Join(PortLines(host?.PortBindings)),
            Volumes = Join(VolumeLines(container.Mounts)),
            // Compose and Swarm labels would make the copy look like part of the project.
            Labels = Join(config?.Labels?.Where(l => !ContainerLabels.IsOrchestratorLabel(l.Key)).Select(l => $"{l.Key}={l.Value}")),
            Network = host?.NetworkMode is { Length: > 0 } mode && mode != "default" ? mode : null,
            RestartPolicy = host?.RestartPolicy?.Name is { Length: > 0 } policy && ContainerSpecBuilder.RestartPolicies.Contains(policy) ? policy : "no",
            MaxRetries = host?.RestartPolicy?.MaximumRetryCount is > 0 and var retries ? retries : null,
            MemoryLimit = host?.Memory is > 0 and var memory ? memory / 1024 / 1024 : null,
            CpuLimit = host?.NanoCpus is > 0 and var nano ? nano / 1_000_000_000m : null,
            User = config?.User is { Length: > 0 } user ? user : null,
            WorkingDir = config?.WorkingDir is { Length: > 0 } dir ? dir : null,
            Tty = config?.Tty ?? false,
            Interactive = config?.OpenStdin ?? false,
        };
    }

    private static string? Join(IEnumerable<string>? lines) => lines is null ? null : string.Join('\n', lines) is { Length: > 0 } text ? text : null;

    private static IEnumerable<string> PortLines(IReadOnlyDictionary<string, IReadOnlyList<PortBinding>?>? bindings)
    {
        if (bindings is null)
            yield break;
        foreach (var (key, list) in bindings.OrderBy(b => b.Key, StringComparer.Ordinal))
        {
            var (port, protocol) = key.Split('/') is [var p, var proto] ? (p, proto) : (key, "tcp");
            var suffix = protocol == "tcp" ? "" : $"/{protocol}";
            foreach (var binding in list ?? [])
            {
                var hostIp = binding.HostIp is { Length: > 0 } ip ? (ip.Contains(':', StringComparison.Ordinal) ? $"[{ip}]:" : $"{ip}:") : "";
                yield return binding.HostPort is { Length: > 0 } hostPort ? $"{hostIp}{hostPort}:{port}{suffix}" : $"{port}{suffix}";
            }
        }
    }

    private static IEnumerable<string> VolumeLines(IReadOnlyList<MountPoint>? mounts)
    {
        foreach (var mount in mounts ?? [])
        {
            var readOnly = mount.ReadWrite ? "" : ":ro";
            switch (mount.Type)
            {
                case "bind":
                    yield return $"{mount.Source}:{mount.Destination}{readOnly}";
                    break;
                case "volume" when IsAnonymous(mount.Name):
                    // A copy gets its own anonymous volume rather than sharing the original's.
                    yield return mount.Destination ?? "";
                    break;
                case "volume":
                    yield return $"{mount.Name}:{mount.Destination}{readOnly}";
                    break;
            }
        }
    }

    /// <summary>Anonymous volumes are named with 64 hex characters.</summary>
    private static bool IsAnonymous(string? name) =>
        name is { Length: 64 } && name.All(char.IsAsciiHexDigitLower);

    public static string FormatCpus(decimal? cpus) => cpus?.ToString("0.##", CultureInfo.InvariantCulture) ?? "";
}
