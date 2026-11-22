using System.Collections.Concurrent;

namespace Sockless.Port.Engines;

/// <summary>All endpoints' live connections, keyed by endpoint id.</summary>
public sealed class EngineConnections
{
    private readonly ConcurrentDictionary<int, EngineConnection> _connections = new();

    public IReadOnlyList<EngineConnection> All => [.. _connections.Values.OrderBy(c => c.Id)];

    public EngineConnection? Get(int id) => _connections.GetValueOrDefault(id);

    /// <summary>Raised when an endpoint is added, so the monitor can start watching it.</summary>
    internal event Action<EngineConnection>? Added;

    /// <summary>Raised when an endpoint is removed, so the monitor stops watching it.</summary>
    internal event Action<EngineConnection>? Removed;

    /// <summary>Raised when an endpoint's settings changed, so the monitor reconnects with them.</summary>
    internal event Action<EngineConnection>? Changed;

    internal EngineConnection Add(EngineConnection connection)
    {
        if (!_connections.TryAdd(connection.Id, connection))
            throw new InvalidOperationException($"Endpoint {connection.Id} is already registered.");
        Added?.Invoke(connection);
        return connection;
    }

    internal bool Remove(int id)
    {
        if (!_connections.TryRemove(id, out var connection))
            return false;
        Removed?.Invoke(connection);
        return true;
    }

    /// <summary>Raised when something pages show changed without a reconnect (a new name).</summary>
    internal event Action<EngineConnection>? Updated;

    /// <summary>Drops the current connection and connects again (e.g. after a new enrolment).</summary>
    internal void Reconnect(EngineConnection connection) => Changed?.Invoke(connection);

    /// <summary>Tells browsers the endpoint changed, e.g. after a rename.</summary>
    internal void Touch(EngineConnection connection) => Updated?.Invoke(connection);
}
