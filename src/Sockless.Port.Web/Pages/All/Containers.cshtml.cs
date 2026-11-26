using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Sockless.Port.Access;
using Sockless.Port.Audit;
using Sockless.Port.Containers;
using Sockless.Port.Engines;
using Sockless.Port.Web.Infrastructure;

namespace Sockless.Port.Web.Pages.All;

/// <summary>
/// The containers of every endpoint the user can see, in one list grouped by stack and endpoint.
/// Endpoints are read in parallel, each with <see cref="EndpointReads.Timeout"/>: one that's
/// offline or slow is named above the list instead of holding it up.
/// </summary>
public sealed class ContainersModel(EngineConnections connections, ContainerListing listing) : PageModel
{
    [BindProperty(SupportsGet = true)]
    public string? State { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? Q { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? Stack { get; set; }

    /// <summary><c>key</c> or <c>key=value</c>.</summary>
    [BindProperty(SupportsGet = true)]
    public string? Label { get; set; }

    /// <summary>Only this endpoint's containers.</summary>
    [BindProperty(SupportsGet = true)]
    public int? Endpoint { get; set; }

    /// <summary>The endpoints the user may see, in order: the filter's choices.</summary>
    public IReadOnlyList<EngineConnection> Endpoints { get; private set; } = [];

    /// <summary>The endpoints the list reads (all of <see cref="Endpoints"/>, or the one chosen).</summary>
    public IReadOnlyList<EngineConnection> Shown { get; private set; } = [];

    public IReadOnlyList<ContainerGroup> Groups { get; private set; } = [];

    /// <summary>The endpoints whose containers aren't in the list, and why.</summary>
    public IReadOnlyList<EndpointProblem> Problems { get; private set; } = [];

    public IReadOnlyList<string> Stacks { get; private set; } = [];

    public int Count { get; private set; }

    public int Total { get; private set; }

    private ContainerFilter Filter => new(State, Q, Stack, Label: Label);

    public bool Filtered => Filter.IsSet || Endpoint is not null;

    public object Filters => new { State, Q, Stack, Label, Endpoint };

    public ContainerTableView Table => new(Groups, AcrossEndpoints: true,
        (row, action) => Url.Page("/All/Containers", "Bulk", new { State, Q, Stack, Label, Endpoint, ids = $"{row.EndpointId}:{row.Summary.Id}", action })!);

    /// <summary>May the user act on any endpoint's containers? Shows the bulk buttons.</summary>
    public bool CanWriteAny => Groups.Any(g => g.Rows.Any(r => r.CanWrite));

    public async Task OnGetAsync(CancellationToken cancellationToken) => await LoadAsync(cancellationToken);

    public async Task<IActionResult> OnGetTableAsync(CancellationToken cancellationToken)
    {
        await LoadAsync(cancellationToken);
        Response.Headers["HX-Replace-Url"] = Url.Page("/All/Containers", Filters);
        return Partial("_AllContainersTable", this);
    }

    /// <summary>
    /// One action on containers of one or more endpoints. Ids are <c>endpointId:containerId</c>;
    /// each endpoint is checked for the operator role, and gets its own audit entry.
    /// </summary>
    public async Task<IActionResult> OnPostBulkAsync(string? action, string[] ids, bool removeVolumes, CancellationToken cancellationToken)
    {
        // The entries are written per endpoint below.
        AuditTrail.Skip(HttpContext);
        if (!Enum.TryParse<ContainerAction>(action, ignoreCase: true, out var verb) || ids.Length == 0)
        {
            Flash.Error(TempData, ids.Length == 0 ? "Select one or more containers first." : "Unknown action.");
            return RedirectToPage(Filters);
        }

        var access = HttpContext.Access()!;
        var done = new List<string>();
        var failed = new List<string>();
        foreach (var (endpointId, containerIds) in EndpointReads.ByEndpoint(ids.Take(ContainerBulk.Max)))
        {
            // Endpoints the user can't see don't exist for them.
            if (connections.Get(endpointId) is not { } connection || !access.CanView(endpointId))
            {
                failed.Add($"{containerIds.Count} on an unknown endpoint.");
                continue;
            }
            var verbText = verb.ToString().ToLowerInvariant();
            if (!access.Can(endpointId, EndpointPermission.Operate))
            {
                failed.Add($"Needs the operator role on {connection.Name}.");
                await AuditTrail.WriteAsync(HttpContext, $"Containers: {verbText}", string.Join(", ", containerIds), connection, AuditOutcome.Denied,
                    $"Needs the operator role on {connection.Name}.");
                continue;
            }
            if (connection.Client is not { } client)
            {
                failed.Add($"{connection.Name} is offline, so nothing was changed there.");
                await AuditTrail.WriteAsync(HttpContext, $"Containers: {verbText}", string.Join(", ", containerIds), connection, AuditOutcome.Failed,
                    "The engine is offline, so nothing was changed.");
                continue;
            }

            var (doneHere, failedHere) = await ContainerBulk.RunAsync(connection, client, verb, containerIds, removeVolumes, cancellationToken);
            done.AddRange(doneHere.Select(name => $"{name} on {connection.Name}"));
            failed.AddRange(failedHere.Select(message => $"{message} (on {connection.Name})"));
            await AuditTrail.WriteAsync(HttpContext, $"Containers: {verbText}",
                string.Join(", ", doneHere.Count > 0 ? doneHere : containerIds), connection,
                failedHere.Count == 0 ? AuditOutcome.Succeeded : AuditOutcome.Failed,
                failedHere.Count == 0 ? null : string.Join(" ", failedHere));
        }

        if (done.Count > 0)
            Flash.Success(TempData, $"{ContainerActions.Done(verb)} {ContainerBulk.Describe(done)}.");
        if (failed.Count > 0)
            Flash.Error(TempData, $"Couldn't {verb.ToString().ToLowerInvariant()} {string.Join(" ", failed)}");
        return RedirectToPage(Filters);
    }

    private async Task LoadAsync(CancellationToken cancellationToken)
    {
        var access = HttpContext.Access()!;
        Endpoints = HttpContext.Visible(connections);
        Shown = Endpoint is { } only ? [.. Endpoints.Where(c => c.Id == only)] : Endpoints;

        var filter = Filter;
        var reads = await EndpointReads.ReadAsync(Shown, (connection, client, token) => listing.LoadAsync(connection, client!, filter, token), cancellationToken);
        Problems = [.. reads.Select(r => r.Problem).OfType<EndpointProblem>()];
        var lists = reads.Where(r => r.Value is not null).Select(r => (r.Connection, List: r.Value!)).ToList();

        var rows = lists.SelectMany(l => l.List.Rows.Select(r => r with { CanWrite = access.Can(l.Connection.Id, EndpointPermission.Operate) })).ToList();
        Count = rows.Count;
        Total = lists.Sum(l => l.List.Total);
        Stacks = [.. lists.SelectMany(l => l.List.Stacks).Distinct(StringComparer.Ordinal).Order(StringComparer.OrdinalIgnoreCase)];
        Groups = ContainerGroup.Of(rows, Shown);
    }
}
