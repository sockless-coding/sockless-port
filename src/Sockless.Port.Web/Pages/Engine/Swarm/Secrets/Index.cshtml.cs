using Sockless.Port.Engines;
using Sockless.Port.Web.Infrastructure;

namespace Sockless.Port.Web.Pages.Engine.Swarm.Secrets;

public sealed class IndexModel(EngineConnections connections) : SwarmObjectsIndexModel(connections)
{
    public override SwarmObjectKind Kind => SwarmObjectKind.Secret;
}
