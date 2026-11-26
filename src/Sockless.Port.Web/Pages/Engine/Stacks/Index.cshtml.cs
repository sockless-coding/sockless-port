using Microsoft.AspNetCore.Mvc;
using Sockless.Port.Docker.Engine;
using Sockless.Port.Engines;
using Sockless.Port.Images;
using Sockless.Port.Stacks;
using Sockless.Port.Swarm;
using Sockless.Port.Web.Infrastructure;

namespace Sockless.Port.Web.Pages.Engine.Stacks;

public sealed class IndexModel(EngineConnections connections, StackListing listing, ComposeCli compose, DockerCli docker, ImageUpdates updates)
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

    public StackTableView Table => new(Rows, AcrossEndpoints: false, new Dictionary<int, string> { [EndpointId] = Connection.Name });

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
        var list = await listing.LoadAsync(Connection, Q, cancellationToken);
        Error = list.Error;
        Total = list.Total;
        Rows = [.. list.Rows.Select(r => r with { CanEdit = r.IsSwarm ? CanManageSwarm : CanOperate })];
    }
}
