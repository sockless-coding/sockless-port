using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Sockless.Port.Backup;
using Sockless.Port.Engines;

namespace Sockless.Port.Maintenance;

/// <summary>
/// Runs scheduled backups and prunes when they're due, checking every five minutes. A backup is
/// due its interval after the last one that worked (an hour after one that failed); a prune its
/// interval after the last run, and only while its endpoint is online. Due times come from the
/// maintenance history, so they survive restarts.
/// </summary>
public sealed partial class MaintenanceSchedule(ScheduledBackups backups, PruneSchedules prunes, PruneRunner runner, MaintenanceLog log,
    EngineConnections connections, TimeProvider time, ILogger<MaintenanceSchedule> logger) : BackgroundService
{
    public static readonly TimeSpan Tick = TimeSpan.FromMinutes(5);

    /// <summary>Lets the monitor connect the endpoints first.</summary>
    public static readonly TimeSpan StartDelay = TimeSpan.FromMinutes(2);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(StartDelay, time, stoppingToken);
            using var timer = new PeriodicTimer(Tick, time);
            do
            {
                try
                {
                    await RunDueAsync(stoppingToken);
                }
                catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
                {
                    LogTickFailed(logger, ex);
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    /// <summary>Runs the backup and the prunes that are due.</summary>
    internal async Task RunDueAsync(CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        var schedule = await backups.GetAsync(cancellationToken);
        if (await backups.NextAsync(schedule, cancellationToken) is { } next && next <= now && !backups.IsRunning)
        {
            try
            {
                await backups.RunAsync(MaintenanceTrigger.Schedule, cancellationToken);
            }
            catch (BackupBusyException)
            {
            }
        }

        var latest = await log.LatestAsync(cancellationToken);
        foreach (var prune in (await prunes.AllAsync(cancellationToken)).Values)
        {
            if (connections.Get(prune.EndpointId) is not { Client: { } client } connection
                || NextPrune(prune, latest.GetValueOrDefault((MaintenanceTask.Prune, prune.EndpointId)), now) is not { } due || due > now)
                continue;
            try
            {
                await runner.RunAsync(connection, client, prune.Options, MaintenanceTrigger.Schedule, cancellationToken);
            }
            catch (PruneBusyException)
            {
            }
        }
    }

    /// <summary>When a prune is due: its interval after the last run, or now when it never ran; null when it's off or removes nothing.</summary>
    public static DateTimeOffset? NextPrune(PruneSchedule schedule, MaintenanceRun? last, DateTimeOffset now) =>
        schedule.IntervalHours <= 0 || schedule.Options.IsEmpty ? null
        : last is null ? now
        : last.StartedAt + TimeSpan.FromHours(schedule.IntervalHours);

    [LoggerMessage(Level = LogLevel.Error, Message = "Scheduled maintenance failed")]
    private static partial void LogTickFailed(ILogger logger, Exception ex);
}
