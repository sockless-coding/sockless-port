namespace Sockless.Port.Engines;

public enum EndpointKind
{
    /// <summary>The engine on the host Sockless Port runs on, found automatically.</summary>
    Local,

    /// <summary>A Sockless Port agent on another host, reached over mutual TLS.</summary>
    Agent,
}

/// <summary>A stored engine endpoint: the local socket, or a remote agent.</summary>
public sealed class EngineEndpoint
{
    public int Id { get; set; }

    public required string Name { get; set; }

    /// <summary>
    /// A <see cref="Docker.DockerAddress"/> string, e.g. <c>unix:///var/run/docker.sock</c>, or for
    /// an agent <c>https://host:9001</c> (its TLS settings come from the fields below).
    /// </summary>
    public required string Address { get; set; }

    public EndpointKind Kind { get; set; }

    public bool IsLocal => Kind == EndpointKind.Local;

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    // ---------------------------------------------------------------- agent

    /// <summary>The certificate issued to the agent; the server accepts only this one.</summary>
    public string? AgentCertificatePem { get; set; }

    /// <summary>SHA-256 thumbprint of <see cref="AgentCertificatePem"/>, the pin.</summary>
    public string? AgentThumbprint { get; set; }

    public DateTimeOffset? AgentCertificateNotAfter { get; set; }

    /// <summary>When the agent first answered with its certificate.</summary>
    public DateTimeOffset? EnrolledAt { get; set; }

    /// <summary>The pending enrolment's public id; null once enrolled or when there's none.</summary>
    public string? EnrolmentTokenId { get; set; }

    /// <summary>The pending enrolment's secret, encrypted with Data Protection.</summary>
    public string? ProtectedEnrolmentSecret { get; set; }

    public DateTimeOffset? EnrolmentExpiresAt { get; set; }
}
