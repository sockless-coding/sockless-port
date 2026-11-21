using Sockless.Port.Docker.Engine;

namespace Sockless.Port.Engines;

/// <summary>The live state of an endpoint's connection. Switch on it exhaustively.</summary>
public closed record EndpointState(DateTimeOffset Since);

public sealed record EndpointConnecting(DateTimeOffset Since) : EndpointState(Since);

/// <summary>Connected; <paramref name="Engine"/> says what it is and what it can do.</summary>
public sealed record EndpointOnline(EngineDescriptor Engine, DateTimeOffset Since) : EndpointState(Since);

/// <summary>Not reachable. <paramref name="Reason"/> is shown to the user, so it says what to check.</summary>
public sealed record EndpointOffline(string Reason, DateTimeOffset Since, DateTimeOffset NextAttempt) : EndpointState(Since);
