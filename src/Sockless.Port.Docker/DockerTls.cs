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

    public override string ToString() =>
        $"DockerTls(CustomCa: {CaCertificatePem is not null}, ClientCertificate: {ClientCertificatePem is not null})";
}
