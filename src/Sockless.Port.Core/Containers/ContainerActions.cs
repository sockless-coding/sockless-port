using Sockless.Port.Docker;

namespace Sockless.Port.Containers;

public enum ContainerAction
{
    Start,
    Stop,
    Restart,
    Kill,
    Pause,
    Unpause,
    Remove,
}

public static class ContainerActions
{
    /// <summary>Past tense for messages: "Started web".</summary>
    public static string Done(ContainerAction action) => action switch
    {
        ContainerAction.Start => "Started",
        ContainerAction.Stop => "Stopped",
        ContainerAction.Restart => "Restarted",
        ContainerAction.Kill => "Killed",
        ContainerAction.Pause => "Paused",
        ContainerAction.Unpause => "Resumed",
        ContainerAction.Remove => "Removed",
        _ => action.ToString(),
    };

    /// <summary>Runs one action. Remove forces (stops a running container first) but keeps volumes unless asked.</summary>
    public static Task RunAsync(DockerClient client, ContainerAction action, string id, bool removeVolumes = false, CancellationToken cancellationToken = default) =>
        action switch
        {
            ContainerAction.Start => client.StartContainerAsync(id, cancellationToken),
            ContainerAction.Stop => client.StopContainerAsync(id, cancellationToken: cancellationToken),
            ContainerAction.Restart => client.RestartContainerAsync(id, cancellationToken: cancellationToken),
            ContainerAction.Kill => client.KillContainerAsync(id, cancellationToken: cancellationToken),
            ContainerAction.Pause => client.PauseContainerAsync(id, cancellationToken),
            ContainerAction.Unpause => client.UnpauseContainerAsync(id, cancellationToken),
            ContainerAction.Remove => client.RemoveContainerAsync(id, force: true, removeVolumes, cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(action)),
        };
}
