using System.Text.Json.Serialization;

namespace Sockless.Port.Docker.Models;

/// <summary><c>GET /images/{name}/json</c>.</summary>
public sealed record ImageInspect
{
    public required string Id { get; init; }
    public IReadOnlyList<string>? RepoTags { get; init; }

    /// <summary>
    /// <c>repo@sha256:…</c> for each digest the image is known by in a registry. Docker records the
    /// digest it pulled (the index, for multi-platform images); Podman records the index and the
    /// platform manifest. Empty for images built locally and never pushed.
    /// </summary>
    public IReadOnlyList<string>? RepoDigests { get; init; }

    public string? Parent { get; init; }
    public string? Comment { get; init; }

    /// <summary>RFC 3339.</summary>
    public string? Created { get; init; }

    public string? Author { get; init; }
    public string? Architecture { get; init; }
    public string? Variant { get; init; }
    public string? Os { get; init; }
    public long Size { get; init; }
    public ImageConfig? Config { get; init; }
    public ImageRootFs? RootFS { get; init; }
}

public sealed record ImageConfig
{
    public string? User { get; init; }
    public IReadOnlyList<string>? Env { get; init; }
    public IReadOnlyList<string>? Cmd { get; init; }
    public IReadOnlyList<string>? Entrypoint { get; init; }
    public string? WorkingDir { get; init; }
    public string? StopSignal { get; init; }
    public IReadOnlyDictionary<string, EmptyObject>? ExposedPorts { get; init; }
    public IReadOnlyDictionary<string, EmptyObject>? Volumes { get; init; }
    public IReadOnlyDictionary<string, string>? Labels { get; init; }
}

public sealed record ImageRootFs
{
    public string? Type { get; init; }
    public IReadOnlyList<string>? Layers { get; init; }
}

/// <summary>An item of <c>GET /images/{name}/history</c>, newest layer first.</summary>
public sealed record ImageHistoryItem
{
    /// <summary>The layer's image ID, or <c>&lt;missing&gt;</c> for layers pulled from a registry.</summary>
    public string? Id { get; init; }

    /// <summary>Unix seconds.</summary>
    public long Created { get; init; }

    public string? CreatedBy { get; init; }
    public IReadOnlyList<string>? Tags { get; init; }
    public long Size { get; init; }
    public string? Comment { get; init; }
}

/// <summary>An item of <c>DELETE /images/{name}</c> and of a prune's <c>ImagesDeleted</c>: one of the two is set.</summary>
public sealed record ImageDeleteItem
{
    public string? Untagged { get; init; }
    public string? Deleted { get; init; }
}

/// <summary><c>POST /images/prune</c>.</summary>
public sealed record ImagePruneResponse
{
    public IReadOnlyList<ImageDeleteItem>? ImagesDeleted { get; init; }
    public long SpaceReclaimed { get; init; }
}

/// <summary><c>POST /build/prune</c>.</summary>
public sealed record BuildCachePruneResponse
{
    public IReadOnlyList<string>? CachesDeleted { get; init; }
    public long SpaceReclaimed { get; init; }
}

/// <summary><c>GET /distribution/{name}/json</c>: what the registry currently serves for a reference.</summary>
public sealed record DistributionInspect
{
    public required DistributionDescriptor Descriptor { get; init; }
    public IReadOnlyList<DistributionPlatform>? Platforms { get; init; }
}

public sealed record DistributionDescriptor
{
    public string? MediaType { get; init; }

    /// <summary>The digest of the top-level manifest: the index for multi-platform images.</summary>
    public required string Digest { get; init; }

    public long Size { get; init; }
}

public sealed record DistributionPlatform
{
    public string? Architecture { get; init; }
    public string? Os { get; init; }
    public string? Variant { get; init; }
}

/// <summary>One line of pull progress (<c>POST /images/create</c>).</summary>
public sealed record PullProgress
{
    public string? Status { get; init; }

    /// <summary>The layer, for per-layer lines. Docker also puts the tag here on its first line.</summary>
    public string? Id { get; init; }

    public string? Progress { get; init; }
    public ProgressDetail? ProgressDetail { get; init; }
    public string? Error { get; init; }
    public ErrorDetail? ErrorDetail { get; init; }
}

public sealed record ProgressDetail
{
    public long Current { get; init; }
    public long Total { get; init; }

    /// <summary>Docker's containerd store reports extraction in seconds (<c>s</c>), not bytes.</summary>
    public string? Units { get; init; }
}

public sealed record ErrorDetail
{
    public string? Message { get; init; }
}

/// <summary>One line of <c>POST /build</c> output: build log text, the built image's ID, or an error.</summary>
public sealed record BuildProgress
{
    public string? Stream { get; init; }
    public BuildAux? Aux { get; init; }
    public string? Error { get; init; }
    public ErrorDetail? ErrorDetail { get; init; }
}

public sealed record BuildAux
{
    [JsonPropertyName("ID")]
    public string? Id { get; init; }
}

/// <summary>
/// Registry credentials as the engine takes them: in <c>X-Registry-Auth</c> (pull), the
/// <c>X-Registry-Config</c> map (build) and <c>POST /auth</c>. Lower-case names, as the engine expects.
/// </summary>
public sealed record AuthConfig
{
    [JsonPropertyName("username")]
    public string? Username { get; init; }

    [JsonPropertyName("password")]
    public string? Password { get; init; }

    /// <summary><c>https://index.docker.io/v1/</c> for Docker Hub, the host otherwise.</summary>
    [JsonPropertyName("serveraddress")]
    public string? ServerAddress { get; init; }
}

/// <summary><c>POST /auth</c>.</summary>
public sealed record AuthResponse
{
    public string? Status { get; init; }
    public string? IdentityToken { get; init; }
}
