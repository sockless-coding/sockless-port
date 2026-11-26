using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Sockless.Port.Access;
using Sockless.Port.Engines;
using Sockless.Port.Web.Infrastructure;

namespace Sockless.Port.Web.Pages.All;

/// <summary>
/// The stacks of every endpoint the user can see, with the endpoint each is on. Offline endpoints
/// still list the stacks Sockless Port manages there. A swarm's stacks are listed once, though
/// every manager of the swarm is an endpoint that lists them.
/// </summary>
public sealed class StacksModel(EngineConnections connections, StackListing listing) : PageModel
{
    [BindProperty(SupportsGet = true)]
    public string? Q { get; set; }

    /// <summary>Only this endpoint's stacks.</summary>
    [BindProperty(SupportsGet = true)]
    public int? Endpoint { get; set; }

    public IReadOnlyList<EngineConnection> Endpoints { get; private set; } = [];

    public IReadOnlyList<EngineConnection> Shown { get; private set; } = [];

    public IReadOnlyList<StackRow> Rows { get; private set; } = [];

    public IReadOnlyList<EndpointProblem> Problems { get; private set; } = [];

    public int Total { get; private set; }

    public bool Filtered => !string.IsNullOrWhiteSpace(Q) || Endpoint is not null;

    public object Filters => new { Q, Endpoint };

    public StackTableView Table => new(Rows, AcrossEndpoints: true, Shown.ToDictionary(c => c.Id, c => c.Name));

    public async Task OnGetAsync(CancellationToken cancellationToken) => await LoadAsync(cancellationToken);

    public async Task<IActionResult> OnGetTableAsync(CancellationToken cancellationToken)
    {
        await LoadAsync(cancellationToken);
        Response.Headers["HX-Replace-Url"] = Url.Page("/All/Stacks", Filters);
        return Partial("_AllStacksTable", this);
    }

    private async Task LoadAsync(CancellationToken cancellationToken)
    {
        var access = HttpContext.Access()!;
        Endpoints = HttpContext.Visible(connections);
        Shown = Endpoint is { } only ? [.. Endpoints.Where(c => c.Id == only)] : Endpoints;

        var q = Q;
        var reads = await EndpointReads.ReadAsync(Shown, (connection, _, token) => listing.LoadAsync(connection, q, token), cancellationToken, readOffline: true);
        // Offline is a problem from the read; an engine that failed mid-read is one from the listing.
        Problems = [.. reads.Select(r => r.Problem ?? (r.Value?.Error is { } error ? new EndpointProblem(r.Connection, error) : null)).OfType<EndpointProblem>()];

        var rows = reads.Where(r => r.Value is not null)
            .SelectMany(r => r.Value!.Rows.Select(row => row with
            {
                CanEdit = access.Can(r.Connection.Id, row.IsSwarm ? EndpointPermission.ManageSwarm : EndpointPermission.Operate),
            }))
            .ToList();
        var order = Shown.Select((c, i) => (c.Id, i)).ToDictionary(p => p.Id, p => p.i);
        Total = StackListing.OnePerSwarm(reads.Where(r => r.Value is not null).SelectMany(r => r.Value!.All)).Count();
        Rows = [.. StackListing.OnePerSwarm(rows)
            .OrderBy(r => r.IsExternal)
            .ThenBy(r => r.Name, StringComparer.Ordinal)
            .ThenBy(r => order.GetValueOrDefault(r.EndpointId))];
    }
}
