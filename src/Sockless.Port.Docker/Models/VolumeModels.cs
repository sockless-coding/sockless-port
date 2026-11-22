namespace Sockless.Port.Docker.Models;

/// <summary><c>GET /volumes</c>.</summary>
public sealed record VolumeListResponse
{
    public IReadOnlyList<VolumeSummary>? Volumes { get; init; }
    public IReadOnlyList<string>? Warnings { get; init; }
}

/// <summary>A volume, as listed, inspected or created.</summary>
public sealed record VolumeSummary
{
    /// <summary>Docker's label on volumes it created for a container's <c>VOLUME</c> or <c>-v /path</c>.</summary>
    public const string AnonymousLabel = "com.docker.volume.anonymous";

    public required string Name { get; init; }
    public string? Driver { get; init; }

    /// <summary>Where the volume lives on the engine's host. Not reachable from Sockless Port's container.</summary>
    public string? Mountpoint { get; init; }

    public string? CreatedAt { get; init; }

    /// <summary><c>local</c>, or <c>global</c> for cluster-wide drivers.</summary>
    public string? Scope { get; init; }

    public IReadOnlyDictionary<string, string>? Labels { get; init; }

    /// <summary>The driver options it was created with (<c>type</c>, <c>device</c>, <c>o</c> for the local driver).</summary>
    public IReadOnlyDictionary<string, string>? Options { get; init; }

    /// <summary>
    /// Created for a container rather than by name. Docker labels these; Podman doesn't, so a
    /// 64-character hex name (what both engines generate) counts too.
    /// </summary>
    public bool IsAnonymous => Labels?.ContainsKey(AnonymousLabel) == true || IsGeneratedName(Name);

    private static bool IsGeneratedName(string name) => name.Length == 64 && name.All(char.IsAsciiHexDigitLower);
}

/// <summary><c>POST /volumes/create</c>.</summary>
public sealed record VolumeCreateRequest
{
    /// <summary>Empty for a generated name.</summary>
    public string? Name { get; init; }

    public string? Driver { get; init; }
    public IReadOnlyDictionary<string, string>? DriverOpts { get; init; }
    public IReadOnlyDictionary<string, string>? Labels { get; init; }
}

/// <summary><c>POST /volumes/prune</c>.</summary>
public sealed record VolumePruneResponse
{
    public IReadOnlyList<string>? VolumesDeleted { get; init; }
    public long SpaceReclaimed { get; init; }
}
