using Microsoft.AspNetCore.Mvc;
using Sockless.Port.Docker;
using Sockless.Port.Docker.Engine;
using Sockless.Port.Docker.Models;
using Sockless.Port.Engines;
using Sockless.Port.Web.Infrastructure;

namespace Sockless.Port.Web.Pages.Engine;

/// <summary>An endpoint's dashboard: engine details, resource counts and disk usage.</summary>
public sealed class IndexModel(EngineConnections connections) : EndpointPageModel(connections)
{
    public EngineOverview? Overview { get; private set; }

    /// <summary>Set when the endpoint is online but a request failed.</summary>
    public string? Error { get; private set; }

    public async Task OnGetAsync(CancellationToken cancellationToken) => await LoadOverviewAsync(cancellationToken);

    /// <summary>htmx refresh of the overview, triggered by engine events and state changes.</summary>
    public async Task<IActionResult> OnGetOverviewAsync(CancellationToken cancellationToken)
    {
        await LoadOverviewAsync(cancellationToken);
        return Partial("_Overview", this);
    }

    /// <summary>Disk usage, loaded separately because it can take seconds on large engines.</summary>
    public async Task<IActionResult> OnGetDiskUsageAsync(CancellationToken cancellationToken)
    {
        DiskUsageSummary? summary = null;
        string? error = null;
        if (Connection.Client is { } client)
        {
            try
            {
                summary = DiskUsageSummary.From(await client.GetDiskUsageAsync(cancellationToken));
            }
            catch (Exception ex) when (EngineFailures.IsFailure(ex, cancellationToken))
            {
                error = EngineErrors.Describe(ex, Connection.Address);
            }
        }
        return Partial("_DiskUsage", new DiskUsageView(Connection, summary, error));
    }

    private async Task LoadOverviewAsync(CancellationToken cancellationToken)
    {
        if (Connection.Client is not { } client || Connection.Engine is not { } engine)
            return;

        try
        {
            var info = client.GetInfoAsync(cancellationToken);
            var volumes = client.ListVolumesAsync(cancellationToken);
            var networks = client.ListNetworksAsync(cancellationToken);
            var pods = Sockless.Port.Pods.PodMembership.LoadAsync(client, engine, cancellationToken);
            await Task.WhenAll(info, volumes, networks, pods);
            Overview = new EngineOverview(engine, info.Result, volumes.Result.Count, networks.Result.Count,
                engine.Has(EngineCapabilities.Pods) ? pods.Result.Pods.Count : null);
        }
        catch (Exception ex) when (EngineFailures.IsFailure(ex, cancellationToken))
        {
            Error = EngineErrors.Describe(ex, Connection.Address);
        }
    }
}

/// <param name="Pods">Podman pods; null on engines without them.</param>
public sealed record EngineOverview(EngineDescriptor Engine, SystemInfo Info, int Volumes, int Networks, int? Pods = null);

public sealed record DiskUsageView(EngineConnection Connection, DiskUsageSummary? Summary, string? Error);

public sealed record DiskUsageRow(string Kind, int Total, int Active, long Size, long Reclaimable);

public sealed record DiskUsageSummary(IReadOnlyList<DiskUsageRow> Rows)
{
    public long TotalSize => Rows.Sum(r => r.Size);

    public long TotalReclaimable => Rows.Sum(r => r.Reclaimable);

    /// <summary>Same accounting as <c>docker system df</c>, near enough for an overview.</summary>
    public static DiskUsageSummary From(DiskUsage df)
    {
        var images = df.Images ?? [];
        var containers = df.Containers ?? [];
        var volumes = df.Volumes ?? [];

        List<DiskUsageRow> rows =
        [
            new("Images", images.Count, images.Count(i => i.Containers > 0),
                df.LayersSize > 0 ? df.LayersSize : images.Sum(i => i.Size),
                images.Where(i => i.Containers <= 0).Sum(i => Math.Max(0, i.Size - Math.Max(0, i.SharedSize)))),
            new("Containers", containers.Count, containers.Count(c => c.State == "running"),
                containers.Sum(c => Math.Max(0, c.SizeRw)),
                containers.Where(c => c.State != "running").Sum(c => Math.Max(0, c.SizeRw))),
            new("Volumes", volumes.Count, volumes.Count(v => v.UsageData?.RefCount > 0),
                volumes.Sum(v => Math.Max(0, v.UsageData?.Size ?? 0)),
                volumes.Where(v => v.UsageData?.RefCount == 0).Sum(v => Math.Max(0, v.UsageData!.Size))),
        ];

        // Podman has no BuildKit cache and sends null.
        if (df.BuildCache is { } cache)
        {
            rows.Add(new("Build cache", cache.Count, cache.Count(b => b.InUse),
                cache.Where(b => !b.Shared).Sum(b => Math.Max(0, b.Size)),
                cache.Where(b => !b.InUse && !b.Shared).Sum(b => Math.Max(0, b.Size))));
        }

        return new DiskUsageSummary(rows);
    }
}
