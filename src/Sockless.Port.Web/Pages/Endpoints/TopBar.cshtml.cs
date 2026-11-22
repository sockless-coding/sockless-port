using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Sockless.Port.Engines;
using Sockless.Port.Web.Infrastructure;

namespace Sockless.Port.Web.Pages.Endpoints;

/// <summary>htmx refresh of the top bar's endpoint switcher and status.</summary>
public sealed class TopBarModel(EngineConnections connections) : PageModel
{
    public IActionResult OnGet(int? current)
    {
        var view = TopBarView.For(HttpContext, connections);
        if (current is { } id && connections.Get(id) is { } selected && HttpContext.CanView(selected))
            view = view with { Current = selected };
        return Partial("_TopBarStatus", view);
    }
}
