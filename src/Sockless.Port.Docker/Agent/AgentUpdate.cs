using System.Globalization;

namespace Sockless.Port.Docker.Agent;

/// <summary>
/// How the server updates an agent that runs in a container. The agent can't replace itself
/// through its own relay (the server loses it as soon as it stops), so the server pulls the new
/// image and starts a helper from it, <c>sockless-port-agent update --target {id} --image {ref}</c>,
/// with the engine's socket. The helper replaces the agent's container on the host, keeping its
/// settings and its <c>/data</c> volume (the identity), waits for the new one to be healthy, and
/// puts the old one back if anything fails. The server reads how it went from the helper's exit
/// code and output once the agent is back, then removes the helper.
/// </summary>
public static class AgentUpdate
{
    /// <summary>The agent binary's first argument that runs the helper.</summary>
    public const string Command = "update";

    /// <summary>The label every helper Sockless Port starts carries; its value says which kind.</summary>
    public const string HelperLabel = "sockless-port.helper";

    public const string HelperKind = "agent-update";

    /// <summary>On the helper: the container it replaces.</summary>
    public const string TargetLabel = "sockless-port.agent-update.target";

    /// <summary>On the helper: the image it updates to.</summary>
    public const string ImageLabel = "sockless-port.agent-update.image";

    /// <summary><c>org.opencontainers.image.title</c> of the agent image, which containers inherit as a label.</summary>
    public const string ImageTitle = "Sockless Port agent";

    public const string ImageTitleLabel = "org.opencontainers.image.title";

    /// <summary>The engine the helper talks to, as the agent's <c>AGENT_ENGINE</c>.</summary>
    public const string EngineVariable = "AGENT_ENGINE";

    /// <summary>The new agent replaced the old one, which was removed.</summary>
    public const int Updated = 0;

    /// <summary>The update failed and the old agent runs again.</summary>
    public const int RolledBack = 1;

    /// <summary>The helper was started wrongly; nothing was changed.</summary>
    public const int Refused = 2;

    /// <summary>The update failed and so did putting the old agent back: someone needs to look at the host.</summary>
    public const int Broken = 3;

    public static string HelperName(DateTimeOffset at) =>
        $"sockless-port-agent-update-{at.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture)}";

    public static IReadOnlyList<string> Arguments(string targetId, string image) => [Command, "--target", targetId, "--image", image];
}
