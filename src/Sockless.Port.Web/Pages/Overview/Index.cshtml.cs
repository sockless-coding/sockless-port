using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Sockless.Port.Engines;
using Sockless.Port.Monitoring;
using Sockless.Port.Overview;
using Sockless.Port.Web.Infrastructure;

namespace Sockless.Port.Web.Pages.Overview;

/// <summary>
/// Every endpoint the user can see on one page. The page lists them from their connection state;
/// each row then loads its summary on its own (<see cref="OnGetRowAsync"/>), so a slow engine
/// doesn't hold up the others, and refreshes on its endpoint's events.
/// </summary>
public sealed class IndexModel(EngineConnections connections, EndpointSummaries summaries, ResourceHistory history) : PageModel
{
    /// <summary>The endpoints the user may see, in order.</summary>
    public IReadOnlyList<EngineConnection> Connections => HttpContext.Visible(connections);

    /// <summary>Whether resource use is being recorded, which is what keeps every endpoint sampled.</summary>
    public bool HistoryEnabled => history.Enabled;

    public void OnGet()
    {
    }

    /// <summary>The list, again when an endpoint the page doesn't show changes state (one was added or given to the user).</summary>
    public IActionResult OnGetTable() => Partial("_OverviewTable", this);

    /// <summary>
    /// One endpoint's row. An endpoint that's gone, or the user can no longer see, answers an
    /// empty body so the row disappears.
    /// </summary>
    public async Task<IActionResult> OnGetRowAsync(int id, CancellationToken cancellationToken)
    {
        if (connections.Get(id) is not { } connection || !HttpContext.CanView(connection))
            return Content("", "text/html");
        return Partial("_OverviewRow", await LoadRowAsync(connection, cancellationToken));
    }

    private async Task<OverviewRow> LoadRowAsync(EngineConnection connection, CancellationToken cancellationToken)
    {
        try
        {
            return new OverviewRow(connection, HistoryEnabled, Loaded: true, await summaries.SummarizeAsync(connection, cancellationToken), null);
        }
        catch (Exception ex) when (EngineFailures.IsFailure(ex, cancellationToken))
        {
            return new OverviewRow(connection, HistoryEnabled, Loaded: true, null, EngineFailures.Describe(ex, connection));
        }
    }
}

/// <summary>An endpoint's row on the overview.</summary>
/// <param name="Loaded">False for the placeholder the page renders before the row loads itself.</param>
/// <param name="Summary">Null while the endpoint is offline, or when reading it failed (<paramref name="Error"/>).</param>
public sealed record OverviewRow(EngineConnection Connection, bool HistoryEnabled, bool Loaded, EndpointSummary? Summary, string? Error);
