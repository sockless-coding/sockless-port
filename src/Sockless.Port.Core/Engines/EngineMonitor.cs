using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Sockless.Port.Docker;
using Sockless.Port.Docker.Engine;
using Sockless.Port.Docker.Models;

namespace Sockless.Port.Engines;

/// <summary>
/// Keeps every endpoint connected: enrols agents that are waiting for it, connects, detects the
/// engine, streams its events to the <see cref="IEngineEventSink"/>, and reconnects with backoff
/// when the engine goes away.
/// </summary>
public sealed partial class EngineMonitor(
    EngineConnections connections,
    IEngineEventSink sink,
    TimeProvider time,
    ILogger<EngineMonitor> logger,
    IAgentEnroller? enroller = null,
    Notifications.Alerts? alerts = null) : BackgroundService
{
    internal static readonly TimeSpan InitialRetryDelay = TimeSpan.FromSeconds(1);
    internal static readonly TimeSpan MaxRetryDelay = TimeSpan.FromSeconds(30);
    internal static readonly TimeSpan MaxEnrolmentRetryDelay = TimeSpan.FromSeconds(10);
    internal static readonly TimeSpan WatchdogInterval = TimeSpan.FromSeconds(30);

    /// <summary>Event types forwarded to the UI. Everything else (plugins, daemon reloads) is ignored.</summary>
    internal static readonly IReadOnlyDictionary<string, string[]> EventFilters = new Dictionary<string, string[]>
    {
        ["type"] = ["container", "image", "volume", "network", "service", "node", "secret", "config"],
    };

    private sealed record Loop(CancellationTokenSource Cancel, Task Task);

    private readonly Lock _loopGate = new();
    private readonly Dictionary<int, Loop> _loops = [];
    private CancellationToken _stopping;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _stopping = stoppingToken;
        connections.Added += Start;
        connections.Changed += Start;
        connections.Removed += Stop;
        connections.Updated += Announce;
        foreach (var connection in connections.All)
            Start(connection);

        try
        {
            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        catch (OperationCanceledException)
        {
        }

        Task[] running;
        lock (_loopGate)
            running = [.. _loops.Values.Select(l => l.Task)];
        await Task.WhenAll(running);
    }

    public override Task StopAsync(CancellationToken cancellationToken)
    {
        connections.Added -= Start;
        connections.Changed -= Start;
        connections.Removed -= Stop;
        connections.Updated -= Announce;
        return base.StopAsync(cancellationToken);
    }

    /// <summary>Starts watching, or restarts after the previous loop for the endpoint has ended.</summary>
    private void Start(EngineConnection connection)
    {
        lock (_loopGate)
        {
            if (_stopping.IsCancellationRequested)
                return;
            var previous = Task.CompletedTask;
            if (_loops.Remove(connection.Id, out var old))
            {
                old.Cancel.Cancel();
                previous = old.Task;
            }
            var cancel = CancellationTokenSource.CreateLinkedTokenSource(_stopping);
            var task = Task.Run(async () =>
            {
                await previous;
                await RunAsync(connection, cancel.Token);
            }, CancellationToken.None);
            _loops[connection.Id] = new Loop(cancel, task);
        }
    }

    private void Stop(EngineConnection connection)
    {
        lock (_loopGate)
        {
            if (_loops.Remove(connection.Id, out var loop))
                loop.Cancel.Cancel();
        }
        // Lists showing the endpoint refresh and leave it out.
        Announce(connection);
    }

    private void Announce(EngineConnection connection) => _ = NotifyStateAsync(connection, _stopping);

    private async Task RunAsync(EngineConnection connection, CancellationToken stopping)
    {
        var delay = InitialRetryDelay;
        var enrolDelay = InitialRetryDelay;
        while (!stopping.IsCancellationRequested)
        {
            DockerClient? client = null;
            string reason;
            try
            {
                if (connection.IsAgent && !await PrepareAgentAsync(connection, enrolDelay, stopping))
                {
                    enrolDelay = TimeSpan.FromTicks(Math.Min(enrolDelay.Ticks * 2, MaxEnrolmentRetryDelay.Ticks));
                    continue;
                }

                if (connection.State is not EndpointConnecting)
                {
                    connection.SetConnecting(time.GetUtcNow());
                    await NotifyStateAsync(connection, stopping);
                }

                client = await DockerClient.ConnectAsync(connection.Address, cancellationToken: stopping);
                var info = await client.GetInfoAsync(stopping);
                var engine = EngineDescriptor.Detect(client.ServerVersion, info, client.ApiVersion);
                if (connection.IsAgent)
                    await ReadAgentAsync(connection, client, stopping);

                connection.SetOnline(client, engine, time.GetUtcNow());
                LogOnline(logger, connection.Name, engine.DisplayName, engine.ApiVersion.ToString());
                await NotifyStateAsync(connection, stopping);
                delay = InitialRetryDelay;
                enrolDelay = InitialRetryDelay;

                await StreamEventsAsync(connection, client, stopping);
                reason = $"The engine at {connection.Address} closed the connection.";
            }
            catch (OperationCanceledException) when (stopping.IsCancellationRequested)
            {
                // Removed or restarted: the next loop (if any) sets its own state.
                if (_stopping.IsCancellationRequested)
                    connection.SetOffline("Sockless Port is shutting down.", time.GetUtcNow(), DateTimeOffset.MaxValue);
                client?.Dispose();
                break;
            }
            catch (Exception ex)
            {
                reason = ex is NotAnAgentException ? ex.Message : EngineErrors.Describe(ex, connection.Address);
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
    /// Enrols the agent if an enrolment is pending. Returns true when there's a certificate to
    /// connect with; otherwise it has waited <paramref name="retryDelay"/> (or for a new enrolment).
    /// </summary>
    private async Task<bool> PrepareAgentAsync(EngineConnection connection, TimeSpan retryDelay, CancellationToken stopping)
    {
        var hasCertificate = connection.Agent?.Thumbprint is not null;
        var now = time.GetUtcNow();
        string? problem = null;

        if (connection.Enrolment is { } pending)
        {
            if (now >= pending.ExpiresAt)
            {
                if (hasCertificate)
                    return true;
                await WaitForChangeAsync(connection, "The enrolment command expired before the agent was enrolled. Create a new one on the endpoint's page.", stopping);
                return false;
            }
            if (enroller is null)
                throw new InvalidOperationException("Agent endpoints need an enroller.");

            switch (await enroller.EnrolAsync(connection, stopping))
            {
                case EnrolmentIssued:
                    return true;
                case EnrolmentAlreadyDone when connection.Agent?.Thumbprint is not null:
                    return true;
                case EnrolmentAlreadyDone done:
                    problem = done.Message;
                    break;
                case EnrolmentFailed failed:
                    problem = failed.Problem;
                    break;
            }

            var changed = connection.State is not EndpointEnrolling { } current || current.Problem != problem;
            connection.SetEnrolling(now, pending.ExpiresAt, problem);
            if (changed)
                await NotifyStateAsync(connection, stopping);
            await Task.Delay(retryDelay, time, stopping);
            return false;
        }

        if (hasCertificate)
            return true;
        await WaitForChangeAsync(connection, "This agent endpoint has no certificate. Create an enrolment command on the endpoint's page.", stopping);
        return false;
    }

    /// <summary>Shows <paramref name="reason"/> and waits until the endpoint changes (which restarts this loop).</summary>
    private async Task WaitForChangeAsync(EngineConnection connection, string reason, CancellationToken stopping)
    {
        connection.SetOffline(reason, time.GetUtcNow(), DateTimeOffset.MaxValue)?.Dispose();
        await NotifyStateAsync(connection, stopping);
        await Task.Delay(Timeout.InfiniteTimeSpan, time, stopping);
    }

    /// <summary>Reads what the agent reports about itself, and finishes a pending enrolment.</summary>
    private async Task ReadAgentAsync(EngineConnection connection, DockerClient client, CancellationToken stopping)
    {
        try
        {
            connection.SetAgentInfo(await client.GetAgentInfoAsync(stopping));
        }
        catch (DockerApiException ex) when (ex.IsNotFound)
        {
            throw new NotAnAgentException($"The engine at {connection.Address} answered, but it isn't a Sockless Port agent.");
        }
        if (connection.Enrolment is not null && enroller is not null)
            await enroller.CompleteAsync(connection, stopping);
    }

    private sealed class NotAnAgentException(string message) : Exception(message);

    /// <summary>
    /// Forwards events until the stream ends. A watchdog pings the engine so a connection that
    /// died silently (no FIN, e.g. a dropped TCP link) is noticed within <see cref="WatchdogInterval"/>.
    /// </summary>
    private async Task StreamEventsAsync(EngineConnection connection, DockerClient client, CancellationToken stopping)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(stopping);
        var watchdog = WatchdogAsync(connection, client, linked, stopping);
        try
        {
            await foreach (var engineEvent in client.MonitorEventsAsync(EventFilters, linked.Token))
            {
                // Alerts want health and stop events too, which the UI ignores.
                await AlertAsync(a => a.EngineEventAsync(connection, engineEvent, stopping));
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

    private async Task WatchdogAsync(EngineConnection connection, DockerClient client, CancellationTokenSource events, CancellationToken stopping)
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
                if (connection.IsAgent)
                    connection.SetAgentInfo(await client.GetAgentInfoAsync(events.Token));
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

    private async Task AlertAsync(Func<Notifications.Alerts, Task> action)
    {
        if (alerts is null)
            return;
        try
        {
            await action(alerts);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogAlertFailed(logger, ex);
        }
    }

    private async Task NotifyStateAsync(EngineConnection connection, CancellationToken cancellationToken)
    {
        await AlertAsync(a => a.StateChangedAsync(connection, cancellationToken));
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

    [LoggerMessage(Level = LogLevel.Error, Message = "Checking an engine event for alerts failed")]
    private static partial void LogAlertFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "Forwarding an engine notification failed")]
    private static partial void LogSinkFailed(ILogger logger, Exception exception);
}
