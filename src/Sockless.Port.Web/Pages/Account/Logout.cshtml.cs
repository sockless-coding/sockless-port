using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Sockless.Port.Data;

namespace Sockless.Port.Web.Pages.Account;

/// <summary>Sign-out is POST only (with antiforgery), so a link or image can't sign someone out.</summary>
public sealed class LogoutModel(SignInManager<PortUser> signIn) : PageModel
{
    public IActionResult OnGet() => RedirectToPage("/Index");

    public async Task<IActionResult> OnPostAsync()
    {
        await signIn.SignOutAsync();
        return RedirectToPage("/Account/Login");
    }
}
