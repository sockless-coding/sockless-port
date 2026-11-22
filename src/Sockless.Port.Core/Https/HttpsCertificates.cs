using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Sockless.Port.Agents;

namespace Sockless.Port.Https;

/// <summary>What Settings shows about the HTTPS listener.</summary>
/// <param name="Problem">Why no certificate could be loaded, e.g. a missing file.</param>
public sealed record HttpsStatus(bool Enabled, int? Port, bool InternalCa, string? Source, string? Subject, IReadOnlyList<string> Names,
    DateTimeOffset? NotAfter, string? Thumbprint, bool RedirectHttp, string? Problem);

/// <summary>
/// The UI's HTTPS certificate, asked for on every TLS handshake. A configured file is reloaded when
/// it changes (checked at most once a minute), so a renewed certificate is picked up without a
/// restart; otherwise the internal CA issues and renews one.
/// </summary>
public sealed partial class HttpsCertificates(IOptions<PortOptions> options, InternalCa ca, TimeProvider time, ILogger<HttpsCertificates> logger)
{
    private static readonly TimeSpan FileCheckInterval = TimeSpan.FromMinutes(1);

    private readonly Lock _gate = new();
    private X509Certificate2? _file;
    private DateTime _fileWritten;
    private DateTimeOffset _fileCheckedAt;

    private HttpsOptions Https => options.Value.Https;

    /// <summary>The certificate to serve now.</summary>
    /// <exception cref="InvalidOperationException">The configured certificate can't be loaded.</exception>
    public X509Certificate2 Current() => Https.UsesInternalCa ? ca.HttpsCertificate(HostNames()) : FromFile();

    /// <summary>The names the internal CA's certificate covers.</summary>
    public IReadOnlyList<string> HostNames()
    {
        var configured = Https.HostNames.SelectMany(n => n.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)).ToList();
        if (configured.Count > 0)
            return configured;
        List<string> names = ["localhost", IPAddress.Loopback.ToString()];
        if (Dns.GetHostName() is { Length: > 0 } host)
            names.Add(host);
        return names;
    }

    public HttpsStatus Status()
    {
        var https = Https;
        if (!https.Enabled)
            return new HttpsStatus(false, null, https.UsesInternalCa, null, null, [], null, null, false, null);
        try
        {
            var certificate = Current();
            var san = certificate.Extensions.OfType<X509SubjectAlternativeNameExtension>().FirstOrDefault();
            IReadOnlyList<string> names = san is null ? [] : [.. san.EnumerateDnsNames(), .. san.EnumerateIPAddresses().Select(ip => ip.ToString())];
            return new HttpsStatus(true, https.Port, https.UsesInternalCa, https.UsesInternalCa ? null : https.Certificate,
                certificate.Subject, names, new DateTimeOffset(certificate.NotAfter.ToUniversalTime()),
                certificate.GetCertHashString(HashAlgorithmName.SHA256), https.RedirectHttp, null);
        }
        catch (Exception ex) when (ex is InvalidOperationException or CryptographicException or IOException or UnauthorizedAccessException)
        {
            return new HttpsStatus(true, https.Port, https.UsesInternalCa, https.Certificate, null, [], null, null, https.RedirectHttp, ex.Message);
        }
    }

    private X509Certificate2 FromFile()
    {
        lock (_gate)
        {
            var now = time.GetUtcNow();
            if (_file is not null && now - _fileCheckedAt < FileCheckInterval)
                return _file;
            _fileCheckedAt = now;

            var https = Https;
            var path = https.Certificate!;
            DateTime written;
            try
            {
                written = Max(File.GetLastWriteTimeUtc(path), string.IsNullOrWhiteSpace(https.CertificateKey) ? DateTime.MinValue : File.GetLastWriteTimeUtc(https.CertificateKey));
                if (!File.Exists(path))
                    throw new FileNotFoundException($"The HTTPS certificate {path} doesn't exist.", path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (_file is not null)
                    return _file;
                throw new InvalidOperationException($"The HTTPS certificate can't be read: {ex.Message}", ex);
            }

            if (_file is not null && written == _fileWritten)
                return _file;

            try
            {
                var loaded = Load(https);
                if (!loaded.HasPrivateKey)
                    throw new InvalidOperationException($"The HTTPS certificate {path} has no private key. Set Port:Https:CertificateKey to the key's file.");
                var previous = _file;
                _file = loaded;
                _fileWritten = written;
                if (previous is not null)
                    LogReloaded(logger, path, loaded.NotAfter);
                return loaded;
            }
            catch (Exception ex) when (ex is CryptographicException or IOException or UnauthorizedAccessException or ArgumentException)
            {
                // A renewal may be half-written: keep serving the old one, and try again later.
                if (_file is not null)
                {
                    LogReloadFailed(logger, path, ex.Message);
                    return _file;
                }
                throw new InvalidOperationException($"The HTTPS certificate {path} can't be loaded: {ex.Message}", ex);
            }
        }
    }

    private static X509Certificate2 Load(HttpsOptions https)
    {
        var path = https.Certificate!;
        var extension = Path.GetExtension(path).ToLowerInvariant();
        if (extension is ".pfx" or ".p12")
            return X509CertificateLoader.LoadPkcs12FromFile(path, https.CertificatePassword);

        var keyPath = string.IsNullOrWhiteSpace(https.CertificateKey) ? path : https.CertificateKey;
        using var ephemeral = string.IsNullOrEmpty(https.CertificatePassword)
            ? X509Certificate2.CreateFromPemFile(path, keyPath)
            : X509Certificate2.CreateFromEncryptedPemFile(path, https.CertificatePassword, keyPath);
        // Windows' TLS stack can't use an ephemeral key; a PKCS#12 round trip gives it a real one.
        return X509CertificateLoader.LoadPkcs12(ephemeral.Export(X509ContentType.Pkcs12), password: null);
    }

    private static DateTime Max(DateTime a, DateTime b) => a > b ? a : b;

    [LoggerMessage(Level = LogLevel.Information, Message = "Reloaded the HTTPS certificate {Path}, valid until {NotAfter}")]
    private static partial void LogReloaded(ILogger logger, string path, DateTime notAfter);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The HTTPS certificate {Path} changed but can't be loaded, so the previous one stays in use: {Reason}")]
    private static partial void LogReloadFailed(ILogger logger, string path, string reason);
}
