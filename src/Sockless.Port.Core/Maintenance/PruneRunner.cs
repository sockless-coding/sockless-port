using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Sockless.Port.Audit;
using Sockless.Port.Docker;
using Sockless.Port.Engines;
using Sockless.Port.Volumes;

namespace Sockless.Port.Maintenance;

/// <summary>What a prune did. <see cref="Summary"/> names what was removed and what failed.</summary>
public sealed record PruneResult(MaintenanceOutcome Outcome, string Summary, long SpaceReclaimed);

/// <summary>A prune of the endpoint is already running.</summary>
public sealed class PruneBusyException() : Exception("A prune of this endpoint is already running.");

/// <summary>
/// Prunes an endpoint: volumes (after releasing the file browser's helpers, which mount them),
/// networks, images, then build cache. A part that fails doesn't stop the others. Each run is
/// recorded in the maintenance history and the audit log with what it removed, and a failure
/// raises a task-failed alert.
/// </summary>
public sealed partial class PruneRunner(VolumeBrowser browser, MaintenanceLog log, AuditLog audit, TimeProvider time, ILogger<PruneRunner> logger,
    Notifications.Alerts? alerts = null)
{
    /// <summary>Names listed in a summary before "and N more".</summary>
    internal const int ListedNames = 10;

    private readonly ConcurrentDictionary<int, bool> _running = new();

    public bool IsRunning(int endpointId) => _running.ContainsKey(endpointId);

    /// <exception cref="PruneBusyException">A prune of the endpoint is already running.</exception>
    public async Task<PruneResult> RunAsync(EngineConnection connection, DockerClient client, PruneOptions options, MaintenanceTrigger trigger,
        CancellationToken cancellationToken = default)
    {
        if (!_running.TryAdd(connection.Id, true))
            throw new PruneBusyException();
        try
        {
            var started = time.GetUtcNow();
            var result = await PruneAsync(connection, client, options, cancellationToken);
            await log.RecordAsync(new MaintenanceRun
            {
                Task = MaintenanceTask.Prune,
                EndpointId = connection.Id,
                EndpointName = connection.Name,
                StartedAt = started,
                FinishedAt = time.GetUtcNow(),
                Outcome = result.Outcome,
                By = trigger.By,
                Summary = result.Summary,
            });
            await audit.WriteAsync(new AuditRecord(trigger.UserId, trigger.By, "Maintenance: prune", options.Describe(), connection.Id, connection.Name,
                result.Outcome == MaintenanceOutcome.Succeeded ? AuditOutcome.Succeeded : AuditOutcome.Failed, result.Summary, trigger.RemoteAddress));
            if (result.Outcome == MaintenanceOutcome.Failed)
            {
                LogFailed(logger, connection.Name, result.Summary);
                if (alerts is not null)
                    await alerts.TaskFailedAsync(connection, $"The {(trigger.IsSchedule ? "scheduled " : "")}prune of {connection.Name} failed", result.Summary,
                        "/maintenance", CancellationToken.None);
            }
            return result;
        }
        finally
        {
            _running.TryRemove(connection.Id, out _);
        }
    }

    private async Task<PruneResult> PruneAsync(EngineConnection connection, DockerClient client, PruneOptions options, CancellationToken cancellationToken)
    {
        List<string> removed = [];
        List<string> failures = [];
        long space = 0;

        async Task StepAsync(string what, Func<Task> step)
        {
            try
            {
                await step();
            }
            catch (Exception ex) when (IsEngineFailure(ex, cancellationToken))
            {
                failures.Add($"{what}: {EngineErrors.ForCall(ex, connection.Address)}");
            }
        }

        if (options.Volumes != PruneVolumes.None)
        {
            await StepAsync("Volumes", async () =>
            {
                var includeNamed = options.Volumes == PruneVolumes.All;
                if (!includeNamed && !client.CanPruneAnonymousVolumesOnly)
                {
                    failures.Add($"Volumes: the engine's API {client.ApiVersion} would remove named volumes too, so none were removed. Docker 23 or later can remove only anonymous ones.");
                    return;
                }
                await browser.ReleaseAllAsync(connection, client, cancellationToken);
                var result = await client.PruneVolumesAsync(includeNamed, cancellationToken: cancellationToken);
                space += result.SpaceReclaimed;
                if (result.VolumesDeleted is { Count: > 0 } volumes)
                    removed.Add($"{Plural(volumes.Count, "volume", "volumes")} ({Names(volumes)})");
            });
        }

        if (options.Networks)
        {
            await StepAsync("Networks", async () =>
            {
                var result = await client.PruneNetworksAsync(cancellationToken: cancellationToken);
                if (result.NetworksDeleted is { Count: > 0 } networks)
                    removed.Add($"{Plural(networks.Count, "network", "networks")} ({Names(networks)})");
            });
        }

        if (options.Images != PruneImages.None)
        {
            await StepAsync("Images", async () =>
            {
                var result = await client.PruneImagesAsync(options.Images == PruneImages.Unused, options.OlderThan, cancellationToken);
                space += result.SpaceReclaimed;
                var items = result.ImagesDeleted ?? [];
                var untagged = items.Select(i => i.Untagged).OfType<string>().ToList();
                var deleted = items.Count(i => i.Deleted is not null);
                if (deleted > 0 || untagged.Count > 0)
                    removed.Add($"{Plural(deleted, "image or layer", "images or layers")}{(untagged.Count > 0 ? $" and {Plural(untagged.Count, "tag", "tags")} ({Names(untagged)})" : "")}");
            });
        }

        if (options.BuildCache)
        {
            await StepAsync("Build cache", async () =>
            {
                try
                {
                    var result = await client.PruneBuildCacheAsync(options.OlderThan, cancellationToken);
                    space += result.SpaceReclaimed;
                    if (result.CachesDeleted is { Count: > 0 } caches)
                        removed.Add(Plural(caches.Count, "build cache entry", "build cache entries"));
                }
                catch (DockerApiException ex) when (ex.IsNotFound)
                {
                    // Podman's compatible API has no build cache to prune.
                    removed.Add("no build cache (the engine has none to prune)");
                }
            });
        }

        var summary = removed.Count == 0 ? "Nothing to remove." : $"Removed {string.Join("; ", removed)}.";
        if (space > 0)
            summary += $" Freed {Bytes(space)}.";
        if (failures.Count > 0)
            summary = string.Join(" ", failures.Select(EngineErrors.Sentence)) + " " + summary;
        return new PruneResult(failures.Count == 0 ? MaintenanceOutcome.Succeeded : MaintenanceOutcome.Failed, summary, space);
    }

    internal static bool IsEngineFailure(Exception ex, CancellationToken cancellationToken) =>
        !cancellationToken.IsCancellationRequested
        && ex is DockerApiException or DockerApiVersionException or HttpRequestException or IOException or TimeoutException
            or TaskCanceledException or ObjectDisposedException or InvalidDataException or System.Text.Json.JsonException or NotSupportedException;

    internal static string Names(IReadOnlyCollection<string> names) =>
        string.Join(", ", names.Take(ListedNames)) + (names.Count > ListedNames ? $" and {names.Count - ListedNames} more" : "");

    private static string Plural(int count, string one, string many) => $"{count} {(count == 1 ? one : many)}";

    /// <summary>Decimal units, as the pages show sizes.</summary>
    internal static string Bytes(long bytes)
    {
        string[] units = ["B", "kB", "MB", "GB", "TB", "PB"];
        if (bytes <= 0)
            return "0 B";
        var value = (double)bytes;
        var unit = 0;
        while (value >= 1000 && unit < units.Length - 1)
        {
            value /= 1000;
            unit++;
        }
        var format = unit == 0 || value >= 100 ? "0" : value >= 10 ? "0.#" : "0.##";
        return $"{value.ToString(format, System.Globalization.CultureInfo.InvariantCulture)} {units[unit]}";
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "The prune of {Endpoint} failed: {Summary}")]
    private static partial void LogFailed(ILogger logger, string endpoint, string summary);
}
