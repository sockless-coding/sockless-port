using Microsoft.AspNetCore.Mvc;
using Sockless.Port.Docker.Models;
using Sockless.Port.Engines;
using Sockless.Port.Web.Infrastructure;

namespace Sockless.Port.Web.Pages.Engine.Swarm.Nodes;

/// <summary>The swarm's nodes, and which of them Sockless Port reaches directly.</summary>
public sealed class IndexModel(EngineConnections connections) : SwarmPageModel(connections)
{
    public IReadOnlyList<SwarmNode> Rows { get; private set; } = [];

    /// <summary>Running tasks per node ID.</summary>
    public IReadOnlyDictionary<string, int> RunningTasks { get; private set; } = new Dictionary<string, int>();

    public async Task OnGetAsync(CancellationToken cancellationToken) => await LoadRowsAsync(cancellationToken);

    public async Task<IActionResult> OnGetTableAsync(CancellationToken cancellationToken)
    {
        await LoadRowsAsync(cancellationToken);
        return Partial("_NodeTable", this);
    }

    private async Task LoadRowsAsync(CancellationToken cancellationToken)
    {
        var nodes = ReadAsync(c => c.ListNodesAsync(cancellationToken), cancellationToken);
        var tasks = ReadAsync(c => c.ListTasksAsync(new Dictionary<string, string[]> { ["desired-state"] = ["running"] }, cancellationToken), cancellationToken);
        await Task.WhenAll(nodes, tasks);
        Rows = [.. (nodes.Result ?? [])
            .OrderByDescending(n => n.IsManager)
            .ThenBy(n => n.DisplayName, StringComparer.OrdinalIgnoreCase)];
        RunningTasks = (tasks.Result ?? [])
            .Where(t => t.Status?.State == "running" && t.NodeId is not null)
            .GroupBy(t => t.NodeId!, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
    }
}
