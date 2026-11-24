using System.Collections.Concurrent;
using System.Globalization;
using System.Net.WebSockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Sockless.Port.Data;
using Sockless.Port.Docker;
using Sockless.Port.Docker.Tunnel;
using Sockless.Port.Engines;

namespace Sockless.Port.Agents;

/// <summary>
/// Edge agents' tunnels. An agent dials <see cref="TunnelProtocol.Path"/> (<see cref="HandleAsync"/>)
/// as its endpoint, or with its enrolment token's id before it's enrolled; the live tunnel per
/// endpoint then carries every connection its <see cref="TunnelAddress"/> opens. Nothing is
/// trusted for being on the tunnel: an enrolled agent's tunnel is only taken once it proves, with
/// a TLS handshake against its pinned certificate, that it holds the agent's key; before
/// enrolment, the enrolment's own proofs decide, as in direct mode.
/// </summary>
public sealed partial class AgentTunnels(
    EngineConnections connections,
    IServiceScopeFactory scopes,
    TimeProvider time,
    ILogger<AgentTunnels> logger,
    IHostApplicationLifetime? lifetime = null)
{
    internal static readonly TimeSpan VerifyTimeout = TimeSpan.FromSeconds(15);

    private readonly ConcurrentDictionary<int, TunnelMux> _tunnels = new();

    /// <summary>Opens streams through the endpoint's tunnel, whichever tunnel is live when a connection is made.</summary>
    public ITunnelConnector Connector(int endpointId) => new TunnelConnector(this, endpointId);

    /// <summary>True while the endpoint's agent has a tunnel up.</summary>
    public bool IsUp(int endpointId) => _tunnels.TryGetValue(endpointId, out var mux) && !mux.IsClosed;

    /// <summary>Closes the endpoint's tunnel, e.g. when it's enrolled again or removed: its certificate is no longer accepted.</summary>
    public void Drop(int endpointId, string reason)
    {
        if (_tunnels.TryGetValue(endpointId, out var mux))
            mux.Close(reason);
    }

    // ---------------------------------------------------------------- accepting

    /// <summary>The WebSocket endpoint agents dial. Anonymous: what's inside the tunnel authenticates.</summary>
    public async Task HandleAsync(HttpContext context)
    {
        if (!context.WebSockets.IsWebSocketRequest || !context.WebSockets.WebSocketRequestedProtocols.Contains(TunnelProtocol.SubProtocol))
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsync("This address is for Sockless Port agents in edge mode.\n");
            return;
        }

        var remote = context.Connection.RemoteIpAddress?.ToString();
        var (connection, enrolling) = Find(context.Request.Query);
        if (connection is null)
        {
            LogUnknown(logger, remote);
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            await context.Response.WriteAsync("No edge endpoint here expects this agent. Run the install command from the endpoint's page.\n");
            return;
        }

        using var socket = await context.WebSockets.AcceptWebSocketAsync(new WebSocketAcceptContext
        {
            SubProtocol = TunnelProtocol.SubProtocol,
            KeepAliveInterval = TunnelProtocol.KeepAliveInterval,
            KeepAliveTimeout = TunnelProtocol.KeepAliveTimeout,
        });
        await ServeAsync(connection, socket, enrolling, remote, context.RequestAborted);
    }

    /// <summary>The endpoint an agent dials as: enrolled (by endpoint id) or enrolling (by token id).</summary>
    private (EngineConnection? Connection, bool Enrolling) Find(IQueryCollection query)
    {
        if (query.TryGetValue(TunnelProtocol.EndpointParameter, out var idText) && idText.Count == 1
            && int.TryParse(idText[0], NumberStyles.None, CultureInfo.InvariantCulture, out var id))
            return connections.Get(id) is { IsEdge: true, Agent.Thumbprint: not null } enrolled ? (enrolled, false) : (null, false);

        if (query.TryGetValue(TunnelProtocol.EnrolmentParameter, out var tokenText) && tokenText.Count == 1 && tokenText[0] is { Length: > 0 and <= 64 } tokenId)
        {
            var now = time.GetUtcNow();
            var given = Encoding.ASCII.GetBytes(tokenId);
            foreach (var candidate in connections.All)
            {
                if (candidate is { IsEdge: true, Enrolment: { } pending } && pending.ExpiresAt > now
                    && CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(pending.Token.Id), given))
                    return (candidate, true);
            }
        }
        return (null, false);
    }

    /// <summary>Runs one accepted tunnel until it closes.</summary>
    internal async Task ServeAsync(EngineConnection connection, WebSocket socket, bool enrolling, string? remote, CancellationToken aborted)
    {
        using var stopping = CancellationTokenSource.CreateLinkedTokenSource(aborted, lifetime?.ApplicationStopping ?? CancellationToken.None);
        var mux = new TunnelMux(socket, opensStreams: true);
        await using var _ = mux;

        if (!enrolling && await VerifyAsync(connection, mux, stopping.Token) is { } problem)
        {
            LogRefused(logger, connection.Name, remote, problem);
            return;
        }

        TunnelMux? replaced = null;
        _tunnels.AddOrUpdate(connection.Id, mux, (_, existing) =>
        {
            replaced = existing;
            return mux;
        });
        replaced?.Close("The agent opened a new tunnel.");

        var now = time.GetUtcNow();
        if (connection.Edge is { } edge)
            connection.SetEdge(edge with { ConnectedSince = now, Remote = remote, SeenAt = now });
        await RecordSeenAsync(connection.Id, now);
        LogUp(logger, connection.Name, remote);
        // Connect (or enrol) through the new tunnel now, rather than at the next retry.
        if (connections.Get(connection.Id) == connection)
            connections.Reconnect(connection);

        try
        {
            await mux.Completion.WaitAsync(stopping.Token);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            if (_tunnels.TryRemove(new KeyValuePair<int, TunnelMux>(connection.Id, mux)))
            {
                var closed = time.GetUtcNow();
                if (connection.Edge is { } current)
                    connection.SetEdge(current with { ConnectedSince = null, SeenAt = closed });
                await RecordSeenAsync(connection.Id, closed);
                LogDown(logger, connection.Name, mux.CloseReason ?? "Sockless Port is shutting down.");
                if (connections.Get(connection.Id) == connection)
                    connections.Touch(connection);
            }
        }
    }

    /// <summary>A TLS handshake through the tunnel against the endpoint's pinned certificate. Null when the agent passed.</summary>
    private static async Task<string?> VerifyAsync(EngineConnection connection, TunnelMux mux, CancellationToken cancellationToken)
    {
        if (connection.Address is not TunnelAddress { Tls: { CaCertificatePem: not null } tls } address)
            return "the endpoint has no certificate to check the agent against";

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(VerifyTimeout);
        try
        {
            var stream = await mux.OpenAsync(timeout.Token);
            await using var ssl = await DockerTransport.AuthenticateAsync(stream, address.Host, tls, timeout.Token);
            return null;
        }
        catch (AuthenticationException)
        {
            return "it didn't present the certificate issued to this endpoint's agent";
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException)
        {
            return $"the TLS handshake through the tunnel didn't finish ({ex.Message})";
        }
    }

    private async Task RecordSeenAsync(int endpointId, DateTimeOffset at)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<PortDbContext>();
            if (await db.Endpoints.FirstOrDefaultAsync(e => e.Id == endpointId) is { } endpoint)
            {
                endpoint.TunnelSeenAt = at;
                await db.SaveChangesAsync();
            }
        }
        catch (Exception ex) when (ex is DbUpdateException or InvalidOperationException or ObjectDisposedException)
        {
            LogRecordFailed(logger, ex, endpointId);
        }
    }

    private string Unavailable(int endpointId) => connections.Get(endpointId)?.Edge switch
    {
        { SeenAt: null } edge => $"The agent hasn't connected yet. Run the install command on its host; the agent dials {edge.ServerUrl}.",
        { } edge => $"The agent's tunnel is down. Check that the agent runs and can reach {edge.ServerUrl}.",
        null => "The agent's tunnel is down.",
    };

    private sealed class TunnelConnector(AgentTunnels owner, int endpointId) : ITunnelConnector
    {
        public async ValueTask<Stream> OpenAsync(CancellationToken cancellationToken)
        {
            if (owner._tunnels.TryGetValue(endpointId, out var mux))
            {
                try
                {
                    return await mux.OpenAsync(cancellationToken);
                }
                catch (IOException) when (mux.IsClosed)
                {
                    // Closed just now: say so as for a missing tunnel.
                }
            }
            throw new TunnelUnavailableException(owner.Unavailable(endpointId));
        }

        public override string ToString() => $"tunnel to endpoint {endpointId}";
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Tunnel of endpoint {Endpoint} up, from {Remote}")]
    private static partial void LogUp(ILogger logger, string endpoint, string? remote);

    [LoggerMessage(Level = LogLevel.Information, Message = "Tunnel of endpoint {Endpoint} down: {Reason}")]
    private static partial void LogDown(ILogger logger, string endpoint, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Refused a tunnel for endpoint {Endpoint} from {Remote}: {Problem}")]
    private static partial void LogRefused(ILogger logger, string endpoint, string? remote, string problem);

    [LoggerMessage(Level = LogLevel.Information, Message = "Refused a tunnel from {Remote}: no edge endpoint expects it")]
    private static partial void LogUnknown(ILogger logger, string? remote);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Couldn't record when the tunnel of endpoint {EndpointId} was up")]
    private static partial void LogRecordFailed(ILogger logger, Exception exception, int endpointId);
}
