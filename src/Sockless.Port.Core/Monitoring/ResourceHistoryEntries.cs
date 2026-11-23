namespace Sockless.Port.Monitoring;

/// <summary>
/// One container's use over a minute or an hour, by name (a recreated container keeps its name,
/// not its ID). Averages are over <see cref="Samples"/> snapshots, so rows combine weighted by it.
/// </summary>
public sealed class ResourceUsageEntry
{
    public long Id { get; set; }

    public int EndpointId { get; set; }

    /// <summary>Start of the period, in Unix seconds.</summary>
    public long Time { get; set; }

    /// <summary>The period's length: <see cref="ResourceHistory.MinuteSeconds"/> or <see cref="ResourceHistory.HourSeconds"/>.</summary>
    public int Seconds { get; set; }

    public required string Name { get; set; }

    public string? Stack { get; set; }

    public int Samples { get; set; }

    /// <summary>Average, where 100 is one full CPU.</summary>
    public double Cpu { get; set; }

    public double CpuPeak { get; set; }

    public long Memory { get; set; }

    public long MemoryPeak { get; set; }

    /// <summary>Average bytes per second, as are the other rates.</summary>
    public double NetworkRx { get; set; }

    public double NetworkTx { get; set; }

    public double BlockRead { get; set; }

    public double BlockWrite { get; set; }
}

/// <summary>An endpoint's totals over a minute or an hour. Its peaks are of the totals, which the containers' peaks can't give.</summary>
public sealed class ResourceTotalEntry
{
    public long Id { get; set; }

    public int EndpointId { get; set; }

    /// <inheritdoc cref="ResourceUsageEntry.Time"/>
    public long Time { get; set; }

    /// <inheritdoc cref="ResourceUsageEntry.Seconds"/>
    public int Seconds { get; set; }

    public int Samples { get; set; }

    public double Cpu { get; set; }

    public double CpuPeak { get; set; }

    public long Memory { get; set; }

    public long MemoryPeak { get; set; }

    public double NetworkRx { get; set; }

    public double NetworkTx { get; set; }

    public double BlockRead { get; set; }

    public double BlockWrite { get; set; }

    /// <summary>The host's CPUs and memory, last seen in the period.</summary>
    public int Cpus { get; set; }

    public long HostMemory { get; set; }

    /// <summary>Most containers measured at once in the period.</summary>
    public int Containers { get; set; }
}
