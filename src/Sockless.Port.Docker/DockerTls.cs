namespace Sockless.Port.Docker;

/// <summary>
/// TLS settings for a TCP engine endpoint. A class, not a record, so the private key never
/// appears in a generated <c>ToString</c> or log line.
/// </summary>
public sealed class DockerTls
{
    /// <summary>PEM of the CA that signed the engine's certificate. Null trusts the system store.</summary>
    public string? CaCertificatePem { get; init; }

    /// <summary>PEM client certificate for mutual TLS. Set together with <see cref="ClientKeyPem"/>.</summary>
    public string? ClientCertificatePem { get; init; }

    public string? ClientKeyPem { get; init; }

    /// <summary>
    /// SHA-256 thumbprint (hex) the engine's certificate must have, on top of chaining to
    /// <see cref="CaCertificatePem"/> and matching the host name. Agents are pinned this way, so
    /// only the certificate issued at enrolment is accepted.
    /// </summary>
    public string? PinnedCertificateSha256 { get; init; }

    public override string ToString() =>
        $"DockerTls(CustomCa: {CaCertificatePem is not null}, ClientCertificate: {ClientCertificatePem is not null}, Pinned: {PinnedCertificateSha256 is not null})";
}
