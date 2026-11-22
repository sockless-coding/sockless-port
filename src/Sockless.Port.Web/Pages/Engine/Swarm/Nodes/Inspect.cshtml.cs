using Sockless.Port.Engines;
using Sockless.Port.Web.Infrastructure;

namespace Sockless.Port.Web.Pages.Engine.Swarm.Nodes;

public sealed class InspectModel(EngineConnections connections) : NodePageModel(connections)
{
    public string? Pretty => Format.Json(InspectJson);

    public void OnGet()
    {
    }
}
