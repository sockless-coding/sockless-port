using Microsoft.AspNetCore.Mvc;
using Sockless.Port.Containers;
using Sockless.Port.Docker.Models;
using Sockless.Port.Engines;
using Sockless.Port.Web.Infrastructure;

namespace Sockless.Port.Web.Pages.Engine.Containers;

public sealed record ContainerRow(ContainerSummary Summary, StatusBadge State, StatusBadge? Health, string? Stack, string? SwarmService, IReadOnlyList<string> Ports)
{
    public string Name => Summary.DisplayName;
    public bool Running => Summary.State is "running";
}

public sealed class IndexModel(EngineConnections connections) : EndpointPageModel(connections)
{
    public static readonly IReadOnlyList<(string Value, string Label)> States =
        [("", "All"), ("running", "Running"), ("stopped", "Stopped"), ("paused", "Paused")];

    [BindProperty(SupportsGet = true)]
    public string? State { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? Q { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? Stack { get; set; }

    /// <summary><c>key</c> or <c>key=value</c>.</summary>
    [BindProperty(SupportsGet = true)]
    public string? Label { get; set; }

    public IReadOnlyList<ContainerRow> Rows { get; private set; } = [];
    public IReadOnlyList<string> Stacks { get; private set; } = [];
    public int Total { get; private set; }
    public string? Error { get; private set; }

    public bool Filtered => !string.IsNullOrEmpty(State) || !string.IsNullOrWhiteSpace(Q) || !string.IsNullOrEmpty(Stack) || !string.IsNullOrWhiteSpace(Label);

    public object Filters => new { endpointId = EndpointId, State, Q, Stack, Label };

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

        var done = new List<string>();
        var failed = new List<string>();
        if (Client is not { } client)
        {
            Flash.Error(TempData, "The engine is offline, so nothing was changed.");
            return RedirectToPage(Filters);
        }

        // One at a time: parallel stops of dependent containers race, and the engine serialises anyway.
        foreach (var id in ids.Distinct().Take(500))
        {
            var name = await NameOfAsync(id, cancellationToken);
            try
            {
                await ContainerActions.RunAsync(client, verb, id, removeVolumes, cancellationToken);
                done.Add(name);
            }
            catch (Exception ex) when (EngineFailures.IsFailure(ex, cancellationToken))
            {
                failed.Add($"{name}: {EngineFailures.Describe(ex, Connection)}");
            }
        }

        if (done.Count > 0)
            Flash.Success(TempData, $"{ContainerActions.Done(verb)} {Describe(done)}.");
        if (failed.Count > 0)
            Flash.Error(TempData, $"Couldn't {verb.ToString().ToLowerInvariant()} {string.Join(" ", failed)}");
        return RedirectToPage(Filters);
    }

    private async Task<string> NameOfAsync(string id, CancellationToken cancellationToken)
    {
        try
        {
            return (await Client!.InspectContainerAsync(id, cancellationToken)).Container.DisplayName;
        }
        catch (Exception ex) when (EngineFailures.IsFailure(ex, cancellationToken))
        {
            return id.Length > 12 ? id[..12] : id;
        }
    }

    private static string Describe(List<string> names) =>
        names.Count <= 3 ? string.Join(", ", names) : $"{names.Count} containers";

    private async Task LoadRowsAsync(CancellationToken cancellationToken)
    {
        if (Client is not { } client)
            return;
        try
        {
            var all = await client.ListContainersAsync(all: true, cancellationToken);
            Total = all.Count;
            Stacks = [.. all.Select(c => ContainerLabels.Stack(c.Labels)).OfType<string>().Distinct().Order(StringComparer.OrdinalIgnoreCase)];
            Rows = [.. all.Where(Matches).Select(ToRow).OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase)];
        }
        catch (Exception ex) when (EngineFailures.IsFailure(ex, cancellationToken))
        {
            Error = EngineFailures.Describe(ex, Connection);
        }
    }

    private bool Matches(ContainerSummary c)
    {
        var stateMatches = State switch
        {
            "running" => c.State is "running",
            "stopped" => c.State is "exited" or "created" or "dead",
            "paused" => c.State is "paused",
            _ => true,
        };
        if (!stateMatches)
            return false;

        if (!string.IsNullOrWhiteSpace(Q))
        {
            var q = Q.Trim();
            if (!c.DisplayName.Contains(q, StringComparison.OrdinalIgnoreCase)
                && c.Image?.Contains(q, StringComparison.OrdinalIgnoreCase) != true
                && !c.Id.StartsWith(q, StringComparison.OrdinalIgnoreCase))
                return false;
        }

        if (!string.IsNullOrEmpty(Stack) && ContainerLabels.Stack(c.Labels) != Stack)
            return false;

        if (!string.IsNullOrWhiteSpace(Label))
        {
            var equals = Label.IndexOf('=', StringComparison.Ordinal);
            var key = (equals < 0 ? Label : Label[..equals]).Trim();
            if (c.Labels?.TryGetValue(key, out var value) != true)
                return false;
            if (equals >= 0 && value != Label[(equals + 1)..].Trim())
                return false;
        }
        return true;
    }

    private static ContainerRow ToRow(ContainerSummary c)
    {
        var (state, health) = ContainerStatus.FromSummary(c);
        return new ContainerRow(c, state, health, ContainerLabels.Stack(c.Labels), ContainerLabels.SwarmServiceName(c.Labels), ContainerStatus.PublishedPorts(c.Ports));
    }
}
