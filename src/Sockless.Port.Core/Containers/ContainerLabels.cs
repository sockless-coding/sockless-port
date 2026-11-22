namespace Sockless.Port.Containers;

/// <summary>Well-known labels that say what manages a container.</summary>
public static class ContainerLabels
{
    public const string ComposeProject = "com.docker.compose.project";
    public const string ComposeService = "com.docker.compose.service";
    public const string StackNamespace = "com.docker.stack.namespace";
    public const string SwarmService = "com.docker.swarm.service.name";
    public const string SwarmTask = "com.docker.swarm.task.id";

    /// <summary>On Sockless Port's own helper containers (e.g. <c>volume-browser</c>).</summary>
    public const string Helper = "sockless-port.helper";

    /// <summary>The Compose project or Swarm stack the container belongs to, if any.</summary>
    public static string? Stack(IReadOnlyDictionary<string, string>? labels) =>
        labels?.GetValueOrDefault(ComposeProject) ?? labels?.GetValueOrDefault(StackNamespace);

    /// <summary>The Swarm service whose task this container runs, if any. Such containers are managed by the swarm.</summary>
    public static string? SwarmServiceName(IReadOnlyDictionary<string, string>? labels) =>
        labels?.ContainsKey(SwarmTask) == true ? labels.GetValueOrDefault(SwarmService) : null;

    /// <summary>
    /// A helper container Sockless Port started for itself, such as the one that reads a volume's
    /// files. It doesn't count as using the volume, and goes away by itself.
    /// </summary>
    public static bool IsHelper(IReadOnlyDictionary<string, string>? labels) => labels?.ContainsKey(Helper) == true;

    /// <summary>Labels added by Compose or Swarm, which a copy of the container must not carry.</summary>
    public static bool IsOrchestratorLabel(string key) =>
        key.StartsWith("com.docker.compose.", StringComparison.Ordinal)
        || key.StartsWith("com.docker.swarm.", StringComparison.Ordinal)
        || key.StartsWith("com.docker.stack.", StringComparison.Ordinal);
}
