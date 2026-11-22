using System.Text.Json.Serialization;

namespace Sockless.Port.Docker.Models;

// Swarm objects, from a manager's API. Only what Sockless Port reads is modelled; writes that
// replace a whole spec (services) go through the raw JSON so unmodelled settings survive.

/// <summary>A swarm object's version. Every update must name the version it was based on.</summary>
public sealed record ObjectVersion
{
    public ulong Index { get; init; }
}

/// <summary><c>GET /swarm</c>: the cluster as a manager sees it.</summary>
public sealed record SwarmInspect
{
    public required string Id { get; init; }
    public ObjectVersion? Version { get; init; }
    public string? CreatedAt { get; init; }
    public string? UpdatedAt { get; init; }
    public SwarmSpecView? Spec { get; init; }

    /// <summary>Secret: anyone holding one can join the cluster. Show only on request.</summary>
    public SwarmJoinTokens? JoinTokens { get; init; }

    public bool RootRotationInProgress { get; init; }

    /// <summary>The UDP port overlay traffic uses (VXLAN), 4789 unless set at init.</summary>
    public int DataPathPort { get; init; }

    /// <summary>The address pools overlay networks get subnets from; null for the default (10.0.0.0/8).</summary>
    public IReadOnlyList<string>? DefaultAddrPool { get; init; }

    public int SubnetSize { get; init; }
}

public sealed record SwarmSpecView
{
    public string? Name { get; init; }
    public SwarmOrchestration? Orchestration { get; init; }
    public SwarmEncryption? EncryptionConfig { get; init; }
}

public sealed record SwarmOrchestration
{
    /// <summary>How many old tasks a service keeps per slot.</summary>
    public long? TaskHistoryRetentionLimit { get; init; }
}

public sealed record SwarmEncryption
{
    /// <summary>Managers need the unlock key after a restart.</summary>
    public bool AutoLockManagers { get; init; }
}

public sealed record SwarmJoinTokens
{
    public string? Worker { get; init; }
    public string? Manager { get; init; }
}

// ---------------------------------------------------------------- nodes

/// <summary>An item of <c>GET /nodes</c>, or <c>GET /nodes/{id}</c>.</summary>
public sealed record SwarmNode
{
    public required string Id { get; init; }
    public ObjectVersion? Version { get; init; }
    public string? CreatedAt { get; init; }
    public string? UpdatedAt { get; init; }
    public NodeSpec? Spec { get; init; }
    public NodeDescription? Description { get; init; }
    public NodeStatus? Status { get; init; }

    /// <summary>Set on managers only.</summary>
    public NodeManagerStatus? ManagerStatus { get; init; }

    /// <summary>The name set on the node, else its host name.</summary>
    public string DisplayName => Spec?.Name is { Length: > 0 } name ? name : Description?.Hostname ?? Id[..Math.Min(12, Id.Length)];

    public bool IsManager => Spec?.Role == "manager";

    public bool IsLeader => ManagerStatus?.Leader == true;

    public bool IsReady => Status?.State == "ready";
}

/// <summary>What can be changed on a node (<c>POST /nodes/{id}/update</c> takes the whole spec).</summary>
public sealed record NodeSpec
{
    public string? Name { get; init; }
    public IReadOnlyDictionary<string, string>? Labels { get; init; }

    /// <summary><c>worker</c> or <c>manager</c>.</summary>
    public string? Role { get; init; }

    /// <summary><c>active</c>, <c>pause</c> (no new tasks) or <c>drain</c> (tasks move elsewhere).</summary>
    public string? Availability { get; init; }
}

public sealed record NodeDescription
{
    public string? Hostname { get; init; }
    public NodePlatform? Platform { get; init; }
    public NodeResources? Resources { get; init; }
    public NodeEngine? Engine { get; init; }
}

public sealed record NodePlatform
{
    /// <summary>As the kernel says it: <c>x86_64</c>, <c>aarch64</c>.</summary>
    public string? Architecture { get; init; }

