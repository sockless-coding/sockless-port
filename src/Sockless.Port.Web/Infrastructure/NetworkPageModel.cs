using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc;
using Sockless.Port.Containers;
using Sockless.Port.Docker;
using Sockless.Port.Docker.Models;
using Sockless.Port.Engines;
using Sockless.Port.Networks;

namespace Sockless.Port.Web.Infrastructure;

/// <summary>
/// Base for a network's pages (<c>/endpoints/{endpointId}/networks/{networkId}/…</c>, by ID or
/// name): inspects the network and finds the containers attached to it, once per request.
/// </summary>
public abstract partial class NetworkPageModel(EngineConnections connections) : EndpointPageModel(connections)
{
    public const string RouteKey = "networkId";

    /// <summary>The ID or name from the route.</summary>
    public string NetworkKey { get; private set; } = "";

    public NetworkSummary? Network { get; private set; }

    /// <summary>The engine's inspect JSON, verbatim.</summary>
    public string? InspectJson { get; private set; }

    /// <summary>Containers attached to the network, running or not, by name.</summary>
    public IReadOnlyList<ContainerSummary> Attached { get; private set; } = [];

    /// <summary>Containers on this engine that aren't attached and can join a network, for the connect form.</summary>
    public IReadOnlyList<ContainerSummary> Others { get; private set; } = [];

    public string? Error { get; private set; }

    /// <summary>For links and form routes: the ID once inspected, otherwise what the route had.</summary>
    public string NetworkId => Network?.Id ?? NetworkKey;

    public string DisplayName => Network?.Name ?? NetworkKey;

    protected override async Task<IActionResult?> LoadAsync(CancellationToken cancellationToken)
    {
        NetworkKey = RouteData.Values[RouteKey]?.ToString() ?? "";
        if (!NetworkKeyPattern().IsMatch(NetworkKey))
            return NotFound();

        if (Client is not { } client)
            return null;
        try
        {
            (Network, InspectJson) = await client.InspectNetworkAsync(NetworkKey, cancellationToken);
            var containers = await client.ListContainersAsync(all: true, cancellationToken);
            Attached = [.. NetworkUsage.ByNetwork(containers)[Network.Name]];
            var attached = Attached.Select(c => c.Id).ToHashSet(StringComparer.Ordinal);
            Others = [.. containers.Where(c => !attached.Contains(c.Id) && c.CanJoinNetworks && !ContainerLabels.IsHelper(c.Labels))
                .OrderBy(c => c.DisplayName, StringComparer.OrdinalIgnoreCase)];
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

    /// <summary>The network's endpoint for a container, when it's running (the engine has no endpoint for stopped ones).</summary>
    public NetworkContainer? EndpointOf(string containerId) => Network?.Containers?.GetValueOrDefault(containerId);

    [GeneratedRegex("^[a-zA-Z0-9][a-zA-Z0-9_.-]*$")]
    private static partial Regex NetworkKeyPattern();
}
