using Microsoft.AspNetCore.Mvc;
using Sockless.Port.Docker;
using Sockless.Port.Docker.Models;
using Sockless.Port.Engines;
using Sockless.Port.Swarm;
using Sockless.Port.Web.Infrastructure;

namespace Sockless.Port.Web.Pages.Engine.Swarm;

/// <param name="Command">The full <c>docker swarm join</c> command.</param>
public sealed record JoinCommand(string Role, string Command);

/// <summary>The swarm as a whole: nodes and managers, what the cluster has, and joining it.</summary>
public sealed class IndexModel(EngineConnections connections) : SwarmPageModel(connections)
{
    public SwarmInspect? Swarm { get; private set; }
    public IReadOnlyList<SwarmNode> Nodes { get; private set; } = [];
    public ClusterTotals? Totals { get; private set; }
    public int Services { get; private set; }
    public int Stacks { get; private set; }
    public int Secrets { get; private set; }
    public int Configs { get; private set; }

    /// <summary>Set only after "Show join commands": tokens never appear on a GET.</summary>
    public IReadOnlyList<JoinCommand>? JoinCommands { get; private set; }

    /// <summary>The address nodes join at: the leader's, else any reachable manager's.</summary>
    public string? JoinAddress => Nodes.Where(n => n.IsLeader).Concat(Nodes.Where(n => n.ManagerStatus?.Reachability == "reachable"))
        .Select(n => n.ManagerStatus?.Addr).FirstOrDefault(a => !string.IsNullOrEmpty(a));

    public IReadOnlyList<SwarmNode> Managers => [.. Nodes.Where(n => n.IsManager).OrderByDescending(n => n.IsLeader).ThenBy(n => n.DisplayName, StringComparer.OrdinalIgnoreCase)];

    public async Task OnGetAsync(CancellationToken cancellationToken) => await LoadClusterAsync(cancellationToken);

    /// <summary>htmx refresh of the overview on engine events.</summary>
    public async Task<IActionResult> OnGetOverviewAsync(CancellationToken cancellationToken)
    {
        await LoadClusterAsync(cancellationToken);
        return Partial("_ClusterOverview", this);
    }

    /// <summary>Shows the join commands, with their tokens, in this response only.</summary>
    public async Task<IActionResult> OnPostShowJoinAsync(CancellationToken cancellationToken)
    {
        await LoadClusterAsync(cancellationToken);
        if (Swarm?.JoinTokens is { } tokens && JoinAddress is { } address)
        {
            JoinCommands =
            [
                new("worker", $"docker swarm join --token {tokens.Worker} {address}"),
                new("manager", $"docker swarm join --token {tokens.Manager} {address}"),
            ];
        }
        else if (Error is null && Manager is not null)
        {
            Error = "The swarm didn't report a manager address to join at.";
        }
        // The tokens are secret: keep this response out of caches and history.
        Response.Headers.CacheControl = "no-store";
        return Page();
    }

    /// <param name="role"><c>worker</c> or <c>manager</c>.</param>
    public async Task<IActionResult> OnPostRotateAsync(string? role)
    {
        if (role is not ("worker" or "manager"))
        {
            Flash.Error(TempData, "Choose the worker or the manager token.");
            return RedirectToPage(new { endpointId = EndpointId });
        }
        var which = role == "worker" ? SwarmJoinRole.Worker : SwarmJoinRole.Manager;
        if (Manager is null)
        {
            Flash.Error(TempData, "Join tokens can only be rotated through a swarm manager.");
            return RedirectToPage(new { endpointId = EndpointId });
        }
        await TryEngineAsync(client => client.RotateJoinTokenAsync(which, HttpContext.RequestAborted),
            $"Rotated the {role} join token. Nodes that already joined stay; the old token no longer works.");
        return RedirectToPage(new { endpointId = EndpointId });
    }

    private async Task LoadClusterAsync(CancellationToken cancellationToken)
    {
        if (Manager is null)
            return;
        var swarm = ReadAsync(async c => (await c.InspectSwarmAsync(cancellationToken)).Swarm, cancellationToken);
        var nodes = ReadAsync(c => c.ListNodesAsync(cancellationToken), cancellationToken);
        var tasks = ReadAsync(c => c.ListTasksAsync(new Dictionary<string, string[]> { ["desired-state"] = ["running"] }, cancellationToken), cancellationToken);
        var services = ReadAsync(c => c.ListServicesAsync(cancellationToken: cancellationToken), cancellationToken);
        var secrets = ReadAsync(c => c.ListSecretsAsync(cancellationToken), cancellationToken);
        var configs = ReadAsync(c => c.ListConfigsAsync(cancellationToken), cancellationToken);
        await Task.WhenAll(swarm, nodes, tasks, services, secrets, configs);

        Swarm = swarm.Result;
        Nodes = nodes.Result ?? [];
        Totals = ClusterTotals.From(Nodes, tasks.Result ?? []);
        Services = services.Result?.Count ?? 0;
        Stacks = services.Result is { } list ? SwarmInventory.Stacks(list).Count : 0;
        Secrets = secrets.Result?.Count ?? 0;
        Configs = configs.Result?.Count ?? 0;
    }
}
