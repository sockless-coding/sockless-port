using Sockless.Port.Docker.Models;

namespace Sockless.Port.Docker.Streams;

/// <summary>
/// One stats reading, computed the way <c>docker stats</c> computes it. Network and block I/O
/// are cumulative byte counters; the UI derives rates from consecutive samples.
/// </summary>
public sealed record StatsSample(
    DateTimeOffset Read,
    double CpuPercent,
    long MemoryUsage,
    long MemoryLimit,
    long NetworkRx,
    long NetworkTx,
    long BlockRead,
    long BlockWrite,
    long Pids)
{
    public double MemoryPercent => MemoryLimit > 0 ? 100.0 * MemoryUsage / MemoryLimit : 0;

    public static StatsSample From(ContainerStats stats)
    {
        var read = LogReader.TryParseTimestamp(stats.Read ?? "", out var parsed) ? parsed : DateTimeOffset.UtcNow;
        return new StatsSample(
            read,
            ComputeCpuPercent(stats),
            ComputeMemoryUsage(stats.MemoryStats),
            stats.MemoryStats?.Limit ?? 0,
            stats.Networks?.Values.Sum(n => n.RxBytes) ?? 0,
            stats.Networks?.Values.Sum(n => n.TxBytes) ?? 0,
            BlockBytes(stats, "read"),
            BlockBytes(stats, "write"),
            stats.PidsStats?.Current ?? 0);
    }

    /// <summary>Share of the whole host, where 100% is one fully used CPU (so it can exceed 100).</summary>
    internal static double ComputeCpuPercent(ContainerStats stats)
    {
        if (stats.CpuStats?.CpuUsage is not { } now || stats.PreCpuStats?.CpuUsage is not { } before)
            return 0;

        var cpuDelta = (double)now.TotalUsage - before.TotalUsage;
        var systemDelta = (double)stats.CpuStats.SystemCpuUsage - stats.PreCpuStats.SystemCpuUsage;
        var cpus = stats.CpuStats.OnlineCpus > 0 ? stats.CpuStats.OnlineCpus : now.PerCpuUsage?.Count ?? 1;

        // The first sample has no previous reading (precpu is all zeros).
        if (cpuDelta <= 0 || systemDelta <= 0 || stats.PreCpuStats.SystemCpuUsage == 0)
            return 0;
        return cpuDelta / systemDelta * cpus * 100.0;
    }

    /// <summary>Usage minus the reclaimable page cache: <c>inactive_file</c> (cgroup v2) or <c>total_inactive_file</c> (v1).</summary>
    internal static long ComputeMemoryUsage(MemoryStats? memory)
    {
        if (memory is null)
            return 0;
        var stats = memory.Stats;
        var cache = stats?.GetValueOrDefault("inactive_file") is > 0 and var v2 ? v2
            : stats?.GetValueOrDefault("total_inactive_file") ?? 0;
        return cache > 0 && cache < memory.Usage ? memory.Usage - cache : memory.Usage;
    }

    private static long BlockBytes(ContainerStats stats, string op) =>
        stats.BlockIoStats?.IoServiceBytesRecursive?
            .Where(e => string.Equals(e.Op, op, StringComparison.OrdinalIgnoreCase))
            .Sum(e => e.Value) ?? 0;
}
