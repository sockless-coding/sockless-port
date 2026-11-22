using Microsoft.AspNetCore.Mvc;
using Sockless.Port.Docker.Models;
using Sockless.Port.Engines;
using Sockless.Port.Swarm;
using Sockless.Port.Web.Infrastructure;

namespace Sockless.Port.Web.Pages.Engine.Swarm.Services;

/// <summary>The swarm's services, filterable by stack, live as they change.</summary>
public sealed class IndexModel(EngineConnections connections) : SwarmPageModel(connections)
{
    [BindProperty(SupportsGet = true)]
    public string? Q { get; set; }

    /// <summary>A stack's name; <c>-</c> for services on their own.</summary>
    [BindProperty(SupportsGet = true)]
    public string? Stack { get; set; }

    public IReadOnlyList<SwarmService> Rows { get; private set; } = [];

    public int Total { get; private set; }

    /// <summary>The stacks there are, for the filter.</summary>
    public IReadOnlyList<string> Stacks { get; private set; } = [];

    public bool Filtered => !string.IsNullOrWhiteSpace(Q) || !string.IsNullOrEmpty(Stack);

    public object Filters => new { endpointId = EndpointId, Q, Stack };

    /// <summary>Any service isn't settled yet, so the table polls: task changes on other nodes send no events here.</summary>
    public bool AnySettling => Rows.Any(SwarmDisplay.IsChanging);

    public async Task OnGetAsync(CancellationToken cancellationToken) => await LoadRowsAsync(cancellationToken);

    public async Task<IActionResult> OnGetTableAsync(CancellationToken cancellationToken)
    {
        await LoadRowsAsync(cancellationToken);
        Response.Headers["HX-Replace-Url"] = Url.Page("/Engine/Swarm/Services/Index", Filters);
        return Partial("_ServiceTable", this);
    }

    public async Task<IActionResult> OnPostRemoveAsync(string? id, string? name)
    {
        if (string.IsNullOrEmpty(id))
            Flash.Error(TempData, "Choose a service to remove.");
        else if (Manager is null)
            Flash.Error(TempData, "Services can only be removed through a swarm manager.");
        else
            await TryEngineAsync(client => client.RemoveServiceAsync(id, HttpContext.RequestAborted), $"Removed the service {name ?? id}. Its tasks stop on every node.");
        return RedirectToPage(Filters);
    }

    private async Task LoadRowsAsync(CancellationToken cancellationToken)
    {
        var services = await ReadAsync(c => c.ListServicesAsync(cancellationToken: cancellationToken), cancellationToken);
        if (services is null)
            return;
        Total = services.Count;
        Stacks = [.. services.Select(s => s.Stack).OfType<string>().Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];
        Rows = [.. services.Where(Matches).OrderBy(s => s.Stack ?? "￿", StringComparer.Ordinal).ThenBy(s => s.Name, StringComparer.Ordinal)];
    }

    private bool Matches(SwarmService service)
    {
        if (Stack == "-" ? service.Stack is not null : !string.IsNullOrEmpty(Stack) && service.Stack != Stack)
            return false;
        if (string.IsNullOrWhiteSpace(Q))
            return true;
        var q = Q.Trim();
        return service.Name.Contains(q, StringComparison.OrdinalIgnoreCase)
               || service.Id.StartsWith(q, StringComparison.OrdinalIgnoreCase)
               || service.Image?.Contains(q, StringComparison.OrdinalIgnoreCase) == true
               || service.Endpoint?.Ports?.Any(p => p.PublishedPort.ToString(System.Globalization.CultureInfo.InvariantCulture) == q) == true;
    }
}
