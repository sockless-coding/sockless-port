using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;
using Sockless.Port.Access;
using Sockless.Port.Engines;
using Sockless.Port.Web.Infrastructure;

namespace Sockless.Port.Web.Pages.Users;

public sealed class CreateModel(UserAdmin users, EngineConnections connections, IOptions<PortOptions> options) : PageModel
{
    [BindProperty]
    public InputModel Input { get; set; } = new();

    [BindProperty]
    public AccessForm Access { get; set; } = new();

    public IReadOnlyList<EngineConnection> Endpoints { get; } = connections.All;

    /// <summary>With single sign-on, a user may have no password.</summary>
    public bool SingleSignOn => options.Value.Oidc.Enabled;

    public void OnGet()
    {
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
    {
        var password = string.IsNullOrEmpty(Input.Password) ? null : Input.Password;
        if (password is null && !SingleSignOn)
            ModelState.AddModelError("Input.Password", "Choose a password.");
        if (password is not null && password != Input.ConfirmPassword)
            ModelState.AddModelError("Input.ConfirmPassword", "The passwords don't match.");
        if (password is null && SingleSignOn && string.IsNullOrWhiteSpace(Input.Email))
            ModelState.AddModelError("Input.Email", "Without a password, the user signs in through single sign-on, which finds them by email address.");
        if (!ModelState.IsValid)
            return Page();

        var (id, errors) = await users.CreateAsync(Input.UserName, Input.Email, password, Access.ToInput(), cancellationToken);
        if (id is null)
        {
            foreach (var error in errors)
                ModelState.AddModelError(error.Field is "" ? "" : $"Input.{error.Field}", error.Message);
            return Page();
        }

        AuditTrail.Target(HttpContext, Input.UserName.Trim());
        Flash.Success(TempData, $"Added {Input.UserName.Trim()}.");
        return RedirectToPage("/Users/Edit", new { id });
    }

    public sealed class InputModel
    {
        [Required(ErrorMessage = "Choose a user name.")]
        [Display(Name = "User name")]
        public string UserName { get; set; } = "";

        [EmailAddress(ErrorMessage = "Enter an email address, or leave it empty.")]
        public string? Email { get; set; }

        [DataType(DataType.Password)]
        public string? Password { get; set; }

        [DataType(DataType.Password)]
        [Display(Name = "Confirm password")]
        public string? ConfirmPassword { get; set; }
    }
}
