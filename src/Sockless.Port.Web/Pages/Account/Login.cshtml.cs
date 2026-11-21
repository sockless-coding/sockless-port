using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Sockless.Port.Data;

namespace Sockless.Port.Web.Pages.Account;

public sealed class LoginModel(SignInManager<PortUser> signIn) : PageModel
{
    [BindProperty]
    public InputModel Input { get; set; } = new();

    [BindProperty(SupportsGet = true)]
    public string? ReturnUrl { get; set; }

    public string? ErrorMessage { get; private set; }

    public IActionResult OnGet()
    {
        if (User.Identity?.IsAuthenticated == true)
            return LocalRedirect(SafeReturnUrl);
        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        if (!ModelState.IsValid)
            return Page();

        var result = await signIn.PasswordSignInAsync(Input.UserName, Input.Password, Input.RememberMe, lockoutOnFailure: true);
        if (result.Succeeded)
            return LocalRedirect(SafeReturnUrl);

        // Same message for an unknown user and a wrong password, so accounts can't be enumerated.
        ErrorMessage = result.IsLockedOut
            ? "Too many failed attempts. Try again in a few minutes."
            : "The user name or password is incorrect.";
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
