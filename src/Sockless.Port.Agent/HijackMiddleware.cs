using System.Buffers;
using System.IO.Pipelines;
using System.Net.Sockets;
using System.Text;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Logging;
using Sockless.Port.Docker;

namespace Sockless.Port.Agent;

/// <summary>
/// Relays hijacked connections (exec and attach with <c>Upgrade: tcp</c>) below HTTP. Kestrel
/// won't upgrade a request that has a body, and the engine's exec start always sends one, so a
/// connection whose first request asks for an upgrade is passed to the engine byte for byte and
/// never reaches Kestrel's HTTP layer. Clients open a fresh connection for each hijack (as
/// <see cref="DockerClient"/> does), since the connection is the engine's afterwards anyway.
/// </summary>
public sealed partial class HijackMiddleware(AgentIdentity identity, AgentOptions options, ILogger<HijackMiddleware> logger)
{
    private const int MaxHeadBytes = 64 * 1024;
    private static readonly TimeSpan HeadTimeout = TimeSpan.FromSeconds(30);
    private static readonly byte[] HeadEnd = "\r\n\r\n"u8.ToArray();

    public async Task InvokeAsync(ConnectionContext connection, ConnectionDelegate next)
    {
        var input = connection.Transport.Input;
        string? head = null;
        // Kestrel's own header timeout only starts once it has the connection, so bound the wait here.
        using var headTimeout = CancellationTokenSource.CreateLinkedTokenSource(connection.ConnectionClosed);
        headTimeout.CancelAfter(HeadTimeout);
        while (true)
        {
            ReadResult result;
            try
            {
                result = await input.ReadAsync(headTimeout.Token);
            }
            catch (OperationCanceledException)
            {
                connection.Abort();
                return;
            }
            var buffer = result.Buffer;
            var end = Find(buffer, HeadEnd);
            if (end is { } position)
            {
                head = Encoding.ASCII.GetString(buffer.Slice(0, position));
                // Leave everything in the pipe: either Kestrel or the relay reads it from the start.
                input.AdvanceTo(buffer.Start);
                break;
            }
            if (result.IsCompleted || buffer.Length > MaxHeadBytes)
            {
                input.AdvanceTo(buffer.Start);
                break;
            }
            input.AdvanceTo(buffer.Start, buffer.End);
        }

        if (head is null || !IsUpgrade(head))
        {
            await next(connection);
            return;
        }

        var certificate = connection.Features.Get<ITlsConnectionFeature>()?.ClientCertificate;
        if (!identity.IsEnrolled || !identity.IsTrustedServer(certificate))
        {
            await RefuseAsync(connection, identity.IsEnrolled
                ? "Only the Sockless Port server this agent is enrolled with can use it."
                : "This agent isn't enrolled yet. Enrol it from Sockless Port.");
            return;
        }

        Stream engine;
        try
        {
            engine = await DockerTransport.ConnectRawAsync(options.Engine, connection.ConnectionClosed);
        }
        catch (Exception ex) when (ex is SocketException or IOException)
        {
            LogEngineUnreachable(logger, options.Engine.ToString(), ex.Message);
            await RefuseAsync(connection, $"The agent can't reach the engine at {options.Engine}: {ex.Message}", "502 Bad Gateway");
            return;
        }

        await using (engine)
        {
            var client = connection.Transport;
            var toEngine = Task.Run(async () =>
            {
                try
                {
                    await client.Input.CopyToAsync(engine, connection.ConnectionClosed);
                    // The server closed its side: the engine sees end of input.
                    if (engine is NetworkStream network)
                        network.Socket.Shutdown(SocketShutdown.Send);
                }
                catch (Exception ex) when (ex is OperationCanceledException or IOException or SocketException or ObjectDisposedException)
                {
                }
            });
            try
            {
                await engine.CopyToAsync(client.Output, connection.ConnectionClosed);
            }
            catch (Exception ex) when (ex is OperationCanceledException or IOException or SocketException)
            {
            }
            await client.Output.CompleteAsync();
            connection.Abort();
            await toEngine;
        }
    }

    /// <summary>An HTTP/1.1 request head asking for a connection upgrade (<c>Connection: Upgrade</c> and an <c>Upgrade</c> header).</summary>
    internal static bool IsUpgrade(string head)
    {
        var lines = head.Split("\r\n");
        if (lines.Length == 0 || !lines[0].EndsWith(" HTTP/1.1", StringComparison.Ordinal))
            return false;
        bool connectionUpgrade = false, upgrade = false;
        foreach (var line in lines.Skip(1))
        {
            var colon = line.IndexOf(':', StringComparison.Ordinal);
            if (colon <= 0)
                continue;
            var name = line[..colon].Trim();
            var value = line[(colon + 1)..].Trim();
            if (name.Equals("Connection", StringComparison.OrdinalIgnoreCase))
                connectionUpgrade |= value.Split(',').Any(v => v.Trim().Equals("upgrade", StringComparison.OrdinalIgnoreCase));
            else if (name.Equals("Upgrade", StringComparison.OrdinalIgnoreCase))
                upgrade |= value.Length > 0;
        }
        return connectionUpgrade && upgrade;
    }

    private static async Task RefuseAsync(ConnectionContext connection, string message, string status = "403 Forbidden")
    {
        var body = System.Text.Json.JsonSerializer.Serialize(new Docker.Agent.AgentError { Message = message }, Docker.Agent.AgentJsonContext.Default.AgentError);
        var response = $"HTTP/1.1 {status}\r\nContent-Type: application/json\r\nContent-Length: {Encoding.UTF8.GetByteCount(body)}\r\nConnection: close\r\n\r\n{body}";
        await connection.Transport.Output.WriteAsync(Encoding.UTF8.GetBytes(response));
        await connection.Transport.Output.CompleteAsync();
        connection.Abort();
    }

    private static long? Find(ReadOnlySequence<byte> buffer, byte[] value)
    {
        var reader = new SequenceReader<byte>(buffer);
        return reader.TryReadTo(out ReadOnlySequence<byte> before, value, advancePastDelimiter: false) ? before.Length : null;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Can't reach the engine at {Engine}: {Reason}")]
    private static partial void LogEngineUnreachable(ILogger logger, string engine, string reason);
}
