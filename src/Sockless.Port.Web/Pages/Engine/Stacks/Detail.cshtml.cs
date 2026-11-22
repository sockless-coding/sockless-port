using Microsoft.AspNetCore.Mvc;
using Sockless.Port.Docker.Models;
using Sockless.Port.Engines;
using Sockless.Port.Images;
using Sockless.Port.Stacks;
using Sockless.Port.Web.Infrastructure;

namespace Sockless.Port.Web.Pages.Engine.Stacks;

/// <summary>A stack's services and containers, its last job, and the handlers for its actions.</summary>
public sealed class DetailModel(EngineConnections connections, StackStore store, StackJobs jobs, ComposeCli compose, DockerCli docker, ImageUpdates updates)
    : StackPageModel(connections, store, jobs)
{
    /// <summary>The stack's tabs: route suffix → (page, label). The editor exists only for managed stacks.</summary>
    public static readonly IReadOnlyList<(string Tab, string Page, string Label)> Tabs =
    [
        ("", "/Engine/Stacks/Detail", "Overview"),
        ("editor", "/Engine/Stacks/Editor", "Editor"),
    ];

    /// <summary>The saved files differ from what was last deployed.</summary>
    public bool HasUndeployedChanges { get; private set; }

    /// <summary>Why nothing can be deployed from here: Compose (or, for swarm stacks, the Docker CLI) is missing, or this isn't a manager.</summary>
    public string? ToolProblem { get; private set; }

    /// <summary>The last update check's result for what a container runs.</summary>
    public UpdateStatus? UpdateOf(ContainerSummary container) => updates.ForContainer(EndpointId, container);

    /// <summary>The last update check's result for the digest a service is pinned to.</summary>
    public UpdateStatus? UpdateOf(SwarmService service) => updates.ForService(EndpointId, service);

    /// <summary>The results for the stack's containers or services, unchecked ones as null.</summary>
    public IReadOnlyList<UpdateStatus?> Updates => IsSwarm ? [.. Swarm.Services.Select(UpdateOf)] : [.. Project.Containers.Select(UpdateOf)];

    /// <summary>Compose services (by name) or swarm services with a newer image in the registry.</summary>
    public IReadOnlyList<string> ServicesWithUpdates => IsSwarm
        ? [.. Swarm.Services.Where(s => UpdateOf(s) is UpdateAvailable).Select(s => SwarmDisplay.InStack(s.Name, StackName))]
        : [.. Project.Services.Where(s => s.Containers.Any(c => UpdateOf(c) is UpdateAvailable)).Select(s => s.Name)];

    public async Task<IActionResult> OnGetAsync(CancellationToken cancellationToken)
    {
        await LoadStateAsync(cancellationToken);
        return Page();
    }

    /// <summary>htmx refresh of the overview on engine events and when a job ends.</summary>
    public async Task<IActionResult> OnGetOverviewAsync(CancellationToken cancellationToken)
    {
        await LoadStateAsync(cancellationToken);
        return Partial("_StackOverview", this);
    }

    /// <param name="operation">A <see cref="StackOperation"/> name: deploy, update, start, stop, restart, down or remove.</param>
    /// <param name="removeVolumes">For down and remove: delete the stack's volumes too.</param>
    /// <param name="keepRunning">For remove: only forget the stack, leaving its containers running.</param>
    public async Task<IActionResult> OnPostRunAsync(string? operation, bool removeVolumes, bool keepRunning)
    {
        if (StackDisplay.Parse(operation) is not { } op)
        {
            Flash.Error(TempData, "Choose what to do with the stack.");
            return Back();
        }

        if (op == StackOperation.Remove && keepRunning)
        {
            // Nothing to run: forget the definition, leave the containers alone.
            if (Jobs.IsBusy(EndpointId, StackName))
            {
                Flash.Error(TempData, $"Something is already running on {StackName}. Wait for it to finish.");
                return Back();
            }
            if (await Store.DeleteAsync(EndpointId, StackName, HttpContext.RequestAborted))
                Flash.Success(TempData, $"Removed the stack {StackName} from Sockless Port. Its {(IsSwarm ? "services" : "containers")} keep running and it's listed as external.");
            return RedirectToPage("/Engine/Stacks/Index", new { endpointId = EndpointId });
        }

        await TryStartAsync(op, new StackJobOptions(RemoveVolumes: removeVolumes, Down: !keepRunning));
        return Back();
    }

    /// <summary>Compares what the stack's containers or services run with their registries. Nothing is pulled.</summary>
    public async Task<IActionResult> OnPostCheckUpdatesAsync(CancellationToken cancellationToken)
    {
        if (Error is not null)
        {
            Flash.Error(TempData, Error);
            return Back();
        }
        UpdateCheckSummary? summary = null;
        await TryEngineAsync(async client => summary = IsSwarm
            ? await updates.CheckServicesAsync(Connection, client, Swarm.Services, cancellationToken)
            : await updates.CheckContainersAsync(Connection, client, Project.Containers, cancellationToken));
        if (summary is not null)
            Flash.Success(TempData, IsSwarm
                ? ImageDisplay.CheckMessage(summary, "service", "services", $"{StackName} has no services to check.", "the table")
                : ImageDisplay.CheckMessage(summary, "container", "containers", $"{StackName} has no containers to check.", "the table"));
        return Back();
    }

    private RedirectToPageResult Back() => RedirectToPage("/Engine/Stacks/Detail", new { endpointId = EndpointId, stackName = StackName });

    private async Task LoadStateAsync(CancellationToken cancellationToken)
    {
        ToolProblem = !IsSwarm ? (await compose.StatusAsync(cancellationToken)).Problem
            : !CanReachSwarm ? $"{StackName} is a swarm stack, and {Connection.Name} isn't a swarm manager."
            : (await docker.StatusAsync(cancellationToken)).Problem;
        if (Definition is null || Definition.DeployedAt is null)
            return;
        var saved = await Store.Files.ReadAsync(EndpointId, StackName, cancellationToken);
        var deployed = await Store.Files.ReadDeployedAsync(EndpointId, StackName, cancellationToken);
        HasUndeployedChanges = saved is not null && deployed is not null && saved != deployed;
    }
}
