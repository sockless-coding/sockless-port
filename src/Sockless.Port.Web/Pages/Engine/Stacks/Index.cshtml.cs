using Microsoft.AspNetCore.Mvc;
using Sockless.Port.Engines;
using Sockless.Port.Stacks;
using Sockless.Port.Web.Infrastructure;

namespace Sockless.Port.Web.Pages.Engine.Stacks;

/// <param name="Definition">Set when Sockless Port manages the stack; null for one deployed some other way.</param>
/// <param name="Job">The running job, or the last one still kept.</param>
public sealed record StackRow(string Name, StackDefinition? Definition, ComposeProject Project, StackJob? Job)
{
    public bool IsExternal => Definition is null;
}

public sealed class IndexModel(EngineConnections connections, StackStore store, StackJobs jobs, ComposeCli compose) : EndpointPageModel(connections)
{
    [BindProperty(SupportsGet = true)]
    public string? Q { get; set; }

    public IReadOnlyList<StackRow> Rows { get; private set; } = [];

    public int Total { get; private set; }

    public string? Error { get; private set; }

    /// <summary>Why stacks can't be deployed from here, when Compose is missing.</summary>
    public string? ComposeProblem { get; private set; }

    public bool Filtered => !string.IsNullOrWhiteSpace(Q);

    public object Filters => new { endpointId = EndpointId, Q };

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        ComposeProblem = (await compose.StatusAsync(cancellationToken)).Problem;
        await LoadRowsAsync(cancellationToken);
    }

    public async Task<IActionResult> OnGetTableAsync(CancellationToken cancellationToken)
    {
        await LoadRowsAsync(cancellationToken);
        Response.Headers["HX-Replace-Url"] = Url.Page("/Engine/Stacks/Index", Filters);
        return Partial("_StackTable", this);
    }

    private async Task LoadRowsAsync(CancellationToken cancellationToken)
    {
        var definitions = await store.ListAsync(EndpointId, cancellationToken);
        IReadOnlyDictionary<string, ComposeProject> projects = new Dictionary<string, ComposeProject>();
        if (Client is { } client)
        {
            try
            {
                projects = StackInventory.Projects(await client.ListContainersAsync(all: true, cancellationToken));
            }
            catch (Exception ex) when (EngineFailures.IsFailure(ex, cancellationToken))
            {
                Error = EngineFailures.Describe(ex, Connection);
            }
        }

        var rows = definitions
            .Select(d => new StackRow(d.Name, d, projects.GetValueOrDefault(d.Name) ?? ComposeProject.Empty(d.Name), jobs.Latest(EndpointId, d.Name)))
            .Concat(projects.Values
                .Where(p => definitions.All(d => d.Name != p.Name))
                .Select(p => new StackRow(p.Name, null, p, jobs.Latest(EndpointId, p.Name))))
            .ToList();
        Total = rows.Count;
        Rows = [.. rows
            .Where(Matches)
            .OrderBy(r => r.IsExternal)
            .ThenBy(r => r.Name, StringComparer.Ordinal)];
    }

    private bool Matches(StackRow row)
    {
        if (string.IsNullOrWhiteSpace(Q))
            return true;
        var q = Q.Trim();
        return row.Name.Contains(q, StringComparison.OrdinalIgnoreCase)
               || row.Project.Services.Any(s => s.Name.Contains(q, StringComparison.OrdinalIgnoreCase) || s.Image?.Contains(q, StringComparison.OrdinalIgnoreCase) == true);
    }
}
