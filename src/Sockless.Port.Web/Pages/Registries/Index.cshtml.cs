using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Sockless.Port.Docker;
using Sockless.Port.Registries;
using Sockless.Port.Web.Infrastructure;

namespace Sockless.Port.Web.Pages.Registries;

public sealed class IndexModel(RegistryCredentials registries) : PageModel
{
    public IReadOnlyList<RegistryInfo> Registries { get; private set; } = [];

    [BindProperty]
    public RegistryForm Form { get; set; } = new();

    public async Task OnGetAsync(CancellationToken cancellationToken) => Registries = await registries.ListAsync(cancellationToken);

    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
    {
        var errors = await registries.SaveAsync(null, Form.ToInput(), cancellationToken);
        if (errors.Count > 0)
        {
            foreach (var error in errors)
                ModelState.AddModelError($"Form.{error.Field}", error.Message);
            Form.Password = null;
            Registries = await registries.ListAsync(cancellationToken);
            return Page();
        }
        Flash.Success(TempData, $"Saved the credentials for {ImageReference.NormalizeRegistry(Form.Registry)}. Pulls from it use them from now on.");
        return RedirectToPage();
    }
}
