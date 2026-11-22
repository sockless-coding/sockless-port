using Sockless.Port.Docker;
using Sockless.Port.Docker.Engine;
using Sockless.Port.Docker.Models;
using Sockless.Port.Engines;

namespace Sockless.Port.Web.Infrastructure;

/// <summary>
/// Base for the swarm pages (<c>/endpoints/{endpointId}/swarm/…</c>). They read through a manager:
/// on an endpoint that isn't one, <see cref="Manager"/> is null and the page explains instead
/// (<c>_SwarmCallout</c>).
/// </summary>
public abstract class SwarmPageModel(EngineConnections connections) : EndpointPageModel(connections)
{
    protected EngineConnections Endpoints { get; } = connections;

    public bool IsSwarm => Connection.Engine?.Has(EngineCapabilities.Swarm) == true;

    public bool IsManager => Connection.Engine?.Has(EngineCapabilities.SwarmManager) == true;

    /// <summary>The client while online and a swarm manager; otherwise null.</summary>
    public DockerClient? Manager => IsManager ? Client : null;

    /// <summary>Set when a read failed.</summary>
    public string? Error { get; protected set; }

    /// <summary>Reads through the manager, turning a failure into <see cref="Error"/>. Null when there's no manager or it failed.</summary>
    protected async Task<T?> ReadAsync<T>(Func<DockerClient, Task<T>> read, CancellationToken cancellationToken) where T : class
    {
        if (Manager is not { } client)
            return null;
        try
        {
            return await read(client);
        }
        catch (Exception ex) when (EngineFailures.IsFailure(ex, cancellationToken))
        {
            Error ??= EngineFailures.Describe(ex, Connection);
            return null;
        }
    }

    /// <summary>
    /// The endpoint that reaches this swarm node directly (an agent on it, or the local engine), for
    /// its own containers, logs and terminals. Null when no online endpoint is that node.
    /// </summary>
    public EngineConnection? EndpointFor(string? nodeId)
    {
        if (nodeId is null)
            return null;
        var cluster = Connection.Engine?.SwarmClusterId;
        return Endpoints.All
            .Where(c => c.Engine is { SwarmNodeId: { } node } engine && node == nodeId
                        && (cluster is null || engine.SwarmClusterId is null || engine.SwarmClusterId == cluster))
            .OrderByDescending(c => c.Id == EndpointId)
            .FirstOrDefault();
    }

    /// <summary>Is this task's container on the node this endpoint talks to?</summary>
    public EngineConnection? EndpointFor(SwarmTask task) => task.ContainerId is null ? null : EndpointFor(task.NodeId);
}
