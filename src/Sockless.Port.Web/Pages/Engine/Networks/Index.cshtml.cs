using Microsoft.AspNetCore.Mvc;
using Sockless.Port.Containers;
using Sockless.Port.Docker;
using Sockless.Port.Docker.Models;
using Sockless.Port.Engines;
using Sockless.Port.Networks;
using Sockless.Port.Web.Infrastructure;

namespace Sockless.Port.Web.Pages.Engine.Networks;

/// <param name="Attached">Containers attached to the network, running or not.</param>
public sealed record NetworkRow(NetworkSummary Summary, IReadOnlyList<ContainerSummary> Attached)
{
    public string Id => Summary.Id;
    public string Name => Summary.Name;
    public string? Stack => ContainerLabels.Stack(Summary.Labels);

    public IReadOnlyList<string> Subnets => [.. (Summary.Ipam?.Config ?? []).Select(c => c.Subnet).OfType<string>().Where(s => s.Length > 0)];
}

public sealed class IndexModel(EngineConnections connections) : EndpointPageModel(connections)
{
    public static readonly IReadOnlyList<(string Value, string Label)> Kinds =
        [("", "All"), ("custom", "Created by users"), ("builtin", "Built in"), ("used", "In use"), ("unused", "Unused")];

    [BindProperty(SupportsGet = true)]
    public string? Q { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? Kind { get; set; }

    public IReadOnlyList<NetworkRow> Rows { get; private set; } = [];
    public int Total { get; private set; }

    /// <summary>User-created networks with no container attached: what a prune removes.</summary>
    public int Unused { get; private set; }

    public string? Error { get; private set; }

    public bool Filtered => !string.IsNullOrWhiteSpace(Q) || !string.IsNullOrEmpty(Kind);

    public object Filters => new { endpointId = EndpointId, Q, Kind };

    public async Task OnGetAsync(CancellationToken cancellationToken) => await LoadRowsAsync(cancellationToken);

    public async Task<IActionResult> OnGetTableAsync(CancellationToken cancellationToken)
    {
        await LoadRowsAsync(cancellationToken);
        Response.Headers["HX-Replace-Url"] = Url.Page("/Engine/Networks/Index", Filters);
        return Partial("_NetworkTable", this);
    }

    /// <summary>Removes the selected networks (the row buttons post a single id).</summary>
    public async Task<IActionResult> OnPostRemoveAsync(string[] ids, CancellationToken cancellationToken)
    {
        if (ids.Length == 0)
        {
            Flash.Error(TempData, "Select one or more networks first.");
            return RedirectToPage(Filters);
        }
        if (Client is not { } client)
        {
            Flash.Error(TempData, "The engine is offline, so nothing was changed.");
            return RedirectToPage(Filters);
        }

        Dictionary<string, string> names;
        try
        {
            names = (await client.ListNetworksAsync(cancellationToken)).ToDictionary(n => n.Id, n => n.Name, StringComparer.Ordinal);
        }
        catch (Exception ex) when (EngineFailures.IsFailure(ex, cancellationToken))
        {
            names = [];
        }

        var removed = new List<string>();
        var failed = new List<string>();
        foreach (var id in ids.Distinct(StringComparer.Ordinal).Take(500))
        {
            var name = names.GetValueOrDefault(id) ?? (id.Length > 12 ? id[..12] : id);
            try
            {
                await client.RemoveNetworkAsync(id, cancellationToken);
                removed.Add(name);
            }
            catch (Exception ex) when (EngineFailures.IsFailure(ex, cancellationToken))
            {
                failed.Add($"{name}: {EngineFailures.Describe(ex, Connection)}");
            }
        }

        if (removed.Count > 0)
            Flash.Success(TempData, $"Removed {(removed.Count <= 3 ? string.Join(", ", removed) : $"{removed.Count} networks")}.");
        if (failed.Count > 0)
            Flash.Error(TempData, $"Couldn't remove {string.Join(" ", failed)}");
        return RedirectToPage(Filters);
    }

    public async Task<IActionResult> OnPostPruneAsync(CancellationToken cancellationToken)
    {
        NetworkPruneResponse? result = null;
        await TryEngineAsync(async client => result = await client.PruneNetworksAsync(cancellationToken: cancellationToken));
        if (result is not null)
        {
            var deleted = result.NetworksDeleted ?? [];
            Flash.Success(TempData, deleted.Count == 0
                ? "Nothing to remove: every network you created has containers attached."
                : $"Removed {(deleted.Count <= 3 ? string.Join(", ", deleted) : $"{deleted.Count} networks")}.");
        }
        return RedirectToPage(Filters);
    }

    private async Task LoadRowsAsync(CancellationToken cancellationToken)
    {
        if (Client is not { } client)
            return;
        try
        {
            var networks = await client.ListNetworksAsync(cancellationToken);
            var attached = NetworkUsage.ByNetwork(await client.ListContainersAsync(all: true, cancellationToken));
            var rows = networks.Select(n => new NetworkRow(n, [.. attached[n.Name]])).ToList();

            Total = rows.Count;
            Unused = rows.Count(r => !r.Summary.IsPredefined && r.Attached.Count == 0);
            Rows = [.. rows
                .Where(Matches)
                .OrderBy(r => r.Summary.IsPredefined)
                .ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase)];
        }
        catch (Exception ex) when (EngineFailures.IsFailure(ex, cancellationToken))
        {
            Error = EngineFailures.Describe(ex, Connection);
        }
    }

    private bool Matches(NetworkRow row)
    {
        var kind = Kind switch
        {
            "custom" => !row.Summary.IsPredefined,
            "builtin" => row.Summary.IsPredefined,
            "used" => row.Attached.Count > 0,
            "unused" => row.Attached.Count == 0,
            _ => true,
        };
        if (!kind)
            return false;
        if (string.IsNullOrWhiteSpace(Q))
            return true;
        var q = Q.Trim();
        return row.Name.Contains(q, StringComparison.OrdinalIgnoreCase)
               || row.Id.StartsWith(q, StringComparison.OrdinalIgnoreCase)
               || row.Summary.Driver?.Contains(q, StringComparison.OrdinalIgnoreCase) == true
               || row.Subnets.Any(s => s.StartsWith(q, StringComparison.OrdinalIgnoreCase))
               || row.Stack?.Contains(q, StringComparison.OrdinalIgnoreCase) == true
               || row.Attached.Any(c => c.DisplayName.Contains(q, StringComparison.OrdinalIgnoreCase));
    }
}
