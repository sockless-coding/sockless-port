using Sockless.Port.Engines;
using Sockless.Port.Web.Infrastructure;

namespace Sockless.Port.Web.Pages.Engine.Swarm.Configs;

public sealed class CreateModel(EngineConnections connections) : SwarmObjectCreateModel(connections)
{
    public override SwarmObjectKind Kind => SwarmObjectKind.Config;
}
