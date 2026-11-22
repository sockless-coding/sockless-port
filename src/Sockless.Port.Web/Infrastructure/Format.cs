using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Sockless.Port.Web.Infrastructure;

public static class Format
{
    private static readonly JsonSerializerOptions IndentedJson = new() { WriteIndented = true };

    private static readonly string[] DecimalUnits = ["B", "kB", "MB", "GB", "TB", "PB"];
    private static readonly string[] BinaryUnits = ["B", "KiB", "MiB", "GiB", "TiB", "PiB"];

    /// <summary>Decimal units, as <c>docker system df</c> reports sizes: <c>1.2 GB</c>.</summary>
    public static string Bytes(long bytes) => Scale(bytes, 1000, DecimalUnits);

    /// <summary>Binary units, as memory is reported: <c>15.6 GiB</c>.</summary>
    public static string BinaryBytes(long bytes) => Scale(bytes, 1024, BinaryUnits);

    private static string Scale(long bytes, double step, string[] units)
    {
        if (bytes <= 0)
            return "0 B";

        var value = (double)bytes;
        var unit = 0;
        while (value >= step && unit < units.Length - 1)
        {
            value /= step;
            unit++;
        }
        var format = unit == 0 || value >= 100 ? "0" : value >= 10 ? "0.#" : "0.##";
        return $"{value.ToString(format, CultureInfo.InvariantCulture)} {units[unit]}";
    }

    public static string Count(long value) => value.ToString("N0", CultureInfo.InvariantCulture);

    /// <summary>"3 minutes ago", "2 days ago". Pair with <see cref="Timestamp"/> in a title for the exact time.</summary>
    public static string Relative(DateTimeOffset value, DateTimeOffset? now = null)
    {
        var age = (now ?? DateTimeOffset.UtcNow) - value;
        if (age < TimeSpan.Zero)
            age = TimeSpan.Zero;
        return age switch
        {
            { TotalSeconds: < 60 } => "just now",
            { TotalMinutes: < 2 } => "a minute ago",
            { TotalHours: < 1 } => $"{(int)age.TotalMinutes} minutes ago",
            { TotalHours: < 2 } => "an hour ago",
            { TotalDays: < 1 } => $"{(int)age.TotalHours} hours ago",
            { TotalDays: < 2 } => "yesterday",
            { TotalDays: < 60 } => $"{(int)age.TotalDays} days ago",
            _ => $"{(int)(age.TotalDays / 30)} months ago",
        };
    }

    /// <summary>"in 42 minutes", "in 3 hours" for a time ahead; "now" once it's passed.</summary>
    public static string Until(DateTimeOffset value, DateTimeOffset? now = null)
    {
        var left = value - (now ?? DateTimeOffset.UtcNow);
        return left switch
        {
            { TotalSeconds: <= 0 } => "now",
            { TotalMinutes: < 2 } => "in a minute",
            { TotalHours: < 2 } => $"in {(int)left.TotalMinutes} minutes",
            { TotalDays: < 2 } => $"in {(int)left.TotalHours} hours",
            _ => $"in {(int)left.TotalDays} days",
        };
    }

    public static DateTimeOffset FromUnix(long seconds) => DateTimeOffset.FromUnixTimeSeconds(seconds);

    /// <summary>Parses the engine's RFC 3339 timestamps; null for the zero time it uses for "never".</summary>
    public static DateTimeOffset? EngineTime(string? value) =>
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed) && parsed.Year > 1
            ? parsed
            : null;

    /// <summary>
    /// The exact time, in the user's preferred zone (<see cref="TimeZones.Current"/> unless
    /// <paramref name="zone"/> is given): <c>2026-11-22 18:04:05 UTC</c> or <c>2026-11-22 19:04:05 UTC+01:00</c>.
    /// </summary>
    public static string Timestamp(DateTimeOffset value, TimeZoneInfo? zone = null)
    {
        zone ??= TimeZones.Current;
        if (zone == TimeZoneInfo.Utc)
            return value.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss 'UTC'", CultureInfo.InvariantCulture);
        var local = TimeZoneInfo.ConvertTime(value, zone);
        var offset = local.Offset;
        var sign = offset < TimeSpan.Zero ? "-" : "+";
        return local.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)
               + (offset == TimeSpan.Zero ? " UTC" : $" UTC{sign}{offset.Duration().ToString(@"hh\:mm", CultureInfo.InvariantCulture)}");
    }

    /// <summary>The engine's JSON, indented for the inspect tabs.</summary>
    public static string? Json(string? json) => json is null ? null : JsonNode.Parse(json)?.ToJsonString(IndentedJson);
}
