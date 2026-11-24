using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Sockless.Port.Docker.Tunnel;

namespace Sockless.Port.Agent;

/// <summary>
/// Edge mode: keeps a tunnel open to the Sockless Port server, reconnecting with backoff, and
/// passes every stream the server opens to the agent's own listener on loopback. So each stream
/// meets the same TLS, client-certificate check, relay and hijack handling as a direct
/// connection, and the tunnel adds nothing to trust.
/// </summary>
public sealed partial class TunnelClient(AgentOptions options, AgentIdentity identity, IServer server, ILogger<TunnelClient> logger) : BackgroundService
{
    internal static readonly TimeSpan InitialDelay = TimeSpan.FromSeconds(1);
    internal static readonly TimeSpan MaxDelay = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(30);

    private volatile bool _connected;

    /// <summary>True while the tunnel is up.</summary>
    public bool IsConnected => _connected;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (options.Server is not { } serverAddress)
            return;
        await Task.Yield();

        var delay = InitialDelay;
        while (!stoppingToken.IsCancellationRequested)
        {
            if (Target(serverAddress) is not { } target)
            {
                LogNothingToDial(logger);
                return;
            }

            string reason;
            var wasUp = false;
            using var socket = new ClientWebSocket();
            try
            {
                socket.Options.AddSubProtocol(TunnelProtocol.SubProtocol);
                socket.Options.KeepAliveInterval = TunnelProtocol.KeepAliveInterval;
                socket.Options.KeepAliveTimeout = TunnelProtocol.KeepAliveTimeout;
                socket.Options.CollectHttpResponseDetails = true;
                socket.Options.SetRequestHeader("User-Agent", $"sockless-port-agent/{AgentApp.Version}");
                // The outer TLS is only transport: each stream inside runs mutual TLS with the
                // server's certificate from enrolment, so a proxy's or the UI's own certificate needn't be trusted here.
                socket.Options.RemoteCertificateValidationCallback = (_, _, _, _) => true;

                using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken))
                {
                    timeout.CancelAfter(ConnectTimeout);
                    await socket.ConnectAsync(target, timeout.Token);
                }

                wasUp = true;
                _connected = true;
                delay = InitialDelay;
                LogConnected(logger, Redact(target));
                await using var mux = new TunnelMux(socket, opensStreams: false);
                await ServeAsync(mux, stoppingToken);
                reason = mux.CloseReason ?? "The tunnel closed.";
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex) when (ex is WebSocketException or HttpRequestException or IOException or OperationCanceledException or SocketException)
            {
                reason = Describe(ex, socket.HttpStatusCode);
            }
            finally
            {
                _connected = false;
            }

            if (wasUp)
                LogDisconnected(logger, reason);
            else
                LogCantConnect(logger, Redact(target), reason, delay.TotalSeconds);

            try
            {
                // Some jitter, so many agents behind one outage don't come back in step.
                await Task.Delay(delay + TimeSpan.FromMilliseconds(Random.Shared.Next(0, 1000)), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            delay = TimeSpan.FromTicks(Math.Min(delay.Ticks * 2, MaxDelay.Ticks));
        }
    }

    /// <summary>The tunnel address: as the enrolled endpoint, or with the enrolment token's id before enrolment.</summary>
    internal Uri? Target(Uri serverAddress)
    {
        if (identity.EndpointId is { } id)
            return TunnelProtocol.Address(serverAddress, TunnelProtocol.EndpointParameter, id.ToString(System.Globalization.CultureInfo.InvariantCulture));
        if (!identity.IsEnrolled && options.Token is { } token)
            return TunnelProtocol.Address(serverAddress, TunnelProtocol.EnrolmentParameter, token.Id);
        return null;
    }

    private async Task ServeAsync(TunnelMux mux, CancellationToken stoppingToken)
    {
        while (await mux.AcceptAsync(stoppingToken) is { } stream)
            _ = SpliceAsync(stream, stoppingToken);
    }

    /// <summary>Joins one tunnel stream to a fresh loopback connection to this agent's listener.</summary>
    private async Task SpliceAsync(TunnelStream stream, CancellationToken stoppingToken)
    {
        await using (stream)
        {
            using var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(new IPEndPoint(IPAddress.Loopback, ListenPort()), stoppingToken);
            }
            catch (Exception ex) when (ex is SocketException or OperationCanceledException or InvalidOperationException)
            {
                LogLocalFailed(logger, ex.Message);
                return;
            }

            await using var local = new NetworkStream(socket, ownsSocket: false);
            var up = Task.Run(async () =>
            {
                try
                {
                    await stream.CopyToAsync(local, stoppingToken);
                }
                catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException or SocketException)
                {
                }
                // The server finished sending: Kestrel sees the end of the connection.
                try { socket.Shutdown(SocketShutdown.Send); } catch (Exception ex) when (ex is SocketException or ObjectDisposedException) { }
            }, CancellationToken.None);

            try
            {
                await local.CopyToAsync(stream, stoppingToken);
                await stream.ShutdownWriteAsync();
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException or SocketException)
            {
            }
            // Kestrel closed its side: give the server's last bytes a moment, then end both.
            await Task.WhenAny(up, Task.Delay(TimeSpan.FromSeconds(5), CancellationToken.None));
        }
    }

    private int ListenPort()
    {
        if (options.Port != 0)
            return options.Port;
        // Port 0 (tests): the one Kestrel picked.
        var address = server.Features.Get<IServerAddressesFeature>()?.Addresses.FirstOrDefault()
                      ?? throw new InvalidOperationException("The agent isn't listening yet.");
        return new Uri(address.Replace("://+", "://localhost", StringComparison.Ordinal).Replace("://*", "://localhost", StringComparison.Ordinal)).Port;
    }

    private static string Describe(Exception ex, HttpStatusCode status) => status switch
    {
        HttpStatusCode.NotFound => "The server doesn't expect this agent: the endpoint was removed or enrolled again, or the enrolment command expired. Run the command from the endpoint's page.",
        HttpStatusCode.TooManyRequests => "The server is limiting how often agents connect from this address.",
        HttpStatusCode.BadRequest => "The address isn't a Sockless Port server that accepts edge agents (or a proxy in between doesn't pass WebSockets).",
        not 0 => $"The server answered {(int)status} instead of opening the tunnel.",
        _ => ex.InnerException is { } inner && ex is WebSocketException ? $"{ex.Message} {inner.Message}" : ex.Message,
    };

    /// <summary>The address without the enrolment id, which only the install command should carry.</summary>
    private static string Redact(Uri target) => target.GetLeftPart(UriPartial.Path);

    [LoggerMessage(Level = LogLevel.Information, Message = "Tunnel to Sockless Port open ({Server})")]
    private static partial void LogConnected(ILogger logger, string server);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Tunnel to Sockless Port closed: {Reason}")]
    private static partial void LogDisconnected(ILogger logger, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Can't open the tunnel to {Server}: {Reason} Trying again in {Seconds} s")]
    private static partial void LogCantConnect(ILogger logger, string server, string reason, double seconds);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Not enrolled and started without AGENT_TOKEN, so there's nothing to dial the server as. Run the command from the endpoint's page.")]
    private static partial void LogNothingToDial(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Can't pass a tunnel stream to the agent's own listener: {Reason}")]
    private static partial void LogLocalFailed(ILogger logger, string reason);
}
