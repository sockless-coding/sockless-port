using Sockless.Port.Docker.Models;

namespace Sockless.Port.Docker.Engine;

public enum EngineKind
{
    Docker,
    Podman,
}

/// <summary>
/// What an endpoint can do. The UI decides what to show from these flags, never from
/// <see cref="EngineKind"/> checks scattered through pages.
/// </summary>
[Flags]
public enum EngineCapabilities
{
    None = 0,

    /// <summary>The node is part of an active swarm (manager or worker).</summary>
    Swarm = 1 << 0,

    /// <summary>The node is a swarm manager, so cluster-wide reads and writes work.</summary>
    SwarmManager = 1 << 1,

    /// <summary>Podman pods, via the libpod API on the same socket.</summary>
    Pods = 1 << 2,

    /// <summary>
    /// The engine runs as an unprivileged user (rootless Docker or Podman), so it can't publish
    /// ports below 1024 and may lack cgroup controllers for resource limits.
    /// </summary>
    Rootless = 1 << 3,
}

/// <summary>An engine as detected when connecting.</summary>
public sealed record EngineDescriptor(
    EngineKind Kind,
    string Version,
    Version ApiVersion,
    string? Os,
    string? Architecture,
    EngineCapabilities Capabilities)
{
    public bool Has(EngineCapabilities capability) => (Capabilities & capability) == capability;

    /// <summary>E.g. <c>Docker 29.0.1</c> or <c>Podman 5.6.2</c>.</summary>
    public string DisplayName => $"{Kind} {Version}";

    /// <summary>E.g. <c>linux/amd64</c>.</summary>
    public string? Platform => Os is null ? Architecture : Architecture is null ? Os : $"{Os}/{Architecture}";

    public static EngineDescriptor Detect(VersionResponse version, SystemInfo info, Version negotiatedApiVersion)
    {
        var podman = version.Components?.FirstOrDefault(c => c.Name?.Contains("Podman", StringComparison.OrdinalIgnoreCase) == true);
        var isPodman = podman is not null || version.Platform?.Name?.Contains("Podman", StringComparison.OrdinalIgnoreCase) == true;

        var capabilities = EngineCapabilities.None;
        if (isPodman)
            capabilities |= EngineCapabilities.Pods;

        if (info.SecurityOptions?.Any(o => o.Split(',').Contains("name=rootless", StringComparer.Ordinal)) == true)
            capabilities |= EngineCapabilities.Rootless;

        if (string.Equals(info.Swarm?.LocalNodeState, "active", StringComparison.OrdinalIgnoreCase))
        {
            capabilities |= EngineCapabilities.Swarm;
            if (info.Swarm!.ControlAvailable)
                capabilities |= EngineCapabilities.SwarmManager;
        }

        return new EngineDescriptor(
            isPodman ? EngineKind.Podman : EngineKind.Docker,
            (isPodman ? podman?.Version : null) ?? version.Version ?? info.ServerVersion ?? "unknown",
            negotiatedApiVersion,
            version.Os ?? info.OsType,
            version.Arch ?? info.Architecture,
            capabilities);
    }
}
