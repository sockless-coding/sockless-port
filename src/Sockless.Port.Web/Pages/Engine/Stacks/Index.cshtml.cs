using Microsoft.AspNetCore.Mvc;
using Sockless.Port.Docker.Engine;
using Sockless.Port.Engines;
using Sockless.Port.Stacks;
using Sockless.Port.Swarm;
using Sockless.Port.Web.Infrastructure;

namespace Sockless.Port.Web.Pages.Engine.Stacks;

/// <param name="Definition">Set when Sockless Port manages the stack; null for one deployed some other way.</param>
/// <param name="Project">A Compose stack's containers.</param>
/// <param name="Swarm">A swarm stack's services.</param>
/// <param name="Job">The running job, or the last one still kept.</param>
public sealed record StackRow(string Name, StackDefinition? Definition, StackKind Kind, ComposeProject Project, SwarmStack Swarm, StackJob? Job)
{
    public bool IsExternal => Definition is null;

    public bool IsSwarm => Kind == StackKind.Swarm;

    public bool IsSelf => !IsSwarm && Project.IsSelf;

    /// <summary>The service names, without the stack prefix swarm adds.</summary>
    public IReadOnlyList<string> ServiceNames => IsSwarm
        ? [.. Swarm.Services.Select(s => SwarmDisplay.InStack(s.Name, Name))]
        : [.. Project.Services.Select(s => s.Name)];

    public IEnumerable<string?> Images => IsSwarm ? Swarm.Services.Select(s => s.Image) : Project.Services.Select(s => s.Image);
}

public sealed class IndexModel(EngineConnections connections, StackStore store, StackJobs jobs, ComposeCli compose, DockerCli docker) : EndpointPageModel(connections)
{
    [BindProperty(SupportsGet = true)]
    public string? Q { get; set; }

    public IReadOnlyList<StackRow> Rows { get; private set; } = [];

    public int Total { get; private set; }

    public string? Error { get; private set; }

    /// <summary>Why stacks can't be deployed from here, when Compose is missing.</summary>
    public string? ComposeProblem { get; private set; }

    /// <summary>Why swarm stacks can't be deployed from here, on a swarm manager without the Docker CLI.</summary>
    public string? DockerCliProblem { get; private set; }

    public bool IsSwarmManager => Connection.Engine?.Has(EngineCapabilities.SwarmManager) == true;

    public bool Filtered => !string.IsNullOrWhiteSpace(Q);

    public object Filters => new { endpointId = EndpointId, Q };

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        ComposeProblem = (await compose.StatusAsync(cancellationToken)).Problem;
        if (IsSwarmManager)
            DockerCliProblem = (await docker.StatusAsync(cancellationToken)).Problem;
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
        IReadOnlyDictionary<string, SwarmStack> swarmStacks = new Dictionary<string, SwarmStack>();
        if (Client is { } client)
        {
            try
            {
                projects = StackInventory.Projects(await client.ListContainersAsync(all: true, cancellationToken));
                if (IsSwarmManager)
                    swarmStacks = SwarmInventory.Stacks(await client.ListServicesAsync(cancellationToken: cancellationToken));
            }
            catch (Exception ex) when (EngineFailures.IsFailure(ex, cancellationToken))
            {
                Error = EngineFailures.Describe(ex, Connection);
            }
        }

        var managed = definitions.Select(d => d.Name).ToHashSet(StringComparer.Ordinal);
        var rows = definitions
            .Select(d => Row(d.Name, d, d.Kind))
            .Concat(projects.Values.Where(p => !managed.Contains(p.Name)).Select(p => Row(p.Name, null, StackKind.Compose)))
            .Concat(swarmStacks.Values.Where(s => !managed.Contains(s.Name) && !projects.ContainsKey(s.Name)).Select(s => Row(s.Name, null, StackKind.Swarm)))
            .ToList();

        StackRow Row(string name, StackDefinition? definition, StackKind kind) => new(name, definition, kind,
            projects.GetValueOrDefault(name) ?? ComposeProject.Empty(name),
            kind == StackKind.Swarm ? swarmStacks.GetValueOrDefault(name) ?? SwarmStack.Empty(name) : SwarmStack.Empty(name),
            jobs.Latest(EndpointId, name));
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
               || row.ServiceNames.Any(s => s.Contains(q, StringComparison.OrdinalIgnoreCase))
               || row.Images.Any(i => i?.Contains(q, StringComparison.OrdinalIgnoreCase) == true);
    }
}
