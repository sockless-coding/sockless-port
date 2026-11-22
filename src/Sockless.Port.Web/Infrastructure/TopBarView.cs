using Sockless.Port.Engines;

namespace Sockless.Port.Web.Infrastructure;

public sealed record TopBarView(EngineConnection? Current, IReadOnlyList<EngineConnection> All)
{
    /// <summary>The current endpoint and the ones the user may switch to.</summary>
    public static TopBarView For(HttpContext context, EngineConnections connections) =>
        new(EndpointSelection.Resolve(context, connections), context.Visible(connections));
}
