using Sockless.Port.Agents;
using Sockless.Port.Docker.Tunnel;

namespace Sockless.Port.Web.Infrastructure;

/// <summary>
/// The address edge agents dial (<see cref="TunnelProtocol.Path"/>): a WebSocket, with no session
/// and no antiforgery token (an agent has neither). The tunnel proves nothing by itself; what runs
/// inside it does (<see cref="AgentTunnels"/>). Rate-limited per client address, so a flood of
/// attempts can't keep the server busy with handshakes.
/// </summary>
public static class AgentTunnelEndpoint
{
    public const string RateLimitPolicy = "agent-tunnels";

    /// <summary>Tunnel attempts per client address per minute. Generous: behind a reverse proxy, every agent shares its address.</summary>
    public const int PerMinute = 120;

    public static IEndpointConventionBuilder MapAgentTunnels(this IEndpointRouteBuilder endpoints)
    {
        // A pipeline of its own, as SignalR has, since WebSockets aren't enabled for the whole app.
        var pipeline = endpoints.CreateApplicationBuilder();
        pipeline.UseWebSockets();
        pipeline.Run(context => context.RequestServices.GetRequiredService<AgentTunnels>().HandleAsync(context));

        return endpoints.Map(TunnelProtocol.Path, pipeline.Build())
            .AllowAnonymous()
            .RequireRateLimiting(RateLimitPolicy)
            .DisableHttpMetrics();
    }
}
