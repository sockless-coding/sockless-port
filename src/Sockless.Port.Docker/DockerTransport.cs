using System.IO.Pipes;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Sockless.Port.Docker;

/// <summary>Builds the <see cref="HttpMessageHandler"/> that carries HTTP over each kind of address.</summary>
public static class DockerTransport
{
    internal static (HttpMessageHandler Handler, Uri BaseAddress) Create(DockerAddress address) => address switch
    {
        UnixSocketAddress or NamedPipeAddress => (CreateHandler((_, ct) => ConnectRawAsync(address, ct)), new Uri("http://docker/")),
        TcpAddress tcp => (CreateTcpHandler(tcp), new Uri(tcp.ToString().Replace("tcp://", "http://", StringComparison.Ordinal) + "/")),
        TunnelAddress tunnel => (CreateTunnelHandler(tunnel), new Uri($"{(tunnel.Tls is null ? "http" : "https")}://{tunnel.Host}/")),
    };

    /// <summary>An HTTP handler for <paramref name="address"/>, and the base address requests go to.</summary>
    public static (HttpMessageHandler Handler, Uri BaseAddress) CreateHandler(DockerAddress address) => Create(address);

    /// <summary>
    /// A plain byte stream to the engine, for callers that speak HTTP themselves: the agent relays
    /// hijacked exec and attach connections this way, and the server bridges the Compose CLI to
    /// tunnels. With TLS, the handshake (client certificate, CA, pin) is done here.
    /// </summary>
    public static async ValueTask<Stream> ConnectRawAsync(DockerAddress address, CancellationToken cancellationToken)
    {
        if (address is TcpAddress { Tls: { } tcpTls } secure)
            return await AuthenticateAsync(await ConnectSocketAsync(new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true },
                new DnsEndPoint(secure.Host, secure.Port), cancellationToken), secure.Host, tcpTls, cancellationToken);
        if (address is TunnelAddress tunnel)
        {
            var opened = await tunnel.Connector.OpenAsync(cancellationToken);
            return tunnel.Tls is { } tunnelTls ? await AuthenticateAsync(opened, tunnel.Host, tunnelTls, cancellationToken) : opened;
        }

        switch (address)
        {
            case UnixSocketAddress unix:
                return await ConnectSocketAsync(new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified),
                    new UnixDomainSocketEndPoint(unix.Path), cancellationToken);

            case NamedPipeAddress pipe:
                var stream = new NamedPipeClientStream(".", pipe.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
                try
                {
                    await stream.ConnectAsync(cancellationToken);
                    return stream;
                }
                catch
                {
                    await stream.DisposeAsync();
                    throw;
                }

            case TcpAddress { Tls: null } tcp:
                return await ConnectSocketAsync(new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true },
                    new DnsEndPoint(tcp.Host, tcp.Port), cancellationToken);

