using Sockless.Port.Docker.Engine;
using Sockless.Port.Engines;
using Sockless.Port.Images;
using Sockless.Port.Stacks;
using Sockless.Port.Swarm;

namespace Sockless.Port.Web.Infrastructure;

/// <param name="EndpointId">The endpoint it's on (for swarm stacks, the manager it was read from).</param>
/// <param name="Definition">Set when Sockless Port manages the stack; null for one deployed some other way.</param>
/// <param name="Project">A Compose stack's containers.</param>
/// <param name="Swarm">A swarm stack's services.</param>
/// <param name="Job">The running job, or the last one still kept.</param>
/// <param name="Updates">Containers (Compose) or services (swarm) the last update check found a newer image for.</param>
public sealed record StackRow(int EndpointId, string Name, StackDefinition? Definition, StackKind Kind, ComposeProject Project, SwarmStack Swarm, StackJob? Job,
    int Updates = 0, StackRepository? Repository = null)
{
    public bool IsExternal => Definition is null;

    public bool IsSwarm => Kind == StackKind.Swarm;

    public bool IsSelf => !IsSwarm && Project.IsSelf;

    /// <summary>The service names, without the stack prefix swarm adds.</summary>
    public IReadOnlyList<string> ServiceNames => IsSwarm
        ? [.. Swarm.Services.Select(s => SwarmDisplay.InStack(s.Name, Name))]
        : [.. Project.Services.Select(s => s.Name)];

    /// <summary>Running containers whose health check fails (Compose only; swarm replaces them itself).</summary>
    public int Unhealthy => IsSwarm ? 0 : Project.Containers.Count(c => c.IsUnhealthy);

    public IEnumerable<string?> Images => IsSwarm ? Swarm.Services.Select(s => s.Image) : Project.Services.Select(s => s.Image);

    /// <summary>Its endpoint is online, so its state is known.</summary>
    public bool Online { get; init; }

    /// <summary>Its endpoint manages a swarm, so a swarm stack's state is known.</summary>
    public bool OnSwarmManager { get; init; }

    /// <summary>The swarm a swarm stack is in: managers of the same swarm list the same stacks.</summary>
    public string? SwarmClusterId { get; init; }

    /// <summary>May the user change it (operator for Compose, swarm operator for swarm stacks)? Set by the page.</summary>
    public bool CanEdit { get; init; }

    /// <summary>What the state column says when it can't show a state; null when it can.</summary>
    public string? Unknown => !Online ? "Unknown while offline" : IsSwarm && !OnSwarmManager ? "Unknown: not a swarm manager" : null;
}

/// <summary>One endpoint's stacks, filtered; <see cref="Error"/> when its engine couldn't be read (managed stacks are still listed).</summary>
/// <param name="All">Every stack on the endpoint, before filtering.</param>
public sealed record StackList(IReadOnlyList<StackRow> Rows, IReadOnlyList<StackRow> All, string? Error)
{
    public int Total => All.Count;
}

/// <summary>Reads an endpoint's stacks for the lists: the ones Sockless Port manages and the ones found on the engine by their labels.</summary>
public sealed class StackListing(StackStore store, StackJobs jobs, ImageUpdates updates)
{
    /// <summary>Reads the stacks. Works while offline too, from what Sockless Port stores.</summary>
    public async Task<StackList> LoadAsync(EngineConnection connection, string? q, CancellationToken cancellationToken)
    {
        var endpointId = connection.Id;
        var definitions = await store.ListAsync(endpointId, cancellationToken);
        var repositories = await store.RepositoriesAsync(endpointId, cancellationToken);
        var engine = connection.Engine;
        var swarmManager = engine?.Has(EngineCapabilities.SwarmManager) == true;
        IReadOnlyDictionary<string, ComposeProject> projects = new Dictionary<string, ComposeProject>();
        IReadOnlyDictionary<string, SwarmStack> swarmStacks = new Dictionary<string, SwarmStack>();
        string? error = null;
        if (connection.Client is { } client)
        {
            try
            {
                projects = StackInventory.Projects(await client.ListContainersAsync(all: true, cancellationToken));
                if (swarmManager)
                    swarmStacks = SwarmInventory.Stacks(await client.ListServicesAsync(cancellationToken: cancellationToken));
            }
            catch (Exception ex) when (EngineFailures.IsFailure(ex, cancellationToken))
            {
                error = EngineFailures.Describe(ex, connection);
            }
        }

        var managed = definitions.Select(d => d.Name).ToHashSet(StringComparer.Ordinal);
        var rows = definitions
            .Select(d => Row(d.Name, d, d.Kind))
            .Concat(projects.Values.Where(p => !managed.Contains(p.Name)).Select(p => Row(p.Name, null, StackKind.Compose)))
            .Concat(swarmStacks.Values.Where(s => !managed.Contains(s.Name) && !projects.ContainsKey(s.Name)).Select(s => Row(s.Name, null, StackKind.Swarm)))
            .ToList();

        StackRow Row(string name, StackDefinition? definition, StackKind kind) => new(endpointId, name, definition, kind,
            projects.GetValueOrDefault(name) ?? ComposeProject.Empty(name),
            kind == StackKind.Swarm ? swarmStacks.GetValueOrDefault(name) ?? SwarmStack.Empty(name) : SwarmStack.Empty(name),
            jobs.Latest(endpointId, name), Updates(name, kind), definition is null ? null : repositories.GetValueOrDefault(definition.Id))
        {
            Online = connection.Client is not null,
            OnSwarmManager = swarmManager,
            SwarmClusterId = kind == StackKind.Swarm ? engine?.SwarmClusterId : null,
        };
        int Updates(string name, StackKind kind) => kind == StackKind.Swarm
            ? swarmStacks.GetValueOrDefault(name)?.Services.Count(s => updates.ForService(endpointId, s) is UpdateAvailable) ?? 0
            : projects.GetValueOrDefault(name)?.Containers.Count(c => updates.ForContainer(endpointId, c) is UpdateAvailable) ?? 0;

        return new StackList([.. rows
            .Where(r => Matches(r, q))
            .OrderBy(r => r.IsExternal)
            .ThenBy(r => r.Name, StringComparer.Ordinal)], rows, error);
    }

    private static bool Matches(StackRow row, string? q)
    {
        if (string.IsNullOrWhiteSpace(q))
            return true;
        q = q.Trim();
        return row.Name.Contains(q, StringComparison.OrdinalIgnoreCase)
               || row.ServiceNames.Any(s => s.Contains(q, StringComparison.OrdinalIgnoreCase))
               || row.Images.Any(i => i?.Contains(q, StringComparison.OrdinalIgnoreCase) == true);
    }

    /// <summary>
    /// Every manager of a swarm lists the same swarm stacks: keep one row per swarm and name, the
    /// managed one if there is one (it's on the endpoint it was deployed from), else the first.
    /// </summary>
    public static IEnumerable<StackRow> OnePerSwarm(IEnumerable<StackRow> rows)
    {
        var seen = new HashSet<(string, string)>();
        var list = rows.ToList();
        foreach (var row in list.OrderBy(r => r.IsExternal))
        {
            if (row.IsSwarm && row.SwarmClusterId is { } cluster && !seen.Add((cluster, row.Name)))
                continue;
            yield return row;
        }
    }
}

/// <summary>What the stacks table shows; <see cref="AcrossEndpoints"/> adds the endpoint column.</summary>
/// <param name="Endpoints">Endpoint names by ID, for that column.</param>
public sealed record StackTableView(IReadOnlyList<StackRow> Rows, bool AcrossEndpoints, IReadOnlyDictionary<int, string> Endpoints);
