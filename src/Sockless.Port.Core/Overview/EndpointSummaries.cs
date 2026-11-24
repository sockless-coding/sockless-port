using Sockless.Port.Containers;
using Sockless.Port.Docker;
using Sockless.Port.Docker.Engine;
using Sockless.Port.Docker.Models;
using Sockless.Port.Engines;
using Sockless.Port.Images;
using Sockless.Port.Monitoring;
using Sockless.Port.Vulnerabilities;

namespace Sockless.Port.Overview;

/// <summary>An endpoint's containers by state. Helpers (the volume file browser) aren't counted.</summary>
/// <param name="Unhealthy">Names of the containers whose health check fails, by name.</param>
/// <param name="Failed">Stopped containers that exited with an error.</param>
public sealed record ContainerCounts(int Running, int Paused, int Stopped, int Failed, IReadOnlyList<string> Unhealthy)
{
    public int Total => Running + Paused + Stopped;
}

/// <summary>What the last update checks found, as the containers and services pages show it.</summary>
/// <param name="Containers">Containers whose image has a newer one in its registry.</param>
/// <param name="Services">Swarm services whose image does; 0 off a swarm manager.</param>
/// <param name="CheckedAt">The last full check of the endpoint, from a page or on schedule; null when there wasn't one.</param>
public sealed record UpdateCounts(int Containers, int Services, DateTimeOffset? CheckedAt)
{
    public int Total => Containers + Services;
}

/// <summary>Open findings (accepted risks left out) in the endpoint's scanned images.</summary>
/// <param name="ImagesWithCritical">Images with at least one critical finding.</param>
public sealed record FindingCounts(int Critical, int High, int ImagesWithCritical, int Scanned, int Images);

/// <summary>Resource use of the endpoint's running containers against its host, from <see cref="ResourceMonitor"/>.</summary>
/// <param name="Cpu">100 is one full CPU, as <c>docker stats</c> shows it.</param>
public sealed record UsageTotals(DateTimeOffset At, double Cpu, int Cpus, long Memory, long HostMemory)
{
    /// <summary>Share of the host's CPUs, 0 to 100; null when the host's CPUs aren't known.</summary>
    public double? CpuShare => Cpus > 0 ? Math.Clamp(Cpu / Cpus, 0, 100) : null;

    /// <summary>Share of the host's memory, 0 to 100; null when it isn't known.</summary>
    public double? MemoryShare => HostMemory > 0 ? Math.Clamp(100.0 * Memory / HostMemory, 0, 100) : null;
}

/// <summary>One endpoint's row on the overview. Built only while the endpoint is online.</summary>
/// <param name="Usage">Null when nothing samples the endpoint (resource history off), or the sample failed.</param>
public sealed record EndpointSummary(EngineDescriptor Engine, ContainerCounts Containers, UpdateCounts Updates, FindingCounts Findings, UsageTotals? Usage);

/// <summary>
/// The overview across endpoints (<c>/overview</c>): each endpoint's containers, available updates,
/// critical findings and resource use, read from the same places as the endpoint's own pages:
/// the engine's container and image lists, <see cref="ImageUpdates"/>' last results, stored scans
/// and <see cref="ResourceMonitor"/>'s latest snapshot. It starts no checks, scans or sampling.
/// </summary>
public sealed class EndpointSummaries(ImageUpdates updates, ImageScans scans, ResourceMonitor monitor)
{
    /// <summary>A resource snapshot older than this isn't shown: the sampler has stopped or stalled.</summary>
    internal static readonly TimeSpan UsageMaxAge = TimeSpan.FromMinutes(1);

    /// <summary>The endpoint's summary; null while it's offline. Engine failures are thrown.</summary>
    public async Task<EndpointSummary?> SummarizeAsync(EngineConnection connection, CancellationToken cancellationToken = default)
    {
        if (connection.Client is not { } client || connection.Engine is not { } engine)
            return null;

        var containersTask = client.ListContainersAsync(all: true, cancellationToken);
        var imagesTask = client.ListImagesAsync(cancellationToken);
        var servicesTask = engine.Has(EngineCapabilities.SwarmManager)
            ? client.ListServicesAsync(cancellationToken: cancellationToken)
            : Task.FromResult<IReadOnlyList<SwarmService>>([]);
        await Task.WhenAll(containersTask, imagesTask, servicesTask);

        var containers = containersTask.Result.Where(c => !ContainerLabels.IsHelper(c.Labels)).ToList();
        var images = imagesTask.Result;
        var scanned = await scans.SummariesAsync(images.Select(i => i.Id), cancellationToken);

        return new EndpointSummary(
            engine,
            Count(containers),
            new UpdateCounts(
                containers.Count(c => updates.ForContainer(connection.Id, c) is UpdateAvailable),
                servicesTask.Result.Count(s => updates.ForService(connection.Id, s) is UpdateAvailable),
                updates.LastChecked(connection.Id)),
            Findings(scanned.Values, images.Count),
            Usage(monitor.Latest(connection, UsageMaxAge)));
    }

    internal static ContainerCounts Count(IReadOnlyList<ContainerSummary> containers)
    {
        int running = 0, paused = 0, stopped = 0, failed = 0;
        List<string> unhealthy = [];
        foreach (var container in containers)
        {
            switch (container.State)
            {
                case "running" or "restarting":
                    running++;
                    break;
                case "paused":
                    paused++;
                    break;
                default:
                    stopped++;
                    if (ExitedWithError(container.Status))
                        failed++;
                    break;
            }
            if (container.IsUnhealthy)
                unhealthy.Add(container.DisplayName);
        }
        unhealthy.Sort(StringComparer.OrdinalIgnoreCase);
        return new ContainerCounts(running, paused, stopped, failed, unhealthy);
    }

    /// <summary>"Exited (1) 3 hours ago" is an error and "Exited (0) …" isn't, as the containers page shows them.</summary>
    internal static bool ExitedWithError(string? status) =>
        status is not null && status.StartsWith("Exited (", StringComparison.Ordinal) && !status.StartsWith("Exited (0)", StringComparison.Ordinal);

    internal static FindingCounts Findings(IEnumerable<ScanSummary> scanned, int images)
    {
        int critical = 0, high = 0, withCritical = 0, count = 0;
        foreach (var scan in scanned)
        {
            count++;
            critical += scan.Counts.Critical;
            high += scan.Counts.High;
            if (scan.Counts.Critical > 0)
                withCritical++;
        }
        return new FindingCounts(critical, high, withCritical, count, images);
    }

    internal static UsageTotals? Usage(UsageSnapshot? snapshot) =>
        snapshot is null || snapshot.Error is not null
            ? null
            : new UsageTotals(snapshot.Time, snapshot.Cpu, snapshot.Cpus, snapshot.Memory, snapshot.HostMemory);
}