    [JsonPropertyName("OS")]
    public string? Os { get; init; }
}

public sealed record NodeResources
{
    [JsonPropertyName("NanoCPUs")]
    public long NanoCpus { get; init; }

    public long MemoryBytes { get; init; }
}

public sealed record NodeEngine
{
    public string? EngineVersion { get; init; }

    /// <summary>The engine's own labels (<c>dockerd --label</c>), unlike the node labels set through the API.</summary>
    public IReadOnlyDictionary<string, string>? Labels { get; init; }
}

public sealed record NodeStatus
{
    /// <summary><c>unknown</c>, <c>down</c>, <c>ready</c> or <c>disconnected</c>.</summary>
    public string? State { get; init; }

    public string? Message { get; init; }

    /// <summary>The address other nodes reach it at.</summary>
    public string? Addr { get; init; }
}

public sealed record NodeManagerStatus
{
    public bool Leader { get; init; }

    /// <summary><c>unknown</c>, <c>unreachable</c> or <c>reachable</c>.</summary>
    public string? Reachability { get; init; }

    /// <summary>The address nodes join at, e.g. <c>10.0.0.5:2377</c>.</summary>
    public string? Addr { get; init; }
}

// ---------------------------------------------------------------- services

/// <summary>An item of <c>GET /services</c>, or <c>GET /services/{id}</c>.</summary>
public sealed record SwarmService
{
    public required string Id { get; init; }
    public ObjectVersion? Version { get; init; }
    public string? CreatedAt { get; init; }
    public string? UpdatedAt { get; init; }
    public ServiceSpec? Spec { get; init; }

    /// <summary>The spec before the last update, which a rollback returns to.</summary>
    public ServiceSpec? PreviousSpec { get; init; }

    public ServiceEndpoint? Endpoint { get; init; }
    public ServiceUpdateStatus? UpdateStatus { get; init; }

    /// <summary>Task counts; filled only when listed with <c>status=true</c>.</summary>
    public ServiceStatusCounts? ServiceStatus { get; init; }

    public string Name => Spec?.Name ?? Id[..Math.Min(12, Id.Length)];

    /// <summary>The stack it belongs to (<c>docker stack deploy</c> sets the label).</summary>
    public string? Stack => Spec?.Labels?.GetValueOrDefault(ServiceSpec.StackNamespaceLabel) is { Length: > 0 } stack ? stack : null;

    public bool IsGlobal => Spec?.Mode?.Global is not null || Spec?.Mode?.GlobalJob is not null;

    public bool IsJob => Spec?.Mode?.ReplicatedJob is not null || Spec?.Mode?.GlobalJob is not null;

    /// <summary>The image without the digest Docker pins it to: <c>nginx:1.27</c>.</summary>
    public string? Image => ServiceSpec.ImageWithoutDigest(Spec?.TaskTemplate?.ContainerSpec?.Image);
}

public sealed record ServiceSpec
{
    public const string StackNamespaceLabel = "com.docker.stack.namespace";

    public string? Name { get; init; }
    public IReadOnlyDictionary<string, string>? Labels { get; init; }
    public TaskSpec? TaskTemplate { get; init; }
    public ServiceMode? Mode { get; init; }
    public ServiceUpdateConfig? UpdateConfig { get; init; }
    public ServiceUpdateConfig? RollbackConfig { get; init; }
    public ServiceEndpointSpec? EndpointSpec { get; init; }

    /// <summary><c>nginx:1.27@sha256:…</c> → <c>nginx:1.27</c>. A reference that is only a digest stays as it is.</summary>
    public static string? ImageWithoutDigest(string? image)
    {
        if (image is null)
            return null;
        var at = image.IndexOf('@', StringComparison.Ordinal);
        return at > 0 ? image[..at] : image;
    }
}

public sealed record TaskSpec
{
    public ContainerTaskSpec? ContainerSpec { get; init; }
    public TaskResources? Resources { get; init; }
    public TaskRestartPolicy? RestartPolicy { get; init; }
    public TaskPlacement? Placement { get; init; }
    public IReadOnlyList<NetworkAttachment>? Networks { get; init; }

