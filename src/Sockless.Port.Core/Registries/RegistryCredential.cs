namespace Sockless.Port.Registries;

/// <summary>Stored credentials for one registry, used for every pull and build from it on any endpoint.</summary>
public sealed class RegistryCredential
{
    public int Id { get; set; }

    /// <summary>A label for people: "Docker Hub", "Company GHCR".</summary>
    public required string Name { get; set; }

    /// <summary>The registry host, normalised (<see cref="Docker.ImageReference.NormalizeRegistry"/>): <c>docker.io</c>, <c>ghcr.io</c>, <c>registry.local:5000</c>.</summary>
    public required string Registry { get; set; }

    public required string Username { get; set; }

    /// <summary>The password or access token, encrypted with Data Protection. Never shown again.</summary>
    public required string ProtectedPassword { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
