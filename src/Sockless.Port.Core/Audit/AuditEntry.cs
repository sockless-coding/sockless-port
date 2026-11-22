namespace Sockless.Port.Audit;

public enum AuditOutcome
{
    Succeeded,

    /// <summary>The action ran and failed, e.g. the engine refused it.</summary>
    Failed,

    /// <summary>The user wasn't allowed to do it.</summary>
    Denied,
}

/// <summary>One mutating action: who did what to which target, on which endpoint, and how it went.</summary>
public sealed class AuditEntry
{
    public long Id { get; set; }

    public DateTimeOffset At { get; set; }

    /// <summary>Null for anonymous attempts (a failed sign-in).</summary>
    public string? UserId { get; set; }

    /// <summary>As it was at the time, so the entry still reads after the user is renamed or removed.</summary>
    public required string UserName { get; set; }

    public int? EndpointId { get; set; }

    public string? EndpointName { get; set; }

    /// <summary>What was done, e.g. <c>Containers: stop</c>.</summary>
    public required string Action { get; set; }

    /// <summary>What it was done to, e.g. a container's name; several are comma-separated.</summary>
    public string? Target { get; set; }

    public AuditOutcome Outcome { get; set; }

    /// <summary>Why it failed or was denied.</summary>
    public string? Detail { get; set; }

    public string? RemoteAddress { get; set; }
}
