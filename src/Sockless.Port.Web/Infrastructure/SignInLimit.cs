using Microsoft.AspNetCore.RateLimiting;
using Sockless.Port.Audit;

namespace Sockless.Port.Web.Infrastructure;

/// <summary>
/// Rate limit for posts that check a secret (sign-in, setup): <c>Port:SignInAttemptsPerMinute</c>
/// per client address. Behind a reverse proxy, turn on forwarded headers so the address is the
/// client's, not the proxy's.
/// </summary>
public static class SignInLimit
{
    public const string Policy = "sign-in";

    public static async ValueTask RejectedAsync(OnRejectedContext context, CancellationToken cancellationToken)
    {
        var http = context.HttpContext;
        // Webhooks (GitHooks) share the rejection: a bare 429, not audited, so a flood can't fill the log.
        if (http.Request.Path.StartsWithSegments("/hooks", StringComparison.OrdinalIgnoreCase))
        {
            http.Response.StatusCode = StatusCodes.Status429TooManyRequests;
            http.Response.Headers.RetryAfter = "60";
            return;
        }
        await AuditTrail.WriteAsync(http, "Account: sign in", outcome: AuditOutcome.Denied, detail: "Too many attempts from this address.");

        // A page the person can read, rather than a bare 429: the form again, with the reason.
        if (http.Request.Path.StartsWithSegments("/account/login", StringComparison.OrdinalIgnoreCase))
        {
            http.Response.StatusCode = StatusCodes.Status303SeeOther;
            http.Response.Headers.Location = "/account/login?limited=true";
            return;
        }
        http.Response.StatusCode = StatusCodes.Status429TooManyRequests;
        http.Response.Headers.RetryAfter = "60";
    }
}
