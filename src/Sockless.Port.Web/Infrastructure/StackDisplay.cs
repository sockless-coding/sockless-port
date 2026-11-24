using System.Globalization;
using Sockless.Port.Stacks;

namespace Sockless.Port.Web.Infrastructure;

/// <summary>How stacks and their jobs read on a page. Badges always carry a word.</summary>
public static class StackDisplay
{
    public static StatusBadge State(ComposeProject project) => project.State switch
    {
        StackState.Running => new($"Running {Count(project.Running)}/{Count(project.Containers.Count)}", "success"),
        StackState.Partial => new($"Partly running {Count(project.Running)}/{Count(project.Containers.Count)}", "warning"),
        StackState.Stopped => new("Stopped", ""),
        StackState.Down => new("Not running", ""),
        _ => new(project.State.ToString(), ""),
    };

    public static StatusBadge State(Swarm.SwarmStack stack) => stack.State switch
    {
        StackState.Running => new($"Running {stack.Running.ToString(CultureInfo.InvariantCulture)}/{stack.Desired.ToString(CultureInfo.InvariantCulture)}", "success"),
        StackState.Partial => new($"Partly running {stack.Running.ToString(CultureInfo.InvariantCulture)}/{stack.Desired.ToString(CultureInfo.InvariantCulture)}", "warning"),
        StackState.Stopped => new(stack.Desired == 0 ? "Scaled to 0" : "Not running", stack.Desired == 0 ? "" : "danger"),
        StackState.Down => new("Not deployed", ""),
        _ => new(stack.State.ToString(), ""),
    };

    public static StatusBadge JobBadge(StackJobStatus status, StackOperation operation) => status switch
    {
        StackJobRunning => new($"{Running(operation)}…", "warning"),
        StackJobSucceeded => new("Done", "success"),
        StackJobFailed => new("Failed", "danger"),
        StackJobCanceled => new("Cancelled", ""),
    };

    public static string JobSummary(StackJobStatus status, StackOperation operation) => status switch
    {
        StackJobRunning => $"{Running(operation)}…",
        StackJobSucceeded s => s.Message,
        StackJobFailed f => f.Error,
        StackJobCanceled => "Cancelled. What Compose already did stays done.",
    };

    /// <summary>The operation as a label: "Deploy", "Take down".</summary>
    public static string Label(StackOperation operation) => operation switch
    {
        StackOperation.Deploy => "Deploy",
        StackOperation.Update => "Update",
        StackOperation.Start => "Start",
        StackOperation.Stop => "Stop",
        StackOperation.Restart => "Restart",
        StackOperation.Down => "Take down",
        StackOperation.Remove => "Remove",
        StackOperation.Fetch => "Fetch",
        StackOperation.Sync => "Fetch and deploy",
        _ => operation.ToString(),
    };

    /// <summary>The operation in progress: "Deploying".</summary>
    public static string Running(StackOperation operation) => operation switch
    {
        StackOperation.Deploy => "Deploying",
        StackOperation.Update => "Updating",
        StackOperation.Start => "Starting",
        StackOperation.Stop => "Stopping",
        StackOperation.Restart => "Restarting",
        StackOperation.Down => "Taking down",
        StackOperation.Remove => "Removing",
        StackOperation.Fetch => "Fetching",
        StackOperation.Sync => "Fetching and deploying",
        _ => operation.ToString(),
    };

    /// <summary>How often a git-backed stack's branch is checked: "Every 5 minutes", "Daily", "Off".</summary>
    public static string Poll(int? minutes) => minutes switch
    {
        null => "Off",
        1440 => "Daily",
        60 => "Every hour",
        >= 60 when minutes % 60 == 0 => $"Every {(minutes / 60).Value.ToString(CultureInfo.InvariantCulture)} hours",
        _ => $"Every {minutes.Value.ToString(CultureInfo.InvariantCulture)} minutes",
    };

    /// <summary>The value the browser sends and the hub reports: <c>deploy</c>, <c>down</c>.</summary>
    public static string Key(StackOperation operation) => operation.ToString().ToLowerInvariant();

    public static StackOperation? Parse(string? key) =>
        Enum.TryParse<StackOperation>(key, ignoreCase: true, out var operation) && Enum.IsDefined(operation) ? operation : null;

    private static string Count(int value) => value.ToString(CultureInfo.InvariantCulture);
}
