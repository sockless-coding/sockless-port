using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.RateLimiting;
using Sockless.Port.Audit;
using Sockless.Port.Data;
using Sockless.Port.Setup;
using Sockless.Port.Web.Infrastructure;

namespace Sockless.Port.Web.Pages.Setup;

[EnableRateLimiting(SignInLimit.Policy)]
[NoAudit] // Writes its own entries, without the token or password.
public sealed class SetupModel(SetupState setup, UserManager<PortUser> users, SignInManager<PortUser> signIn) : PageModel
{
    [BindProperty]
    public InputModel Input { get; set; } = new();

    public IReadOnlyList<string> Errors { get; private set; } = [];

    public IActionResult OnGet() => setup.IsComplete ? RedirectToPage("/Account/Login") : Page();

    public async Task<IActionResult> OnPostAsync()
    {
        if (setup.IsComplete)
            return RedirectToPage("/Account/Login");

        if (!ModelState.IsValid)
            return Page();

        if (!setup.ValidateToken(Input.SetupToken))
        {
            await AuditTrail.WriteAsync(HttpContext, "Setup: create administrator", Input.UserName, outcome: AuditOutcome.Failed,
                detail: "Wrong setup token.", userName: "anonymous");
            ModelState.AddModelError("Input.SetupToken", "That setup token is not valid. Copy it again from the container log.");
            return Page();
        }

        var result = await setup.CreateAdministratorAsync(users, Input.SetupToken, Input.UserName.Trim(), Input.Password);
        if (!result.Succeeded)
        {
            Errors = [.. result.Errors.Select(e => e.Description)];
            return Page();
        }

        var user = await users.FindByNameAsync(Input.UserName.Trim());
        await signIn.SignInAsync(user!, isPersistent: false);
        await AuditTrail.WriteAsync(HttpContext, "Setup: create administrator", user!.UserName, userName: user.UserName);
        return RedirectToPage("/Index");
    }

    public sealed class InputModel
    {
        [Required(ErrorMessage = "Enter the setup token from the container log.")]
        [Display(Name = "Setup token")]
        public string SetupToken { get; set; } = "";

        [Required(ErrorMessage = "Choose a user name.")]
        [StringLength(64, MinimumLength = 2, ErrorMessage = "Use 2 to 64 characters.")]
        [RegularExpression(@"^[A-Za-z0-9._@-]+$", ErrorMessage = "Use letters, digits and . _ @ - only.")]
        [Display(Name = "User name")]
        public string UserName { get; set; } = "";

        [Required(ErrorMessage = "Choose a password.")]
        [StringLength(256, MinimumLength = 12, ErrorMessage = "Use at least 12 characters.")]
        [DataType(DataType.Password)]
        public string Password { get; set; } = "";

        [Required(ErrorMessage = "Enter the password again.")]
        [Compare(nameof(Password), ErrorMessage = "The passwords don't match.")]
        [DataType(DataType.Password)]
        [Display(Name = "Confirm password")]
        public string ConfirmPassword { get; set; } = "";
    }
}