    /// <summary>Bumped to restart every task without changing anything else.</summary>
    public ulong ForceUpdate { get; init; }
}

public sealed record ContainerTaskSpec
{
    public string? Image { get; init; }
    public IReadOnlyDictionary<string, string>? Labels { get; init; }

    /// <summary>The entrypoint, when overridden.</summary>
    public IReadOnlyList<string>? Command { get; init; }

    /// <summary>The command (arguments to the entrypoint), as <c>docker service create IMAGE cmd…</c> sets it.</summary>
    public IReadOnlyList<string>? Args { get; init; }

    public IReadOnlyList<string>? Env { get; init; }
    public string? Dir { get; init; }
    public string? User { get; init; }
    public string? Hostname { get; init; }

    [JsonPropertyName("TTY")]
    public bool Tty { get; init; }

    public IReadOnlyList<ServiceMount>? Mounts { get; init; }
    public IReadOnlyList<SecretReference>? Secrets { get; init; }
    public IReadOnlyList<ConfigReference>? Configs { get; init; }
}

public sealed record ServiceMount
{
    /// <summary><c>bind</c>, <c>volume</c>, <c>tmpfs</c>, <c>npipe</c> or <c>cluster</c>.</summary>
    public string? Type { get; init; }

    public string? Source { get; init; }
    public string? Target { get; init; }
    public bool ReadOnly { get; init; }
}

/// <summary>A secret given to a service's tasks, as a file under <c>/run/secrets</c>.</summary>
public sealed record SecretReference
{
    public ObjectFileTarget? File { get; init; }

    [JsonPropertyName("SecretID")]
    public string? SecretId { get; init; }

    public string? SecretName { get; init; }
}

/// <summary>A config given to a service's tasks, as a file (at <see cref="ObjectFileTarget.Name"/>, or <c>/name</c>).</summary>
public sealed record ConfigReference
{
    public ObjectFileTarget? File { get; init; }

    [JsonPropertyName("ConfigID")]
    public string? ConfigId { get; init; }

    public string? ConfigName { get; init; }
}

public sealed record ObjectFileTarget
{
    /// <summary>A file name under <c>/run/secrets</c> for secrets; a path for configs.</summary>
    public string? Name { get; init; }

    [JsonPropertyName("UID")]
    public string? Uid { get; init; }

    [JsonPropertyName("GID")]
    public string? Gid { get; init; }

    /// <summary>Permissions, as a decimal number (292 is 0444).</summary>
    public int Mode { get; init; }
}

public sealed record TaskResources
{
    public ResourceAmounts? Limits { get; init; }
    public ResourceAmounts? Reservations { get; init; }
}

public sealed record ResourceAmounts
{
    [JsonPropertyName("NanoCPUs")]
    public long NanoCpus { get; init; }

    public long MemoryBytes { get; init; }
}

public sealed record TaskRestartPolicy
{
    /// <summary><c>none</c>, <c>on-failure</c> or <c>any</c>.</summary>
    public string? Condition { get; init; }

    /// <summary>Nanoseconds.</summary>
    public long? Delay { get; init; }

    public ulong? MaxAttempts { get; init; }
}

public sealed record TaskPlacement
{
    /// <summary>E.g. <c>node.role==manager</c>, <c>node.labels.zone!=a</c>.</summary>
    public IReadOnlyList<string>? Constraints { get; init; }

    public IReadOnlyList<PlacementPreference>? Preferences { get; init; }

    /// <summary>Most replicas on one node; 0 for no limit.</summary>
    public ulong MaxReplicas { get; init; }
}

public sealed record PlacementPreference
{
    public SpreadOver? Spread { get; init; }
}

public sealed record SpreadOver
{
    /// <summary>E.g. <c>node.labels.zone</c>: tasks are spread evenly over its values.</summary>
    public string? SpreadDescriptor { get; init; }
}

