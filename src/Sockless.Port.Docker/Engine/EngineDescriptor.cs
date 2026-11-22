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

    /// <summary>
    /// The engine reports what a registry serves for a tag without pulling it
    /// (<c>/distribution/{name}/json</c>), so image update checks work. Docker has it; Podman has
    /// it from 6.2.
    /// </summary>
    RegistryDigests = 1 << 4,
}

/// <summary>An engine as detected when connecting.</summary>
/// <param name="SwarmNodeId">The engine's node in its swarm, when it's in one: how a swarm node is matched to the endpoint that reaches it.</param>
/// <param name="SwarmClusterId">The swarm's ID. Set on managers only (workers don't know it).</param>
/// <param name="CgroupVersion">1 or 2, from <c>/info</c>; null when the engine doesn't say. Rootless engines on cgroups v1 can't limit or measure containers.</param>
public sealed record EngineDescriptor(
    EngineKind Kind,
    string Version,
    Version ApiVersion,
    string? Os,
    string? Architecture,
    EngineCapabilities Capabilities,
    string? SwarmNodeId = null,
    string? SwarmClusterId = null,
    int? CgroupVersion = null)
{
    public bool Has(EngineCapabilities capability) => (Capabilities & capability) == capability;

    /// <summary>E.g. <c>Docker 29.0.1</c> or <c>Podman 5.6.2</c>.</summary>
    public string DisplayName => $"{Kind} {Version}";

    /// <summary>E.g. <c>linux/amd64</c>.</summary>
    public string? Platform => Os is null ? Architecture : Architecture is null ? Os : $"{Os}/{Architecture}";

    /// <summary>A rootless engine on cgroups v1: resource limits and stats don't work at all.</summary>
    public bool RootlessWithoutCgroups => Has(EngineCapabilities.Rootless) && CgroupVersion == 1;

    public static EngineDescriptor Detect(VersionResponse version, SystemInfo info, Version negotiatedApiVersion)
    {
        var podman = version.Components?.FirstOrDefault(c => c.Name?.Contains("Podman", StringComparison.OrdinalIgnoreCase) == true);
        var isPodman = podman is not null || version.Platform?.Name?.Contains("Podman", StringComparison.OrdinalIgnoreCase) == true;

        var engineVersion = (isPodman ? podman?.Version : null) ?? version.Version ?? info.ServerVersion ?? "unknown";

        var capabilities = EngineCapabilities.None;
        if (isPodman)
            capabilities |= EngineCapabilities.Pods;

        if (!isPodman || AtLeast(engineVersion, PodmanRegistryDigests))
            capabilities |= EngineCapabilities.RegistryDigests;

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
            engineVersion,
            negotiatedApiVersion,
            version.Os ?? info.OsType,
            version.Arch ?? info.Architecture,
            capabilities,
            capabilities.HasFlag(EngineCapabilities.Swarm) && info.Swarm?.NodeId is { Length: > 0 } node ? node : null,
            capabilities.HasFlag(EngineCapabilities.Swarm) && info.Swarm?.Cluster?.Id is { Length: > 0 } cluster ? cluster : null,
            info.CgroupVersion?.Trim().TrimStart('v', 'V') switch { "1" => 1, "2" => 2, _ => null });
    }

    /// <summary>The first Podman release that serves <c>/distribution/{name}/json</c> (6.1 and 5.x answer 404).</summary>
    internal static readonly Version PodmanRegistryDigests = new(6, 2);

    /// <summary>Whether a release string (<c>6.2.0</c>, <c>6.2.0-rc1</c>, <c>6.3.0-dev</c>) is at least <paramref name="minimum"/>. Unreadable ones aren't.</summary>
    internal static bool AtLeast(string release, Version minimum)
    {
        var numbers = release.Split('-', '+', '~')[0].Split('.');
        var major = numbers.Length > 0 && int.TryParse(numbers[0], out var m) ? m : -1;
        var minor = numbers.Length > 1 && int.TryParse(numbers[1], out var n) ? n : 0;
        return major >= 0 && new Version(major, minor) >= minimum;
    }
}
