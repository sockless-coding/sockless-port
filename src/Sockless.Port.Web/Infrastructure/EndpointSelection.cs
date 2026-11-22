using System.Globalization;
using Sockless.Port.Engines;

namespace Sockless.Port.Web.Infrastructure;

/// <summary>
/// Which endpoint the user is looking at. Endpoint pages carry it in the route
/// (<c>/endpoints/{endpointId}/…</c>); elsewhere the last one visited is remembered in a cookie.
/// Only endpoints the user may see count.
/// </summary>
public static class EndpointSelection
{
    public const string RouteKey = "endpointId";
    private const string CookieName = "sockless-port.endpoint";

    public static EngineConnection? Resolve(HttpContext context, EngineConnections connections)
    {
        var access = context.Access();
        bool Visible(EngineConnection connection) => access?.CanView(connection.Id) == true;

        if (TryGetId(context.GetRouteValue(RouteKey)?.ToString(), out var routeId))
            return connections.Get(routeId) is { } routed && Visible(routed) ? routed : null;

        if (TryGetId(context.Request.Cookies[CookieName], out var cookieId) && connections.Get(cookieId) is { } remembered && Visible(remembered))
            return remembered;

        return connections.All.FirstOrDefault(Visible);
    }

    public static void Remember(HttpResponse response, int endpointId) =>
        response.Cookies.Append(CookieName, endpointId.ToString(CultureInfo.InvariantCulture), new CookieOptions
        {
            HttpOnly = true,
            SameSite = SameSiteMode.Strict,
            IsEssential = true,
            MaxAge = TimeSpan.FromDays(365),
        });

    private static bool TryGetId(string? value, out int id) =>
        int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out id) && id > 0;
}
