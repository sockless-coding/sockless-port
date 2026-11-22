using Sockless.Port.Engines;
using Sockless.Port.Web.Infrastructure;

namespace Sockless.Port.Web.Pages.Engine.Swarm.Configs;

public sealed class DetailModel(EngineConnections connections) : SwarmObjectDetailModel(connections)
{
    public override SwarmObjectKind Kind => SwarmObjectKind.Config;
}
