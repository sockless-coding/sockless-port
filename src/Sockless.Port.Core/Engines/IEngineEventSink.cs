using Sockless.Port.Docker.Models;

namespace Sockless.Port.Engines;

/// <summary>Receives endpoint state changes and engine events. The web app forwards them to browsers.</summary>
public interface IEngineEventSink
{
    Task StateChangedAsync(EngineConnection connection, CancellationToken cancellationToken);

    Task EngineEventAsync(EngineConnection connection, DockerEvent engineEvent, CancellationToken cancellationToken);
}

internal sealed class NullEngineEventSink : IEngineEventSink
{
    public Task StateChangedAsync(EngineConnection connection, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task EngineEventAsync(EngineConnection connection, DockerEvent engineEvent, CancellationToken cancellationToken) => Task.CompletedTask;
}
