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
public sealed record AppSettings(TimeDisplay TimeDisplay, int SessionTimeoutMinutes, int AuditRetentionDays)
{
    public static AppSettings Default { get; } = new(TimeDisplay.Local, 12 * 60, 365);

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
        return problems;
    }
}

/// <summary>One stored setting.</summary>
public sealed class SettingEntry
{
    public required string Key { get; set; }

    public required string Value { get; set; }
}
