using Sockless.Port.Engines;
using Sockless.Port.Web.Infrastructure;

namespace Sockless.Port.Web.Pages.Engine.Containers;

public sealed class StatsModel(EngineConnections connections) : ContainerPageModel(connections)
{
    public void OnGet()
    {
    }
}
