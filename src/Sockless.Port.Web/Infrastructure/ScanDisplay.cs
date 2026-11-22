using Sockless.Port.Vulnerabilities;

namespace Sockless.Port.Web.Infrastructure;

/// <summary>What a list shows for an image's vulnerabilities: its stored result and any scan in progress or failed.</summary>
public sealed record ScanFlag(ScanSummary? Summary, ScanState? State)
{
    public static ScanFlag None { get; } = new(null, null);
}

/// <summary>How vulnerability scans read on the page. Status is always a word, never colour alone.</summary>
public static class ScanDisplay
{
    /// <summary>Rust for critical and high, brass for medium, neutral below.</summary>
    public static string Kind(Severity severity) => severity switch
    {
        Severity.Critical or Severity.High => "danger",
        Severity.Medium => "warning",
        _ => "",
    };

    public static string Label(Severity severity) => severity switch
    {
        Severity.Unknown => "Unrated",
        _ => severity.ToString(),
    };

    /// <summary>"3 critical" for the worst severity found, or "No known vulnerabilities".</summary>
    public static StatusBadge? Badge(ScanSummary? summary)
    {
        if (summary is null)
            return null;
        if (summary.Counts.Worst is not { } worst)
            return new("No known vulnerabilities", "success");
        return new($"{summary.Counts.Of(worst)} {Label(worst).ToLowerInvariant()}", Kind(worst));
    }

    /// <summary>A badge for a scan that's queued, running or failed; null when there's none.</summary>
    public static StatusBadge? StateBadge(ScanState? state) => state switch
    {
        null => null,
        ScanQueued => new("Scan queued", ""),
        ScanRunning => new("Scanning…", "info"),
        ScanFailed => new("Scan failed", "danger"),
    };

    /// <summary>"3 critical, 12 high, 4 medium; 15 have a fix. Scanned 2 days ago."</summary>
    public static string Detail(ScanSummary summary)
    {
        var counts = summary.Counts;
        var when = $"Scanned {Format.Relative(summary.ScannedAt)}.";
        if (counts.Total == 0)
            return $"No known vulnerabilities. {when}";
        var parts = Severities.WorstFirst.Where(s => counts.Of(s) > 0).Select(s => $"{counts.Of(s)} {Label(s).ToLowerInvariant()}");
        var fixable = counts.Fixable switch
        {
            0 => "none has a fix yet",
            var n when n == counts.Total => "all have a fix",
            1 => "1 has a fix",
            var n => $"{n} have a fix",
        };
        return $"{string.Join(", ", parts)}; {fixable}. {when}";
    }

    public static string StateDetail(ScanState state) => state switch
    {
        ScanQueued q => $"Waiting for other scans, queued {Format.Relative(q.Since)}.",
        ScanRunning r => $"{r.Phase}…",
        ScanFailed f => $"{f.Reason} ({Format.Relative(f.Since)})",
    };

    /// <summary>"Queued 3 scans." for the scan buttons.</summary>
    public static string QueuedMessage(int queued, int skipped, string nothing)
    {
        if (queued == 0 && skipped == 0)
            return nothing;
        var message = queued switch
        {
            0 => "Nothing new was queued",
            1 => "Queued 1 scan",
            _ => $"Queued {queued} scans",
        };
        if (skipped > 0)
            message += $" ({skipped} {(skipped == 1 ? "was" : "were")} already queued or running)";
        return message + ". Results appear here when each scan ends.";
    }
}
