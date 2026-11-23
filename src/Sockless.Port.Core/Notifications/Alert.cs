namespace Sockless.Port.Notifications;

/// <summary>What an alert is about. Rules choose which kinds they send.</summary>
public enum AlertKind
{
    /// <summary>A container stopped with an error, or ran out of memory, without being asked to stop.</summary>
    ContainerExited,

    /// <summary>A container died three times within ten minutes.</summary>
    ContainerRestarting,

    /// <summary>A container's health check started failing.</summary>
    ContainerUnhealthy,

    /// <summary>An endpoint has been unreachable for two minutes.</summary>
    EndpointOffline,

    /// <summary>An agent is older than the server.</summary>
    AgentOutdated,

    /// <summary>A scan found critical or high vulnerabilities that the image's last scan didn't have.</summary>
    Vulnerabilities,

    /// <summary>A scheduled check found newer images in their registries.</summary>
    UpdatesAvailable,

    /// <summary>CPU or memory stayed above a rule's threshold.</summary>
    ResourceUsage,

    /// <summary>A stack job, or a scheduled update check, failed.</summary>
    TaskFailed,
}

public enum AlertSeverity
{
    Info,
    Warning,
    Critical,
}

/// <summary>
/// Something worth telling people. An alert with a <see cref="Key"/> fires once, and again only
/// after it cleared; clearing it can send a "resolved" notice (<see cref="Resolvable"/>).
/// </summary>
/// <param name="Key">Identifies what it's about, e.g. <c>unhealthy:1:web-1</c>.</param>
/// <param name="Title">One line: "web-1 is unhealthy on local".</param>
/// <param name="Message">Details, a few lines of plain text.</param>
/// <param name="Link">A path on this server, e.g. <c>/endpoints/1/containers/abc</c>; made absolute with the server's address when it's set.</param>
/// <param name="RuleId">Only for this rule (resource thresholds belong to the rule that set them); null for every rule that matches.</param>
public sealed record Alert(AlertKind Kind, string Key, int? EndpointId, string? EndpointName, AlertSeverity Severity, string Title, string Message,
    string? Link = null, int? RuleId = null)
{
    /// <summary>Clearing it means the problem is over, which is worth saying (a container that's healthy again).</summary>
    public bool Resolvable => Kind is AlertKind.ContainerExited or AlertKind.ContainerRestarting or AlertKind.ContainerUnhealthy or AlertKind.EndpointOffline
        or AlertKind.ResourceUsage;

    public static string Describe(AlertKind kind) => kind switch
    {
        AlertKind.ContainerExited => "A container stops with an error",
        AlertKind.ContainerRestarting => "A container keeps restarting",
        AlertKind.ContainerUnhealthy => "A container turns unhealthy",
        AlertKind.EndpointOffline => "An endpoint goes offline",
        AlertKind.AgentOutdated => "An agent is older than the server",
        AlertKind.Vulnerabilities => "A scan finds new critical or high vulnerabilities",
        AlertKind.UpdatesAvailable => "A scheduled check finds image updates",
        AlertKind.ResourceUsage => "CPU or memory stays above a threshold",
        AlertKind.TaskFailed => "A stack job or scheduled check fails",
        _ => kind.ToString(),
    };
}