            default:
                throw new NotSupportedException($"A raw connection to {address} isn't supported.");
        }
    }

    private static async Task<Stream> ConnectSocketAsync(Socket socket, EndPoint endPoint, CancellationToken cancellationToken)
    {
        try
        {
            await socket.ConnectAsync(endPoint, cancellationToken);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    private static SocketsHttpHandler CreateHandler(Func<SocketsHttpConnectionContext, CancellationToken, ValueTask<Stream>>? connect)
    {
        var handler = new SocketsHttpHandler
        {
            ConnectTimeout = TimeSpan.FromSeconds(10),
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(1),
            AutomaticDecompression = DecompressionMethods.None,
            UseProxy = false,
            UseCookies = false,
        };
        if (connect is not null)
            handler.ConnectCallback = connect;
        return handler;
    }

    private static SocketsHttpHandler CreateTcpHandler(TcpAddress tcp)
    {
        var handler = CreateHandler(connect: null);
        if (tcp.Tls is { } tls)
            handler.SslOptions = ClientOptions(tls);
        return handler;
    }

    /// <summary>Every connection is a new stream through the agent's tunnel; TLS runs over it as over a socket.</summary>
    private static SocketsHttpHandler CreateTunnelHandler(TunnelAddress tunnel)
    {
        var handler = CreateHandler((_, ct) => tunnel.Connector.OpenAsync(ct));
        if (tunnel.Tls is { } tls)
            handler.SslOptions = ClientOptions(tls);
        return handler;
    }

    /// <summary>
    /// The client side of a TLS connection with <paramref name="tls"/>: its client certificate, its
    /// CA instead of the system's, and its pins. The target host is left to the caller.
    /// </summary>
    public static SslClientAuthenticationOptions ClientOptions(DockerTls tls)
    {
        var options = new SslClientAuthenticationOptions();
        if (tls.ClientCertificatePem is not null)
        {
            if (tls.ClientKeyPem is null)
                throw new ArgumentException("A client certificate needs its private key.", nameof(tls));

            var certificate = LoadCertificateWithKey(tls.ClientCertificatePem, tls.ClientKeyPem);
            options.ClientCertificates = [certificate];
            // Always offer it: Windows servers may send an issuer list that leaves a private CA out,
            // and the default selection would then send no certificate at all.
            options.LocalCertificateSelectionCallback = (_, _, _, _, _) => certificate;
        }

        string?[] pins = [tls.PinnedCertificateSha256, tls.PreviousPinnedCertificateSha256];
        if (tls.CaCertificatePem is not null)
        {
            var ca = X509Certificate2.CreateFromPem(tls.CaCertificatePem);
            options.RemoteCertificateValidationCallback = (_, certificate, _, errors) =>
                ValidateAgainstCustomCa(certificate, errors, ca, pins);
        }
        else if (tls.PinnedCertificateSha256 is not null)
        {
            options.RemoteCertificateValidationCallback = (_, certificate, _, errors) =>
                errors == SslPolicyErrors.None && certificate is not null && MatchesPin(certificate, pins);
        }
        return options;
    }

    /// <summary>A TLS client handshake over <paramref name="inner"/> with <paramref name="tls"/>, checking <paramref name="host"/>.</summary>
    /// <exception cref="System.Security.Authentication.AuthenticationException">The other side isn't the one <paramref name="tls"/> trusts.</exception>
    public static async Task<SslStream> AuthenticateAsync(Stream inner, string host, DockerTls tls, CancellationToken cancellationToken)
    {
        var ssl = new SslStream(inner, leaveInnerStreamOpen: false);
        try
        {
            var options = ClientOptions(tls);
            options.TargetHost = host;
            await ssl.AuthenticateAsClientAsync(options, cancellationToken);
            return ssl;
        }
        catch
        {
            await ssl.DisposeAsync();
            throw;
        }
    }

    /// <summary>
    /// A certificate with its private key, usable by <see cref="SslStream"/> on every OS: Windows
    /// can't use an ephemeral key, so it round-trips through PKCS#12 to persist it.
    /// </summary>
    public static X509Certificate2 LoadCertificateWithKey(string certificatePem, string keyPem)
    {
        using var ephemeral = X509Certificate2.CreateFromPem(certificatePem, keyPem);
        return X509CertificateLoader.LoadPkcs12(ephemeral.Export(X509ContentType.Pkcs12), password: null);
    }

    /// <summary>The certificate's SHA-256 thumbprint as upper-case hex, the form pins are stored in.</summary>
    public static string Thumbprint(X509Certificate certificate) => certificate.GetCertHashString(HashAlgorithmName.SHA256);

    /// <summary>
    /// Trusts only <paramref name="ca"/> for the chain, but still requires the host name to match,
    /// and with <paramref name="pin"/> the exact certificate.
    /// </summary>
    internal static bool ValidateAgainstCustomCa(X509Certificate? certificate, SslPolicyErrors errors, X509Certificate2 ca, params string?[] pins)
    {
        if (certificate is null || errors.HasFlag(SslPolicyErrors.RemoteCertificateNameMismatch) ||
            errors.HasFlag(SslPolicyErrors.RemoteCertificateNotAvailable))
            return false;
        if (!MatchesPin(certificate, pins))
            return false;

        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.Add(ca);
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        return chain.Build(certificate as X509Certificate2 ?? X509CertificateLoader.LoadCertificate(certificate.GetRawCertData()));
    }

    /// <summary>True when no pin is set, or the certificate matches one of them.</summary>
    private static bool MatchesPin(X509Certificate certificate, string?[] pins)
    {
        if (pins.All(p => p is null))
            return true;
        var thumbprint = Thumbprint(certificate);
        return pins.Any(p => p is not null && string.Equals(thumbprint, p, StringComparison.OrdinalIgnoreCase));
    }
}
