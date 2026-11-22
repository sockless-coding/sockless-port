using Sockless.Port.Settings;

namespace Sockless.Port.Web.Infrastructure;

/// <summary>
/// Which time zone timestamps are shown in: the user's preference (local or UTC), else the
/// server's default from Settings. "Local" is the browser's zone, which <c>site.js</c> keeps in a
/// cookie, with its current UTC offset in another for hosts without that zone's rules (Windows
/// in invariant globalization mode). Until it's known (the very first page), times are UTC and say so.
/// </summary>
public static class TimeZones
{
    public const string CookieName = "sockless-port.tz";

    /// <summary>Minutes east of UTC, e.g. <c>60</c> for UTC+01:00.</summary>
    public const string OffsetCookieName = "sockless-port.tzo";

    private static readonly AsyncLocal<TimeZoneInfo?> Ambient = new();

    /// <summary>The zone for the request being rendered; UTC outside one.</summary>
    public static TimeZoneInfo Current => Ambient.Value ?? TimeZoneInfo.Utc;

    public static TimeDisplay Preference(HttpContext context) =>
        context.Access()?.TimeDisplay ?? context.RequestServices.GetRequiredService<PortSettings>().Current.TimeDisplay;

    public static TimeZoneInfo For(HttpContext? context)
    {
        if (context is null || Preference(context) == TimeDisplay.Utc)
            return TimeZoneInfo.Utc;
        var cookies = context.Request.Cookies;
        if (cookies[CookieName] is { Length: > 0 and < 64 } id && TimeZoneInfo.TryFindSystemTimeZoneById(id, out var zone))
            return zone;
        if (int.TryParse(cookies[OffsetCookieName], System.Globalization.NumberStyles.AllowLeadingSign, System.Globalization.CultureInfo.InvariantCulture, out var minutes)
            && minutes is >= -14 * 60 and <= 14 * 60)
        {
            var offset = TimeSpan.FromMinutes(minutes);
            return offset == TimeSpan.Zero ? TimeZoneInfo.Utc : TimeZoneInfo.CreateCustomTimeZone($"UTC{minutes:+0;-0}", offset, "Browser", "Browser");
        }
        return TimeZoneInfo.Utc;
    }

    /// <summary>Sets <see cref="Current"/> for the rest of the request.</summary>
    public static IApplicationBuilder UseTimeZones(this IApplicationBuilder app) =>
        app.Use(async (context, next) =>
        {
            Ambient.Value = For(context);
            await next(context);
        });
}
