using Sockless.Port.Engines;

namespace Sockless.Port.Web.Infrastructure;

public sealed record TopBarView(EngineConnection? Current, IReadOnlyList<EngineConnection> All)
{
    public static TopBarView For(HttpContext context, EngineConnections connections) =>
        new(EndpointSelection.Resolve(context, connections), connections.All);
}
