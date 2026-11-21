using Sockless.Port.Docker;
using Sockless.Port.Docker.Engine;

namespace Sockless.Port.Engines;

/// <summary>
/// One endpoint's connection: its address, current state and (while online) the client.
/// Owned and updated by <see cref="EngineMonitor"/>; pages only read it.
/// </summary>
public sealed class EngineConnection
{
    private readonly Lock _gate = new();
    private EndpointState _state;
    private DockerClient? _client;

    public EngineConnection(int id, string name, DockerAddress address, bool isLocal, TimeProvider? time = null)
    {
        Id = id;
        Name = name;
        Address = address;
        IsLocal = isLocal;
        _state = new EndpointConnecting((time ?? TimeProvider.System).GetUtcNow());
    }

    public int Id { get; }

    public string Name { get; }

    public DockerAddress Address { get; }

    public bool IsLocal { get; }

    public EndpointState State
    {
        get { lock (_gate) return _state; }
    }

    /// <summary>The engine while online, otherwise null.</summary>
    public EngineDescriptor? Engine => State is EndpointOnline online ? online.Engine : null;

    /// <summary>
    /// The client while online, otherwise null. A request in flight when the endpoint drops may
    /// see <see cref="ObjectDisposedException"/>; treat it like any other connection failure.
    /// </summary>
    public DockerClient? Client
    {
        get { lock (_gate) return _state is EndpointOnline ? _client : null; }
    }

    internal void SetConnecting(DateTimeOffset now)
    {
        lock (_gate) _state = new EndpointConnecting(now);
    }

    internal void SetOnline(DockerClient client, EngineDescriptor engine, DateTimeOffset now)
    {
        lock (_gate)
        {
            _client = client;
            _state = new EndpointOnline(engine, now);
        }
    }

    /// <summary>Goes offline and returns the previous client for the caller to dispose.</summary>
    internal DockerClient? SetOffline(string reason, DateTimeOffset now, DateTimeOffset nextAttempt)
    {
        lock (_gate)
        {
            var previous = _client;
            _client = null;
            _state = new EndpointOffline(reason, now, nextAttempt);
            return previous;
        }
    }
}
