using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Sockless.Port.Access;
using Sockless.Port.Engines;

namespace Sockless.Port.Web.Infrastructure;

/// <summary>
/// The signed-in user's access for this request, read from the database once per request by
/// <see cref="UseUserAccess"/>. A session whose user was removed, disabled or had their password
/// reset is signed out here, so it ends at its next request.
/// </summary>
public static class CurrentAccess
{
    /// <summary>Added to the request's principal (not the cookie) for administrators; the <see cref="AdminPolicy"/> requires it.</summary>
    public const string AdminClaim = "sockless-port.admin";

    public const string AdminPolicy = "Admin";

    private const string ItemKey = "sockless-port.access";

    /// <summary>The user's access; null when nobody is signed in.</summary>
    public static UserAccess? Access(this HttpContext context) => context.Items[ItemKey] as UserAccess;

    public static bool CanView(this HttpContext context, EngineConnection endpoint) => context.Access()?.CanView(endpoint.Id) == true;

    /// <summary>The endpoints the user may see, in order.</summary>
    public static IReadOnlyList<EngineConnection> Visible(this HttpContext context, EngineConnections connections) =>
        context.Access() is { } access ? [.. connections.All.Where(c => access.CanView(c.Id))] : [];

    public static IApplicationBuilder UseUserAccess(this IApplicationBuilder app) =>
        app.Use(async (context, next) =>
        {
            // Static assets (anonymous endpoints for a file name) need no access. Pages can have a
            // dot in their path too (a volume named my.data), but they aren't anonymous.
            var asset = HttpMethods.IsGet(context.Request.Method) && Path.HasExtension(context.Request.Path.Value)
                        && context.GetEndpoint()?.Metadata.GetMetadata<Microsoft.AspNetCore.Authorization.IAllowAnonymous>() is not null;
            if (!asset && context.User.Identity?.IsAuthenticated == true)
            {
                var loader = context.RequestServices.GetRequiredService<AccessLoader>();
                if (await loader.LoadAsync(context.User, context.RequestAborted) is { } access)
                {
                    context.Items[ItemKey] = access;
                    if (access.IsAdmin && context.User.Identity is ClaimsIdentity identity)
                        identity.AddClaim(new Claim(AdminClaim, "true"));
                }
                else
                {
                    await context.SignOutAsync(IdentityConstants.ApplicationScheme);
                    context.User = new ClaimsPrincipal(new ClaimsIdentity());
                }
            }
            await next(context);
        });
}
