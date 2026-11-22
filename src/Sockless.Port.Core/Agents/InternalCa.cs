using System.Net;
using System.Numerics;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Sockless.Port.Docker;
using Sockless.Port.Docker.Agent;

namespace Sockless.Port.Agents;

/// <summary>A certificate the CA issued for an agent.</summary>
public sealed record IssuedCertificate(string CertificatePem, string Thumbprint, DateTimeOffset NotAfter);

/// <summary>The CSR can't be signed. The message says why.</summary>
public sealed class CertificateRequestException(string message) : Exception(message);

/// <summary>
/// Sockless Port's own certificate authority. It signs each agent's server certificate at
/// enrolment and the server's client certificate, and nothing else, so agents trust exactly this
/// server. Keys live in <c>/data/pki</c>, encrypted with Data Protection like registry passwords.
/// </summary>
public sealed partial class InternalCa(IOptions<PortOptions> options, IDataProtectionProvider protection, TimeProvider time, ILogger<InternalCa> logger)
{
    internal static readonly TimeSpan CaLifetime = TimeSpan.FromDays(365 * 20);
    internal static readonly TimeSpan AgentLifetime = TimeSpan.FromDays(365 * 5);
    internal static readonly TimeSpan ClientLifetime = TimeSpan.FromDays(365);

    /// <summary>The client certificate is replaced this long before it expires. Agents accept any the CA signed.</summary>
    internal static readonly TimeSpan ClientRenewBefore = TimeSpan.FromDays(30);

    private const string CaCertificateFile = "ca.pem";
    private const string CaKeyFile = "ca.key.protected";
    private const string ClientCertificateFile = "client.pem";
    private const string ClientKeyFile = "client.key.protected";

    private static readonly Oid ServerAuth = new("1.3.6.1.5.5.7.3.1");
    private static readonly Oid ClientAuth = new("1.3.6.1.5.5.7.3.2");

    private readonly IDataProtector _protector = protection.CreateProtector("Sockless.Port.Pki.v1");
    private readonly Lock _gate = new();
    private (X509Certificate2 Certificate, string Pem)? _ca;
    private (string CertificatePem, string KeyPem, DateTimeOffset NotAfter)? _client;

    private string Directory => Path.Combine(options.Value.DataPath, "pki");

    /// <summary>The CA certificate (public), created on first use.</summary>
    public string CaCertificatePem
    {
        get { lock (_gate) return LoadOrCreateCa().Pem; }
    }

    /// <summary>
    /// TLS settings for talking to an enrolled agent: trust only this CA, present the server's
    /// client certificate, and accept only <paramref name="pin"/>.
    /// </summary>
    public DockerTls AgentTls(string pin)
    {
        lock (_gate)
        {
            var ca = LoadOrCreateCa();
            var client = LoadOrCreateClient(ca.Certificate);
            return new DockerTls
            {
                CaCertificatePem = ca.Pem,
                ClientCertificatePem = client.CertificatePem,
                ClientKeyPem = client.KeyPem,
                PinnedCertificateSha256 = pin,
            };
        }
    }

