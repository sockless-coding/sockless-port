using System.Text.Json.Serialization;

namespace Sockless.Port.Docker.Models;

/// <summary>A network, from <c>GET /networks</c> (without <see cref="Containers"/>) or <c>GET /networks/{id}</c>.</summary>
public sealed record NetworkSummary
{
    /// <summary>Networks every engine creates, which can't be removed: Docker's and Podman's compatible API both name them so.</summary>
    private static readonly HashSet<string> PredefinedNames = new(StringComparer.Ordinal) { "bridge", "host", "none", "docker_gwbridge" };

    public required string Id { get; init; }
    public required string Name { get; init; }

    /// <summary><c>bridge</c>, <c>host</c>, <c>null</c>, <c>overlay</c>, <c>macvlan</c>, <c>ipvlan</c>, or a plugin's name.</summary>
    public string? Driver { get; init; }

    /// <summary><c>local</c>, or <c>swarm</c> for networks that span the cluster.</summary>
    public string? Scope { get; init; }

    public string? Created { get; init; }
    public bool Internal { get; init; }
    public bool Attachable { get; init; }

    /// <summary>Swarm's routing-mesh network.</summary>
    public bool Ingress { get; init; }

    [JsonPropertyName("EnableIPv6")]
    public bool EnableIpv6 { get; init; }

    [JsonPropertyName("IPAM")]
    public NetworkIpam? Ipam { get; init; }

    public IReadOnlyDictionary<string, string>? Options { get; init; }
    public IReadOnlyDictionary<string, string>? Labels { get; init; }

    /// <summary>Connected containers by container ID. Only filled by inspect, and only for this node's containers.</summary>
    public IReadOnlyDictionary<string, NetworkContainer>? Containers { get; init; }

    /// <summary>Built in to the engine (or to swarm mode), so it can't be removed and isn't pruned.</summary>
    public bool IsPredefined => Ingress || PredefinedNames.Contains(Name);
}

public sealed record NetworkIpam
{
    public string? Driver { get; init; }
    public IReadOnlyList<NetworkIpamConfig>? Config { get; init; }
    public IReadOnlyDictionary<string, string>? Options { get; init; }
}

public sealed record NetworkIpamConfig
{
    /// <summary>CIDR, e.g. <c>172.20.0.0/16</c>.</summary>
    public string? Subnet { get; init; }

    /// <summary>The part of the subnet containers get addresses from.</summary>
    [JsonPropertyName("IPRange")]
    public string? IpRange { get; init; }

    public string? Gateway { get; init; }
}

/// <summary>A container's endpoint in <see cref="NetworkSummary.Containers"/>.</summary>
public sealed record NetworkContainer
{
    public string? Name { get; init; }

    /// <summary>Empty on Podman.</summary>
    public string? EndpointId { get; init; }

    public string? MacAddress { get; init; }

    /// <summary>CIDR, e.g. <c>10.0.0.2/24</c>.</summary>
    [JsonPropertyName("IPv4Address")]
    public string? Ipv4Address { get; init; }

    [JsonPropertyName("IPv6Address")]
    public string? Ipv6Address { get; init; }
}

/// <summary><c>POST /networks/create</c>.</summary>
public sealed record NetworkCreateRequest
{
    public required string Name { get; init; }
    public string? Driver { get; init; }
    public bool Internal { get; init; }
    public bool Attachable { get; init; }

    [JsonPropertyName("EnableIPv6")]
    public bool EnableIpv6 { get; init; }

    [JsonPropertyName("IPAM")]
    public NetworkIpam? Ipam { get; init; }

    public IReadOnlyDictionary<string, string>? Options { get; init; }
    public IReadOnlyDictionary<string, string>? Labels { get; init; }
}

/// <summary><c>POST /networks/create</c> response.</summary>
public sealed record NetworkCreateResponse
{
    public required string Id { get; init; }

    /// <summary>Empty unless the engine warns (e.g. about a deprecated option).</summary>
    public string? Warning { get; init; }
}

/// <summary><c>POST /networks/prune</c>.</summary>
public sealed record NetworkPruneResponse
{
    public IReadOnlyList<string>? NetworksDeleted { get; init; }
}

/// <summary>A container's endpoint settings when connecting it (<c>EndpointConfig</c>).</summary>
public sealed record NetworkConnectOptions
{
    /// <summary>Extra names the container answers to on this network.</summary>
    public IReadOnlyList<string>? Aliases { get; init; }

    /// <summary>A fixed IPv4 address in the network's subnet.</summary>
    public string? Ipv4Address { get; init; }

    /// <summary>A fixed IPv6 address in the network's subnet.</summary>
    public string? Ipv6Address { get; init; }
}
