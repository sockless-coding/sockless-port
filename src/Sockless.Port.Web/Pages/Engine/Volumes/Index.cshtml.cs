using Microsoft.AspNetCore.Mvc;
using Sockless.Port.Containers;
using Sockless.Port.Docker;
using Sockless.Port.Docker.Models;
using Sockless.Port.Engines;
using Sockless.Port.Volumes;
using Sockless.Port.Web.Infrastructure;

namespace Sockless.Port.Web.Pages.Engine.Volumes;

/// <param name="UsedBy">Containers mounting the volume.</param>
/// <param name="Size">Bytes on disk, when sizes were asked for and the engine knows.</param>
public sealed record VolumeRow(VolumeSummary Summary, IReadOnlyList<VolumeUse> UsedBy, long? Size)
{
    public string Name => Summary.Name;
    public string ShortName => VolumePageModel.ShortName(Summary.Name);
    public string? Stack => ContainerLabels.Stack(Summary.Labels);
}

public sealed class IndexModel(EngineConnections connections, VolumeBrowser browser) : EndpointPageModel(connections)
{
    public static readonly IReadOnlyList<(string Value, string Label)> Usages =
        [("", "All"), ("used", "In use"), ("unused", "Unused"), ("named", "Named"), ("anonymous", "Anonymous")];

    [BindProperty(SupportsGet = true)]
    public string? Q { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? Usage { get; set; }

    /// <summary>Also compute sizes, which makes the engine walk every volume.</summary>
    [BindProperty(SupportsGet = true)]
    public bool Sizes { get; set; }

    public IReadOnlyList<VolumeRow> Rows { get; private set; } = [];
    public int Total { get; private set; }
    public int Unused { get; private set; }
    public int UnusedAnonymous { get; private set; }
    public long? TotalSize { get; private set; }
    public string? Error { get; private set; }
    public string? SizeError { get; private set; }

    /// <summary>Below API 1.42 a prune can't keep named volumes, so only the "all unused" choice is offered.</summary>
    public bool CanPruneAnonymousOnly => Client?.CanPruneAnonymousVolumesOnly ?? true;

    public bool Filtered => !string.IsNullOrWhiteSpace(Q) || !string.IsNullOrEmpty(Usage);

    public object Filters => new { endpointId = EndpointId, Q, Usage, sizes = Sizes ? "true" : null };

    public async Task OnGetAsync(CancellationToken cancellationToken) => await LoadRowsAsync(cancellationToken);

    public async Task<IActionResult> OnGetTableAsync(CancellationToken cancellationToken)
    {
        await LoadRowsAsync(cancellationToken);
        Response.Headers["HX-Replace-Url"] = Url.Page("/Engine/Volumes/Index", Filters);
        return Partial("_VolumeTable", this);
    }

    /// <summary>Removes the selected volumes (the row buttons post a single name).</summary>
    public async Task<IActionResult> OnPostRemoveAsync(string[] ids, CancellationToken cancellationToken)
    {
        if (ids.Length == 0)
        {
            Flash.Error(TempData, "Select one or more volumes first.");
            return RedirectToPage(Filters);
        }
        if (Client is not { } client)
        {
            Flash.Error(TempData, "The engine is offline, so nothing was changed.");
            return RedirectToPage(Filters);
        }

        var removed = new List<string>();
        var failed = new List<string>();
        foreach (var name in ids.Distinct(StringComparer.Ordinal).Take(500))
        {
            try
            {
                // An open file browser mounts the volume, which would block the removal.
                await browser.ReleaseAsync(Connection, client, name, cancellationToken);
                await client.RemoveVolumeAsync(name, cancellationToken: cancellationToken);
                removed.Add(VolumePageModel.ShortName(name));
            }
            catch (Exception ex) when (EngineFailures.IsFailure(ex, cancellationToken))
            {
                failed.Add($"{VolumePageModel.ShortName(name)}: {Describe(ex)}");
            }
        }

        if (removed.Count > 0)
            Flash.Success(TempData, $"Removed {(removed.Count <= 3 ? string.Join(", ", removed) : $"{removed.Count} volumes")}.");
        if (failed.Count > 0)
            Flash.Error(TempData, $"Couldn't remove {string.Join(" ", failed)}");
        return RedirectToPage(Filters);
    }

    /// <param name="all">Also named volumes, not only anonymous ones.</param>
    public async Task<IActionResult> OnPostPruneAsync(bool all, CancellationToken cancellationToken)
    {
        VolumePruneResponse? result = null;
        var includeNamed = all || !CanPruneAnonymousOnly;
        await TryEngineAsync(async client =>
        {
            await browser.ReleaseAllAsync(Connection, client, cancellationToken);
            result = await client.PruneVolumesAsync(includeNamed, cancellationToken: cancellationToken);
        });
        if (result is not null)
        {
            var count = result.VolumesDeleted?.Count ?? 0;
            var kind = includeNamed ? "unused" : "unused anonymous";
            Flash.Success(TempData, count == 0
                ? $"Nothing to remove: there are no {kind} volumes."
                : $"Removed {count} {kind} {(count == 1 ? "volume" : "volumes")}{(result.SpaceReclaimed > 0 ? $", freeing {Format.Bytes(result.SpaceReclaimed)}" : "")}.");
        }
        return RedirectToPage(Filters);
    }

    /// <summary>"In use" errors name container IDs; say it in words.</summary>
    private string Describe(Exception ex) =>
        ex is DockerApiException { IsConflict: true } ? "a container uses it. Remove the container first." : EngineFailures.Describe(ex, Connection);

    private async Task LoadRowsAsync(CancellationToken cancellationToken)
    {
        if (Client is not { } client)
            return;
        try
        {
            var volumes = await client.ListVolumesAsync(cancellationToken);
            var usage = VolumeUsage.ByVolume(await client.ListContainersAsync(all: true, cancellationToken));
            var sizes = Sizes ? await SizesAsync(client, cancellationToken) : null;

            Total = volumes.Count;
            Unused = volumes.Count(v => !usage.Contains(v.Name));
            UnusedAnonymous = volumes.Count(v => v.IsAnonymous && !usage.Contains(v.Name));
            var rows = volumes
                .Select(v => new VolumeRow(v, [.. usage[v.Name]], sizes?.GetValueOrDefault(v.Name) is { } size and >= 0 ? size : null))
                .ToList();
            TotalSize = sizes is null ? null : rows.Sum(r => r.Size ?? 0);
            Rows = [.. rows
                .Where(Matches)
                .OrderBy(r => r.Summary.IsAnonymous)
                .ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase)];
        }
        catch (Exception ex) when (EngineFailures.IsFailure(ex, cancellationToken))
        {
            Error = EngineFailures.Describe(ex, Connection);
        }
    }

