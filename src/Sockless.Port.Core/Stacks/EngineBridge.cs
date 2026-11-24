using System.Net;
using System.Net.Sockets;
using Sockless.Port.Docker;

namespace Sockless.Port.Stacks;

/// <summary>
/// Lets the Compose and Docker CLIs reach an engine they can't dial themselves: an edge agent,
/// behind its tunnel. The bridge listens on a socket in the run's private folder (only this
/// server's user can open it) and passes each connection on through
/// <see cref="DockerTransport.ConnectRawAsync"/>, which does the mutual TLS with the agent, so the
/// CLI speaks plain HTTP to the socket. Windows has no such folder permissions for the CLI's
/// socket support, so there it's a loopback port, for development only.
/// </summary>
internal sealed class EngineBridge : IAsyncDisposable
{
    private readonly Socket _listener;
    private readonly DockerAddress _engine;
    private readonly CancellationTokenSource _stopping = new();
    private readonly Task _accepting;
    private readonly string? _socketPath;

    private EngineBridge(Socket listener, DockerAddress engine, DockerAddress address, string? socketPath)
    {
        _listener = listener;
        _engine = engine;
        _socketPath = socketPath;
        Address = address;
        _accepting = Task.Run(AcceptAsync);
    }

    /// <summary>Where the CLI connects instead of the engine.</summary>
    public DockerAddress Address { get; }

    /// <summary>A bridge for engines the CLI can't reach itself (tunnels), else null: the CLI uses the engine's own address.</summary>
    public static EngineBridge? StartIfNeeded(DockerAddress? engine, string runDirectory) =>
        engine is TunnelAddress ? Start(engine, runDirectory) : null;

    public static EngineBridge Start(DockerAddress engine, string runDirectory)
    {
        if (OperatingSystem.IsWindows())
        {
            var tcp = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            tcp.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            tcp.Listen(16);
            var port = ((IPEndPoint)tcp.LocalEndPoint!).Port;
            return new EngineBridge(tcp, engine, new TcpAddress("127.0.0.1", port), null);
        }

        var path = Path.Combine(runDirectory, "engine.sock");
        var unix = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        unix.Bind(new UnixDomainSocketEndPoint(path));
        unix.Listen(16);
        return new EngineBridge(unix, engine, new UnixSocketAddress(path), path);
    }

    private async Task AcceptAsync()
    {
        while (!_stopping.IsCancellationRequested)
        {
            Socket client;
            try
            {
                client = await _listener.AcceptAsync(_stopping.Token);
            }
            catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException)
            {
                return;
            }
            _ = ForwardAsync(client);
        }
    }

    private async Task ForwardAsync(Socket client)
    {
        using var _ = client;
        await using var local = new NetworkStream(client, ownsSocket: false);
        Stream engine;
        try
        {
            engine = await DockerTransport.ConnectRawAsync(_engine, _stopping.Token);
        }
        catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or System.Security.Authentication.AuthenticationException)
        {
            // The CLI sees the connection close and reports it; tell it why first.
            await RefuseAsync(local, ex.Message);
            return;
        }

        await using (engine)
        {
            using var done = CancellationTokenSource.CreateLinkedTokenSource(_stopping.Token);
            var up = CopyAsync(local, engine, done.Token);
            var down = CopyAsync(engine, local, done.Token);
            var first = await Task.WhenAny(up, down);
            if (first == down)
            {
                // The engine finished: let the CLI read to the end.
                try { client.Shutdown(SocketShutdown.Send); } catch (Exception ex) when (ex is SocketException or ObjectDisposedException) { }
                await Task.WhenAny(up, Task.Delay(TimeSpan.FromSeconds(5), CancellationToken.None));
            }
            await done.CancelAsync();
            await Task.WhenAll(up, down);
        }
    }

    private static async Task CopyAsync(Stream from, Stream to, CancellationToken cancellationToken)
    {
        try
        {
            await from.CopyToAsync(to, cancellationToken);
        }
        catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException)
        {
        }
    }

    private static async Task RefuseAsync(Stream local, string message)
    {
        var body = System.Text.Json.JsonSerializer.Serialize(new Docker.Agent.AgentError { Message = message }, Docker.Agent.AgentJsonContext.Default.AgentError);
        var response = $"HTTP/1.1 502 Bad Gateway\r\nContent-Type: application/json\r\nContent-Length: {System.Text.Encoding.UTF8.GetByteCount(body)}\r\nConnection: close\r\n\r\n{body}";
        try
        {
            await local.WriteAsync(System.Text.Encoding.UTF8.GetBytes(response));
        }
        catch (Exception ex) when (ex is IOException or SocketException)
        {
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stopping.CancelAsync();
        _listener.Dispose();
        await _accepting;
        if (_socketPath is not null)
        {
            try
            {
                File.Delete(_socketPath);
            }
            catch (IOException)
            {
            }
        }
        _stopping.Dispose();
    }
}
