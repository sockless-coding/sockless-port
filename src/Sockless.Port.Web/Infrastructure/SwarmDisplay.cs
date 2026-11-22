using System.Globalization;
using Sockless.Port.Docker.Models;
using Sockless.Port.Swarm;

namespace Sockless.Port.Web.Infrastructure;

/// <summary>How swarm objects read on a page. Badges always carry a word.</summary>
public static class SwarmDisplay
{
    public static StatusBadge NodeState(SwarmNode node) => node.Status?.State switch
    {
        "ready" => new("Ready", "success"),
        "down" => new("Down", "danger"),
        "disconnected" => new("Disconnected", "danger"),
        var other => new(Word(other ?? "unknown"), "warning"),
    };

    public static StatusBadge Availability(SwarmNode node) => node.Spec?.Availability switch
    {
        "active" => new("Active", ""),
        "pause" => new("Paused", "warning"),
        "drain" => new("Draining", "warning"),
        var other => new(Word(other ?? "unknown"), ""),
    };

    /// <summary>The node's role, with leader and reachability for managers.</summary>
    public static StatusBadge Role(SwarmNode node) => node switch
    {
        { IsLeader: true } => new("Leader", "info"),
        { IsManager: true, ManagerStatus.Reachability: "unreachable" } => new("Manager, unreachable", "danger"),
        { IsManager: true } => new("Manager", ""),
        _ => new("Worker", ""),
    };

    public static StatusBadge ServiceState(SwarmService service)
    {
        var counts = Replicas(service);
        return SwarmInventory.StateOf(service) switch
        {
            Swarm.ServiceState.Running => new($"Running {counts}", "success"),
            Swarm.ServiceState.Stopped => new("Scaled to 0", ""),
            Swarm.ServiceState.Converging => new($"Starting {counts}", "warning"),
            Swarm.ServiceState.Failing => new($"Not running {counts}", "danger"),
            Swarm.ServiceState.Updating => new($"Updating {counts}", "warning"),
            Swarm.ServiceState.UpdatePaused => new("Update paused", "danger"),
            Swarm.ServiceState.RollingBack => new($"Rolling back {counts}", "warning"),
            Swarm.ServiceState.RollbackPaused => new("Rollback paused", "danger"),
            Swarm.ServiceState.Completed => new("Completed", "success"),
            _ => new(counts, ""),
        };
    }

    /// <summary>Moving by itself: an update or rollback rolling, or tasks starting. Worth polling often.</summary>
    public static bool IsChanging(SwarmService service) =>
        SwarmInventory.StateOf(service) is Swarm.ServiceState.Converging or Swarm.ServiceState.Updating or Swarm.ServiceState.RollingBack;

    /// <summary><c>2/3</c>: tasks running of those wanted.</summary>
    public static string Replicas(SwarmService service) => service.ServiceStatus is { } status
        ? $"{status.RunningTasks.ToString(CultureInfo.InvariantCulture)}/{status.DesiredTasks.ToString(CultureInfo.InvariantCulture)}"
        : service.Spec?.Mode?.Replicated?.Replicas.ToString(CultureInfo.InvariantCulture) ?? "—";

    public static string Mode(SwarmService service) => service.Spec?.Mode switch
    {
        { Global: not null } => "Global",
        { ReplicatedJob: not null } => "Replicated job",
        { GlobalJob: not null } => "Global job",
        { Replicated: { } r } => $"Replicated × {r.Replicas.ToString(CultureInfo.InvariantCulture)}",
        _ => "Replicated",
    };

    public static StatusBadge TaskState(SwarmTask task) => task.Status?.State switch
    {
        "running" => new("Running", "success"),
        "complete" => new("Complete", ""),
        "shutdown" => new("Shut down", ""),
        "failed" => new("Failed", "danger"),
        "rejected" => new("Rejected", "danger"),
        "orphaned" => new("Orphaned", "danger"),
        "remove" => new("Removing", ""),
        var other => new(Word(other ?? "unknown"), "warning"),
    };

    /// <summary>What the task says about itself: its error if it has one, else its message.</summary>
    public static string? TaskMessage(SwarmTask task) =>
        task.Status?.Err is { Length: > 0 } error ? error : task.Status?.Message is { Length: > 0 } message ? message : null;

    /// <summary><c>web.2</c> for a replica, <c>web.node-1</c> for a global service's task.</summary>
    public static string TaskName(SwarmTask task, string serviceName, IReadOnlyDictionary<string, SwarmNode> nodes) =>
        task.Slot is { } slot
            ? $"{serviceName}.{slot.ToString(CultureInfo.InvariantCulture)}"
            : $"{serviceName}.{(task.NodeId is { } id && nodes.TryGetValue(id, out var node) ? node.DisplayName : Short(task.NodeId))}";

    public static string? UpdateState(SwarmService service) => service.UpdateStatus?.State switch
    {
        "updating" => "Updating",
        "paused" => "Update paused",
        "completed" => "Update completed",
        "rollback_started" => "Rolling back",
        "rollback_paused" => "Rollback paused",
        "rollback_completed" => "Rolled back",
        null or "" => null,
        var other => Word(other),
    };

    /// <summary>A published port: <c>8080→80/tcp</c>, <c>host 8080→80/tcp</c> when only on the task's node.</summary>
    public static string Port(ServicePort port) =>
        $"{(port.PublishMode == "host" ? "host " : "")}{(port.PublishedPort > 0 ? port.PublishedPort.ToString(CultureInfo.InvariantCulture) : "?")}→{port.TargetPort.ToString(CultureInfo.InvariantCulture)}/{port.Protocol ?? "tcp"}";

    /// <summary>A service's name inside its stack: <c>web_api</c> in <c>web</c> reads <c>api</c>.</summary>
    public static string InStack(string serviceName, string stack) =>
        serviceName.StartsWith(stack + "_", StringComparison.Ordinal) ? serviceName[(stack.Length + 1)..] : serviceName;

    public static string Short(string? id) => id is null ? "—" : id[..Math.Min(12, id.Length)];

    private static string Word(string value) => value.Length == 0 ? value : char.ToUpperInvariant(value[0]) + value[1..].Replace('_', ' ');
}
