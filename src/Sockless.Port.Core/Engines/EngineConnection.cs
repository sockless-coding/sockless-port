using Sockless.Port.Docker;
using Sockless.Port.Docker.Agent;
using Sockless.Port.Docker.Engine;

namespace Sockless.Port.Engines;

/// <summary>An agent's certificate, and what it last reported about itself.</summary>
/// <param name="Info">From <c>/agent/info</c> while online; null before the first connection.</param>
public sealed record AgentStatus(string? Thumbprint, DateTimeOffset? CertificateNotAfter, DateTimeOffset? EnrolledAt, AgentInfo? Info = null);

/// <summary>An edge agent's tunnel: the server address it dials, and whether it's up.</summary>
/// <param name="ConnectedSince">Since when the tunnel is up; null while it's down.</param>
/// <param name="Remote">Where it last connected from, as this server saw it (a reverse proxy's address, behind one).</param>
/// <param name="SeenAt">When the tunnel was last up: when it connected, or when it closed.</param>
public sealed record EdgeStatus(string ServerUrl, DateTimeOffset? ConnectedSince = null, string? Remote = null, DateTimeOffset? SeenAt = null);

/// <summary>An enrolment waiting for its agent.</summary>
public sealed record PendingEnrolment(AgentToken Token, DateTimeOffset ExpiresAt);

/// <summary>
/// One endpoint's connection: its address, current state and (while online) the client.
/// Owned and updated by <see cref="EngineMonitor"/>; pages only read it.
/// </summary>
public sealed class EngineConnection
{
    private readonly Lock _gate = new();
    private EndpointState _state;
    private DockerClient? _client;
    private string _name;
    private DockerAddress _address;
    private AgentStatus? _agent;
    private PendingEnrolment? _enrolment;
    private EdgeStatus? _edge;

    public EngineConnection(int id, string name, DockerAddress address, EndpointKind kind, TimeProvider? time = null)
    {
        Id = id;
        _name = name;
        _address = address;
        Kind = kind;
        _state = new EndpointConnecting((time ?? TimeProvider.System).GetUtcNow());
        if (kind == EndpointKind.Agent)
            _agent = new AgentStatus(null, null, null);
    }

    public int Id { get; }

    public EndpointKind Kind { get; }

    public bool IsLocal => Kind == EndpointKind.Local;

    public bool IsAgent => Kind == EndpointKind.Agent;

    /// <summary>An agent that dials this server through a tunnel (edge mode).</summary>
    public bool IsEdge => Edge is not null;

    /// <summary>For edge agents: the tunnel's state. Null for other endpoints.</summary>
    public EdgeStatus? Edge
    {
        get { lock (_gate) return _edge; }
    }

    public string Name
    {
        get { lock (_gate) return _name; }
    }

    /// <summary>Where the engine is. For an agent this carries the TLS settings once it's enrolled.</summary>
    public DockerAddress Address
    {
        get { lock (_gate) return _address; }
    }

    /// <summary>For agent endpoints: the certificate and the agent's last report. Null for other kinds.</summary>
    public AgentStatus? Agent
    {
        get { lock (_gate) return _agent; }
    }

    /// <summary>The enrolment waiting for its agent, if any.</summary>
    public PendingEnrolment? Enrolment
    {
        get { lock (_gate) return _enrolment; }
    }

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

    internal void SetName(string name)
    {
        lock (_gate) _name = name;
    }

    internal void SetAgent(DockerAddress address, AgentStatus agent)
    {
        lock (_gate)
        {
            _address = address;
            _agent = agent;
        }
    }

    internal void SetAgentInfo(AgentInfo? info)
    {
        lock (_gate)
        {
            if (_agent is not null)
                _agent = _agent with { Info = info };
        }
    }

    internal void SetEdge(EdgeStatus? edge)
    {
        lock (_gate) _edge = edge;
    }

    internal void SetEnrolment(PendingEnrolment? enrolment)
    {
        lock (_gate) _enrolment = enrolment;
    }

    internal void SetConnecting(DateTimeOffset now)
    {
        lock (_gate) _state = new EndpointConnecting(now);
    }

    internal void SetEnrolling(DateTimeOffset now, DateTimeOffset expiresAt, string? problem)
    {
        lock (_gate) _state = new EndpointEnrolling(_state is EndpointEnrolling previous ? previous.Since : now, expiresAt, problem);
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
