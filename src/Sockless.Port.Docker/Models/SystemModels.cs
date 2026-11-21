namespace Sockless.Port.Docker.Models;

// Only the fields Sockless Port uses are modelled. Everything is nullable or defaulted because
// Podman's compatible API and older engines omit some of them.

/// <summary><c>GET /version</c>.</summary>
public sealed record VersionResponse
{
    public PlatformInfo? Platform { get; init; }
    public IReadOnlyList<VersionComponent>? Components { get; init; }
    public string? Version { get; init; }
    public string? ApiVersion { get; init; }
    public string? MinApiVersion { get; init; }
    public string? Os { get; init; }
    public string? Arch { get; init; }
    public string? KernelVersion { get; init; }
}

public sealed record PlatformInfo
{
    public string? Name { get; init; }
}

public sealed record VersionComponent
{
    public string? Name { get; init; }
    public string? Version { get; init; }
}

/// <summary><c>GET /info</c>.</summary>
public sealed record SystemInfo
{
    public string? Id { get; init; }
    public string? Name { get; init; }
    public string? ServerVersion { get; init; }
    public string? OperatingSystem { get; init; }
    public string? OsType { get; init; }
    public string? Architecture { get; init; }
    public string? KernelVersion { get; init; }
    public int NCpu { get; init; }
    public long MemTotal { get; init; }
    public int Containers { get; init; }
    public int ContainersRunning { get; init; }
    public int ContainersPaused { get; init; }
    public int ContainersStopped { get; init; }
    public int Images { get; init; }
    public string? Driver { get; init; }
    public string? DockerRootDir { get; init; }
    public string? CgroupVersion { get; init; }
    public string? CgroupDriver { get; init; }
    public SwarmInfo? Swarm { get; init; }
    public IReadOnlyList<string>? Warnings { get; init; }
}

public sealed record SwarmInfo
{
    public string? NodeId { get; init; }

    /// <summary><c>inactive</c>, <c>pending</c>, <c>active</c>, <c>error</c> or <c>locked</c>.</summary>
    public string? LocalNodeState { get; init; }

    /// <summary>True on managers.</summary>
    public bool ControlAvailable { get; init; }

    /// <summary>Set on managers only.</summary>
    public int? Nodes { get; init; }

    /// <summary>Set on managers only.</summary>
    public int? Managers { get; init; }

    public SwarmClusterInfo? Cluster { get; init; }
}

public sealed record SwarmClusterInfo
{
    public string? Id { get; init; }
}

/// <summary><c>GET /system/df</c>.</summary>
public sealed record DiskUsage
{
    public long LayersSize { get; init; }
    public IReadOnlyList<DiskUsageImage>? Images { get; init; }
    public IReadOnlyList<DiskUsageContainer>? Containers { get; init; }
    public IReadOnlyList<DiskUsageVolume>? Volumes { get; init; }
    public IReadOnlyList<DiskUsageBuildCache>? BuildCache { get; init; }
}

public sealed record DiskUsageImage
{
    public string? Id { get; init; }
    public long Size { get; init; }
    public long SharedSize { get; init; }

    /// <summary>Containers using the image; <c>-1</c> when the engine didn't compute it.</summary>
    public long Containers { get; init; }
}

public sealed record DiskUsageContainer
{
    public string? Id { get; init; }
    public string? State { get; init; }
    public long SizeRw { get; init; }
}

public sealed record DiskUsageVolume
{
    public string? Name { get; init; }
    public VolumeUsageData? UsageData { get; init; }
}

public sealed record VolumeUsageData
{
    /// <summary>Bytes, or <c>-1</c> when unknown.</summary>
    public long Size { get; init; }

    /// <summary>Containers referencing the volume, or <c>-1</c> when unknown.</summary>
    public long RefCount { get; init; }
}

public sealed record DiskUsageBuildCache
{
    public string? Id { get; init; }
    public long Size { get; init; }
    public bool InUse { get; init; }
    public bool Shared { get; init; }
}

/// <summary>An error body: <c>{"message": "..."}</c>.</summary>
internal sealed record ErrorResponse
{
    public string? Message { get; init; }
}
