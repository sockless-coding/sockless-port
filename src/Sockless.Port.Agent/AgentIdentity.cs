using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Sockless.Port.Docker;
using Sockless.Port.Docker.Agent;

namespace Sockless.Port.Agent;

/// <summary>
/// The agent's key, its certificate from the server's CA, and that CA. Until enrolment the agent
/// serves a throwaway self-signed certificate: the enrolment messages carry their own proofs.
/// </summary>
public sealed class AgentIdentity
{
    internal const string KeyFile = "key.pem";
    internal const string CertificateFile = "cert.pem";

    /// <summary>Written last, so its presence means the other two are complete.</summary>
    internal const string CaFile = "ca.pem";

    private static readonly Oid ServerAuth = new("1.3.6.1.5.5.7.3.1");
    private static readonly Oid ClientAuth = new("1.3.6.1.5.5.7.3.2");
    private static readonly TimeSpan TrustCacheTime = TimeSpan.FromMinutes(5);

    private readonly string _directory;
    private readonly TimeProvider _time;
    private readonly Lock _gate = new();
    private readonly ConcurrentDictionary<string, DateTimeOffset> _trusted = new(StringComparer.Ordinal);

    private X509Certificate2 _serverCertificate;
    private X509Certificate2? _ca;
    private ECDsa? _pendingKey;

    public AgentIdentity(string directory, TimeProvider? time = null)
    {
        _directory = directory;
        _time = time ?? TimeProvider.System;
        Directory.CreateDirectory(directory);

        var key = Path.Combine(directory, KeyFile);
        var certificate = Path.Combine(directory, CertificateFile);
        var ca = Path.Combine(directory, CaFile);
        if (File.Exists(ca) && File.Exists(certificate) && File.Exists(key))
        {
            _serverCertificate = DockerTransport.LoadCertificateWithKey(File.ReadAllText(certificate), File.ReadAllText(key));
            _ca = X509Certificate2.CreateFromPem(File.ReadAllText(ca));
        }
        else
        {
            _serverCertificate = CreateSelfSigned(_time);
        }
    }

    public bool IsEnrolled
    {
        get { lock (_gate) return _ca is not null; }
    }

    /// <summary>The certificate Kestrel presents right now.</summary>
    public X509Certificate2 ServerCertificate
    {
        get { lock (_gate) return _serverCertificate; }
    }

    /// <summary>When enrolment happened (the certificate's start), or null before.</summary>
    public DateTimeOffset? EnrolledAt
    {
        get { lock (_gate) return _ca is null ? null : new DateTimeOffset(_serverCertificate.NotBefore.ToUniversalTime()); }
    }

    public DateTimeOffset? CertificateNotAfter
    {
        get { lock (_gate) return _ca is null ? null : new DateTimeOffset(_serverCertificate.NotAfter.ToUniversalTime()); }
    }

    /// <summary>A new key and a request for it. The key stays here; only the latest request can complete.</summary>
    public string CreateSigningRequest()
    {
        var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=sockless-port-agent", key, HashAlgorithmName.SHA256);
        var pem = request.CreateSigningRequestPem();
        lock (_gate)
        {
            _pendingKey?.Dispose();
            _pendingKey = key;
        }
        return pem;
    }

    /// <summary>
    /// Takes the certificate the server issued for the pending key, checks it, stores it and starts
    /// serving it. From then on, only clients with the server's certificate are let in.
    /// </summary>
    /// <returns>Null on success, otherwise what's wrong.</returns>
    public string? Complete(string certificatePem, string caCertificatePem)
    {
        X509Certificate2 certificate, ca;
        try
        {
            certificate = X509Certificate2.CreateFromPem(certificatePem);
            ca = X509Certificate2.CreateFromPem(caCertificatePem);
        }
        catch (CryptographicException)
        {
            return "The certificates aren't valid PEM.";
        }

        lock (_gate)
        {
            if (_ca is not null)
                return "This agent is already enrolled.";
            if (_pendingKey is not { } key)
                return "No certificate request is pending. Start the enrolment again.";
            if (!certificate.PublicKey.ExportSubjectPublicKeyInfo().AsSpan().SequenceEqual(key.ExportSubjectPublicKeyInfo()))
                return "The certificate isn't for this agent's key.";
            if (!Chains(certificate, ca, ServerAuth))
                return "The certificate doesn't chain to the CA it came with, or doesn't allow server authentication.";

            var keyPem = key.ExportPkcs8PrivateKeyPem();
            WritePrivate(KeyFile, keyPem);
            WritePrivate(CertificateFile, certificatePem);
            WritePrivate(CaFile, caCertificatePem);

            _serverCertificate = DockerTransport.LoadCertificateWithKey(certificatePem, keyPem);
            _ca = ca;
            _pendingKey = null;
            key.Dispose();
            return null;
        }
    }

    /// <summary>
    /// True for the Sockless Port server's client certificate: chains to the enrolment CA, allows
    /// client authentication and is named <see cref="AgentProtocol.ServerCommonName"/>.
    /// </summary>
    public bool IsTrustedServer(X509Certificate2? certificate)
    {
        X509Certificate2? ca;
        lock (_gate) ca = _ca;
        if (certificate is null || ca is null)
            return false;

        var now = _time.GetUtcNow();
        var thumbprint = DockerTransport.Thumbprint(certificate);
        if (_trusted.TryGetValue(thumbprint, out var until) && until > now)
            return true;

        var trusted = certificate.GetNameInfo(X509NameType.SimpleName, forIssuer: false) == AgentProtocol.ServerCommonName
                      && Chains(certificate, ca, ClientAuth);
        if (trusted)
        {
            var notAfter = new DateTimeOffset(certificate.NotAfter.ToUniversalTime());
            _trusted[thumbprint] = notAfter < now + TrustCacheTime ? notAfter : now + TrustCacheTime;
        }
        return trusted;
    }

    private bool Chains(X509Certificate2 certificate, X509Certificate2 ca, Oid usage)
    {
        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.Add(ca);
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        chain.ChainPolicy.ApplicationPolicy.Add(usage);
        chain.ChainPolicy.VerificationTime = _time.GetUtcNow().UtcDateTime;
        return chain.Build(certificate);
    }

    private void WritePrivate(string name, string content)
    {
        var path = Path.Combine(_directory, name);
        var temp = path + ".tmp";
        var options = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows())
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        using (var file = new StreamWriter(temp, System.Text.Encoding.ASCII, options))
            file.Write(content);
        File.Move(temp, path, overwrite: true);
    }

    private static X509Certificate2 CreateSelfSigned(TimeProvider time)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=sockless-port-agent (not enrolled)", key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([ServerAuth], critical: false));
        var now = time.GetUtcNow();
        using var certificate = request.CreateSelfSigned(now.AddMinutes(-5), now.AddDays(30));
        return X509CertificateLoader.LoadPkcs12(certificate.Export(X509ContentType.Pkcs12), password: null);
    }
}
