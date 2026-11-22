using Sockless.Port.Engines;
using Sockless.Port.Web.Infrastructure;

namespace Sockless.Port.Web.Pages.Engine.Swarm.Services;

/// <summary>Every task's logs, followed live through the hub (<c>StreamServiceLogs</c>).</summary>
public sealed class LogsModel(EngineConnections connections) : ServicePageModel(connections)
{
    public void OnGet()
    {
    }
}
