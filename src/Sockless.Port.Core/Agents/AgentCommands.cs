using System.Globalization;
using System.Reflection;
using Sockless.Port.Docker.Agent;

namespace Sockless.Port.Agents;

/// <summary>How the agent is installed on its host.</summary>
public enum AgentInstallVariant
{
    Docker,
    PodmanRootful,
    PodmanRootless,

    /// <summary>A global service: one agent per node. The endpoint enrols the one at its address.</summary>
    Swarm,
}

/// <summary>A command to run on the agent's host, and what to know before running it.</summary>
public sealed record AgentCommand(AgentInstallVariant Variant, string Label, string Text, IReadOnlyList<string> Notes);

/// <summary>Builds the install and update commands shown on an agent endpoint's page.</summary>
public static class AgentCommands
{
    public const string ContainerName = "sockless-port-agent";
    public const string VolumeName = "sockless-port-agent";

    /// <summary>The server's version, e.g. <c>0.2.0</c>; agents of the same version are current.</summary>
    public static string ServerVersion { get; } =
        (typeof(AgentCommands).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0").Split('+')[0];

    public static IReadOnlyList<(AgentInstallVariant Variant, string Label)> Variants { get; } =
    [
        (AgentInstallVariant.Docker, "Docker"),
        (AgentInstallVariant.PodmanRootful, "Podman (rootful)"),
        (AgentInstallVariant.PodmanRootless, "Podman (rootless)"),
        (AgentInstallVariant.Swarm, "Docker Swarm"),
    ];

    /// <summary>The agent image matching this server: its version, or <c>latest</c> for development builds.</summary>
    public static string DefaultImage(string? configured) =>
        configured is { Length: > 0 } image ? image
        : $"ghcr.io/sockless-coding/sockless-port-agent:{(ServerVersion.Contains('-', StringComparison.Ordinal) ? "latest" : ServerVersion)}";

    /// <summary>The command that installs and enrols the agent.</summary>
    /// <param name="port">The port the server connects to; published to the agent's own 9001.</param>
    public static AgentCommand Install(AgentInstallVariant variant, AgentToken token, int port, string image) =>
        Build(variant, token, new DirectTarget(port), image, update: false);

    /// <summary>The command that replaces the agent with <paramref name="image"/>, keeping its identity (in the volume).</summary>
    public static AgentCommand Update(AgentInstallVariant variant, int port, string image) =>
        Build(variant, token: null, new DirectTarget(port), image, update: true);

    /// <summary>The command that installs an edge agent, which dials <paramref name="serverUrl"/> and enrols through its tunnel.</summary>
    public static AgentCommand InstallEdge(AgentInstallVariant variant, AgentToken token, string serverUrl, string image) =>
        Build(variant, token, new EdgeTarget(serverUrl), image, update: false);

    /// <summary>The command that replaces an edge agent with <paramref name="image"/>, keeping its identity.</summary>
    public static AgentCommand UpdateEdge(AgentInstallVariant variant, string serverUrl, string image) =>
        Build(variant, token: null, new EdgeTarget(serverUrl), image, update: true);

    /// <summary>How the agent and the server meet: the server connects to a published port, or the agent dials the server.</summary>
    private closed record Target;

    private sealed record DirectTarget(int Port) : Target;

    private sealed record EdgeTarget(string ServerUrl) : Target;

    private static AgentCommand Build(AgentInstallVariant variant, AgentToken? token, Target target, string image, bool update)
    {
        var agentPort = AgentProtocol.DefaultPort.ToString(CultureInfo.InvariantCulture);
        var label = Variants.First(v => v.Variant == variant).Label;
        List<string> notes = [];
        string text;

        // Edge agents publish nothing: they dial out. Their environment names the server every time.
        var publish = target is DirectTarget direct ? $"  -p {direct.Port.ToString(CultureInfo.InvariantCulture)}:{agentPort}" : null;
        List<string> environment = [];
        if (target is EdgeTarget edge)
            environment.Add($"AGENT_SERVER={ShellQuote(edge.ServerUrl)}");
        if (token is not null)
            environment.Add($"AGENT_TOKEN={token.Reveal()}");
        string[] env = [.. environment.Select(e => $"  -e {e}")];
        string[] serviceEnv = [.. environment.Select(e => $"  --env {e}")];

        switch (variant)
        {
            case AgentInstallVariant.Docker:
                text = Lines([
                    update ? $"docker pull {image} && docker rm -f {ContainerName} && \\" : null,
                    $"docker run -d --name {ContainerName} --restart unless-stopped",
                    publish,
                    "  -v /var/run/docker.sock:/var/run/docker.sock",
                    "  --group-add \"$(stat -c %g /var/run/docker.sock)\"",
                    $"  -v {VolumeName}:/data",
                    .. env,
                    $"  {image}"]);
                notes.Add("Run it as a user that may use Docker (root, or a member of the docker group).");
                break;

            case AgentInstallVariant.PodmanRootful:
                text = Lines([
                    update ? $"sudo podman pull {image} && sudo podman rm -f {ContainerName} && \\" : "sudo systemctl enable --now podman.socket && \\",
                    $"sudo podman run -d --name {ContainerName} --restart always",
                    publish,
                    "  -v /run/podman/podman.sock:/var/run/docker.sock",
                    "  --group-add \"$(sudo stat -c %g /run/podman/podman.sock)\"",
                    "  --security-opt label=disable",
                    $"  -v {VolumeName}:/data",
                    .. env,
                    $"  {image}"]);
                notes.Add("label=disable lets the agent use the socket on SELinux hosts, as the server's own container does.");
                break;

            case AgentInstallVariant.PodmanRootless:
                text = Lines([
                    update ? $"podman pull {image} && podman rm -f {ContainerName} && \\" : "systemctl --user enable --now podman.socket && \\",
                    $"podman run -d --name {ContainerName} --restart always",
                    publish,
                    "  -v \"$XDG_RUNTIME_DIR/podman/podman.sock:/var/run/docker.sock\"",
                    "  --user 0 --security-opt label=disable",
                    $"  -v {VolumeName}:/data",
                    .. env,
                    $"  {image}"]);
                notes.Add("Run it as the user whose containers it manages. Inside a rootless container, user 0 is that user, not root.");
                notes.Add("To start it after a reboot: loginctl enable-linger, and systemctl --user enable podman-restart.service.");
                break;

            case AgentInstallVariant.Swarm when target is DirectTarget swarmDirect:
                text = update
                    ? $"docker service update --image {image} {ContainerName}"
                    : Lines([
                        $"docker service create --name {ContainerName} --mode global",
                        $"  --publish mode=host,target={agentPort},published={swarmDirect.Port.ToString(CultureInfo.InvariantCulture)}",
                        "  --mount type=bind,src=/var/run/docker.sock,dst=/var/run/docker.sock",
                        "  --group \"$(stat -c %g /var/run/docker.sock)\"",
                        $"  --mount type=volume,src={VolumeName},dst=/data",
                        .. serviceEnv,
                        $"  {image}"]);
                if (!update)
                {
                    notes.Add("Run it on a manager. Every node gets an agent; this endpoint enrols the one at its address, so give it a manager's address.");
                    notes.Add("The socket's group is read on the node you run this on; nodes where it differs need the same group.");
                }
                break;

            case AgentInstallVariant.Swarm:
                // An enrolment works once, so one agent, kept on the manager the command runs on.
                text = update
                    ? Lines([$"docker service update --image {image}", .. environment.Select(e => $"  --env-add {e}"), $"  {ContainerName}"])
                    : Lines([
                        $"docker service create --name {ContainerName} --replicas 1",
                        "  --constraint \"node.id==$(docker info -f '{{.Swarm.NodeID}}')\"",
                        "  --mount type=bind,src=/var/run/docker.sock,dst=/var/run/docker.sock",
                        "  --group \"$(stat -c %g /var/run/docker.sock)\"",
                        $"  --mount type=volume,src={VolumeName},dst=/data",
                        .. serviceEnv,
                        $"  {image}"]);
                if (!update)
                    notes.Add("Run it on a manager: the agent stays on that node (its identity is in the node's volume) and manages the swarm through it.");
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(variant));
        }

        if (!update)
        {
            notes.Add(target switch
            {
                EdgeTarget edgeTarget =>
                    $"The agent connects out to {edgeTarget.ServerUrl}, so its host must reach that address; nothing needs to reach the agent. A reverse proxy in between must pass WebSockets.",
                DirectTarget directTarget => $"Port {directTarget.Port.ToString(CultureInfo.InvariantCulture)} must be reachable from Sockless Port.",
            });
            notes.Add("The token works once, within the hour.");
        }
        return new AgentCommand(variant, label, text, notes);
    }

