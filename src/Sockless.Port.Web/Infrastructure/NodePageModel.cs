using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc;
using Sockless.Port.Docker;
using Sockless.Port.Docker.Models;
using Sockless.Port.Engines;
using Sockless.Port.Swarm;

namespace Sockless.Port.Web.Infrastructure;

/// <summary>
/// Base for a node's pages (<c>/endpoints/{endpointId}/swarm/nodes/{nodeId}/…</c>): inspects the
/// node and lists the tasks placed on it, once per request.
/// </summary>
public abstract partial class NodePageModel(EngineConnections connections) : SwarmPageModel(connections)
{
    public const string RouteKey = "nodeId";

    public string NodeKey { get; private set; } = "";

    public SwarmNode? Node { get; private set; }

    public string? InspectJson { get; private set; }

    /// <summary>The node's current tasks, then a few ended ones per slot.</summary>
    public IReadOnlyList<SwarmTask> Tasks { get; private set; } = [];

    /// <summary>Service names by ID, for the tasks.</summary>
    public IReadOnlyDictionary<string, string> ServiceNames { get; private set; } = new Dictionary<string, string>();

    public string NodeId => Node?.Id ?? NodeKey;

    public string DisplayName => Node?.DisplayName ?? NodeKey;

    /// <summary>The endpoint that reaches this node itself, for its containers, logs and terminals.</summary>
    public EngineConnection? NodeEndpoint => EndpointFor(Node?.Id);

    /// <summary>This endpoint is the node, so it can't be removed from here and demoting it ends management.</summary>
    public bool IsThisNode => Node is not null && Connection.Engine?.SwarmNodeId == Node.Id;

    protected override async Task<IActionResult?> LoadAsync(CancellationToken cancellationToken)
    {
        NodeKey = RouteData.Values[RouteKey]?.ToString() ?? "";
        if (!NodeKeyPattern().IsMatch(NodeKey))
            return NotFound();
        if (Manager is not { } client)
            return null;
        try
        {
            (Node, InspectJson) = await client.InspectNodeAsync(NodeKey, cancellationToken);
            var tasks = client.ListTasksAsync(new Dictionary<string, string[]> { ["node"] = [Node.Id] }, cancellationToken);
            var services = client.ListServicesAsync(cancellationToken: cancellationToken);
            await Task.WhenAll(tasks, services);
            Tasks = SwarmInventory.Ordered(tasks.Result.Where(t => t.NodeId == Node.Id), historyPerSlot: 1);
            ServiceNames = services.Result.ToDictionary(s => s.Id, s => s.Name, StringComparer.Ordinal);
        }
        catch (DockerApiException ex) when (ex.IsNotFound)
        {
            return NotFound();
        }
        catch (Exception ex) when (EngineFailures.IsFailure(ex, cancellationToken))
        {
            Error = EngineFailures.Describe(ex, Connection);
        }
        return null;
    }

    public string ServiceName(string? serviceId) => serviceId is not null && ServiceNames.TryGetValue(serviceId, out var name) ? name : SwarmDisplay.Short(serviceId);

    [GeneratedRegex("^[a-zA-Z0-9][a-zA-Z0-9_.-]*$")]
    private static partial Regex NodeKeyPattern();
}
