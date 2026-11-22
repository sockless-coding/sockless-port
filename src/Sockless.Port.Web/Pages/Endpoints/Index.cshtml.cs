using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Sockless.Port.Engines;
using Sockless.Port.Web.Infrastructure;

namespace Sockless.Port.Web.Pages.Endpoints;

public sealed class IndexModel(EngineConnections connections) : PageModel
{
    /// <summary>The endpoints the user may see.</summary>
    public IReadOnlyList<EngineConnection> Connections => HttpContext.Visible(connections);

    public void OnGet()
    {
    }

    public IActionResult OnGetTable() => Partial("_EndpointTable", Connections);
}
