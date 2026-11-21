namespace Sockless.Port.Engines;

/// <summary>A stored engine endpoint: the local socket, or (from Phase 6) a remote agent.</summary>
public sealed class EngineEndpoint
{
    public int Id { get; set; }

    public required string Name { get; set; }

    /// <summary>A <see cref="Docker.DockerAddress"/> string, e.g. <c>unix:///var/run/docker.sock</c>.</summary>
    public required string Address { get; set; }

    /// <summary>The engine on the host Sockless Port runs on, found automatically.</summary>
    public bool IsLocal { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
