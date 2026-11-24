using Microsoft.AspNetCore.Mvc;
using Sockless.Port.Docker.Engine;
using Sockless.Port.Engines;
using Sockless.Port.Images;
using Sockless.Port.Stacks;
using Sockless.Port.Swarm;
using Sockless.Port.Web.Infrastructure;

namespace Sockless.Port.Web.Pages.Engine.Stacks;

/// <param name="Definition">Set when Sockless Port manages the stack; null for one deployed some other way.</param>
/// <param name="Project">A Compose stack's containers.</param>
/// <param name="Swarm">A swarm stack's services.</param>
/// <param name="Job">The running job, or the last one still kept.</param>
/// <param name="Updates">Containers (Compose) or services (swarm) the last update check found a newer image for.</param>
public sealed record StackRow(string Name, StackDefinition? Definition, StackKind Kind, ComposeProject Project, SwarmStack Swarm, StackJob? Job, int Updates = 0,
    StackRepository? Repository = null)
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

public sealed class IndexModel(EngineConnections connections, StackStore store, StackJobs jobs, ComposeCli compose, DockerCli docker, ImageUpdates updates)
    : EndpointPageModel(connections)
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

    /// <summary>Compares what every stack's containers and services run with their registries. Nothing is pulled.</summary>
    public async Task<IActionResult> OnPostCheckUpdatesAsync(CancellationToken cancellationToken)
    {
        await LoadRowsAsync(cancellationToken);
        if (Error is not null)
        {
            Flash.Error(TempData, Error);
            return RedirectToPage(Filters);
        }
        UpdateCheckSummary? containers = null, services = null;
        await TryEngineAsync(async client =>
        {
            containers = await updates.CheckContainersAsync(Connection, client, [.. Rows.Where(r => !r.IsSwarm).SelectMany(r => r.Project.Containers)], cancellationToken);
            services = await updates.CheckServicesAsync(Connection, client, [.. Rows.Where(r => r.IsSwarm).SelectMany(r => r.Swarm.Services)], cancellationToken);
        });
        if (containers is not null && services is not null)
            Flash.Success(TempData, Describe(containers, services));
        return RedirectToPage(Filters);
    }

    /// <summary>One sentence for both kinds; services are only mentioned when there were any.</summary>
    internal static string Describe(UpdateCheckSummary containers, UpdateCheckSummary services)
    {
        if (services.Checked == 0)
            return ImageDisplay.CheckMessage(containers, "container", "containers", "No stack has containers to check.", "each container's page");
        var swarm = ImageDisplay.CheckMessage(services, "swarm service", "swarm services", "", "the stack's page");
        return containers.Checked == 0 ? swarm
            : $"{ImageDisplay.CheckMessage(containers, "container", "containers", "", "each container's page")} {swarm}";
    }

    private async Task LoadRowsAsync(CancellationToken cancellationToken)
    {
        var definitions = await store.ListAsync(EndpointId, cancellationToken);
        var repositories = await store.RepositoriesAsync(EndpointId, cancellationToken);
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
            jobs.Latest(EndpointId, name), Updates(name, kind), definition is null ? null : repositories.GetValueOrDefault(definition.Id));
        int Updates(string name, StackKind kind) => kind == StackKind.Swarm
            ? swarmStacks.GetValueOrDefault(name)?.Services.Count(s => updates.ForService(EndpointId, s) is UpdateAvailable) ?? 0
            : projects.GetValueOrDefault(name)?.Containers.Count(c => updates.ForContainer(EndpointId, c) is UpdateAvailable) ?? 0;
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