public sealed record NetworkAttachment
{
    /// <summary>The network's ID (a name when created that way; the engine resolves it).</summary>
    public string? Target { get; init; }

    public IReadOnlyList<string>? Aliases { get; init; }
}

public sealed record ServiceMode
{
    public ReplicatedMode? Replicated { get; init; }

    /// <summary>One task on every node that matches the placement. An empty object when set.</summary>
    public EmptyObject? Global { get; init; }

    public ReplicatedJobMode? ReplicatedJob { get; init; }
    public EmptyObject? GlobalJob { get; init; }
}

public sealed record ReplicatedMode
{
    public ulong Replicas { get; init; }
}

public sealed record ReplicatedJobMode
{
    public ulong MaxConcurrent { get; init; }
    public ulong TotalCompletions { get; init; }
}

public sealed record ServiceUpdateConfig
{
    /// <summary>Tasks updated at once; 0 for all at once.</summary>
    public ulong Parallelism { get; init; }

    /// <summary>Nanoseconds between batches.</summary>
    public long Delay { get; init; }

    /// <summary><c>continue</c>, <c>pause</c> or (updates only) <c>rollback</c>.</summary>
    public string? FailureAction { get; init; }

    /// <summary>Nanoseconds each task is watched for failure after it starts.</summary>
    public long Monitor { get; init; }

    public double MaxFailureRatio { get; init; }

    /// <summary><c>stop-first</c> or <c>start-first</c>.</summary>
    public string? Order { get; init; }
}

public sealed record ServiceEndpointSpec
{
    /// <summary><c>vip</c> (one virtual IP) or <c>dnsrr</c> (DNS round robin).</summary>
    public string? Mode { get; init; }

    public IReadOnlyList<ServicePort>? Ports { get; init; }
}

public sealed record ServicePort
{
    public string? Name { get; init; }

    /// <summary><c>tcp</c>, <c>udp</c> or <c>sctp</c>.</summary>
    public string? Protocol { get; init; }

    public int TargetPort { get; init; }

    /// <summary>0 when the engine picks one (it then shows in <see cref="ServiceEndpoint.Ports"/>).</summary>
    public int PublishedPort { get; init; }

    /// <summary><c>ingress</c> (routing mesh, every node) or <c>host</c> (only where a task runs).</summary>
    public string? PublishMode { get; init; }
}

public sealed record ServiceEndpoint
{
    /// <summary>The ports as published, with the ones the engine picked.</summary>
    public IReadOnlyList<ServicePort>? Ports { get; init; }

    [JsonPropertyName("VirtualIPs")]
    public IReadOnlyList<ServiceVirtualIp>? VirtualIps { get; init; }
}

public sealed record ServiceVirtualIp
{
    [JsonPropertyName("NetworkID")]
    public string? NetworkId { get; init; }

    public string? Addr { get; init; }
}

public sealed record ServiceUpdateStatus
{
    /// <summary>
    /// <c>updating</c>, <c>paused</c>, <c>completed</c>, <c>rollback_started</c>,
    /// <c>rollback_paused</c> or <c>rollback_completed</c>.
    /// </summary>
    public string? State { get; init; }

    public string? StartedAt { get; init; }
    public string? CompletedAt { get; init; }
    public string? Message { get; init; }
}

public sealed record ServiceStatusCounts
{
    public ulong RunningTasks { get; init; }
    public ulong DesiredTasks { get; init; }
    public ulong CompletedTasks { get; init; }
}

/// <summary><c>POST /services/create</c> response.</summary>
public sealed record ServiceCreateResponse
{
    [JsonPropertyName("ID")]
    public required string Id { get; init; }

    public IReadOnlyList<string>? Warnings { get; init; }
}

/// <summary><c>POST /services/{id}/update</c> response.</summary>
public sealed record ServiceUpdateResponse
{
    public IReadOnlyList<string>? Warnings { get; init; }
}

// ---------------------------------------------------------------- tasks

