using Sockless.Port.Engines;
using Sockless.Port.Web.Infrastructure;

namespace Sockless.Port.Web.Pages.Engine.Pods;

public sealed class InspectModel(EngineConnections connections) : PodPageModel(connections)
{
    public string? Pretty => Format.Json(InspectJson);

    public void OnGet()
    {
    }
}
