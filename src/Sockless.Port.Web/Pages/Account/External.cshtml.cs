using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;
using Sockless.Port.Access;
using Sockless.Port.Audit;
using Sockless.Port.Data;
using Sockless.Port.Web.Infrastructure;

namespace Sockless.Port.Web.Pages.Account;

/// <summary>
/// Single sign-on: a post here goes to the OpenID Connect provider, which comes back to
/// <c>/signin-oidc</c> and then to the callback, where the login is matched to a user.
/// </summary>
[NoAudit] // Writes its own entries.
public sealed class ExternalModel(SignInManager<PortUser> signIn, ExternalSignIn external, UserManager<PortUser> users,
    IOptions<PortOptions> options, TimeProvider time) : PageModel
{
    /// <summary>Why the sign-in was refused, for the page.</summary>
    public string? Refusal { get; private set; }

    /// <summary>Where to continue once signed in.</summary>
    public string? ContinueTo { get; private set; }

    public IActionResult OnGet() => RedirectToPage("/Account/Login");

    public IActionResult OnPost(string? returnUrl)
    {
        if (!options.Value.Oidc.Enabled)
            return RedirectToPage("/Account/Login");
        var callback = Url.Page("/Account/External", "Callback", new { returnUrl = Url.IsLocalUrl(returnUrl) ? returnUrl : null });
        var properties = signIn.ConfigureExternalAuthenticationProperties(ExternalLogins.Scheme, callback);
        return Challenge(properties, ExternalLogins.Scheme);
    }

    public async Task<IActionResult> OnGetCallbackAsync(string? returnUrl, CancellationToken cancellationToken)
    {
        if (await signIn.GetExternalLoginInfoAsync() is not { } info)
            return RedirectToPage("/Account/Login", new { sso = "failed" });

        var result = await external.ResolveAsync(info, info.Principal, cancellationToken);
        await HttpContext.SignOutAsync(IdentityConstants.ExternalScheme);
        var name = info.Principal.Identity?.Name ?? info.Principal.FindFirst("preferred_username")?.Value ?? info.ProviderKey;

        switch (result)
        {
            case ExternalSignInResult.Refused refused:
                Refusal = refused.Reason;
                await AuditTrail.WriteAsync(HttpContext, "Account: sign in", userName: name, outcome: AuditOutcome.Failed,
                    detail: $"Single sign-on: {refused.Reason}");
                return Page();

            case ExternalSignInResult.Found found:
                if (!await signIn.CanSignInAsync(found.User))
                {
                    Refusal = "Your user is disabled. Ask an administrator.";
                    await AuditTrail.WriteAsync(HttpContext, "Account: sign in", userName: found.User.UserName, outcome: AuditOutcome.Failed,
                        detail: "Single sign-on: the user is disabled.");
                    return Page();
                }
                await signIn.SignInAsync(found.User, isPersistent: false, authenticationMethod: ExternalLogins.Scheme);
                found.User.LastSignInAt = time.GetUtcNow();
                await users.UpdateAsync(found.User);
                await AuditTrail.WriteAsync(HttpContext, "Account: sign in", userName: found.User.UserName,
                    detail: found.Created ? "Single sign-on, first time: user created" : found.Linked ? "Single sign-on, linked by email address" : "Single sign-on");
                if (found.Created)
                    await AuditTrail.WriteAsync(HttpContext, "Users: create", found.User.UserName, userName: found.User.UserName,
                        detail: "Created on the first single sign-on.");
                // The redirect chain started at the provider, so a browser wouldn't send the
                // SameSite=Strict session cookie on a redirect from here. A page that moves on
                // by itself starts a new, same-site navigation.
                ContinueTo = Url.IsLocalUrl(returnUrl) ? returnUrl : "/";
                return Page();

            default:
                return RedirectToPage("/Account/Login", new { sso = "failed" });
        }
    }
}
