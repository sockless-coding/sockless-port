using Sockless.Port.Engines;
using Sockless.Port.Web.Infrastructure;

namespace Sockless.Port.Web.Pages.Engine.Networks;

public sealed class InspectModel(EngineConnections connections) : NetworkPageModel(connections)
{
    public string? Pretty => Format.Json(InspectJson);

    public void OnGet()
    {
    }
}
