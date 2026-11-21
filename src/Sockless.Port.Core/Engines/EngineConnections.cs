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

    internal EngineConnection Add(EngineConnection connection)
    {
        if (!_connections.TryAdd(connection.Id, connection))
            throw new InvalidOperationException($"Endpoint {connection.Id} is already registered.");
        Added?.Invoke(connection);
        return connection;
    }
}
