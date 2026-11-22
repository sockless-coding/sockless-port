using Microsoft.AspNetCore.SignalR;
using Sockless.Port.Docker.Models;
using Sockless.Port.Engines;

namespace Sockless.Port.Web.Hubs;

/// <summary>Forwards engine notifications to browsers through <see cref="EngineHub"/>.</summary>
internal sealed class SignalREngineEventSink(IHubContext<EngineHub> hub) : IEngineEventSink
{
    public Task StateChangedAsync(EngineConnection connection, CancellationToken cancellationToken) =>
        hub.Clients.All.SendAsync("endpointState", new EndpointStateMessage(connection.Id, StateName(connection.State)), cancellationToken);

    public Task EngineEventAsync(EngineConnection connection, DockerEvent engineEvent, CancellationToken cancellationToken) =>
        // Only the type and action: actor attributes can carry labels and environment-like data.
        hub.Clients.Group(EngineHub.Group(connection.Id)).SendAsync(
            "engineEvent", new EngineEventMessage(connection.Id, engineEvent.Type, engineEvent.Action), cancellationToken);

    internal static string StateName(EndpointState state) => state switch
    {
        EndpointOnline => "online",
        EndpointConnecting => "connecting",
        EndpointOffline => "offline",
        EndpointEnrolling => "enrolling",
    };

    internal sealed record EndpointStateMessage(int EndpointId, string State);

    internal sealed record EngineEventMessage(int EndpointId, string? Type, string? Action);
}
