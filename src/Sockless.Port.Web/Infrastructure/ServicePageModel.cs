using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc;
using Sockless.Port.Docker;
using Sockless.Port.Docker.Models;
using Sockless.Port.Engines;
using Sockless.Port.Swarm;

namespace Sockless.Port.Web.Infrastructure;

/// <summary>
/// Base for a service's pages (<c>/endpoints/{endpointId}/swarm/services/{serviceId}/…</c>, by ID
/// or name): inspects the service and lists its tasks and the nodes, once per request.
/// </summary>
public abstract partial class ServicePageModel(EngineConnections connections) : SwarmPageModel(connections)
{
    public override string? AuditTarget => DisplayName;

    public const string RouteKey = "serviceId";

    /// <summary>The ID or name from the route.</summary>
    public string ServiceKey { get; private set; } = "";

    public SwarmService? Service { get; private set; }

    /// <summary>The engine's inspect JSON, verbatim.</summary>
    public string? InspectJson { get; private set; }

    /// <summary>The service's current tasks, then a few ended ones per slot, in slot order.</summary>
    public IReadOnlyList<SwarmTask> Tasks { get; private set; } = [];

    public IReadOnlyDictionary<string, SwarmNode> Nodes { get; private set; } = new Dictionary<string, SwarmNode>();

    /// <summary>Every network on the engine, for names and the edit form.</summary>
    public IReadOnlyList<NetworkSummary> Networks { get; private set; } = [];

    /// <summary>For links and form routes: the ID once inspected, otherwise what the route had.</summary>
    public string ServiceId => Service?.Id ?? ServiceKey;

    public string DisplayName => Service?.Name ?? ServiceKey;

    public bool IsReplicated => Service is { IsGlobal: false, IsJob: false };

    /// <summary>The service's spec as JSON: what updates start from.</summary>
    public JsonObject? SpecJson => InspectJson is null ? null : JsonNode.Parse(InspectJson)?["Spec"] as JsonObject;

    /// <summary>The service is being rolled out or is starting tasks, so its page polls often. Failing or paused ones wait for someone.</summary>
    public bool IsSettling => Service is not null && SwarmDisplay.IsChanging(Service);

    protected override async Task<IActionResult?> LoadAsync(CancellationToken cancellationToken)
    {
        ServiceKey = RouteData.Values[RouteKey]?.ToString() ?? "";
        if (!ServiceKeyPattern().IsMatch(ServiceKey))
            return NotFound();
        if (Manager is not { } client)
            return null;
        try
        {
            (Service, InspectJson) = await client.InspectServiceAsync(ServiceKey, cancellationToken);
            // Inspect doesn't carry task counts; the list does.
            if ((await client.ListServicesAsync(new Dictionary<string, string[]> { ["id"] = [Service.Id] }, cancellationToken))
                .FirstOrDefault(s => s.Id == Service.Id) is { ServiceStatus: { } status })
                Service = Service with { ServiceStatus = status };
            var tasks = client.ListTasksAsync(new Dictionary<string, string[]> { ["service"] = [Service.Id] }, cancellationToken);
            var nodes = client.ListNodesAsync(cancellationToken);
            var networks = client.ListNetworksAsync(cancellationToken);
            await Task.WhenAll(tasks, nodes, networks);
            Tasks = SwarmInventory.Ordered(tasks.Result.Where(t => t.ServiceId == Service.Id));
            Nodes = nodes.Result.ToDictionary(n => n.Id, StringComparer.Ordinal);
            Networks = networks.Result;
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

    /// <summary>A network's name from its ID (a target may already be a name).</summary>
    public string NetworkName(string? target) => Networks.FirstOrDefault(n => n.Id == target)?.Name ?? target ?? "—";

    public string NodeName(string? nodeId) => nodeId is not null && Nodes.TryGetValue(nodeId, out var node) ? node.DisplayName : SwarmDisplay.Short(nodeId);

    [GeneratedRegex("^[a-zA-Z0-9][a-zA-Z0-9_.-]*$")]
    private static partial Regex ServiceKeyPattern();
}
