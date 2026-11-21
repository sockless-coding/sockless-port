using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Sockless.Port.Engines;

namespace Sockless.Port.Web.Pages.Endpoints;

public sealed class IndexModel(EngineConnections connections) : PageModel
{
    public IReadOnlyList<EngineConnection> Connections { get; } = connections.All;

    public void OnGet()
    {
    }

    public IActionResult OnGetTable() => Partial("_EndpointTable", Connections);
}
