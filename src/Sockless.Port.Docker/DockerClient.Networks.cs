using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Sockless.Port.Docker.Models;

namespace Sockless.Port.Docker;

public sealed partial class DockerClient
{
    public async Task<IReadOnlyList<NetworkSummary>> ListNetworksAsync(CancellationToken cancellationToken = default) =>
        await GetAsync("/networks", Json.ListNetworkSummary, cancellationToken);

    /// <summary>Inspects a network by ID or name, with its connected containers. Returns the typed view and the engine's JSON, verbatim.</summary>
    public async Task<(NetworkSummary Network, string Json)> InspectNetworkAsync(string idOrName, CancellationToken cancellationToken = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(_requestTimeout);
        using var response = await SendCoreAsync(HttpMethod.Get, $"/networks/{Escape(idOrName)}", content: null, cts.Token);
        var json = await response.Content.ReadAsStringAsync(cts.Token);
        var network = JsonSerializer.Deserialize(json, Json.NetworkSummary)
                      ?? throw new InvalidDataException($"The engine returned an empty inspect for network {idOrName}.");
        return (network, json);
    }

    public Task<NetworkCreateResponse> CreateNetworkAsync(NetworkCreateRequest request, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Post, "/networks/create", JsonContent.Create(request, Json.NetworkCreateRequest), Json.NetworkCreateResponse, cancellationToken);

    /// <summary>Removes a network. The engine refuses while containers are connected, and always for built-in networks.</summary>
    public Task RemoveNetworkAsync(string idOrName, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Delete, $"/networks/{Escape(idOrName)}", content: null, cancellationToken);

    /// <summary>Removes every user-defined network no container is connected to.</summary>
    /// <param name="label">Only networks with this label: <c>key</c> or <c>key=value</c>.</param>
    public Task<NetworkPruneResponse> PruneNetworksAsync(string? label = null, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Post, Path("/networks/prune", Query(("filters", label is null ? null : Filters(new Dictionary<string, string[]> { ["label"] = [label] })))),
            content: null, Json.NetworkPruneResponse, cancellationToken, timeout: _requestTimeout * 2);

    /// <summary>Connects a container to a network, optionally with raw endpoint settings (from a container's inspect).</summary>
    public Task ConnectNetworkAsync(string network, string containerId, JsonObject? endpointConfig = null, CancellationToken cancellationToken = default)
    {
        var body = new JsonObject { ["Container"] = containerId };
        if (endpointConfig is not null)
            body["EndpointConfig"] = endpointConfig;
        return SendAsync(HttpMethod.Post, $"/networks/{Escape(network)}/connect",
            new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"), cancellationToken);
    }

    /// <summary>Connects a container to a network with aliases or a fixed address.</summary>
    public Task ConnectNetworkAsync(string network, string containerId, NetworkConnectOptions options, CancellationToken cancellationToken = default)
    {
        var endpoint = new JsonObject();
        if (options.Aliases is { Count: > 0 } aliases)
            endpoint["Aliases"] = new JsonArray([.. aliases.Select(a => (JsonNode)JsonValue.Create(a))]);
        if (options.Ipv4Address is not null || options.Ipv6Address is not null)
        {
            var ipam = new JsonObject();
            if (options.Ipv4Address is { } v4)
                ipam["IPv4Address"] = v4;
            if (options.Ipv6Address is { } v6)
                ipam["IPv6Address"] = v6;
            endpoint["IPAMConfig"] = ipam;
        }
        return ConnectNetworkAsync(network, containerId, endpoint.Count == 0 ? null : endpoint, cancellationToken);
    }

    /// <param name="force">Disconnect even if the container is stopped and its endpoint can't be cleaned up normally.</param>
    public Task DisconnectNetworkAsync(string network, string containerId, bool force = false, CancellationToken cancellationToken = default)
    {
        var body = new JsonObject { ["Container"] = containerId };
        if (force)
            body["Force"] = true;
        return SendAsync(HttpMethod.Post, $"/networks/{Escape(network)}/disconnect",
            new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"), cancellationToken);
    }
}
