using System.Text.Json.Serialization;

namespace Sockless.Port.Docker.Models;

/// <summary><c>GET /containers/{id}/json</c>. Only the fields the UI shows are modelled.</summary>
public sealed record ContainerInspect
{
    public required string Id { get; init; }

    /// <summary>With a leading slash, e.g. <c>/web</c>. Use <see cref="DisplayName"/> for display.</summary>
    public string? Name { get; init; }

    public string? Created { get; init; }
    public string? Path { get; init; }
    public IReadOnlyList<string>? Args { get; init; }
    public ContainerState? State { get; init; }

    /// <summary>The image ID (<c>sha256:…</c>). The reference the user gave is <see cref="ContainerConfig.Image"/>.</summary>
    public string? Image { get; init; }

    public int RestartCount { get; init; }
    public ContainerConfig? Config { get; init; }
    public HostConfig? HostConfig { get; init; }
    public IReadOnlyList<MountPoint>? Mounts { get; init; }
    public NetworkSettings? NetworkSettings { get; init; }

    public string DisplayName => Name?.TrimStart('/') is { Length: > 0 } name ? name : Id[..Math.Min(12, Id.Length)];
}

public sealed record ContainerState
{
    /// <summary><c>created</c>, <c>running</c>, <c>paused</c>, <c>restarting</c>, <c>removing</c>, <c>exited</c> or <c>dead</c>.</summary>
    public string? Status { get; init; }

    public bool Running { get; init; }
    public bool Paused { get; init; }
    public bool Restarting { get; init; }
    [JsonPropertyName("OOMKilled")]
    public bool OomKilled { get; init; }
    public bool Dead { get; init; }
    public int Pid { get; init; }
    public int ExitCode { get; init; }
    public string? Error { get; init; }
    public string? StartedAt { get; init; }
    public string? FinishedAt { get; init; }
    public ContainerHealth? Health { get; init; }
}

public sealed record ContainerHealth
{
    /// <summary><c>starting</c>, <c>healthy</c> or <c>unhealthy</c>.</summary>
    public string? Status { get; init; }

    public int FailingStreak { get; init; }
    public IReadOnlyList<HealthCheckResult>? Log { get; init; }
}

public sealed record HealthCheckResult
{
    public string? Start { get; init; }
    public string? End { get; init; }
    public int ExitCode { get; init; }
    public string? Output { get; init; }
}

public sealed record ContainerConfig
{
    public string? Hostname { get; init; }
    public string? User { get; init; }
    public IReadOnlyList<string>? Env { get; init; }
    public IReadOnlyList<string>? Cmd { get; init; }
    public IReadOnlyList<string>? Entrypoint { get; init; }
    public string? Image { get; init; }
    public string? WorkingDir { get; init; }
    public bool Tty { get; init; }
    public bool OpenStdin { get; init; }
    public IReadOnlyDictionary<string, string>? Labels { get; init; }
    public IReadOnlyDictionary<string, EmptyObject>? ExposedPorts { get; init; }
}

/// <summary>The engine's <c>{}</c> placeholder values (e.g. in <c>ExposedPorts</c>).</summary>
public sealed record EmptyObject;

public sealed record HostConfig
{
    public RestartPolicy? RestartPolicy { get; init; }
    public string? NetworkMode { get; init; }

    /// <summary>Memory limit in bytes; 0 for none.</summary>
    public long Memory { get; init; }

    /// <summary>CPU limit in billionths of a CPU; 0 for none.</summary>
    public long NanoCpus { get; init; }

    public bool Privileged { get; init; }
    public bool AutoRemove { get; init; }
    public IReadOnlyDictionary<string, IReadOnlyList<PortBinding>?>? PortBindings { get; init; }
}

public sealed record RestartPolicy
{
    /// <summary><c>no</c> (or empty), <c>always</c>, <c>unless-stopped</c> or <c>on-failure</c>.</summary>
    public string? Name { get; init; }

    public int MaximumRetryCount { get; init; }
}

public sealed record PortBinding
{
    public string? HostIp { get; init; }
    public string? HostPort { get; init; }
}

public sealed record MountPoint
{
    /// <summary><c>bind</c>, <c>volume</c>, <c>tmpfs</c>, <c>npipe</c> or <c>cluster</c>.</summary>
    public string? Type { get; init; }

    public string? Name { get; init; }
    public string? Source { get; init; }
    public string? Destination { get; init; }
    public string? Mode { get; init; }

    [JsonPropertyName("RW")]
    public bool ReadWrite { get; init; }
}

public sealed record NetworkSettings
{
    /// <summary>Published ports. A null value means the port is exposed but not published.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<PortBinding>?>? Ports { get; init; }

