using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Sockless.Port.Engines;

namespace Sockless.Port.Web.Hubs;

/// <summary>
/// Pushes live endpoint state and engine events to browsers. Every client receives
/// <c>endpointState</c>; <c>engineEvent</c> goes only to clients watching that endpoint.
/// </summary>
[Authorize]
public sealed class EngineHub(EngineConnections connections) : Hub
{
    public const string Path = "/hubs/engine";

    public static string Group(int endpointId) => $"endpoint-{endpointId}";

    public async Task Watch(int endpointId)
    {
        if (connections.Get(endpointId) is null)
            throw new HubException("Unknown endpoint.");
        await Groups.AddToGroupAsync(Context.ConnectionId, Group(endpointId));
    }

    public Task Unwatch(int endpointId) => Groups.RemoveFromGroupAsync(Context.ConnectionId, Group(endpointId));
}
