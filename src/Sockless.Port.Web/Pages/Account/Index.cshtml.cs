using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Sockless.Port.Access;
using Sockless.Port.Data;
using Sockless.Port.Engines;
using Sockless.Port.Settings;
using Sockless.Port.Web.Infrastructure;

namespace Sockless.Port.Web.Pages.Account;

/// <summary>The signed-in user's own account: their access, password and time display.</summary>
public sealed class IndexModel(UserManager<PortUser> users, SignInManager<PortUser> signIn, EngineConnections connections, PortSettings settings) : PageModel
{
    public PortUser Me { get; private set; } = null!;

    public bool HasPassword { get; private set; }

    public IReadOnlyList<(EngineConnection Endpoint, EndpointRole Role)> Roles { get; private set; } = [];

    public TimeDisplay DefaultTimeDisplay => settings.Current.TimeDisplay;

    [BindProperty]
    public PasswordInput Input { get; set; } = new();

    public async Task<IActionResult> OnGetAsync() => await LoadAsync() ? Page() : Challenge();

    public async Task<IActionResult> OnPostPasswordAsync()
    {
        if (!await LoadAsync())
            return Challenge();
        if (!ModelState.IsValid)
            return Page();

        var result = HasPassword
            ? await users.ChangePasswordAsync(Me, Input.CurrentPassword ?? "", Input.NewPassword)
            : await users.AddPasswordAsync(Me, Input.NewPassword);
        if (!result.Succeeded)
        {
            foreach (var error in result.Errors)
                ModelState.AddModelError(error.Code == "PasswordMismatch" ? "Input.CurrentPassword" : "Input.NewPassword",
                    error.Code == "PasswordMismatch" ? "That isn't your current password." : error.Description);
            return Page();
        }

        // The security stamp changed, which ends every session; this one carries on with the new stamp.
        await signIn.RefreshSignInAsync(Me);
        Flash.Success(TempData, "Changed your password. Your other sessions have ended.");
        return RedirectToPage();
    }

    /// <summary>The sidebar's time display choice. Empty follows the server's default.</summary>
    [NoAudit]
    public async Task<IActionResult> OnPostTimeAsync(TimeDisplay? display, string? returnUrl)
    {
        if (await users.GetUserAsync(User) is { } me)
        {
            me.TimeDisplay = display;
            await users.UpdateAsync(me);
        }
        return LocalRedirect(Url.IsLocalUrl(returnUrl) ? returnUrl : "/account");
    }

    private async Task<bool> LoadAsync()
    {
        if (await users.GetUserAsync(User) is not { } me)
            return false;
        Me = me;
        HasPassword = await users.HasPasswordAsync(me);
        var access = HttpContext.Access()!;
        Roles = [.. connections.All.Select(c => (c, access.RoleFor(c.Id))).Where(r => r.Item2 != EndpointRole.None)];
        return true;
    }

    public sealed class PasswordInput
    {
        [DataType(DataType.Password)]
        [Display(Name = "Current password")]
        public string? CurrentPassword { get; set; }

        [Required(ErrorMessage = "Choose a new password.")]
        [StringLength(256, MinimumLength = 12, ErrorMessage = "Use at least 12 characters.")]
        [DataType(DataType.Password)]
        [Display(Name = "New password")]
        public string NewPassword { get; set; } = "";

        [Compare(nameof(NewPassword), ErrorMessage = "The passwords don't match.")]
        [DataType(DataType.Password)]
        [Display(Name = "Confirm new password")]
        public string? ConfirmPassword { get; set; }
    }
}
