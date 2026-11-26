using Microsoft.AspNetCore.Mvc;
using Sockless.Port.Containers;
using Sockless.Port.Engines;
using Sockless.Port.Images;
using Sockless.Port.Web.Infrastructure;

namespace Sockless.Port.Web.Pages.Engine.Containers;

public sealed class IndexModel(EngineConnections connections, ContainerListing listing, ImageUpdates updates) : EndpointPageModel(connections)
{
    public static IReadOnlyList<(string Value, string Label)> States => ContainerListing.States;

    [BindProperty(SupportsGet = true)]
    public string? State { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? Q { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? Stack { get; set; }

    /// <summary>A pod's ID: only its containers.</summary>
    [BindProperty(SupportsGet = true)]
    public string? Pod { get; set; }

    /// <summary><c>key</c> or <c>key=value</c>.</summary>
    [BindProperty(SupportsGet = true)]
    public string? Label { get; set; }

    public IReadOnlyList<ContainerRow> Rows { get; private set; } = [];

    /// <summary>The rows by stack (stacks by name, then the containers without one).</summary>
    public IReadOnlyList<ContainerGroup> Groups { get; private set; } = [];

    public IReadOnlyList<string> Stacks { get; private set; } = [];

    /// <summary>Pods on the engine, for the filter; empty on engines without pods.</summary>
    public IReadOnlyList<Docker.Models.PodSummary> PodChoices { get; private set; } = [];
    public int Total { get; private set; }
    public string? Error { get; private set; }

    private ContainerFilter Filter => new(State, Q, Stack, Pod, Label);

    public bool Filtered => Filter.IsSet;

    public object Filters => new { endpointId = EndpointId, State, Q, Stack, Pod, Label };

    /// <summary>The table, with row buttons that post to this page's bulk handler.</summary>
    public ContainerTableView Table => new(Groups, AcrossEndpoints: false,
        (row, action) => Url.Page("/Engine/Containers/Index", "Bulk", new { endpointId = EndpointId, State, Q, Stack, Pod, Label, ids = row.Summary.Id, action })!);

    public async Task OnGetAsync(CancellationToken cancellationToken) => await LoadRowsAsync(cancellationToken);

    public async Task<IActionResult> OnGetTableAsync(CancellationToken cancellationToken)
    {
        await LoadRowsAsync(cancellationToken);
        // Keep the address bar in step with the filters, so reload and back keep them.
        Response.Headers["HX-Replace-Url"] = Url.Page("/Engine/Containers/Index", Filters);
        return Partial("_ContainerTable", this);
    }

    /// <summary>One action on one or more containers (the row buttons post a single id).</summary>
    public async Task<IActionResult> OnPostBulkAsync(string? action, string[] ids, bool removeVolumes, CancellationToken cancellationToken)
    {
        if (!Enum.TryParse<ContainerAction>(action, ignoreCase: true, out var verb) || ids.Length == 0)
        {
            Flash.Error(TempData, ids.Length == 0 ? "Select one or more containers first." : "Unknown action.");
            return RedirectToPage(Filters);
        }
        if (Client is not { } client)
        {
            Flash.Error(TempData, "The engine is offline, so nothing was changed.");
            return RedirectToPage(Filters);
        }

        var (done, failed) = await ContainerBulk.RunAsync(Connection, client, verb, ids, removeVolumes, cancellationToken);
        if (done.Count > 0)
            Flash.Success(TempData, $"{ContainerActions.Done(verb)} {ContainerBulk.Describe(done)}.");
        if (failed.Count > 0)
            Flash.Error(TempData, $"Couldn't {verb.ToString().ToLowerInvariant()} {string.Join(" ", failed)}");
        return RedirectToPage(Filters);
    }

    /// <summary>Compares what the shown containers run with their registries. Nothing is pulled.</summary>
    public async Task<IActionResult> OnPostCheckUpdatesAsync(CancellationToken cancellationToken)
    {
        await LoadRowsAsync(cancellationToken);
        if (Error is not null)
        {
            Flash.Error(TempData, Error);
            return RedirectToPage(Filters);
        }
        UpdateCheckSummary? summary = null;
        await TryEngineAsync(async client => summary = await updates.CheckContainersAsync(Connection, client, [.. Rows.Select(r => r.Summary)], cancellationToken));
        if (summary is not null)
            Flash.Success(TempData, Describe(summary));
        return RedirectToPage(Filters);
    }

    internal static string Describe(UpdateCheckSummary summary) =>
        ImageDisplay.CheckMessage(summary, "container", "containers", "No container to check.", "each container's page");

    private async Task LoadRowsAsync(CancellationToken cancellationToken)
    {
        if (Client is not { } client)
            return;
        try
        {
            var list = await listing.LoadAsync(Connection, client, Filter, cancellationToken);
            Total = list.Total;
            Stacks = list.Stacks;
            PodChoices = list.Pods;
            Rows = [.. list.Rows.Select(r => r with { CanWrite = CanWrite })];
            Groups = ContainerGroup.Of(Rows);
        }
        catch (Exception ex) when (EngineFailures.IsFailure(ex, cancellationToken))
        {
            Error = EngineFailures.Describe(ex, Connection);
        }
    }
}
