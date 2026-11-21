using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Sockless.Port.Web.Hubs;

namespace Sockless.Port.Web.Infrastructure;

internal static class AuthRedirects
{
    /// <summary>
    /// A redirect to the login page is right for a full page load, but not for an htmx partial
    /// (it would swap the login page into a fragment) or a SignalR hub. Those get 401; htmx also
    /// gets <c>HX-Redirect</c> so the whole page goes to the login page.
    /// </summary>
    public static Task ToLoginAsync(RedirectContext<CookieAuthenticationOptions> context)
    {
        var request = context.Request;
        if (request.Path.StartsWithSegments(EngineHub.Path, StringComparison.OrdinalIgnoreCase))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return Task.CompletedTask;
        }

        if (request.Headers.ContainsKey("HX-Request"))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            var returnUrl = request.Headers["HX-Current-URL"].FirstOrDefault() is { } current && Uri.TryCreate(current, UriKind.Absolute, out var uri)
                ? uri.PathAndQuery
                : "/";
            context.Response.Headers["HX-Redirect"] = $"/account/login?returnUrl={Uri.EscapeDataString(returnUrl)}";
            return Task.CompletedTask;
        }

        context.Response.Redirect(context.RedirectUri);
        return Task.CompletedTask;
    }
}
