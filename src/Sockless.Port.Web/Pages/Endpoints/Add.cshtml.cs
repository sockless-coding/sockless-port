using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Sockless.Port.Engines;
using Sockless.Port.Web.Infrastructure;

namespace Sockless.Port.Web.Pages.Endpoints;

public sealed class AddModel(EndpointRegistry registry) : PageModel
{
    [BindProperty]
    public string Name { get; set; } = "";

    [BindProperty]
    public string Address { get; set; } = "";

    public void OnGet()
    {
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
    {
        var (id, errors) = await registry.AddAgentAsync(new AgentEndpointInput(Name ?? "", Address ?? ""), cancellationToken);
        if (id is null)
        {
            foreach (var error in errors)
                ModelState.AddModelError(error.Field, error.Message);
            return Page();
        }
        Flash.Success(TempData, $"Added {Name!.Trim()}. Run the install command on its host within the hour.");
        return RedirectToPage("/Endpoints/Settings", new { endpointId = id });
    }
}
