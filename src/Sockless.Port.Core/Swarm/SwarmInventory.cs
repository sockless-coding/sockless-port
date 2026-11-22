using Sockless.Port.Containers;
using Sockless.Port.Docker.Models;
using Sockless.Port.Stacks;

namespace Sockless.Port.Swarm;

/// <summary>A stack deployed with <c>docker stack deploy</c>: its services, found by label.</summary>
public sealed record SwarmStack(string Name, IReadOnlyList<SwarmService> Services)
{
    public static SwarmStack Empty(string name) => new(name, []);

    public ulong Running => Services.Aggregate(0UL, (sum, s) => sum + (s.ServiceStatus?.RunningTasks ?? 0));

    public ulong Desired => Services.Aggregate(0UL, (sum, s) => sum + (s.ServiceStatus?.DesiredTasks ?? 0));

    /// <summary>Like a Compose project's: down when it has no services, else by tasks running against tasks wanted.</summary>
    public StackState State => Services.Count == 0 ? StackState.Down
        : Desired == 0 ? StackState.Stopped
        : Running >= Desired ? StackState.Running
        : Running == 0 ? StackState.Stopped
        : StackState.Partial;
}

/// <summary>Where a service is, as a word for its badge.</summary>
public enum ServiceState
{
    /// <summary>Every task wanted runs.</summary>
    Running,

    /// <summary>Scaled to zero.</summary>
    Stopped,

    /// <summary>Some tasks wanted aren't running (starting, failing, or no node fits).</summary>
    Converging,

    /// <summary>None of the tasks wanted runs.</summary>
    Failing,

    Updating,

    /// <summary>An update stopped after failures; it waits for someone.</summary>
    UpdatePaused,

    RollingBack,

    RollbackPaused,

    /// <summary>A job that ran to completion.</summary>
    Completed,
}

public static class SwarmInventory
{
    /// <summary>Every swarm stack with services, keyed by name.</summary>
    public static IReadOnlyDictionary<string, SwarmStack> Stacks(IEnumerable<SwarmService> services) =>
        services
            .Where(s => s.Stack is not null)
            .GroupBy(s => s.Stack!, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => new SwarmStack(g.Key, [.. g.OrderBy(s => s.Name, StringComparer.Ordinal)]), StringComparer.Ordinal);

    public static ServiceState StateOf(SwarmService service)
    {
        switch (service.UpdateStatus?.State)
        {
            case "updating":
                return ServiceState.Updating;
            case "paused":
                return ServiceState.UpdatePaused;
            case "rollback_started":
                return ServiceState.RollingBack;
            case "rollback_paused":
                return ServiceState.RollbackPaused;
        }

        var status = service.ServiceStatus;
        if (status is null)
            return ServiceState.Running;
        if (service.IsJob)
            return status.RunningTasks == 0 && status.CompletedTasks > 0 ? ServiceState.Completed : status.RunningTasks > 0 ? ServiceState.Running : ServiceState.Converging;
        if (status.DesiredTasks == 0)
            return service.IsGlobal ? ServiceState.Failing : ServiceState.Stopped;
        if (status.RunningTasks >= status.DesiredTasks)
            return ServiceState.Running;
        return status.RunningTasks == 0 ? ServiceState.Failing : ServiceState.Converging;
    }

    /// <summary>The services that use each secret, by secret ID.</summary>
    public static ILookup<string, SwarmService> SecretUsers(IEnumerable<SwarmService> services) =>
        services
            .SelectMany(s => (s.Spec?.TaskTemplate?.ContainerSpec?.Secrets ?? []).Select(r => (r.SecretId, Service: s)))
            .Where(x => x.SecretId is not null)
            .Distinct()
            .ToLookup(x => x.SecretId!, x => x.Service, StringComparer.Ordinal);

    /// <summary>The services that use each config, by config ID.</summary>
    public static ILookup<string, SwarmService> ConfigUsers(IEnumerable<SwarmService> services) =>
        services
            .SelectMany(s => (s.Spec?.TaskTemplate?.ContainerSpec?.Configs ?? []).Select(r => (r.ConfigId, Service: s)))
            .Where(x => x.ConfigId is not null)
            .Distinct()
            .ToLookup(x => x.ConfigId!, x => x.Service, StringComparer.Ordinal);

    /// <summary>The services attached to each network, by network ID.</summary>
    public static ILookup<string, SwarmService> NetworkUsers(IEnumerable<SwarmService> services) =>
        services
            .SelectMany(s => (s.Spec?.TaskTemplate?.Networks ?? []).Select(n => (n.Target, Service: s)))
            .Where(x => x.Target is not null)
            .Distinct()
            .ToLookup(x => x.Target!, x => x.Service, StringComparer.Ordinal);

    /// <summary>
    /// The tasks to show for a service or node: the current ones, then for each slot (or node, for
    /// global services) its most recent ended ones, newest first.
    /// </summary>
    public static IReadOnlyList<SwarmTask> Ordered(IEnumerable<SwarmTask> tasks, int historyPerSlot = 3) =>
        [.. tasks
            .GroupBy(t => t.Slot is { } slot ? $"slot:{slot}" : $"node:{t.NodeId}", StringComparer.Ordinal)
            .OrderBy(g => g.Min(t => t.Slot ?? int.MaxValue))
            .ThenBy(g => g.Key, StringComparer.Ordinal)
            .SelectMany(g =>
            {
                var newest = g.OrderByDescending(t => t.Status?.Timestamp ?? t.UpdatedAt ?? "", StringComparer.Ordinal).ToList();
                var current = newest.Where(t => t.IsCurrent).ToList();
                return current.Concat(newest.Where(t => !t.IsCurrent).Take(historyPerSlot));
            })];

    /// <summary>Sockless Port's own container is one of these tasks.</summary>
    public static bool IncludesSelf(IEnumerable<SwarmTask> tasks) => tasks.Any(t => t.ContainerId is { } id && t.IsCurrent && SelfContainer.Is(id));
}

/// <summary>What the cluster has, and how much running tasks have reserved of it.</summary>
public sealed record ClusterTotals(int Nodes, int Ready, int Managers, int Reachable, long NanoCpus, long MemoryBytes, long ReservedNanoCpus, long ReservedMemoryBytes)
{
    public static ClusterTotals From(IReadOnlyList<SwarmNode> nodes, IReadOnlyList<SwarmTask> tasks)
    {
        var ready = nodes.Where(n => n.IsReady).ToList();
        var running = tasks.Where(t => t.Status?.State == "running").ToList();
        return new ClusterTotals(
            nodes.Count,
            ready.Count,
            nodes.Count(n => n.IsManager),
            nodes.Count(n => n.IsManager && n.ManagerStatus?.Reachability == "reachable"),
            ready.Sum(n => n.Description?.Resources?.NanoCpus ?? 0),
            ready.Sum(n => n.Description?.Resources?.MemoryBytes ?? 0),
            running.Sum(t => t.Spec?.Resources?.Reservations?.NanoCpus ?? 0),
            running.Sum(t => t.Spec?.Resources?.Reservations?.MemoryBytes ?? 0));
    }
}