    /// <summary>The value as one shell word: as it is when it's plain, else single-quoted.</summary>
    internal static string ShellQuote(string value) =>
        value.Length > 0 && value.All(c => char.IsAsciiLetterOrDigit(c) || "_./:@%+=,-~".Contains(c))
            ? value
            : "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";

    private static string Lines(ReadOnlySpan<string?> lines)
    {
        List<string> present = [];
        foreach (var line in lines)
        {
            if (line is not null)
                present.Add(line);
        }
        // Continue every line but the last; a line ending in "&& \" already continues.
        for (var i = 0; i < present.Count - 1; i++)
        {
            if (!present[i].EndsWith('\\'))
                present[i] += " \\";
        }
        return string.Join('\n', present);
    }

    /// <summary>True when <paramref name="agentVersion"/> is older than the server's.</summary>
    public static bool IsOutdated(string? agentVersion) => IsOutdated(agentVersion, ServerVersion);

    /// <summary>True when <paramref name="agentVersion"/> is older than <paramref name="serverVersion"/>. Development builds are 0.0.0, so nothing is older.</summary>
    internal static bool IsOutdated(string? agentVersion, string serverVersion) =>
        TryNumeric(agentVersion, out var agent) && TryNumeric(serverVersion, out var server) && agent < server;

    private static bool TryNumeric(string? version, out Version parsed)
    {
        parsed = new Version();
        if (string.IsNullOrWhiteSpace(version))
            return false;
        var core = version.Split('-', '+')[0];
        return Version.TryParse(core, out parsed!);
    }
}
