using Sockless.Port.Engines;
using Sockless.Port.Web.Infrastructure;

namespace Sockless.Port.Web.Pages.Engine.Swarm.Services;

public sealed class InspectModel(EngineConnections connections) : ServicePageModel(connections)
{
    public string? Pretty => Format.Json(InspectJson);

    public void OnGet()
    {
    }
}
