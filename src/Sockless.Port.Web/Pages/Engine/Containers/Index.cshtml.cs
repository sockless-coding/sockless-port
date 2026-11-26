using Microsoft.AspNetCore.Mvc;
using Sockless.Port.Containers;
using Sockless.Port.Docker.Models;
using Sockless.Port.Engines;
using Sockless.Port.Images;
using Sockless.Port.Pods;
using Sockless.Port.Vulnerabilities;
using Sockless.Port.Web.Infrastructure;

namespace Sockless.Port.Web.Pages.Engine.Containers;

/// <param name="Pod">The Podman pod it's in, if any.</param>
/// <param name="Update">The last update check's result for what it runs, if it was checked.</param>
public sealed record ContainerRow(ContainerSummary Summary, StatusBadge State, StatusBadge? Health, string? Stack, string? SwarmService, IReadOnlyList<string> Ports,
    PodMember? Pod = null, UpdateStatus? Update = null, ScanFlag? Scan = null)
{
    public string Name => Summary.DisplayName;
    public bool Running => Summary.State is "running";

    /// <summary>State and health as the one badge the list shows.</summary>
    public StatusBadge Badge => ContainerStatus.Combined(State, Health);
}

/// <summary>The containers of one stack, or of none (<paramref name="Stack"/> null), as the list groups them.</summary>
public sealed record ContainerGroup(string? Stack, IReadOnlyList<ContainerRow> Rows)
{
    public int Running => Rows.Count(r => r.Running);

    /// <summary>A key for the group's select box: the stack's name, or empty for containers without one.</summary>
    public string Key => Stack ?? "";
}

public sealed class IndexModel(EngineConnections connections, ImageUpdates updates, ImageScans scans) : EndpointPageModel(connections)
{
    public static readonly IReadOnlyList<(string Value, string Label)> States =
        [("", "All"), ("running", "Running"), ("stopped", "Stopped"), ("paused", "Paused"), ("unhealthy", "Unhealthy"), ("update", "Update available")];

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

    /// <summary>Group headings are worth showing only when some container is in a stack.</summary>
    public bool ShowGroups => Groups.Any(g => g.Stack is not null);
    public IReadOnlyList<string> Stacks { get; private set; } = [];

    /// <summary>Pods on the engine, for the filter; empty on engines without pods.</summary>
    public IReadOnlyList<Docker.Models.PodSummary> PodChoices { get; private set; } = [];
    public int Total { get; private set; }
    public string? Error { get; private set; }

    public bool Filtered => !string.IsNullOrEmpty(State) || !string.IsNullOrWhiteSpace(Q) || !string.IsNullOrEmpty(Stack) || !string.IsNullOrEmpty(Pod) || !string.IsNullOrWhiteSpace(Label);

    public object Filters => new { endpointId = EndpointId, State, Q, Stack, Pod, Label };

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
            var pods = await PodMembership.LoadAsync(client, Connection.Engine, cancellationToken);
            Total = all.Count;
            Stacks = [.. all.Select(c => ContainerLabels.Stack(c.Labels)).OfType<string>().Distinct().Order(StringComparer.OrdinalIgnoreCase)];
            PodChoices = [.. pods.Pods.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)];
            var shown = all.Where(c => Matches(c, pods.Of(c.Id))).ToList();
            var scanned = await scans.SummariesAsync(shown.Select(c => c.ImageId).OfType<string>(), cancellationToken);
            Rows = [.. shown.Select(c => ToRow(c, pods.Of(c.Id), scanned)).OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase)];
            Groups = [.. Rows.GroupBy(r => r.Stack)
                .OrderBy(g => g.Key is null)
                .ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
                .Select(g => new ContainerGroup(g.Key, [.. g]))];
        }
        catch (Exception ex) when (EngineFailures.IsFailure(ex, cancellationToken))
        {
            Error = EngineFailures.Describe(ex, Connection);
        }
    }

    private bool Matches(ContainerSummary c, PodMember? pod)
    {
        var stateMatches = State switch
        {
            "running" => c.State is "running",
            "stopped" => c.State is "exited" or "created" or "dead",
            "paused" => c.State is "paused",
            "unhealthy" => c.IsUnhealthy,
            "update" => updates.ForContainer(EndpointId, c) is UpdateAvailable,
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

        if (!string.IsNullOrEmpty(Pod) && pod?.Pod.Id != Pod)
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

    private ContainerRow ToRow(ContainerSummary c, PodMember? pod, IReadOnlyDictionary<string, ScanSummary> scanned)
    {
        var (state, health) = ContainerStatus.FromSummary(c);
        return new ContainerRow(c, state, health, ContainerLabels.Stack(c.Labels), ContainerLabels.SwarmServiceName(c.Labels), ContainerStatus.CompactPorts(c.Ports), pod,
            updates.ForContainer(EndpointId, c),
            c.ImageId is { } imageId ? new ScanFlag(scanned.GetValueOrDefault(imageId), scans.StateOf(imageId)) : null);
    }
}