/// <summary>An item of <c>GET /tasks</c>: one replica (or, for global services, one node's copy) of a service.</summary>
public sealed record SwarmTask
{
    public required string Id { get; init; }
    public ObjectVersion? Version { get; init; }
    public string? CreatedAt { get; init; }
    public string? UpdatedAt { get; init; }

    [JsonPropertyName("ServiceID")]
    public string? ServiceId { get; init; }

    /// <summary>The replica number; absent for global services.</summary>
    public int? Slot { get; init; }

    /// <summary>Empty until the task is assigned to a node.</summary>
    [JsonPropertyName("NodeID")]
    public string? NodeId { get; init; }

    public TaskStatus? Status { get; init; }

    /// <summary>What the orchestrator wants: <c>running</c>, <c>shutdown</c>, <c>ready</c>, ….</summary>
    public string? DesiredState { get; init; }

    public TaskSpec? Spec { get; init; }

    public string? Image => ServiceSpec.ImageWithoutDigest(Spec?.ContainerSpec?.Image);

    public string? ContainerId => Status?.ContainerStatus?.ContainerId is { Length: > 0 } id ? id : null;

    /// <summary>A task the orchestrator still wants running (current, not history).</summary>
    public bool IsCurrent => DesiredState is "running" or "ready" or "accepted" or "assigned" or "pending" or "new";
}

public sealed record TaskStatus
{
    public string? Timestamp { get; init; }

    /// <summary>
    /// <c>new</c>, <c>allocated</c>, <c>pending</c>, <c>assigned</c>, <c>accepted</c>, <c>preparing</c>,
    /// <c>ready</c>, <c>starting</c>, <c>running</c>, <c>complete</c>, <c>shutdown</c>, <c>failed</c>,
    /// <c>rejected</c>, <c>remove</c> or <c>orphaned</c>.
    /// </summary>
    public string? State { get; init; }

    public string? Message { get; init; }

    /// <summary>Why it failed, when it did.</summary>
    public string? Err { get; init; }

    public TaskContainerStatus? ContainerStatus { get; init; }
}

public sealed record TaskContainerStatus
{
    [JsonPropertyName("ContainerID")]
    public string? ContainerId { get; init; }

    [JsonPropertyName("PID")]
    public int Pid { get; init; }

    public int ExitCode { get; init; }
}

// ---------------------------------------------------------------- secrets and configs

/// <summary>An item of <c>GET /secrets</c>. The engine never returns a secret's value.</summary>
public sealed record SwarmSecret
{
    public required string Id { get; init; }
    public ObjectVersion? Version { get; init; }
    public string? CreatedAt { get; init; }
    public string? UpdatedAt { get; init; }
    public SwarmObjectSpec? Spec { get; init; }

    public string Name => Spec?.Name ?? Id;
}

/// <summary>An item of <c>GET /configs</c>. Unlike a secret, its data is readable.</summary>
public sealed record SwarmConfig
{
    public required string Id { get; init; }
    public ObjectVersion? Version { get; init; }
    public string? CreatedAt { get; init; }
    public string? UpdatedAt { get; init; }
    public SwarmObjectSpec? Spec { get; init; }

    public string Name => Spec?.Name ?? Id;
}

public sealed record SwarmObjectSpec
{
    public string? Name { get; init; }
    public IReadOnlyDictionary<string, string>? Labels { get; init; }

    /// <summary>Base64. Configs only; never filled for secrets.</summary>
    public string? Data { get; init; }

    /// <summary>A secret store plugin, when the value isn't kept by swarm itself.</summary>
    public SwarmDriver? Driver { get; init; }

    /// <summary>Go templating of the value per task.</summary>
    public SwarmDriver? Templating { get; init; }
}

public sealed record SwarmDriver
{
    public string? Name { get; init; }
}

/// <summary><c>POST /secrets/create</c> and <c>POST /configs/create</c>.</summary>
public sealed record SwarmObjectCreateRequest
{
    public required string Name { get; init; }
    public IReadOnlyDictionary<string, string>? Labels { get; init; }

    /// <summary>Base64 of the value.</summary>
    public required string Data { get; init; }
}
