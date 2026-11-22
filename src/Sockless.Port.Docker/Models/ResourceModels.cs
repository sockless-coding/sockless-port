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

    /// <summary>Volumes and bind mounts, so volume usage needs no inspect per container.</summary>
    public IReadOnlyList<MountPoint>? Mounts { get; init; }

    /// <summary>Only <see cref="NetworkSettings.Networks"/> is filled in a list.</summary>
    public NetworkSettings? NetworkSettings { get; init; }

    /// <summary>Only <see cref="Models.HostConfig.NetworkMode"/> is filled in a list.</summary>
    public HostConfig? HostConfig { get; init; }

    /// <summary>
    /// Whether the container can be connected to another network: not when it uses the host's
    /// network, none, or another container's (<c>container:…</c>). Engines refuse those.
    /// </summary>
    public bool CanJoinNetworks => HostConfig?.NetworkMode is not ("host" or "none") && HostConfig?.NetworkMode?.StartsWith("container:", StringComparison.Ordinal) != true;

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

    /// <summary>Containers using the image; <c>-1</c> when not computed (Docker below API 1.51).</summary>
    public long Containers { get; init; }

    public IReadOnlyDictionary<string, string>? Labels { get; init; }

    /// <summary>The tags that are real names: older engines list <c>&lt;none&gt;:&lt;none&gt;</c> for untagged images.</summary>
    public IReadOnlyList<string> Tags => RepoTags is null ? [] : [.. RepoTags.Where(t => t != "<none>:<none>")];

    /// <summary>No tag left: a leftover from a rebuild or re-pull, removed by a dangling prune.</summary>
    public bool IsDangling => Tags.Count == 0;
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
