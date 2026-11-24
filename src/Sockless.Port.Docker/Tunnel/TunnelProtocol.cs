namespace Sockless.Port.Docker.Tunnel;

/// <summary>
/// Edge mode: the agent dials out to the server over a WebSocket and the server opens streams
/// through it (<see cref="TunnelMux"/>). Each stream carries what a TCP connection to the agent
/// would carry in direct mode, mutual TLS included, so the tunnel itself needs no trust: a reverse
/// proxy, a plain-HTTP UI or an impostor on either end sees only TLS records.
/// </summary>
public static class TunnelProtocol
{
    /// <summary>The server's WebSocket address, below the server's base URL.</summary>
    public const string Path = "/agent/tunnel";

    public const string SubProtocol = "sockless-port.tunnel.v1";

    /// <summary>Query parameter of an enrolled agent: its endpoint id, which its certificate proves.</summary>
    public const string EndpointParameter = "endpoint";

    /// <summary>Query parameter of an agent waiting for enrolment: its token's public id.</summary>
    public const string EnrolmentParameter = "enrolment";

    /// <summary>Both sides ping this often, so idle tunnels survive proxies and NAT timeouts.</summary>
    public static readonly TimeSpan KeepAliveInterval = TimeSpan.FromSeconds(15);

    /// <summary>A tunnel whose ping isn't answered within this long is dead.</summary>
    public static readonly TimeSpan KeepAliveTimeout = TimeSpan.FromSeconds(30);

    /// <summary>The tunnel address under <paramref name="server"/> (http becomes ws, https wss), keeping any path the server runs under.</summary>
    public static Uri Address(Uri server, string parameter, string value)
    {
        var builder = new UriBuilder(server)
        {
            Scheme = server.Scheme == Uri.UriSchemeHttp ? "ws" : "wss",
            Path = server.AbsolutePath.TrimEnd('/') + Path,
            Query = $"{parameter}={Uri.EscapeDataString(value)}",
            Fragment = "",
        };
        if (server.IsDefaultPort)
            builder.Port = -1;
        return builder.Uri;
    }
}

/// <summary>An edge agent's tunnel isn't up. The message says what to check.</summary>
public sealed class TunnelUnavailableException(string message) : IOException(message);
