using Sockless.Port.Docker;

namespace Sockless.Port.Pods;

public enum PodAction
{
    Start,
    Stop,
    Restart,
    Remove,
}

public static class PodActions
{
    /// <summary>Past tense for messages: "Started web".</summary>
    public static string Done(PodAction action) => action switch
    {
        PodAction.Start => "Started",
        PodAction.Stop => "Stopped",
        PodAction.Restart => "Restarted",
        PodAction.Remove => "Removed",
        _ => action.ToString(),
    };

    /// <summary>
    /// Whether the action would end what Sockless Port runs in: everything but start, on a pod one
    /// of whose containers is ours.
    /// </summary>
    public static bool EndsSelf(PodAction action, bool podIsSelf) => podIsSelf && action != PodAction.Start;

    /// <summary>Runs one action. Remove stops and removes every container of the pod.</summary>
    public static Task RunAsync(DockerClient client, PodAction action, string idOrName, CancellationToken cancellationToken = default) =>
        action switch
        {
            PodAction.Start => client.StartPodAsync(idOrName, cancellationToken),
            PodAction.Stop => client.StopPodAsync(idOrName, cancellationToken: cancellationToken),
            PodAction.Restart => client.RestartPodAsync(idOrName, cancellationToken),
            PodAction.Remove => client.RemovePodAsync(idOrName, cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(action)),
        };
}
