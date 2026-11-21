using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Sockless.Port.Engines;
using Sockless.Port.Web.Infrastructure;

namespace Sockless.Port.Web.Pages;

/// <summary>The home page is the current endpoint's dashboard.</summary>
public sealed class IndexModel(EngineConnections connections) : PageModel
{
    public IActionResult OnGet() =>
        EndpointSelection.Resolve(HttpContext, connections) is { } endpoint
            ? RedirectToPage("/Engine/Index", new { endpointId = endpoint.Id })
            : RedirectToPage("/Endpoints/Index");
}