    public IReadOnlyDictionary<string, EndpointSettings>? Networks { get; init; }
}

public sealed record EndpointSettings
{
    public string? NetworkId { get; init; }
    public string? IpAddress { get; init; }
    public int IpPrefixLen { get; init; }
    public string? Gateway { get; init; }
    public string? GlobalIPv6Address { get; init; }
    public string? MacAddress { get; init; }
    public IReadOnlyList<string>? Aliases { get; init; }
}

/// <summary>An item of <c>Ports</c> in <c>GET /containers/json</c>.</summary>
public sealed record ContainerPort
{
    public string? Ip { get; init; }
    public int PrivatePort { get; init; }
    public int PublicPort { get; init; }
    public string? Type { get; init; }
}

/// <summary><c>POST /containers/create</c> response.</summary>
public sealed record ContainerCreateResponse
{
    public required string Id { get; init; }
    public IReadOnlyList<string>? Warnings { get; init; }
}

/// <summary>One sample of <c>GET /containers/{id}/stats</c>. Field names are snake_case on the wire.</summary>
public sealed record ContainerStats
{
    [JsonPropertyName("read")]
    public string? Read { get; init; }

    [JsonPropertyName("cpu_stats")]
    public CpuStats? CpuStats { get; init; }

    [JsonPropertyName("precpu_stats")]
    public CpuStats? PreCpuStats { get; init; }

    [JsonPropertyName("memory_stats")]
    public MemoryStats? MemoryStats { get; init; }

    [JsonPropertyName("networks")]
    public IReadOnlyDictionary<string, NetworkStats>? Networks { get; init; }

    [JsonPropertyName("blkio_stats")]
    public BlockIoStats? BlockIoStats { get; init; }

    [JsonPropertyName("pids_stats")]
    public PidsStats? PidsStats { get; init; }
}

public sealed record CpuStats
{
    [JsonPropertyName("cpu_usage")]
    public CpuUsage? CpuUsage { get; init; }

    [JsonPropertyName("system_cpu_usage")]
    public ulong SystemCpuUsage { get; init; }

    [JsonPropertyName("online_cpus")]
    public int OnlineCpus { get; init; }
}

public sealed record CpuUsage
{
    [JsonPropertyName("total_usage")]
    public ulong TotalUsage { get; init; }

    [JsonPropertyName("percpu_usage")]
    public IReadOnlyList<ulong>? PerCpuUsage { get; init; }
}

public sealed record MemoryStats
{
    [JsonPropertyName("usage")]
    public long Usage { get; init; }

    [JsonPropertyName("limit")]
    public long Limit { get; init; }

    [JsonPropertyName("stats")]
    public IReadOnlyDictionary<string, long>? Stats { get; init; }
}

public sealed record NetworkStats
{
    [JsonPropertyName("rx_bytes")]
    public long RxBytes { get; init; }

    [JsonPropertyName("tx_bytes")]
    public long TxBytes { get; init; }
}

public sealed record BlockIoStats
{
    [JsonPropertyName("io_service_bytes_recursive")]
    public IReadOnlyList<BlockIoEntry>? IoServiceBytesRecursive { get; init; }
}

public sealed record BlockIoEntry
{
    [JsonPropertyName("op")]
    public string? Op { get; init; }

    [JsonPropertyName("value")]
    public long Value { get; init; }
}

public sealed record PidsStats
{
    [JsonPropertyName("current")]
    public long Current { get; init; }
}

/// <summary><c>POST /containers/{id}/exec</c>.</summary>
public sealed record ExecCreateRequest
{
    public bool AttachStdin { get; init; } = true;
    public bool AttachStdout { get; init; } = true;
    public bool AttachStderr { get; init; } = true;
    public bool Tty { get; init; } = true;
    public required IReadOnlyList<string> Cmd { get; init; }
    public IReadOnlyList<string>? Env { get; init; }
    public string? User { get; init; }
    public string? WorkingDir { get; init; }
}

internal sealed record IdResponse
{
    public required string Id { get; init; }
}

internal sealed record ExecStartRequest
{
    public bool Detach { get; init; }
    public bool Tty { get; init; }
}

/// <summary><c>GET /exec/{id}/json</c>.</summary>
public sealed record ExecInspect
{
    public bool Running { get; init; }
    public int? ExitCode { get; init; }
}

/// <summary>What a command run with <see cref="DockerClient.RunExecAsync"/> printed, and how it ended.</summary>
/// <param name="ExitCode">Null when the output was truncated (the command may still be running) or the engine didn't report it.</param>
public sealed record ExecResult(string Stdout, string Stderr, int? ExitCode, bool Truncated);
