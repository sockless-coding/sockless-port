using Sockless.Port.Vulnerabilities;

namespace Sockless.Port.Web.Infrastructure;

/// <summary>What a list shows for an image's vulnerabilities: its stored result and any scan in progress or failed.</summary>
public sealed record ScanFlag(ScanSummary? Summary, ScanState? State)
{
    public static ScanFlag None { get; } = new(null, null);

    /// <summary>The badge and its detail: a scan in progress or a failure says more than an older result.</summary>
    public (StatusBadge? Badge, string? Detail) Display()
    {
        var state = State is ScanFailed && Summary is not null && Summary.ScannedAt > State.Since ? null : State;
        var badge = ScanDisplay.StateBadge(state) ?? ScanDisplay.Badge(Summary);
        var detail = state is not null ? ScanDisplay.StateDetail(state) : Summary is { } summary ? ScanDisplay.Detail(summary) : null;
        return (badge, detail);
    }
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
            return summary.Accepted > 0 ? new("Only accepted risks", "") : new("No known vulnerabilities", "success");
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
        var accepted = summary.Accepted switch { 0 => "", 1 => " 1 accepted risk isn't counted.", var n => $" {n} accepted risks aren't counted." };
        if (counts.Total == 0)
            return $"No known vulnerabilities.{accepted} {when}";
        var parts = Severities.WorstFirst.Where(s => counts.Of(s) > 0).Select(s => $"{counts.Of(s)} {Label(s).ToLowerInvariant()}");
        var fixable = counts.Fixable switch
        {
            0 => "none has a fix yet",
            var n when n == counts.Total => "all have a fix",
            1 => "1 has a fix",
            var n => $"{n} have a fix",
        };
        return $"{string.Join(", ", parts)}; {fixable}.{accepted} {when}";
    }

    /// <summary>"This image · expires in 30 days", "Every image · no expiry", "Expired 3 days ago".</summary>
    public static string RiskScope(Sockless.Port.Vulnerabilities.AcceptedRisk risk, DateTimeOffset now)
    {
        var scope = risk.ImageId is null ? "Every image" : "This image";
        return risk.ExpiresAt switch
        {
            null => $"{scope} · no expiry",
            { } at when at > now => $"{scope} · expires {Format.Until(at, now)}",
            { } at => $"Expired {Format.Relative(at, now)}",
        };
    }

    /// <summary>For the expiry select: 0 is never.</summary>
    public static string ExpiryLabel(int days) => days switch
    {
        0 => "Never (until revoked)",
        30 => "In 30 days",
        365 => "In a year",
        _ => $"In {days / 30} months",
    };

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
