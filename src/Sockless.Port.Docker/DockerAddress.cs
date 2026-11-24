using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace Sockless.Port.Docker;

/// <summary>
/// Where an engine API listens. Parsed from and formatted as <c>DOCKER_HOST</c>-style strings:
/// <c>unix:///var/run/docker.sock</c>, <c>npipe:////./pipe/docker_engine</c>, <c>tcp://host:2376</c>.
/// </summary>
public closed record DockerAddress
{
    public const int DefaultTcpPort = 2375;
    public const int DefaultTlsPort = 2376;

    public static DockerAddress Parse(string value) =>
        TryParse(value, out var address, out var error) ? address : throw new FormatException(error);

    public static bool TryParse(string? value, [NotNullWhen(true)] out DockerAddress? address) =>
        TryParse(value, out address, out _);

    private static bool TryParse(string? value, [NotNullWhen(true)] out DockerAddress? address, out string error)
    {
        address = null;
        error = $"'{value}' is not a valid engine address. Use unix:///path, npipe:////./pipe/name or tcp://host:port.";
        if (string.IsNullOrWhiteSpace(value))
            return false;

        value = value.Trim();
        var separator = value.IndexOf("://", StringComparison.Ordinal);
        if (separator <= 0)
            return false;

        var scheme = value[..separator].ToLowerInvariant();
        var rest = value[(separator + 3)..];

        switch (scheme)
        {
            case "unix":
                if (rest.Length < 2 || rest[0] != '/')
                    return false;
                address = new UnixSocketAddress(rest);
                return true;

            case "npipe":
                // npipe:////./pipe/docker_engine (Docker's form) or npipe://./pipe/docker_engine
                var pipe = rest.TrimStart('/');
                const string prefix = "./pipe/";
                if (!pipe.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || pipe.Length == prefix.Length)
                    return false;
                address = new NamedPipeAddress(pipe[prefix.Length..]);
                return true;

            case "tcp" or "http" or "https":
                // An unregistered scheme makes Uri report Port == -1 when no port is given.
                if (!Uri.TryCreate($"docker-tcp://{rest}", UriKind.Absolute, out var uri) || string.IsNullOrEmpty(uri.Host)
                    || uri.AbsolutePath is not ("/" or "") || !string.IsNullOrEmpty(uri.Query))
                    return false;
                var tls = scheme == "https" ? new DockerTls() : null;
                var port = uri.Port > 0 ? uri.Port : tls is null ? DefaultTcpPort : DefaultTlsPort;
                address = new TcpAddress(uri.Host.Trim('[', ']'), port, tls);
                return true;

            default:
                return false;
        }
    }
}

/// <summary>A Unix domain socket, e.g. <c>/var/run/docker.sock</c> or <c>/run/podman/podman.sock</c>.</summary>
public sealed record UnixSocketAddress(string Path) : DockerAddress
{
    public override string ToString() => $"unix://{Path}";
}

/// <summary>A Windows named pipe, e.g. <c>docker_engine</c>.</summary>
public sealed record NamedPipeAddress(string PipeName) : DockerAddress
{
    public override string ToString() => $"npipe:////./pipe/{PipeName}";
}

/// <summary>A TCP endpoint, optionally with TLS (and mutual TLS when a client certificate is set).</summary>
public sealed record TcpAddress(string Host, int Port, DockerTls? Tls = null) : DockerAddress
{
    public override string ToString() =>
        $"{(Tls is null ? "tcp" : "https")}://{(Host.Contains(':', StringComparison.Ordinal) ? $"[{Host}]" : Host)}:{Port.ToString(CultureInfo.InvariantCulture)}";
}

/// <summary>Opens byte streams to an agent that dialled out to this server (edge mode).</summary>
public interface ITunnelConnector
{
    /// <exception cref="Tunnel.TunnelUnavailableException">The agent's tunnel isn't up.</exception>
    ValueTask<Stream> OpenAsync(CancellationToken cancellationToken);
}

/// <summary>
/// An agent in edge mode, reached through the tunnel it dialled to the server. Each connection is a
/// new stream from <paramref name="Connector"/>, with TLS on top as for a <see cref="TcpAddress"/>.
/// Not parsed from text: the server builds it for the endpoint.
/// </summary>
/// <param name="Host">The name the agent's certificate carries, checked like a host name.</param>
public sealed record TunnelAddress(string Host, ITunnelConnector Connector, DockerTls? Tls = null) : DockerAddress
{
    public override string ToString() => $"tunnel://{Host}";
}
