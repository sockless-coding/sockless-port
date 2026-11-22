using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc;
using Sockless.Port.Containers;
using Sockless.Port.Docker;
using Sockless.Port.Docker.Models;
using Sockless.Port.Engines;
using Sockless.Port.Pods;

namespace Sockless.Port.Web.Infrastructure;

/// <summary>
/// Base for a container's pages (<c>/endpoints/{endpointId}/containers/{containerId}/…</c>): inspects
/// the container once per request. Unknown containers are 404; an offline engine renders the page
/// with <see cref="Container"/> null so it can say why.
/// </summary>
public abstract partial class ContainerPageModel(EngineConnections connections) : EndpointPageModel(connections)
{
    public override string? AuditTarget => DisplayName;

    public const string RouteKey = "containerId";

    public string ContainerId { get; private set; } = "";

    public ContainerInspect? Container { get; private set; }

    /// <summary>The engine's inspect JSON, verbatim.</summary>
    public string? InspectJson { get; private set; }

    /// <summary>Set when the engine is online but inspecting failed.</summary>
    public string? Error { get; private set; }

    public string? Stack => ContainerLabels.Stack(Container?.Config?.Labels);

    /// <summary>The Swarm service this container is a task of: the swarm replaces it, so editing it is pointless.</summary>
    public string? SwarmService => ContainerLabels.SwarmServiceName(Container?.Config?.Labels);

    /// <summary>
    /// The Podman pod the container is in. Docker's API can't create a container in a pod, so a
    /// recreated one would leave it: recreating isn't offered.
    /// </summary>
    public PodMember? Pod { get; private set; }

    public string DisplayName => Container?.DisplayName ?? ContainerId;

    public bool IsRunning => Container?.State is { Running: true, Paused: false };

    protected override async Task<IActionResult?> LoadAsync(CancellationToken cancellationToken)
    {
        ContainerId = RouteData.Values[RouteKey]?.ToString() ?? "";
        if (!ContainerIdPattern().IsMatch(ContainerId))
            return NotFound();

        if (Client is not { } client)
            return null;
        try
        {
            (Container, InspectJson) = await client.InspectContainerAsync(ContainerId, cancellationToken);
            Pod = (await PodMembership.LoadAsync(client, Connection.Engine, cancellationToken)).Of(Container.Id);
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

    /// <summary>Container ids are hex; names are [a-zA-Z0-9][a-zA-Z0-9_.-]+. Both are accepted.</summary>
    [GeneratedRegex("^[a-zA-Z0-9][a-zA-Z0-9_.-]{0,127}$")]
    private static partial Regex ContainerIdPattern();
}