    /// <summary>
    /// Signs an agent's server certificate for the key in <paramref name="csrPem"/>. Only the key is
    /// taken from the request; the name, usage and host names are the server's.
    /// </summary>
    /// <param name="host">The host the server connects to (a DNS name or IP address), which the certificate must name.</param>
    /// <exception cref="CertificateRequestException">The request isn't valid or its key is too weak.</exception>
    public IssuedCertificate SignAgent(string csrPem, int endpointId, string host)
    {
        CertificateRequest request;
        try
        {
            request = CertificateRequest.LoadSigningRequestPem(csrPem, HashAlgorithmName.SHA256,
                CertificateRequestLoadOptions.Default, RSASignaturePadding.Pkcs1);
        }
        catch (CryptographicException ex)
        {
            throw new CertificateRequestException($"The agent's certificate request isn't valid: {ex.Message}");
        }

        using (var ecdsa = request.PublicKey.GetECDsaPublicKey())
        using (var rsa = request.PublicKey.GetRSAPublicKey())
        {
            if (ecdsa is null && rsa is null)
                throw new CertificateRequestException("The agent's key must be ECDSA or RSA.");
            if (ecdsa is { KeySize: < 256 } || rsa is { KeySize: < 2048 })
                throw new CertificateRequestException("The agent's key is too short.");
        }

        var issued = new CertificateRequest(new X500DistinguishedName($"CN={AgentProtocol.AgentCommonName(endpointId)}"), request.PublicKey, HashAlgorithmName.SHA256);
        issued.CertificateExtensions.Add(new X509BasicConstraintsExtension(certificateAuthority: false, false, 0, critical: true));
        issued.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, critical: true));
        issued.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([ServerAuth], critical: false));
        var names = new SubjectAlternativeNameBuilder();
        if (IPAddress.TryParse(host, out var ip))
            names.AddIpAddress(ip);
        else
            names.AddDnsName(host);
        issued.CertificateExtensions.Add(names.Build());
        issued.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, critical: false));

        lock (_gate)
        {
            var ca = LoadOrCreateCa().Certificate;
            issued.CertificateExtensions.Add(X509AuthorityKeyIdentifierExtension.CreateFromCertificate(ca, includeKeyIdentifier: true, includeIssuerAndSerial: false));
            var now = time.GetUtcNow();
            var notAfter = Min(now + AgentLifetime, new DateTimeOffset(ca.NotAfter.ToUniversalTime()));
            using var certificate = issued.Create(ca, now.AddMinutes(-5), notAfter, NewSerial());
            LogIssued(logger, endpointId, host);
            return new IssuedCertificate(certificate.ExportCertificatePem(), DockerTransport.Thumbprint(certificate), notAfter);
        }
    }

    // ---------------------------------------------------------------- storage

    private (X509Certificate2 Certificate, string Pem) LoadOrCreateCa()
    {
        if (_ca is { } cached)
            return cached;

        var certificatePath = Path.Combine(Directory, CaCertificateFile);
        var keyPath = Path.Combine(Directory, CaKeyFile);
        if (File.Exists(certificatePath) && File.Exists(keyPath))
        {
            var pem = File.ReadAllText(certificatePath);
            var keyPem = Unprotect(keyPath, "CA");
            return (_ca = (LoadWithKey(pem, keyPem), pem)).Value;
        }

        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP384);
        var request = new CertificateRequest("CN=Sockless Port internal CA", key, HashAlgorithmName.SHA384);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(certificateAuthority: true, true, 0, critical: true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, critical: true));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, critical: false));
        var now = time.GetUtcNow();
        using var created = request.CreateSelfSigned(now.AddMinutes(-5), now + CaLifetime);
        var certificatePem = created.ExportCertificatePem();

        System.IO.Directory.CreateDirectory(Directory);
        File.WriteAllText(keyPath, _protector.Protect(key.ExportPkcs8PrivateKeyPem()));
        File.WriteAllText(certificatePath, certificatePem);
        LogCreatedCa(logger, Directory);
        return (_ca = (LoadWithKey(certificatePem, key.ExportPkcs8PrivateKeyPem()), certificatePem)).Value;
    }

    private (string CertificatePem, string KeyPem, DateTimeOffset NotAfter) LoadOrCreateClient(X509Certificate2 ca)
    {
        var now = time.GetUtcNow();
        if (_client is { } cached && cached.NotAfter - now > ClientRenewBefore)
            return cached;

        var certificatePath = Path.Combine(Directory, ClientCertificateFile);
        var keyPath = Path.Combine(Directory, ClientKeyFile);
        if (_client is null && File.Exists(certificatePath) && File.Exists(keyPath))
        {
            var pem = File.ReadAllText(certificatePath);
            using var stored = X509Certificate2.CreateFromPem(pem);
            var notAfter = new DateTimeOffset(stored.NotAfter.ToUniversalTime());
            if (notAfter - now > ClientRenewBefore && stored.Issuer == ca.Subject)
                return (_client = (pem, Unprotect(keyPath, "client"), notAfter)).Value;
        }

        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest($"CN={AgentProtocol.ServerCommonName}", key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(certificateAuthority: false, false, 0, critical: true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, critical: true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([ClientAuth], critical: false));
        request.CertificateExtensions.Add(X509AuthorityKeyIdentifierExtension.CreateFromCertificate(ca, includeKeyIdentifier: true, includeIssuerAndSerial: false));
        var expires = Min(now + ClientLifetime, new DateTimeOffset(ca.NotAfter.ToUniversalTime()));
        using var certificate = request.Create(ca, now.AddMinutes(-5), expires, NewSerial());
        var certificatePem = certificate.ExportCertificatePem();
        var keyPem = key.ExportPkcs8PrivateKeyPem();

        System.IO.Directory.CreateDirectory(Directory);
        File.WriteAllText(keyPath, _protector.Protect(keyPem));
        File.WriteAllText(certificatePath, certificatePem);
        LogIssuedClient(logger, expires);
        return (_client = (certificatePem, keyPem, expires)).Value;
    }

    private string Unprotect(string path, string what)
    {
        try
        {
            return _protector.Unprotect(File.ReadAllText(path));
        }
        catch (CryptographicException ex)
        {
            throw new InvalidOperationException(
                $"The internal {what} key in {Directory} can't be decrypted: the Data Protection keys changed. Restore them, or remove {Directory} and enrol every agent again.", ex);
        }
    }

    private static X509Certificate2 LoadWithKey(string certificatePem, string keyPem) => DockerTransport.LoadCertificateWithKey(certificatePem, keyPem);

    /// <summary>A positive 128-bit serial (RFC 5280 allows up to 20 octets; the top bit stays clear).</summary>
    private static byte[] NewSerial()
    {
        var serial = RandomNumberGenerator.GetBytes(16);
        serial[0] &= 0x7F;
        if (serial[0] == 0)
            serial[0] = 1;
        return serial;
    }

    private static DateTimeOffset Min(DateTimeOffset a, DateTimeOffset b) => a < b ? a : b;

    [LoggerMessage(Level = LogLevel.Information, Message = "Created the internal CA for agents in {Directory}")]
    private static partial void LogCreatedCa(ILogger logger, string directory);

    [LoggerMessage(Level = LogLevel.Information, Message = "Issued a client certificate for agents, valid until {NotAfter}")]
    private static partial void LogIssuedClient(ILogger logger, DateTimeOffset notAfter);

    [LoggerMessage(Level = LogLevel.Information, Message = "Issued a certificate for the agent of endpoint {EndpointId} ({Host})")]
    private static partial void LogIssued(ILogger logger, int endpointId, string host);
}
