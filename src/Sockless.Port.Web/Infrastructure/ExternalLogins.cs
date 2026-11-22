using Microsoft.AspNetCore.Authentication;
using Sockless.Port.Audit;

namespace Sockless.Port.Web.Infrastructure;

/// <summary>Single sign-on through OpenID Connect (<c>Port:Oidc</c>).</summary>
public static class ExternalLogins
{
    public const string Scheme = "oidc";

    /// <summary>The redirect URI to register at the provider: <c>https://{host}/signin-oidc</c>.</summary>
    public const string CallbackPath = "/signin-oidc";

    /// <summary>The provider said no, or the answer couldn't be checked: back to the login page, with the reason.</summary>
    public static async Task RemoteFailureAsync(RemoteFailureContext context)
    {
        var reason = context.Failure?.Message ?? "The sign-in provider didn't answer as expected.";
        await AuditTrail.WriteAsync(context.HttpContext, "Account: single sign-on", outcome: AuditOutcome.Failed, detail: reason);
        context.Response.Redirect("/account/login?sso=failed");
        context.HandleResponse();
    }
}
