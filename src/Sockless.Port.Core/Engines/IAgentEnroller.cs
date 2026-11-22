namespace Sockless.Port.Engines;

/// <summary>What one enrolment attempt achieved.</summary>
public closed record EnrolmentResult;

/// <summary>The agent took its certificate; the connection's address now carries it.</summary>
public sealed record EnrolmentIssued : EnrolmentResult;

/// <summary>The agent says it's already enrolled. If the server has its certificate, connecting tells whether it's this one.</summary>
public sealed record EnrolmentAlreadyDone(string Message) : EnrolmentResult;

/// <summary>Not this time. <paramref name="Problem"/> is shown while the endpoint waits for its agent.</summary>
public sealed record EnrolmentFailed(string Problem) : EnrolmentResult;

/// <summary>Enrols agents for the monitor. Implemented by <c>Agents.AgentEnrolments</c>.</summary>
public interface IAgentEnroller
{
    /// <summary>One attempt at enrolling the agent the connection's pending enrolment is for.</summary>
    Task<EnrolmentResult> EnrolAsync(EngineConnection connection, CancellationToken cancellationToken);

    /// <summary>The agent answered over mutual TLS with its certificate: the enrolment is done.</summary>
    Task CompleteAsync(EngineConnection connection, CancellationToken cancellationToken);
}
