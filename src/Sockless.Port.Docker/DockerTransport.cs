using System.IO.Pipes;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;

namespace Sockless.Port.Docker;

/// <summary>Builds the <see cref="HttpMessageHandler"/> that carries HTTP over each kind of address.</summary>
internal static class DockerTransport
{
    public static (HttpMessageHandler Handler, Uri BaseAddress) Create(DockerAddress address) => address switch
    {
        UnixSocketAddress unix => (CreateHandler(async (_, ct) =>
        {
            var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            try
            {
                await socket.ConnectAsync(new UnixDomainSocketEndPoint(unix.Path), ct);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        }), new Uri("http://docker/")),

        NamedPipeAddress pipe => (CreateHandler(async (_, ct) =>
        {
            var stream = new NamedPipeClientStream(".", pipe.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            try
            {
                await stream.ConnectAsync(ct);
                return stream;
            }
            catch
            {
                await stream.DisposeAsync();
                throw;
            }
        }), new Uri("http://docker/")),

        TcpAddress tcp => (CreateTcpHandler(tcp), new Uri(tcp.ToString().Replace("tcp://", "http://", StringComparison.Ordinal) + "/")),
    };

    private static SocketsHttpHandler CreateHandler(Func<SocketsHttpConnectionContext, CancellationToken, ValueTask<Stream>>? connect)
    {
        var handler = new SocketsHttpHandler
        {
            ConnectTimeout = TimeSpan.FromSeconds(10),
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(1),
            AutomaticDecompression = System.Net.DecompressionMethods.None,
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
        if (tcp.Tls is not { } tls)
            return handler;

        if (tls.ClientCertificatePem is not null)
        {
            if (tls.ClientKeyPem is null)
                throw new ArgumentException("A client certificate needs its private key.", nameof(tcp));

            // SslStream on Windows can't use an ephemeral key; round-trip through PKCS#12 to persist it.
            using var ephemeral = X509Certificate2.CreateFromPem(tls.ClientCertificatePem, tls.ClientKeyPem);
            var certificate = X509CertificateLoader.LoadPkcs12(ephemeral.Export(X509ContentType.Pkcs12), password: null);
            handler.SslOptions.ClientCertificates = [certificate];
        }

        if (tls.CaCertificatePem is not null)
        {
            var ca = X509Certificate2.CreateFromPem(tls.CaCertificatePem);
            handler.SslOptions.RemoteCertificateValidationCallback = (_, certificate, _, errors) =>
                ValidateAgainstCustomCa(certificate, errors, ca);
        }

        return handler;
    }

    /// <summary>
    /// Trusts only <paramref name="ca"/> for the chain, but still requires the host name to match.
    /// </summary>
    internal static bool ValidateAgainstCustomCa(X509Certificate? certificate, SslPolicyErrors errors, X509Certificate2 ca)
    {
        if (certificate is null || errors.HasFlag(SslPolicyErrors.RemoteCertificateNameMismatch) ||
            errors.HasFlag(SslPolicyErrors.RemoteCertificateNotAvailable))
            return false;

        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.Add(ca);
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        return chain.Build(certificate as X509Certificate2 ?? X509CertificateLoader.LoadCertificate(certificate.GetRawCertData()));
    }
}
