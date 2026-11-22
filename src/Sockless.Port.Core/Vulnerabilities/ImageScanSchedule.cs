using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Sockless.Port.Docker;
using Sockless.Port.Engines;
using Sockless.Port.Settings;

namespace Sockless.Port.Vulnerabilities;

/// <summary>
/// Queues scans of the images containers use, on every online endpoint, when their result is
/// older than the interval in the settings (never by default: each scan copies the image to the
/// server). Failed scans wait an interval too. Once a day it also deletes results of images that
/// no online endpoint has any more and that weren't scanned for <see cref="KeepUnused"/>.
/// </summary>
internal sealed partial class ImageScanSchedule(ImageScans scans, EngineConnections connections, PortSettings settings, TimeProvider time,
    ILogger<ImageScanSchedule> logger) : BackgroundService
{
    internal static readonly TimeSpan Tick = TimeSpan.FromMinutes(15);

    /// <summary>How long a result outlives its image, e.g. for an endpoint that's offline a while.</summary>
    internal static readonly TimeSpan KeepUnused = TimeSpan.FromDays(30);

    private DateTimeOffset _lastPrune = DateTimeOffset.MinValue;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            // Let the monitor connect the endpoints first.
            await Task.Delay(TimeSpan.FromMinutes(3), time, stoppingToken);
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

    /// <summary>Queues the scans that are due, and prunes once a day.</summary>
    /// <returns>How many scans were queued.</returns>
    internal async Task<int> RunDueAsync(CancellationToken cancellationToken)
    {
        var hours = settings.Current.VulnerabilityScanHours;
        var now = time.GetUtcNow();
        var interval = TimeSpan.FromHours(hours);
        var present = new HashSet<string>(StringComparer.Ordinal);
        var queued = 0;
        foreach (var connection in connections.All)
        {
            if (connection.Client is not { } client)
                continue;
            try
            {
                var images = await client.ListImagesAsync(cancellationToken);
                present.UnionWith(images.Select(i => i.Id));
                if (hours <= 0)
                    continue;
                var inUse = (await client.ListContainersAsync(all: true, cancellationToken))
                    .Select(c => c.ImageId).OfType<string>().Distinct(StringComparer.Ordinal).ToList();
                var stored = await scans.SummariesAsync(inUse, cancellationToken);
                foreach (var imageId in inUse)
                {
                    if (stored.TryGetValue(imageId, out var last) && now - last.ScannedAt < interval)
                        continue;
                    if (scans.StateOf(imageId) is ScanFailed failed && now - failed.Since < interval)
                        continue;
                    if (scans.Enqueue(connection, imageId))
                        queued++;
                }
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested
                                       && ex is DockerApiException or DockerApiVersionException or HttpRequestException or IOException or TimeoutException
                                           or TaskCanceledException or ObjectDisposedException or InvalidDataException or System.Text.Json.JsonException)
            {
                LogFailed(logger, connection.Name, EngineErrors.ForCall(ex, connection.Address));
            }
        }

        if (now - _lastPrune >= TimeSpan.FromDays(1))
        {
            _lastPrune = now;
            if (await scans.PruneAsync(present, now - KeepUnused, cancellationToken) is > 0 and var pruned)
                LogPruned(logger, pruned);
        }
        return queued;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "The scheduled vulnerability scan of {Endpoint} failed: {Reason}")]
    private static partial void LogFailed(ILogger logger, string endpoint, string reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "Deleted {Count} vulnerability scans of images no endpoint has any more")]
    private static partial void LogPruned(ILogger logger, int count);
}
