namespace Sockless.Port.Docker.Models;

/// <summary>An item of <c>GET /containers/json</c>.</summary>
public sealed record ContainerSummary
{
    public required string Id { get; init; }
    public IReadOnlyList<string>? Names { get; init; }
    public string? Image { get; init; }
    public string? ImageId { get; init; }
    public string? Command { get; init; }

    /// <summary>Unix seconds.</summary>
    public long Created { get; init; }

    /// <summary><c>created</c>, <c>running</c>, <c>paused</c>, <c>restarting</c>, <c>removing</c>, <c>exited</c> or <c>dead</c>.</summary>
    public string? State { get; init; }

    /// <summary>Human-readable status, e.g. <c>Up 2 hours (healthy)</c>.</summary>
    public string? Status { get; init; }

    public IReadOnlyDictionary<string, string>? Labels { get; init; }

    public IReadOnlyList<ContainerPort>? Ports { get; init; }

    public string DisplayName => Names is [var first, ..] && first.TrimStart('/') is { Length: > 0 } name ? name : Id[..Math.Min(12, Id.Length)];
}

/// <summary>An item of <c>GET /images/json</c>.</summary>
public sealed record ImageSummary
{
    public required string Id { get; init; }
    public IReadOnlyList<string>? RepoTags { get; init; }
    public IReadOnlyList<string>? RepoDigests { get; init; }

    /// <summary>Unix seconds.</summary>
    public long Created { get; init; }

    public long Size { get; init; }

    /// <summary>Containers using the image; <c>-1</c> when not computed.</summary>
    public long Containers { get; init; }

    public IReadOnlyDictionary<string, string>? Labels { get; init; }
}

/// <summary><c>GET /volumes</c>.</summary>
public sealed record VolumeListResponse
{
    public IReadOnlyList<VolumeSummary>? Volumes { get; init; }
    public IReadOnlyList<string>? Warnings { get; init; }
}

/// <summary>A volume, as listed or created.</summary>
public sealed record VolumeSummary
{
    public required string Name { get; init; }
    public string? Driver { get; init; }
    public string? Mountpoint { get; init; }
    public string? CreatedAt { get; init; }
    public string? Scope { get; init; }
    public IReadOnlyDictionary<string, string>? Labels { get; init; }
}

/// <summary><c>POST /volumes/create</c>.</summary>
public sealed record VolumeCreateRequest
{
    public string? Name { get; init; }
    public string? Driver { get; init; }
    public IReadOnlyDictionary<string, string>? DriverOpts { get; init; }
    public IReadOnlyDictionary<string, string>? Labels { get; init; }
}

/// <summary>An item of <c>GET /networks</c>.</summary>
public sealed record NetworkSummary
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public string? Driver { get; init; }
    public string? Scope { get; init; }
    public bool Internal { get; init; }
    public bool Attachable { get; init; }
    public IReadOnlyDictionary<string, string>? Labels { get; init; }
}

/// <summary>An item of the <c>GET /events</c> stream.</summary>
public sealed record DockerEvent
{
    /// <summary><c>container</c>, <c>image</c>, <c>volume</c>, <c>network</c>, <c>service</c>, <c>node</c>, ….</summary>
    public string? Type { get; init; }

    public string? Action { get; init; }
    public EventActor? Actor { get; init; }

    /// <summary><c>local</c> or <c>swarm</c>.</summary>
    public string? Scope { get; init; }

    /// <summary>Unix seconds.</summary>
    public long Time { get; init; }

    public long TimeNano { get; init; }
}

public sealed record EventActor
{
    public string? Id { get; init; }
    public IReadOnlyDictionary<string, string>? Attributes { get; init; }
}
