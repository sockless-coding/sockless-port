using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Sockless.Port.Docker;
using Sockless.Port.Docker.Models;
using Sockless.Port.Engines;
using Sockless.Port.Settings;

namespace Sockless.Port.Images;

/// <summary>
/// Checks every online endpoint for image updates as often as the settings say (never by default:
/// on Docker Hub each check counts against the pull rate limit). An endpoint is due when its last
/// check, from a page or from here, is older than the interval, so it's also checked soon after
/// a start, since results are only kept in memory. Open pages refresh when a check ends.
/// </summary>
internal sealed partial class ImageUpdateSchedule(ImageUpdates updates, EngineConnections connections, PortSettings settings, IEngineEventSink sink,
    Maintenance.MaintenanceLog log, TimeProvider time, ILogger<ImageUpdateSchedule> logger, Notifications.Alerts? alerts = null) : BackgroundService
{
    internal static readonly TimeSpan Tick = TimeSpan.FromMinutes(15);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            // Let the monitor connect the endpoints first.
            await Task.Delay(TimeSpan.FromMinutes(2), time, stoppingToken);
            using var timer = new PeriodicTimer(Tick, time);
            do
            {
                await RunDueAsync(stoppingToken);
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    /// <summary>Checks the endpoints that are online and due.</summary>
    internal async Task RunDueAsync(CancellationToken cancellationToken)
    {
        var hours = settings.Current.ImageUpdateCheckHours;
        if (hours <= 0)
            return;
        var now = time.GetUtcNow();
        foreach (var connection in connections.All)
        {
            if (connection.Client is not { } client || !ImageUpdates.CanCheck(connection)
                || (updates.LastChecked(connection.Id) is { } last && now - last < TimeSpan.FromHours(hours)))
                continue;
            var started = time.GetUtcNow();
            try
            {
                await updates.CheckEndpointAsync(connection, client, cancellationToken);
                await sink.EngineEventAsync(connection, new DockerEvent { Type = "image", Action = "update-check" }, cancellationToken);
                var available = updates.Available(connection.Id);
                await RecordAsync(connection, started, Maintenance.MaintenanceOutcome.Succeeded, available.Count == 0
                    ? "No updates found."
                    : $"{available.Count} {(available.Count == 1 ? "image has an update" : "images have updates")}: {Maintenance.PruneRunner.Names(available)}.");
                if (alerts is not null)
                    await alerts.UpdatesCheckedAsync(connection, available, cancellationToken);
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested
                                       && ex is DockerApiException or DockerApiVersionException or HttpRequestException or IOException or TimeoutException
                                           or TaskCanceledException or ObjectDisposedException or InvalidDataException or System.Text.Json.JsonException)
            {
                var reason = EngineErrors.ForCall(ex, connection.Address);
                LogFailed(logger, connection.Name, reason);
                await RecordAsync(connection, started, Maintenance.MaintenanceOutcome.Failed, reason);
                if (alerts is not null)
                    await alerts.TaskFailedAsync(connection, $"The scheduled update check of {connection.Name} failed", reason, $"/endpoints/{connection.Id}/images", cancellationToken);
            }
        }
    }

    private Task RecordAsync(EngineConnection connection, DateTimeOffset started, Maintenance.MaintenanceOutcome outcome, string summary) =>
        log.RecordAsync(new Maintenance.MaintenanceRun
        {
            Task = Maintenance.MaintenanceTask.UpdateCheck,
            EndpointId = connection.Id,
            EndpointName = connection.Name,
            StartedAt = started,
            FinishedAt = time.GetUtcNow(),
            Outcome = outcome,
            By = Maintenance.MaintenanceTrigger.Schedule.By,
            Summary = summary,
        });

    [LoggerMessage(Level = LogLevel.Warning, Message = "The scheduled image update check of {Endpoint} failed: {Reason}")]
    private static partial void LogFailed(ILogger logger, string endpoint, string reason);
}
