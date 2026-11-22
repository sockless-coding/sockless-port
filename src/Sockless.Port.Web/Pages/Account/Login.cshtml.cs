using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
using Sockless.Port.Audit;
using Sockless.Port.Data;
using Sockless.Port.Web.Infrastructure;

namespace Sockless.Port.Web.Pages.Account;

[EnableRateLimiting(SignInLimit.Policy)]
[NoAudit] // Writes its own entries: the attempt's user name, never the password.
public sealed class LoginModel(SignInManager<PortUser> signIn, UserManager<PortUser> users, IOptions<PortOptions> options, TimeProvider time) : PageModel
{
    [BindProperty]
    public InputModel Input { get; set; } = new();

    [BindProperty(SupportsGet = true)]
    public string? ReturnUrl { get; set; }

    /// <summary>Show the password form even when single sign-on hides it (for recovery).</summary>
    [BindProperty(SupportsGet = true)]
    public bool Local { get; set; }

    [BindProperty(SupportsGet = true)]
    public bool Limited { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? Sso { get; set; }

    public string? ErrorMessage { get; private set; }

    public OidcOptions Oidc => options.Value.Oidc;

    public bool ShowPasswordForm => !Oidc.Enabled || !Oidc.DisablePasswordLogin || Local;

    public IActionResult OnGet()
    {
        if (User.Identity?.IsAuthenticated == true)
            return LocalRedirect(SafeReturnUrl);
        if (Limited)
            ErrorMessage = "Too many sign-in attempts from your address. Wait a minute, then try again.";
        else if (Sso == "failed")
            ErrorMessage = "Single sign-on didn't work. Try again, or ask an administrator to check the server log.";
        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        if (!ModelState.IsValid)
            return Page();

        var userName = Input.UserName.Trim();
        var result = await signIn.PasswordSignInAsync(userName, Input.Password, Input.RememberMe, lockoutOnFailure: true);
        if (result.Succeeded)
        {
            if (await users.FindByNameAsync(userName) is { } user)
            {
                user.LastSignInAt = time.GetUtcNow();
                await users.UpdateAsync(user);
            }
            await AuditTrail.WriteAsync(HttpContext, "Account: sign in", userName: userName, detail: "Password");
            return LocalRedirect(SafeReturnUrl);
        }

        // Same message for an unknown user and a wrong password, so accounts can't be enumerated.
        ErrorMessage = result.IsLockedOut
            ? "Too many failed attempts. Try again in a few minutes."
            : "The user name or password is incorrect, or the user is disabled.";
        await AuditTrail.WriteAsync(HttpContext, "Account: sign in", userName: userName, outcome: AuditOutcome.Failed,
            detail: result.IsLockedOut ? "Locked out after too many wrong passwords." : result.IsNotAllowed ? "The user is disabled." : "Wrong user name or password.");
        return Page();
    }

    private string SafeReturnUrl => Url.IsLocalUrl(ReturnUrl) ? ReturnUrl! : "/";

    public sealed class InputModel
    {
        [Required(ErrorMessage = "Enter your user name.")]
        [Display(Name = "User name")]
        public string UserName { get; set; } = "";

        [Required(ErrorMessage = "Enter your password.")]
        [DataType(DataType.Password)]
        public string Password { get; set; } = "";

        public bool RememberMe { get; set; }
    }
}
