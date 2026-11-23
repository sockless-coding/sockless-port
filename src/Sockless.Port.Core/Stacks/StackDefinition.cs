namespace Sockless.Port.Stacks;

/// <summary>How a stack is deployed: <c>docker compose up</c>, or <c>docker stack deploy</c> on a swarm manager.</summary>
public enum StackKind
{
    Compose,
    Swarm,
}

/// <summary>
/// A stack Sockless Port manages on an endpoint. Its files live in
/// <c>{DataPath}/stacks/{EndpointId}/{Name}/</c> (see <see cref="StackFiles"/>); the row records what isn't in them.
/// </summary>
public sealed class StackDefinition
{
    public int Id { get; set; }

    public int EndpointId { get; set; }

    /// <summary>The Compose project name: lower-case letters, digits, <c>-</c> and <c>_</c>.</summary>
    public required string Name { get; set; }

    public StackKind Kind { get; set; } = StackKind.Compose;

    /// <summary>Taken over from a project deployed outside Sockless Port.</summary>
    public bool Adopted { get; set; }

    public const int MaxTemplate = 200;

    /// <summary>The template it was created from and its source, e.g. "Uptime Kuma (Built-in)"; null when it wasn't.</summary>
    public string? Template { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>When the files were last deployed successfully; null if they never were.</summary>
    public DateTimeOffset? DeployedAt { get; set; }
}
