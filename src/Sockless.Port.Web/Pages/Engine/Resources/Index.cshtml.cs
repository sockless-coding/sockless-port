using Sockless.Port.Engines;
using Sockless.Port.Web.Infrastructure;

namespace Sockless.Port.Web.Pages.Engine.Resources;

/// <summary>
/// What the endpoint's running containers use: totals against the host, and a live table per
/// container or stack. The numbers arrive through <c>EngineHub.StreamResourceUsage</c>.
/// </summary>
public sealed class IndexModel(EngineConnections connections) : EndpointPageModel(connections)
{
    public void OnGet()
    {
    }
}
