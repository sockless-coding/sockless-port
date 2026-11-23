using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Sockless.Port.Settings;

namespace Sockless.Port.Audit;

/// <summary>Deletes audit entries and notification history past the retention setting, a minute after start and then daily.</summary>
internal sealed partial class AuditRetention(AuditLog audit, Notifications.Notifier notifier, PortSettings settings, TimeProvider time, ILogger<AuditRetention> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromMinutes(1), time, stoppingToken);
            using var timer = new PeriodicTimer(TimeSpan.FromDays(1), time);
            do
            {
                var days = settings.Current.AuditRetentionDays;
                var removed = await audit.PruneAsync(TimeSpan.FromDays(days), stoppingToken);
                if (removed > 0)
                    LogPruned(logger, removed, days);
                // Sent notifications are kept as long as the audit log.
                await notifier.PruneAsync(TimeSpan.FromDays(days), stoppingToken);
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Deleted {Count} audit entries older than {Days} days")]
    private static partial void LogPruned(ILogger logger, int count, int days);
}
