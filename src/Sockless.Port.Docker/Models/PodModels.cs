using System.Text.Json;
using System.Text.Json.Serialization;

namespace Sockless.Port.Docker.Models;

/// <summary>Pod states as libpod names them.</summary>
public static class PodStates
{
    public const string Created = "Created";
    public const string Running = "Running";

    /// <summary>Some containers run and others don't.</summary>
    public const string Degraded = "Degraded";

    public const string Paused = "Paused";
    public const string Exited = "Exited";
    public const string Stopped = "Stopped";
    public const string Error = "Error";

    /// <summary>A container of the pod runs, so stop applies (and start may still start the rest).</summary>
    public static bool IsActive(string? state) => state is Running or Degraded or Paused;
}

/// <summary>A pod, from libpod's <c>GET /libpod/pods/json</c>.</summary>
public sealed record PodSummary
{
    public required string Id { get; init; }
    public required string Name { get; init; }

    /// <summary>RFC 3339.</summary>
    public string? Created { get; init; }

    /// <summary>See <see cref="PodStates"/>.</summary>
    public string? Status { get; init; }

    /// <summary>The infra container holding the shared namespaces; empty for a pod created without one.</summary>
    public string? InfraId { get; init; }

    /// <summary>The pod's containers, its infra container included.</summary>
    public IReadOnlyList<PodSummaryContainer>? Containers { get; init; }

    /// <summary>Networks the infra container joined; empty with host networking.</summary>
    public IReadOnlyList<string>? Networks { get; init; }

    public IReadOnlyDictionary<string, string>? Labels { get; init; }

    public bool IsInfra(string containerId) => InfraId is { Length: > 0 } infra && infra == containerId;

    /// <summary>Containers other than the infra container: what the pod runs.</summary>
    public IEnumerable<PodSummaryContainer> Workloads => (Containers ?? []).Where(c => !IsInfra(c.Id));
}

public sealed record PodSummaryContainer
{
    public required string Id { get; init; }

    /// <summary>One name, without a leading slash (libpod's field is plural but holds a string).</summary>
    public string? Names { get; init; }

    /// <summary>Lower case, like a container's state: <c>running</c>, <c>created</c>, <c>exited</c>.</summary>
    public string? Status { get; init; }

    public int RestartCount { get; init; }

    public string DisplayName => Names is { Length: > 0 } name ? name : Id[..Math.Min(12, Id.Length)];
}

/// <summary>A pod, from libpod's <c>GET /libpod/pods/{name}/json</c>.</summary>
public sealed record PodInspect
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public string? Created { get; init; }

    /// <summary>The command that created it, e.g. <c>podman pod create --name web -p 8080:80</c>.</summary>
    public IReadOnlyList<string>? CreateCommand { get; init; }

    /// <summary><c>continue</c> (the infra container keeps running when the others exit) or <c>stop</c> (the pod stops once the last one exits).</summary>
    public string? ExitPolicy { get; init; }

    /// <summary>See <see cref="PodStates"/>.</summary>
    public string? State { get; init; }

    public string? Hostname { get; init; }
    public IReadOnlyDictionary<string, string>? Labels { get; init; }
    public string? CgroupParent { get; init; }
    public bool CreateInfra { get; init; }

    [JsonPropertyName("InfraContainerID")]
    public string? InfraContainerId { get; init; }

    public PodInfraConfig? InfraConfig { get; init; }

    /// <summary>Namespaces the containers share, e.g. <c>ipc</c>, <c>net</c>, <c>uts</c>.</summary>
    public IReadOnlyList<string>? SharedNamespaces { get; init; }

    public int NumContainers { get; init; }
    public IReadOnlyList<PodInspectContainer>? Containers { get; init; }

    /// <summary><c>no</c>, <c>on-failure</c>, <c>always</c> or <c>unless-stopped</c>, applied to the pod's containers. Empty when unset.</summary>
    public string? RestartPolicy { get; init; }

    public bool IsInfra(string containerId) => InfraContainerId is { Length: > 0 } infra && infra == containerId;
}

public sealed record PodInspectContainer
{
    public required string Id { get; init; }
    public string? Name { get; init; }
    public string? State { get; init; }
}

/// <summary>The infra container's settings: what the pod's containers share.</summary>
public sealed record PodInfraConfig
{
    /// <summary>Published ports, by container port (<c>80/tcp</c>).</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<PortBinding>?>? PortBindings { get; init; }

    public bool HostNetwork { get; init; }

    [JsonPropertyName("StaticIP")]
    public string? StaticIp { get; init; }

    public IReadOnlyList<string>? Networks { get; init; }

    [JsonPropertyName("DNSServer")]
    public IReadOnlyList<string>? DnsServer { get; init; }
}

/// <summary>The answer to <c>DELETE /libpod/pods/{name}</c>.</summary>
public sealed record PodRemoveReport
{
    public string? Id { get; init; }

    /// <summary>Removed containers by ID; a value is that container's error, null when it went.</summary>
    public IReadOnlyDictionary<string, JsonElement?>? RemovedCtrs { get; init; }
}

/// <summary>Body of <c>POST /libpod/pods/create</c>. Libpod's spec uses lower-case names.</summary>
public sealed record PodCreateRequest
{
    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("labels")]
    public IReadOnlyDictionary<string, string>? Labels { get; init; }
}

/// <summary>Body of <c>POST /libpod/containers/create</c>, reduced to running a command in a pod.</summary>
public sealed record PodContainerCreateRequest
{
    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("image")]
    public required string Image { get; init; }

    [JsonPropertyName("pod")]
    public required string Pod { get; init; }

    [JsonPropertyName("command")]
    public IReadOnlyList<string>? Command { get; init; }
}