    private async Task<Dictionary<string, long>?> SizesAsync(DockerClient client, CancellationToken cancellationToken)
    {
        try
        {
            return (await client.GetVolumeUsageAsync(cancellationToken))
                .Where(v => v.Name is not null && v.UsageData is not null)
                .ToDictionary(v => v.Name!, v => v.UsageData!.Size, StringComparer.Ordinal);
        }
        catch (Exception ex) when (EngineFailures.IsFailure(ex, cancellationToken))
        {
            SizeError = $"Sizes aren't available: {EngineFailures.Describe(ex, Connection)}";
            return null;
        }
    }

    private bool Matches(VolumeRow row)
    {
        var usage = Usage switch
        {
            "used" => row.UsedBy.Count > 0,
            "unused" => row.UsedBy.Count == 0,
            "named" => !row.Summary.IsAnonymous,
            "anonymous" => row.Summary.IsAnonymous,
            _ => true,
        };
        if (!usage)
            return false;
        if (string.IsNullOrWhiteSpace(Q))
            return true;
        var q = Q.Trim();
        return row.Name.Contains(q, StringComparison.OrdinalIgnoreCase)
               || row.Summary.Driver?.Contains(q, StringComparison.OrdinalIgnoreCase) == true
               || row.Stack?.Contains(q, StringComparison.OrdinalIgnoreCase) == true
               || row.UsedBy.Any(u => u.Container.DisplayName.Contains(q, StringComparison.OrdinalIgnoreCase));
    }
}
