using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Sockless.Port.Data;

namespace Sockless.Port.Settings;

/// <summary>The administrator's settings, read once at start and cached; saving replaces the cache.</summary>
public sealed class PortSettings(IServiceScopeFactory scopes)
{
    private const string TimeDisplayKey = "time-display";
    private const string SessionTimeoutKey = "session-timeout-minutes";
    private const string AuditRetentionKey = "audit-retention-days";
    private const string ImageUpdateCheckKey = "image-update-check-hours";
    private const string VulnerabilityScanKey = "vulnerability-scan-hours";
    private const string ResourceHistoryKey = "resource-history-days";

    private volatile AppSettings _current = AppSettings.Default;

    public AppSettings Current => _current;

    public async Task LoadAsync(PortDbContext db, CancellationToken cancellationToken = default)
    {
        var stored = await db.Settings.AsNoTracking().ToDictionaryAsync(s => s.Key, s => s.Value, cancellationToken);
        var defaults = AppSettings.Default;
        _current = new AppSettings(
            stored.TryGetValue(TimeDisplayKey, out var time) && Enum.TryParse<TimeDisplay>(time, out var display) ? display : defaults.TimeDisplay,
            Int(stored, SessionTimeoutKey) ?? defaults.SessionTimeoutMinutes,
            Int(stored, AuditRetentionKey) ?? defaults.AuditRetentionDays,
            Int(stored, ImageUpdateCheckKey) ?? defaults.ImageUpdateCheckHours,
            Int(stored, VulnerabilityScanKey) ?? defaults.VulnerabilityScanHours,
            Int(stored, ResourceHistoryKey) ?? defaults.ResourceHistoryDays);
    }

    /// <returns>What's wrong with <paramref name="settings"/>; nothing was saved then.</returns>
    public async Task<IReadOnlyList<string>> SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        if (settings.Problems() is { Count: > 0 } problems)
            return problems;

        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PortDbContext>();
        var values = new Dictionary<string, string>
        {
            [TimeDisplayKey] = settings.TimeDisplay.ToString(),
            [SessionTimeoutKey] = settings.SessionTimeoutMinutes.ToString(CultureInfo.InvariantCulture),
            [AuditRetentionKey] = settings.AuditRetentionDays.ToString(CultureInfo.InvariantCulture),
            [ImageUpdateCheckKey] = settings.ImageUpdateCheckHours.ToString(CultureInfo.InvariantCulture),
            [VulnerabilityScanKey] = settings.VulnerabilityScanHours.ToString(CultureInfo.InvariantCulture),
            [ResourceHistoryKey] = settings.ResourceHistoryDays.ToString(CultureInfo.InvariantCulture),
        };
        var existing = await db.Settings.ToDictionaryAsync(s => s.Key, cancellationToken);
        foreach (var (key, value) in values)
        {
            if (existing.TryGetValue(key, out var entry))
                entry.Value = value;
            else
                db.Settings.Add(new SettingEntry { Key = key, Value = value });
        }
        await db.SaveChangesAsync(cancellationToken);
        _current = settings;
        return [];
    }

    private static int? Int(Dictionary<string, string> stored, string key) =>
        stored.TryGetValue(key, out var value) && int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var number) ? number : null;
}
