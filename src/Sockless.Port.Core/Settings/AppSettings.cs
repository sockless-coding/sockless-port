namespace Sockless.Port.Settings;

public enum TimeDisplay
{
    /// <summary>In the browser's time zone.</summary>
    Local,

    Utc,
}

/// <summary>Settings an administrator changes in the UI, stored in the database.</summary>
/// <param name="TimeDisplay">For users who haven't chosen.</param>
/// <param name="SessionTimeoutMinutes">A session ends after this long without a request.</param>
/// <param name="AuditRetentionDays">Audit entries older than this are deleted.</param>
/// <param name="ImageUpdateCheckHours">How often every online endpoint's images are compared with their registries; 0 is never.</param>
/// <param name="VulnerabilityScanHours">How often the images containers use are scanned for known vulnerabilities; 0 is never.</param>
/// <param name="ResourceHistoryDays">How long each endpoint's resource use is kept for trends; 0 records none.</param>
public sealed record AppSettings(TimeDisplay TimeDisplay, int SessionTimeoutMinutes, int AuditRetentionDays, int ImageUpdateCheckHours = 0, int VulnerabilityScanHours = 0,
    int ResourceHistoryDays = 7)
{
    public static AppSettings Default { get; } = new(TimeDisplay.Local, 12 * 60, 365);

    /// <summary>The choices for <see cref="ImageUpdateCheckHours"/>: never, or every 6 hours up to every week.</summary>
    public static readonly IReadOnlyList<int> ImageUpdateCheckChoices = [0, 6, 12, 24, 7 * 24];

    /// <summary>The choices for <see cref="VulnerabilityScanHours"/>: never, every day or every week. New findings come from the database, so more often adds little.</summary>
    public static readonly IReadOnlyList<int> VulnerabilityScanChoices = [0, 24, 7 * 24];

    /// <summary>The choices for <see cref="ResourceHistoryDays"/>: off, or a day up to three months.</summary>
    public static readonly IReadOnlyList<int> ResourceHistoryChoices = [0, 1, 7, 30, 90];

    public const int MinSessionTimeout = 5;
    public const int MaxSessionTimeout = 30 * 24 * 60;
    public const int MinAuditRetention = 7;
    public const int MaxAuditRetention = 10 * 365;

    public TimeSpan SessionTimeout => TimeSpan.FromMinutes(SessionTimeoutMinutes);

    public IReadOnlyList<string> Problems()
    {
        List<string> problems = [];
        if (SessionTimeoutMinutes is < MinSessionTimeout or > MaxSessionTimeout)
            problems.Add($"The session timeout must be between {MinSessionTimeout} minutes and {MaxSessionTimeout / 60 / 24} days.");
        if (AuditRetentionDays is < MinAuditRetention or > MaxAuditRetention)
            problems.Add($"Keep the audit log between {MinAuditRetention} and {MaxAuditRetention} days.");
        if (!ImageUpdateCheckChoices.Contains(ImageUpdateCheckHours))
            problems.Add("Choose how often to check for image updates from the list.");
        if (!VulnerabilityScanChoices.Contains(VulnerabilityScanHours))
            problems.Add("Choose how often to scan for vulnerabilities from the list.");
        if (!ResourceHistoryChoices.Contains(ResourceHistoryDays))
            problems.Add("Choose how long to keep resource history from the list.");
        return problems;
    }
}

/// <summary>One stored setting.</summary>
public sealed class SettingEntry
{
    public required string Key { get; set; }

    public required string Value { get; set; }
}
