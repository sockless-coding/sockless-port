using System.Collections.Concurrent;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Sockless.Port.Docker;
using Sockless.Port.Docker.Engine;
using Sockless.Port.Docker.Models;

namespace Sockless.Port.Engines;

/// <summary>
/// Keeps every endpoint connected: connects, detects the engine, streams its events to the
/// <see cref="IEngineEventSink"/>, and reconnects with backoff when the engine goes away.
/// </summary>
public sealed partial class EngineMonitor(
    EngineConnections connections,
    IEngineEventSink sink,
    TimeProvider time,
    ILogger<EngineMonitor> logger) : BackgroundService
{
    internal static readonly TimeSpan InitialRetryDelay = TimeSpan.FromSeconds(1);
    internal static readonly TimeSpan MaxRetryDelay = TimeSpan.FromSeconds(30);
    internal static readonly TimeSpan WatchdogInterval = TimeSpan.FromSeconds(30);

    /// <summary>Event types forwarded to the UI. Everything else (plugins, daemon reloads) is ignored.</summary>
    internal static readonly IReadOnlyDictionary<string, string[]> EventFilters = new Dictionary<string, string[]>
    {
        ["type"] = ["container", "image", "volume", "network", "service", "node", "secret", "config"],
    };

    private readonly ConcurrentDictionary<int, Task> _loops = new();
    private CancellationToken _stopping;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _stopping = stoppingToken;
        connections.Added += Watch;
        foreach (var connection in connections.All)
            Watch(connection);

        try
        {
            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        catch (OperationCanceledException)
        {
        }
        await Task.WhenAll(_loops.Values);
    }

    public override Task StopAsync(CancellationToken cancellationToken)
    {
        connections.Added -= Watch;
        return base.StopAsync(cancellationToken);
    }

    private void Watch(EngineConnection connection) =>
        _loops.GetOrAdd(connection.Id, _ => Task.Run(() => RunAsync(connection, _stopping), CancellationToken.None));

    private async Task RunAsync(EngineConnection connection, CancellationToken stopping)
    {
        var delay = InitialRetryDelay;
        while (!stopping.IsCancellationRequested)
        {
            if (connection.State is not EndpointConnecting)
            {
                connection.SetConnecting(time.GetUtcNow());
                await NotifyStateAsync(connection, stopping);
            }

            string reason;
            DockerClient? client = null;
            try
            {
                client = await DockerClient.ConnectAsync(connection.Address, cancellationToken: stopping);
                var info = await client.GetInfoAsync(stopping);
                var engine = EngineDescriptor.Detect(client.ServerVersion, info, client.ApiVersion);

                connection.SetOnline(client, engine, time.GetUtcNow());
                LogOnline(logger, connection.Name, engine.DisplayName, engine.ApiVersion.ToString());
                await NotifyStateAsync(connection, stopping);
                delay = InitialRetryDelay;

                await StreamEventsAsync(connection, client, stopping);
                reason = $"The engine at {connection.Address} closed the connection.";
            }
            catch (OperationCanceledException) when (stopping.IsCancellationRequested)
            {
                connection.SetOffline("Sockless Port is shutting down.", time.GetUtcNow(), DateTimeOffset.MaxValue);
                client?.Dispose();
                break;
            }
            catch (Exception ex)
            {
                reason = EngineErrors.Describe(ex, connection.Address);
            }

            var now = time.GetUtcNow();
            // Take the client out of the connection before disposing it, so pages stop using it.
            connection.SetOffline(reason, now, now + delay);
            client?.Dispose();
            LogOffline(logger, connection.Name, reason);
            await NotifyStateAsync(connection, stopping);

            try
            {
                await Task.Delay(delay, time, stopping);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            delay = TimeSpan.FromTicks(Math.Min(delay.Ticks * 2, MaxRetryDelay.Ticks));
        }
    }

    /// <summary>
    /// Forwards events until the stream ends. A watchdog pings the engine so a connection that
    /// died silently (no FIN, e.g. a dropped TCP link) is noticed within <see cref="WatchdogInterval"/>.
    /// </summary>
    private async Task StreamEventsAsync(EngineConnection connection, DockerClient client, CancellationToken stopping)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(stopping);
        var watchdog = WatchdogAsync(client, linked, stopping);
        try
        {
            await foreach (var engineEvent in client.MonitorEventsAsync(EventFilters, linked.Token))
            {
                if (IsRelevant(engineEvent))
                    await NotifyEventAsync(connection, engineEvent, stopping);
            }
        }
        catch (OperationCanceledException) when (!stopping.IsCancellationRequested && watchdog.IsCompleted)
        {
            // The watchdog cancelled the stream: surface its failure as the reason.
            await watchdog;
        }
        finally
        {
            await linked.CancelAsync();
            try { await watchdog; } catch (Exception) when (!stopping.IsCancellationRequested) { }
        }
    }

    private async Task WatchdogAsync(DockerClient client, CancellationTokenSource events, CancellationToken stopping)
    {
        while (!events.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(WatchdogInterval, time, events.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            try
            {
                await client.GetVersionAsync(events.Token);
            }
            catch (Exception) when (!events.IsCancellationRequested)
            {
                await events.CancelAsync();
                throw;
            }
        }
    }

    /// <summary>Drops chatty events that never change what the UI shows.</summary>
    internal static bool IsRelevant(DockerEvent e) =>
        e.Action is { } action
        && !action.StartsWith("exec_", StringComparison.Ordinal)
        && !action.StartsWith("health_status", StringComparison.Ordinal)
        && action is not ("attach" or "detach" or "resize" or "top" or "copy" or "archive-path" or "export" or "commit");

    private async Task NotifyStateAsync(EngineConnection connection, CancellationToken cancellationToken)
    {
        try
        {
            await sink.StateChangedAsync(connection, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogSinkFailed(logger, ex);
        }
    }

    private async Task NotifyEventAsync(EngineConnection connection, DockerEvent engineEvent, CancellationToken cancellationToken)
    {
        try
        {
            await sink.EngineEventAsync(connection, engineEvent, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogSinkFailed(logger, ex);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Endpoint {Endpoint} online: {Engine}, API {ApiVersion}")]
    private static partial void LogOnline(ILogger logger, string endpoint, string engine, string apiVersion);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Endpoint {Endpoint} offline: {Reason}")]
    private static partial void LogOffline(ILogger logger, string endpoint, string reason);

    [LoggerMessage(Level = LogLevel.Error, Message = "Forwarding an engine notification failed")]
    private static partial void LogSinkFailed(ILogger logger, Exception exception);
}
