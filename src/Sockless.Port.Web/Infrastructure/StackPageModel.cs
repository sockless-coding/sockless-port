using Microsoft.AspNetCore.Mvc;
using Sockless.Port.Access;
using Sockless.Port.Containers;
using Sockless.Port.Docker;
using Sockless.Port.Docker.Engine;
using Sockless.Port.Engines;
using Sockless.Port.Stacks;
using Sockless.Port.Swarm;

namespace Sockless.Port.Web.Infrastructure;

/// <summary>
/// Base for a stack's pages (<c>/endpoints/{endpointId}/stacks/{stackName}/…</c>). A stack is
/// either managed (a definition with files) or external (a Compose project or swarm stack on the
/// engine that Sockless Port didn't deploy). Unknown names are 404, unless a job on that name is
/// still shown (after a remove, its page shows how the removal went).
/// </summary>
public abstract class StackPageModel(EngineConnections connections, StackStore store, StackJobs jobs) : EndpointPageModel(connections)
{
    /// <summary>A swarm stack's changes reach the whole swarm.</summary>
    protected override EndpointPermission WritePermission => IsSwarm ? EndpointPermission.ManageSwarm : EndpointPermission.Operate;

    public override string? AuditTarget => StackName;

    public const string RouteKey = "stackName";

    public string StackName { get; private set; } = "";

    /// <summary>Set when Sockless Port manages the stack.</summary>
    public StackDefinition? Definition { get; private set; }

    /// <summary>Compose, or swarm: from the definition, else from what's on the engine.</summary>
    public StackKind Kind { get; private set; }

    public bool IsSwarm => Kind == StackKind.Swarm;

    /// <summary>A Compose stack's containers on the engine; empty while offline, when it's down, or for a swarm stack.</summary>
    public ComposeProject Project { get; private set; } = ComposeProject.Empty("");

    /// <summary>A swarm stack's services; empty while offline or when it's down.</summary>
    public SwarmStack Swarm { get; private set; } = SwarmStack.Empty("");

    /// <summary>The running job, or the last one that's still kept.</summary>
    public StackJob? Job { get; private set; }

    /// <summary>Set when the engine is online but listing failed.</summary>
    public string? Error { get; private set; }

    public bool IsManaged => Definition is not null;

    /// <summary>Sockless Port runs in this stack, so it mustn't be stopped or redeployed from here.</summary>
    public bool IsSelf { get; private set; }

    /// <summary>Something runs: containers for Compose, services for swarm.</summary>
    public bool IsDeployed => IsSwarm ? Swarm.Services.Count > 0 : Project.Containers.Count > 0;

    /// <summary>Neither managed nor on the engine: only a finished job's outcome is left to show.</summary>
    public bool IsGone => Definition is null && !IsDeployed;

    public bool IsBusy => Job?.IsRunning == true;

    /// <summary>Where the stack's files are, and where Compose runs.</summary>
    public string StackFolder => store.Files.Directory(EndpointId, StackName);

    /// <summary>Swarm stacks are only reached through a manager.</summary>
    public bool CanReachSwarm => Connection.Engine?.Has(EngineCapabilities.SwarmManager) == true;

    protected StackStore Store => store;

    protected StackJobs Jobs => jobs;

    protected override async Task<IActionResult?> LoadAsync(CancellationToken cancellationToken)
    {
        StackName = RouteData.Values[RouteKey]?.ToString() ?? "";
        if (StackNames.Problem(StackName) is not null)
            return NotFound();

        Definition = await store.GetAsync(EndpointId, StackName, cancellationToken);
        Kind = Definition?.Kind ?? StackKind.Compose;
        Project = ComposeProject.Empty(StackName);
        Swarm = SwarmStack.Empty(StackName);
        Job = jobs.Latest(EndpointId, StackName);

        if (Client is { } client)
        {
            try
            {
                if (Kind == StackKind.Compose)
                {
                    Project = await LoadProjectAsync(client, StackName, cancellationToken);
                    IsSelf = Project.IsSelf;
                }
                // An external stack is a swarm stack when it has services and no Compose containers.
                if ((Kind == StackKind.Swarm || (Definition is null && Project.Containers.Count == 0)) && CanReachSwarm)
                {
                    var swarm = await LoadSwarmAsync(client, StackName, cancellationToken);
                    if (swarm.Services.Count > 0 || Kind == StackKind.Swarm)
                    {
                        Kind = StackKind.Swarm;
                        Swarm = swarm;
                        IsSelf = await IncludesSelfAsync(client, swarm, cancellationToken);
                    }
                }
            }
            catch (Exception ex) when (EngineFailures.IsFailure(ex, cancellationToken))
            {
                Error = EngineFailures.Describe(ex, Connection);
            }
        }

        // Offline, an external stack can't be told apart from a typo; a managed one still opens.
        if (Definition is null && !IsDeployed && Job is null && Error is null)
            return NotFound();
        return null;
    }

    /// <summary>The project's containers, found by the Compose label.</summary>
    public static async Task<ComposeProject> LoadProjectAsync(DockerClient client, string name, CancellationToken cancellationToken)
    {
        var containers = await client.ListContainersAsync(
            new Dictionary<string, string[]> { ["label"] = [$"{ContainerLabels.ComposeProject}={name}"] }, all: true, cancellationToken);
        return StackInventory.Projects(containers).GetValueOrDefault(name) ?? ComposeProject.Empty(name);
    }

    /// <summary>The swarm stack's services, found by the stack label.</summary>
    public static async Task<SwarmStack> LoadSwarmAsync(DockerClient client, string name, CancellationToken cancellationToken)
    {
        var services = await client.ListServicesAsync(
            new Dictionary<string, string[]> { ["label"] = [$"{Docker.Models.ServiceSpec.StackNamespaceLabel}={name}"] }, cancellationToken);
        return new SwarmStack(name, [.. services.OrderBy(s => s.Name, StringComparer.Ordinal)]);
    }

    private static async Task<bool> IncludesSelfAsync(DockerClient client, SwarmStack swarm, CancellationToken cancellationToken)
    {
        if (swarm.Services.Count == 0)
            return false;
        var tasks = await client.ListTasksAsync(new Dictionary<string, string[]>
        {
            ["service"] = [.. swarm.Services.Select(s => s.Id)],
            ["desired-state"] = ["running"],
        }, cancellationToken);
        return SwarmInventory.IncludesSelf(tasks);
    }

    /// <summary>Starts a job, flashing why when it can't. Returns whether it started.</summary>
    protected async Task<bool> TryStartAsync(StackOperation operation, StackJobOptions? options = null)
    {
        try
        {
            Job = await jobs.StartAsync(Connection, StackName, operation, options);
            return true;
        }
        catch (StackJobRejectedException ex)
        {
            Flash.Error(TempData, ex.Message);
            return false;
        }
    }
}
