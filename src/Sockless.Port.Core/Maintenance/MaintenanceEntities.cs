namespace Sockless.Port.Maintenance;

/// <summary>Which images a scheduled prune removes.</summary>
public enum PruneImages
{
    None,

    /// <summary>Untagged leftovers of rebuilds and re-pulls.</summary>
    Dangling,

    /// <summary>Every image no container uses, tagged or not.</summary>
    Unused,
}

/// <summary>Which volumes a scheduled prune removes.</summary>
public enum PruneVolumes
{
    None,

    /// <summary>Unused volumes without a name of their own (Docker API 1.42 and later).</summary>
    Anonymous,

    /// <summary>Every unused volume, named ones too: their data is lost.</summary>
    All,
}

/// <summary>What a scheduled prune of one endpoint removes, and how often.</summary>
public sealed class PruneSchedule
{
    public int Id { get; set; }

    public int EndpointId { get; set; }

    /// <summary>24 (daily) or 168 (weekly); 0 is off, keeping the choices.</summary>
    public int IntervalHours { get; set; }

    public PruneImages Images { get; set; }

    /// <summary>Only images (and build cache) at least this many days old; 0 is any age.</summary>
    public int OlderThanDays { get; set; }

    public PruneVolumes Volumes { get; set; }

    public bool Networks { get; set; }

    public bool BuildCache { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public PruneOptions Options => new(Images, OlderThanDays, Volumes, Networks, BuildCache);
}

/// <summary>What a prune removes.</summary>
public sealed record PruneOptions(PruneImages Images, int OlderThanDays, PruneVolumes Volumes, bool Networks, bool BuildCache)
{
    public static readonly IReadOnlyList<int> IntervalChoices = [0, 24, 7 * 24];

    public const int MaxOlderThanDays = 365;

    public bool IsEmpty => Images == PruneImages.None && Volumes == PruneVolumes.None && !Networks && !BuildCache;

    public TimeSpan? OlderThan => OlderThanDays > 0 ? TimeSpan.FromDays(OlderThanDays) : null;

    /// <summary>"dangling images older than 7 days, unused anonymous volumes, unused networks".</summary>
    public string Describe()
    {
        var age = OlderThanDays > 0 ? $" older than {OlderThanDays} {(OlderThanDays == 1 ? "day" : "days")}" : "";
        List<string> parts = [];
        if (Images != PruneImages.None)
            parts.Add($"{(Images == PruneImages.Dangling ? "dangling" : "unused")} images{age}");
        if (Volumes != PruneVolumes.None)
            parts.Add(Volumes == PruneVolumes.Anonymous ? "unused anonymous volumes" : "all unused volumes, named ones too");
        if (Networks)
            parts.Add("unused networks");
        if (BuildCache)
            parts.Add($"build cache{age}");
        return parts.Count == 0 ? "nothing" : string.Join(", ", parts);
    }
}

public enum MaintenanceTask
{
    Backup,
    Prune,
    UpdateCheck,
    VulnerabilityScan,
}

public enum MaintenanceOutcome
{
    Succeeded,
    Failed,
}

/// <summary>One run of scheduled (or "run now") maintenance, for the maintenance page.</summary>
public sealed class MaintenanceRun
{
    public const int MaxSummary = 2000;

    public long Id { get; set; }

    public MaintenanceTask Task { get; set; }

    /// <summary>Null for backups and other work that isn't about one endpoint.</summary>
    public int? EndpointId { get; set; }

    /// <summary>As it was at the time.</summary>
    public string? EndpointName { get; set; }

    public DateTimeOffset StartedAt { get; set; }

    public DateTimeOffset FinishedAt { get; set; }

    public MaintenanceOutcome Outcome { get; set; }

    /// <summary>"schedule", or the user who asked.</summary>
    public required string By { get; set; }

    /// <summary>What it did, or why it failed.</summary>
    public required string Summary { get; set; }
}
