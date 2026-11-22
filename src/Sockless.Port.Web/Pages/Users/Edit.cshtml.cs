using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;
using Sockless.Port.Access;
using Sockless.Port.Engines;
using Sockless.Port.Web.Infrastructure;

namespace Sockless.Port.Web.Pages.Users;

public sealed class EditModel(UserAdmin users, EngineConnections connections, IOptions<PortOptions> options) : PageModel
{
    public UserSummary Summary { get; private set; } = null!;

    [BindProperty]
    public string? Email { get; set; }

    [BindProperty]
    public AccessForm Access { get; set; } = new();

    [BindProperty]
    [DataType(DataType.Password)]
    public string? Password { get; set; }

    public IReadOnlyList<EngineConnection> Endpoints { get; } = connections.All;

    public bool SingleSignOn => options.Value.Oidc.Enabled;

    public bool IsSelf => Summary.User.Id == ActingUserId;

    private string ActingUserId => HttpContext.Access()!.UserId;

    public override async Task OnPageHandlerExecutionAsync(PageHandlerExecutingContext context, PageHandlerExecutionDelegate next)
    {
        var id = RouteData.Values["id"]?.ToString() ?? "";
        if (await users.GetAsync(id, HttpContext.RequestAborted) is not { } summary)
        {
            context.Result = NotFound();
            return;
        }
        Summary = summary;
        AuditTrail.Target(HttpContext, summary.User.UserName);
        await next();
    }

    public void OnGet()
    {
        Email = Summary.User.Email;
        Access = AccessForm.From(Summary);
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(Email) && !new EmailAddressAttribute().IsValid(Email))
            ModelState.AddModelError(nameof(Email), "Enter an email address, or leave it empty.");
        if (ModelState.IsValid)
        {
            var errors = await users.UpdateAsync(Summary.User.Id, Email, Access.ToInput(), ActingUserId, cancellationToken);
            if (errors.Count == 0)
            {
                Flash.Success(TempData, $"Saved {Summary.User.UserName}.");
                return RedirectToPage(new { id = Summary.User.Id });
            }
            AddErrors(errors);
        }
        return Page();
    }

    public async Task<IActionResult> OnPostPasswordAsync()
    {
        if (string.IsNullOrEmpty(Password))
            ModelState.AddModelError(nameof(Password), "Enter the new password.");
        else if (await users.SetPasswordAsync(Summary.User.Id, Password) is { Count: > 0 } errors)
            AddErrors(errors, nameof(Password));
        if (!ModelState.IsValid)
        {
            Email = Summary.User.Email;
            Access = AccessForm.From(Summary);
            return Page();
        }
        Flash.Success(TempData, IsSelf
            ? "Set your new password. Sign in with it now."
            : $"Set a new password for {Summary.User.UserName}. Their sessions have ended, and a lockout is lifted.");
        return RedirectToPage(new { id = Summary.User.Id });
    }

    public Task<IActionResult> OnPostDisableAsync(CancellationToken cancellationToken) => SetDisabledAsync(true, cancellationToken);

    public Task<IActionResult> OnPostEnableAsync(CancellationToken cancellationToken) => SetDisabledAsync(false, cancellationToken);

    public async Task<IActionResult> OnPostRemoveLoginAsync(string provider)
    {
        if (await users.RemoveLoginAsync(Summary.User.Id, provider) is [var error, ..])
            Flash.Error(TempData, error.Message);
        else
            Flash.Success(TempData, $"Unlinked {Summary.User.UserName}'s single sign-on. Their next sign-in through the provider links again by email address, if it matches.");
        return RedirectToPage(new { id = Summary.User.Id });
    }

    public async Task<IActionResult> OnPostRemoveAsync(CancellationToken cancellationToken)
    {
        if (await users.DeleteAsync(Summary.User.Id, ActingUserId, cancellationToken) is [var error, ..])
        {
            Flash.Error(TempData, error.Message);
            return RedirectToPage(new { id = Summary.User.Id });
        }
        Flash.Success(TempData, $"Removed {Summary.User.UserName}.");
        return RedirectToPage("/Users/Index");
    }

    private async Task<IActionResult> SetDisabledAsync(bool disabled, CancellationToken cancellationToken)
    {
        if (await users.SetDisabledAsync(Summary.User.Id, disabled, ActingUserId, cancellationToken) is [var error, ..])
            Flash.Error(TempData, error.Message);
        else
            Flash.Success(TempData, disabled
                ? $"Disabled {Summary.User.UserName}. Their sessions have ended."
                : $"Enabled {Summary.User.UserName}. They can sign in again.");
        return RedirectToPage(new { id = Summary.User.Id });
    }

    private void AddErrors(IReadOnlyList<UserError> errors, string? field = null)
    {
        foreach (var error in errors)
            ModelState.AddModelError(field ?? (error.Field is "Admin" ? "Admin" : error.Field is "" ? "" : error.Field), error.Message);
    }
}
